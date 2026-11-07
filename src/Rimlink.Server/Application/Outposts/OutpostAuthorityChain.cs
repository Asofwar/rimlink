using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority
{
    internal sealed record StoredDomainObservation(OutpostDomainFormat Format, OutpostSchemaObservation Schema, ByteString Digest);

    /// <summary>Schema admission precedes typed readers. Only the cryptographically verified chain selects the domain writer.</summary>
    internal static async Task<StoredDomainObservation> ReadStoredChainAsync(RimlinkDbContext db, SaveFiles files,
        Storage.Entities.OutpostAuthority row, AuthorityHead expected, bool currentRuntime, CancellationToken ct)
    {
        OutpostSchemaObservation schema;
        try { schema = await OutpostDomainSchema.ObserveAsync(db, ct); }
        catch (InvalidDataException error) { throw new OutpostEvidenceUnavailableException(error.Message); }
        if (currentRuntime && !schema.IsCurrent) throw new OutpostEvidenceUnavailableException("outpost-schema-current-profile-required");
        var retained = await db.OutpostTransitions.AsNoTracking().ToListAsync(ct);
        AuthorityHead head = GenesisHead();
        string? predecessor = null, boundWorld = null, boundInstallation = null, boundBoundary = null;
        OutpostTransition? tip = null;
        OutpostDomainFormat format = OutpostDomainFormat.V1;
        foreach (var transition in OrderTransitions(retained))
        {
            if (transition.PredecessorId != predecessor || predecessor is null && transition.Kind != "Initialize"
                || !OutpostTransitionDigest.Compute(head, transition).Span.SequenceEqual(transition.Digest))
                throw new OutpostEvidenceUnavailableException("authority-chain-digest-mismatch");
            var binding = OutpostAdminReceiptEvidence.ReadBinding(transition);
            if (predecessor is null)
            {
                if (binding is null)
                {
                    var original = OutpostRestoreEvidence.Decode<LegacyInitialBinding>(transition.Intent);
                    // ReadBinding already proved the exact legacy shape and identities, not just the absence of a version field.
                    boundInstallation = original.Installation; boundBoundary = original.Boundary; boundWorld = original.World;
                    format = OutpostDomainFormat.V1;
                }
                else
                {
                    var initial = OutpostAdminReceiptEvidence.ReadInitial(transition);
                    boundInstallation = initial.Installation; boundBoundary = initial.Boundary; boundWorld = initial.World;
                    format = initial.DomainFormat;
                }
            }
            else if (transition.Kind == "BulkRestore")
            {
                var plan = OutpostRestoreEvidence.Plan(transition);
                // An immutable v1 restore is not an implicit format activation. Versioned restore admission is separate.
                if ((plan.Version == 1 ? format != OutpostDomainFormat.V1 : plan.DomainStep!.From != format)
                    || plan.Cut.Authority.InstallationId != boundInstallation
                    || plan.Cut.Authority.RestoreBoundary != boundBoundary || plan.Cut.Authority.WorldId != boundWorld
                    || !plan.Cut.Authority.HeadDigest.AsSpan().SequenceEqual(head.Digest.Span))
                    throw new OutpostEvidenceUnavailableException("authority-chain-restore-source-mismatch");
                format = OutpostRestoreEvidence.TargetFormat(plan);
                boundInstallation = plan.Journal.Selection.TargetInstallationId; boundBoundary = plan.Journal.Selection.TargetBoundary;
            }
            else if (transition.Kind == "Initialize" || transition.RestoreBoundary != boundBoundary)
                throw new OutpostEvidenceUnavailableException("authority-chain-identity-mismatch");
            if (transition.WorldId != boundWorld) throw new OutpostEvidenceUnavailableException("authority-chain-world-mismatch");
            if (binding is not null)
            {
                if (!schema.IsCurrent) throw new OutpostEvidenceUnavailableException("stored-admin-command-evidence-mismatch");
                var command = await ReadBoundCommandAsync(db, transition, binding, ct);
                if (predecessor is not null)
                {
                    var observed = command.Expected.Committed;
                    if (observed is null || observed.DomainFormat != format || observed.HeadId != head.Id
                        || !observed.HeadDigest.AsSpan().SequenceEqual(head.Digest.Span)
                        || observed.TransitionId != predecessor || observed.Installation != boundInstallation
                        || observed.World != boundWorld || observed.Boundary != boundBoundary)
                        throw new OutpostEvidenceUnavailableException("authority-chain-domain-format-mismatch");
                    if (command.Action == OutpostAdminActionKind.ActivateDomainFormat)
                    {
                        var activation = command.Activation!;
                        if (format != OutpostDomainFormat.V1 || activation.From != format || activation.To != OutpostDomainFormat.V2
                            || tip is null || !activation.PreviousDomainDigest.AsSpan().SequenceEqual(tip.Effects))
                            throw new OutpostEvidenceUnavailableException("authority-chain-domain-format-mismatch");
                        format = OutpostDomainFormat.V2;
                    }
                    else if (format != OutpostDomainFormat.V2)
                        throw new OutpostEvidenceUnavailableException("authority-chain-domain-format-mismatch");
                }
            }
            if (transition.Kind == "WorldBind")
                boundWorld = await new OutpostLifecycleEvidence(db, files).ReadWorldBindTargetAsync(transition, ct);
            head = new(transition.NextHeadId, ByteString.CopyFrom(transition.Digest));
            predecessor = transition.Id; tip = transition;
        }
        await OutpostAdminReceiptEvidence.ValidateAsync(db, retained, schema, ct);
        await OutpostAdminResolveEvidence.ValidateAllAsync(db, files, retained.ToDictionary(t => t.Id, StringComparer.Ordinal), ct);
        // Compute exactly once with the verified fold. Never try the other hash when this one disagrees.
        ByteString domainDigest = await OutpostDomainDigest.ComputeAsync(db, format, ct);
        if (tip is null || boundWorld != row.WorldId || boundInstallation != row.InstallationId || boundBoundary != row.RestoreBoundary
            || head != expected || tip.Id != row.TransitionId || !tip.Effects.AsSpan().SequenceEqual(domainDigest.Span))
            throw new OutpostEvidenceUnavailableException("authority-protected-state-mismatch");
        return new(format, schema, domainDigest);
    }

    internal static IEnumerable<OutpostTransition> OrderTransitions(IEnumerable<OutpostTransition> transitions)
    {
        var remaining = new Dictionary<string, OutpostTransition>(StringComparer.Ordinal);
        foreach (var transition in transitions)
            if (!remaining.TryAdd(transition.OldHeadId, transition))
                throw new OutpostEvidenceUnavailableException("authority-chain-disconnected-or-forked");
        string headId = GenesisHead().Id;
        while (remaining.Count != 0)
        {
            if (!remaining.Remove(headId, out var transition))
                throw new OutpostEvidenceUnavailableException("authority-chain-disconnected-or-forked");
            yield return transition;
            headId = transition.NextHeadId;
        }
    }

    private static async Task<OutpostAdminIntent> ReadBoundCommandAsync(RimlinkDbContext db, OutpostTransition transition,
        OutpostAdminTransitionBinding binding, CancellationToken ct)
    {
        var receipt = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(r => r.Id == binding.CommandId
            && r.TransitionId == transition.Id && r.Command.Length <= OutpostAdminIntentCodec.MaximumCommandBytes
            && r.CommandDigest.Length == 32, ct);
        if (receipt is null || !receipt.CommandDigest.AsSpan().SequenceEqual(binding.CommandDigest)
            || !SHA256.HashData(receipt.Command).AsSpan().SequenceEqual(binding.CommandDigest))
            throw new OutpostEvidenceUnavailableException("stored-admin-command-evidence-mismatch");
        try { return OutpostAdminIntentCodec.Decode(receipt.Command); }
        catch (InvalidDataException) { throw new OutpostEvidenceUnavailableException("stored-admin-command-evidence-mismatch"); }
    }

    private sealed record LegacyInitialBinding(string Installation, string Boundary, string World);
}
