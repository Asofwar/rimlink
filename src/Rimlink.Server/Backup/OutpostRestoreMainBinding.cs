using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    private static bool IsAdditive(RestoreMainPlan plan) => plan.Version == 2 && plan.DomainStep?.Version == 2 && plan.DomainStep.Action == "Additive";
    private static bool IsBound(RestoreMainPlan plan) => IsAdditive(plan) || plan.Version == 3;

    private static AnchorEnvelope MainMarker(RestoreMainPlan plan)
    {
        byte[] commitment = plan.JournalDigest;
        if (IsBound(plan))
        {
            // Fixed-width raw operands; the one zero byte terminates the domain/version. No self-hash field in P.
            byte[] prefix = Encoding.UTF8.GetBytes(plan.Version == 3 ? "RLORESTORE-MAIN-BOUND/v3\0" : "RLORESTORE-MAIN-BOUND/v1\0");
            commitment = SHA256.HashData([.. prefix, .. plan.JournalDigest, .. SHA256.HashData(OutpostRestoreEvidence.Encode(plan))]);
        }
        return OutpostRestoreFence.Marker(plan.Journal, commitment);
    }

    private static RestoreMainPlan ReadExactMainPlan(string journalRoot)
    {
        byte[] bytes = ReadJournalBytes(Path.Combine(journalRoot, "main.json"));
        var plan = OutpostRestoreEvidence.DecodePlan(bytes);
        _ = OutpostRestoreEvidence.Plan(RootTransition(plan));
        if (IsBound(plan) && !bytes.AsSpan().SequenceEqual(OutpostRestoreEvidence.Encode(plan)))
            throw new InvalidDataException("restore-main-plan-not-canonical");
        return plan;
    }

    private static void RequireExactMainBytes(RestoreMainPlan plan, string journalRoot)
    {
        var actual = ReadExactMainPlan(journalRoot);
        if (!OutpostRestoreEvidence.Same(actual, plan)) throw new InvalidDataException("restore-main-bound-plan-changed");
    }

    private async Task BindMainPlanAsync(RestoreMainPlan plan, RestoreJournal journal, string journalRoot,
        OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        RequireExactMainBytes(plan, journalRoot);
        var observed = anchor.ReadEnvelope(held); var unbound = OutpostRestoreFence.Marker(plan.Journal, plan.JournalDigest);
        var bound = MainMarker(plan);
        if (!IsBound(plan))
        {
            if (observed != unbound) throw new InvalidDataException("restore-main-marker-mismatch");
            return;
        }
        if (journal.Phase != RestorePhase.InstalledVerified) throw new InvalidDataException("restore-main-binding-phase-invalid");
        if (observed != unbound && observed != bound) throw new InvalidDataException("restore-main-bound-marker-mismatch");
        // Both permitted InstalledVerified gaps have the original exact pre-schema, never a post-DDL result.
        await ValidatePreSchemaCandidateAsync(plan, requireExecutor: IsAdditive(plan) && observed == unbound, ct);
        if (observed == unbound)
        {
            anchor.WriteEnvelopeDurable(held, bound, CancellationToken.None);
            if (anchor.ReadEnvelope(held) != bound) throw new InvalidDataException("restore-main-binding-readback-mismatch");
            Checkpoint?.Invoke("AfterMainPlanBound");
        }
    }

    private static async Task ValidatePreSchemaCandidateAsync(RestoreMainPlan plan, bool requireExecutor, CancellationToken ct)
    {
        await using var db = OpenRestoreDatabase(plan.Journal.Selection.DataDirectory, readOnly: true);
        var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        if (authority.HeadId != plan.Cut.Authority.HeadId || !authority.HeadDigest.AsSpan().SequenceEqual(plan.Cut.Authority.HeadDigest))
            throw new InvalidDataException("restore-main-unbound-source-changed");
        var observed = await OutpostAuthority.ReadStoredChainAsync(db, new SaveFiles(DataDirectory.Existing(plan.Journal.Selection.DataDirectory)),
            authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), false, ct);
        OutpostRestoreEvidence.ValidateSourceDomain(plan.DomainStep!, plan.Cut, observed, selectedAcceptance: plan.Journal.Selection.Isolated?.Source);
        var expected = await OutpostRestoreEvidence.CreatePlanAsync(db, plan.Journal, plan.JournalDigest, plan.Cut, ct,
            plan.CutManifest, plan.Version, plan.DomainStep);
        if (!OutpostRestoreEvidence.Same(expected with { NextHeadId = plan.NextHeadId }, plan))
            throw new InvalidDataException("restore-main-candidate-map-mismatch");
        if (requireExecutor) await OutpostRestoreSchemaMigration.RequireFrozenExecutorForDdlAsync(db, plan.DomainStep!, ct);
    }

    private async Task<AnchorEnvelope> BackoutMainMarkerAsync(RestoreIntent intent, RestoreJournal journal, string journalRoot,
        OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct, bool terminalEmpty = false)
    {
        var observed = anchor.ReadEnvelope(held); var unbound = OutpostRestoreFence.Marker(intent, journal.IntentDigest);
        bool Matches(AnchorEnvelope expected) => observed == expected
            || terminalEmpty && expected is FreshAnchorFence fresh && observed == fresh with { State = FreshFenceState.BackedOutEmpty };
        if (!File.Exists(Path.Combine(journalRoot, "main.json")))
        {
            if (!Matches(unbound)) throw new InvalidDataException("restore-backout-bound-plan-missing");
            return unbound;
        }
        var plan = ReadExactMainPlan(journalRoot);
        if (!OutpostRestoreEvidence.Same(plan.Journal, intent) || !plan.JournalDigest.AsSpan().SequenceEqual(journal.IntentDigest))
            throw new InvalidDataException("restore-backout-bound-intent-mismatch");
        if (!IsBound(plan))
        {
            if (!Matches(unbound)) throw new InvalidDataException("restore-backout-bound-marker-mismatch");
            return unbound;
        }
        if (observed == unbound)
        {
            if (journal.Phase == RestorePhase.BackingOut && journal.BackoutConfirmation is { Length: 32 }
                && !File.Exists(Path.Combine(journalRoot, "backout-installed.json")))
            {
                // Completion of the licensed unbound pre-plan backout. The existing per-file protocol admits ONLY
                // original I-bound C bytes or verified old E bytes; a migrated DB/R cannot match that inventory.
                return unbound;
            }
            if (journal.Phase != RestorePhase.InstalledVerified) throw new InvalidDataException("restore-backout-unbound-phase-invalid");
            await ValidatePreSchemaCandidateAsync(plan, requireExecutor: false, ct);
            return unbound;
        }
        var bound = MainMarker(plan);
        if (!Matches(bound) || journal.Phase is not (RestorePhase.InstalledVerified or RestorePhase.MainPrepared or RestorePhase.MainCommitted
            or RestorePhase.BackingOut or RestorePhase.BackedOut)) throw new InvalidDataException("restore-backout-bound-marker-mismatch");
        if (journal.Phase == RestorePhase.InstalledVerified) await ValidatePreSchemaCandidateAsync(plan, requireExecutor: false, ct);
        return bound;
    }

    private static void RequireMainMarker(RestoreMainPlan plan, string journalRoot, OutpostAnchorStore anchor,
        OutpostAnchorStore.OutpostAnchorLease held)
    {
        RequireExactMainBytes(plan, journalRoot);
        if (anchor.ReadEnvelope(held) != MainMarker(plan)) throw new InvalidDataException("restore-main-bound-marker-mismatch");
    }
}
