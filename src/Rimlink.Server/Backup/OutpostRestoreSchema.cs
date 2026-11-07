using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    private async Task EnsureAdditiveSchemaAsync(RimlinkDbContext db, RestoreMainPlan plan, string journalRoot,
        OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        RequireMainMarker(plan, journalRoot, anchor, held);
        OutpostRestoreSchemaMigration.ValidateRecordedStep(plan.DomainStep!);
        var actual = await OutpostDomainSchema.ObserveAsync(db, ct);
        var binding = new OutpostSchemaBinding(actual.ProfileId, actual.Digest.ToByteArray());
        if (OutpostRestoreEvidence.Same(binding, plan.DomainStep!.Source))
        {
            await ValidatePreRAdditiveStateAsync(db, plan, allowPostSchema: false, ct);
            // No outer transaction: EF owns precisely the frozen, already-qualified additive migration transaction.
            await OutpostRestoreSchemaMigration.RequireFrozenExecutorForDdlAsync(db, plan.DomainStep, ct);
            RequireMainMarker(plan, journalRoot, anchor, held);
            Checkpoint?.Invoke("BeforeSchemaMigration");
            await OutpostRestoreSchemaMigration.ApplyFrozenAsync(db, plan.DomainStep, ct);
            Checkpoint?.Invoke("AfterSchemaMigration");
        }
        else if (!OutpostRestoreEvidence.Same(binding, plan.DomainStep.Target))
            throw new InvalidDataException("restore-schema-partial-or-unqualified");
        // Post-schema continuation deliberately calls neither executor observation, generator nor migrator.
        RequireMainMarker(plan, journalRoot, anchor, held);
        await ValidatePreRAdditiveStateAsync(db, plan, allowPostSchema: true, ct);
    }

    private static async Task ValidatePreRAdditiveStateAsync(RimlinkDbContext db, RestoreMainPlan plan,
        bool allowPostSchema, CancellationToken ct, string? dataRoot = null)
    {
        var schema = await OutpostDomainSchema.ObserveAsync(db, ct);
        var expectedSchema = allowPostSchema ? plan.DomainStep!.Target : plan.DomainStep!.Source;
        if (schema.ProfileId != expectedSchema.ProfileId || !schema.Digest.Span.SequenceEqual(expectedSchema.Digest))
            throw new InvalidDataException("restore-schema-source-result-mismatch");
        if (schema.IsCurrent && await db.OutpostAdminCommands.AsNoTracking().AnyAsync(ct))
            throw new InvalidDataException("restore-schema-new-admin-domain-not-empty");
        var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var cut = plan.Cut.Authority;
        if (authority.InstallationId != cut.InstallationId || authority.WorldId != cut.WorldId || authority.RestoreBoundary != cut.RestoreBoundary
            || authority.HeadId != cut.HeadId || authority.TransitionId != cut.TransitionId || !authority.HeadDigest.AsSpan().SequenceEqual(cut.HeadDigest))
            throw new InvalidDataException("restore-schema-pre-r-authority-changed");
        var observed = await OutpostAuthority.ReadStoredChainAsync(db, new SaveFiles(DataDirectory.Existing(dataRoot ?? plan.Journal.Selection.DataDirectory)),
            authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), currentRuntime: false, ct);
        OutpostRestoreEvidence.ValidateSourceDomain(plan.DomainStep!, plan.Cut, observed, allowPostSchema);
        var expected = await OutpostRestoreEvidence.CreatePlanAsync(db, plan.Journal, plan.JournalDigest, plan.Cut, ct,
            plan.CutManifest, plan.Version, plan.DomainStep, allowPostSchema);
        if (!OutpostRestoreEvidence.Same(expected with { NextHeadId = plan.NextHeadId }, plan))
            throw new InvalidDataException("restore-schema-pre-r-projection-changed");
    }
}
