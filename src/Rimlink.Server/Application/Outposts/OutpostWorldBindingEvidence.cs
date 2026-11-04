using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed record LifecycleWorld(long Id, string InstanceId, string Seed, byte[] Parameters, string NpcTiles, DateTime CreatedAt);
public sealed record LifecycleBinding(string InstallationId, string RetirementId, byte[] RetirementIntentDigest,
    byte[] RetirementTransitionDigest, LifecycleWorld Target);

public sealed partial class OutpostLifecycleEvidence
{
    private static void ValidateBindingIntent(LifecycleIntent value)
    {
        var binding = value.Binding;
        if (binding is null || !Guid.TryParseExact(binding.InstallationId, "D", out _)
            || !Guid.TryParseExact(binding.RetirementId, "D", out _) || binding.RetirementIntentDigest.Length != 32
            || binding.RetirementTransitionDigest.Length != 32 || value.PlayerId != 0 || value.ArchiveId != 0
            || value.Outposts.Length != 0 || value.Versions.Length != 0 || value.Files.Length != 0
            || value.ResultScopes is null || value.ResultScopes.Length != value.Scopes.Length
            || binding.Target.Id <= 0 || !Guid.TryParseExact(binding.Target.InstanceId, "N", out _)
            || binding.Target.InstanceId == value.WorldId || string.IsNullOrWhiteSpace(binding.Target.Seed)
            || binding.Target.Parameters.Length is 0 or > Rimlink.Protocol.Limits.MaxMessageBytes
            || binding.Target.CreatedAt.Kind != DateTimeKind.Utc)
            throw Invalid("lifecycle-world-binding-invalid");
        WorldParams parameters;
        try { parameters = WorldParams.Parser.ParseFrom(binding.Target.Parameters); }
        catch (InvalidProtocolBufferException) { throw Invalid("lifecycle-world-parameters-invalid"); }
        if (parameters.Seed != binding.Target.Seed) throw Invalid("lifecycle-world-parameters-invalid");
        for (int i = 0; i < value.Scopes.Length; i++)
        {
            var expected = OutpostScopeMessage.Parser.ParseFrom(value.Scopes[i].Scope);
            expected.WorldInstanceId = binding.Target.InstanceId;
            if (!expected.ToByteArray().AsSpan().SequenceEqual(value.ResultScopes[i].Scope))
                throw Invalid("lifecycle-world-binding-scope-invalid");
        }
    }

    private async Task ValidateWorldBindStoredAsync(OutpostLifecycleOperation row, LifecycleIntent intent,
        IReadOnlyList<OutpostLifecycleOperation> operations, IReadOnlyDictionary<string, OutpostTransition> transitions,
        HashSet<string> referenced, CancellationToken ct)
    {
        var binding = intent.Binding!;
        var sources = operations.Where(r => r.Id == binding.RetirementId && r.Kind == "Recreate" && r.Phase == "Completed").ToArray();
        if (sources.Length != 1 || !sources[0].IntentDigest.AsSpan().SequenceEqual(binding.RetirementIntentDigest)
            || row.Phase != "Completed" || sources[0].WorldId != row.WorldId || sources[0].RestoreBoundary != row.RestoreBoundary)
            throw Invalid("lifecycle-world-retirement-mismatch");
        var retirement = Intent(sources[0]); var retired = Decode<LifecycleProgress>(sources[0].Progress);
        if (!transitions.TryGetValue(retired.TransitionId, out var source)
            || !source.Digest.AsSpan().SequenceEqual(binding.RetirementTransitionDigest)
            || intent.HeadId != source.NextHeadId || !intent.HeadDigest.AsSpan().SequenceEqual(source.Digest)
            || operations.Count(r => r.Kind == "WorldBind" && Intent(r).Binding!.RetirementId == binding.RetirementId) != 1)
            throw Invalid("lifecycle-world-retirement-mismatch");
        foreach (var owner in retirement.ResultScopes!)
        {
            var expected = OutpostScopeMessage.Parser.ParseFrom(owner.Scope);
            int index = Array.IndexOf(intent.Owners, expected.OwnerPlayerId);
            if (index < 0 || !intent.Scopes[index].Scope.AsSpan().SequenceEqual(owner.Scope))
                throw Invalid("lifecycle-world-retirement-scope-mismatch");
        }
        var matches = transitions.Values.Where(t => t.Kind == "WorldBind" && LifecycleOperation(t.Intent) == row.Id).ToArray();
        if (matches.Length != 1) throw Invalid("lifecycle-world-binding-evidence-missing");
        var transition = matches[0]; var evidence = Decode<LifecycleTransition>(transition.Intent);
        var progress = Decode<LifecycleProgress>(row.Progress); var result = Decode<LifecycleResult>(row.Result);
        if (transition.WorldId != row.WorldId || transition.RestoreBoundary != row.RestoreBoundary || transition.PlayerId != 0
            || transition.SaveGeneration != 0 || transition.Actor != row.Actor || transition.OldHeadId != intent.HeadId
            || transition.PredecessorId != source.Id || evidence.Version != 1 || evidence.OperationId != row.Id || evidence.Phase != "Completed"
            || evidence.PreviousTransitionId is not null || evidence.ArchiveId != 0 || !evidence.SourceCleaned
            || !evidence.IntentDigest.AsSpan().SequenceEqual(row.IntentDigest) || evidence.PreviousDigest.Length != 0
            || progress.Version != 1 || progress.Phase != "Completed" || progress.TransitionId != transition.Id
            || progress.PreviousTransitionId is not null || progress.PreviousDigest.Length != 0 || progress.ArchiveId != 0
            || !progress.SourceCleaned || result != new LifecycleResult(1, "WorldBind", 0))
            throw Invalid("lifecycle-world-binding-transition-mismatch");
        var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        if (authority.InstallationId != binding.InstallationId) throw Invalid("lifecycle-world-installation-mismatch");
        referenced.Add(transition.Id);
    }

    internal async Task<string> ReadWorldBindTargetAsync(OutpostTransition transition, CancellationToken ct)
    {
        var evidence = Decode<LifecycleTransition>(transition.Intent);
        var row = await db.OutpostLifecycleOperations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == evidence.OperationId, ct);
        if (transition.Kind != "WorldBind" || row is null || row.Kind != "WorldBind" || row.Phase != "Completed"
            || row.WorldId != transition.WorldId || !row.IntentDigest.AsSpan().SequenceEqual(evidence.IntentDigest))
            throw Invalid("lifecycle-world-binding-target-unavailable");
        return Intent(row).Binding!.Target.InstanceId;
    }

    private async Task ValidateCurrentWorldAsync(LifecycleWorld expected, CancellationToken ct)
    {
        var worlds = await db.World.AsNoTracking().ToListAsync(ct);
        if (worlds.Count != 1) throw Invalid("lifecycle-current-world-mismatch");
        var world = worlds[0];
        if (world.Id != expected.Id || world.InfluenceInstanceId != expected.InstanceId || world.Seed != expected.Seed
            || !world.Params.AsSpan().SequenceEqual(expected.Parameters) || world.NpcTiles != expected.NpcTiles
            || world.CreatedAt != expected.CreatedAt || world.InfluenceRevision < 0)
            throw Invalid("lifecycle-current-world-mismatch");
    }
}
