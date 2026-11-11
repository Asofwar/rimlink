using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildFreezeCommand(GuildMutationScope Scope, string RequestId, string ExpectedHeadId, bool Frozen, string Reason);
internal sealed record GuildFreezeReceipt(int Version, long Revision, Actor Actor, GuildFreezeCommand Command, string TransitionId)
{
    internal const string Kind = "GuildCaptureFreeze";
}

internal sealed partial class GuildOutpostService
{
    internal Task<GuildFreezeReceipt> SetCaptureFrozenAsync(Actor actor, GuildFreezeCommand command, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        if (command.Scope is null || !Guid.TryParseExact(command.ExpectedHeadId, "D", out _)
            || !ValidFreezeReason(command.Reason)) throw Refused();
        return MutateAsync(command.Scope, actor.PlayerId, actor.Name, command.RequestId, GuildFreezeReceipt.Kind,
            new { actor, command }, async (_, id, token) =>
            {
                if (!await db.OutpostAuthority.AsNoTracking().AnyAsync(a => a.HeadId == command.ExpectedHeadId, token))
                    throw new OutpostEvidenceUnavailableException("guild-freeze-preview-stale");
                var current = await GuildAuthorityEvidence.LatestFreezeAsync(db, command.Scope.WorldId, token);
                if ((current?.Command.Frozen ?? false) == command.Frozen) throw Refused();
                if (!command.Frozen && await db.Set<GuildCampaign>().AnyAsync(c => c.Active && c.WorldId == command.Scope.WorldId, token))
                    throw new OutpostEvidenceUnavailableException("guild-freeze-active-campaigns");
                if (command.Frozen)
                    foreach (var grant in await db.Set<GuildBattleControlGrant>().Where(g => g.Active
                        && db.Set<GuildBattle>().Any(b => b.Id == g.BattleId && b.Active && b.WorldId == command.Scope.WorldId)).ToArrayAsync(token))
                        grant.Active = false;
                return new GuildFreezeReceipt(1, checked((current?.Revision ?? 0) + 1), actor, command, id);
            }, ct, battleLock: true);
    }

    private async Task RequireCaptureOpenHeldAsync(string worldId, CancellationToken ct)
    {
        if ((await GuildAuthorityEvidence.LatestFreezeAsync(db, worldId, ct))?.Command.Frozen == true)
            throw new OutpostEvidenceUnavailableException("guild-capture-frozen");
    }

    internal static bool ValidFreezeReason(string reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 500 && !reason.Any(char.IsControl);
}

internal static partial class GuildAuthorityEvidence
{
    internal static async Task<GuildFreezeReceipt?> LatestFreezeAsync(RimlinkDbContext db, string worldId, CancellationToken ct)
    {
        var receipts = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == GuildFreezeReceipt.Kind)
            .Select(o => o.Receipt).ToArrayAsync(ct);
        return receipts.Select(ReadFreeze).Where(r => r.Command.Scope.WorldId == worldId)
            .OrderByDescending(r => r.Revision).FirstOrDefault();
    }

    private static void ValidateFreezes(GuildCaptureOperation[] operations, IReadOnlyDictionary<string, OutpostTransition> transitions)
    {
        var freezes = operations.Where(o => o.Kind == GuildFreezeReceipt.Kind)
            .ToDictionary(o => o.TransitionId, o => (Operation: o, Receipt: ReadFreeze(o.Receipt)), StringComparer.Ordinal);
        if (freezes.Count == 0) return;
        var current = new Dictionary<string, GuildFreezeReceipt>(StringComparer.Ordinal);
        foreach (var transition in Outposts.OutpostAuthority.OrderTransitions(transitions.Values))
        {
            if (transition.Kind != GuildFreezeReceipt.Kind) continue;
            if (!freezes.TryGetValue(transition.Id, out var entry)) throw InvalidFreeze();
            var (operation, receipt) = entry;
            var command = receipt.Command;
            var actor = receipt.Actor;
            var previous = current.GetValueOrDefault(transition.WorldId);
            if (receipt.Version != 1 || receipt.TransitionId != transition.Id || receipt.Revision != (previous?.Revision ?? 0) + 1
                || command.Frozen == (previous?.Command.Frozen ?? false) || command.ExpectedHeadId != transition.OldHeadId
                || command.Scope.WorldId != transition.WorldId || command.Scope.Boundary != transition.RestoreBoundary
                || command.RequestId != operation.RequestId || !GuildOutpostService.ValidFreezeReason(command.Reason)
                || actor.Role != Role.Admin || actor.PlayerId != operation.ActorId || actor.Name != transition.Actor
                || !IntentDigest(new { actor, command }).AsSpan().SequenceEqual(operation.IntentDigest))
                throw InvalidFreeze();
            current[transition.WorldId] = receipt;
        }
    }

    private static GuildFreezeReceipt ReadFreeze(byte[] bytes)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<GuildFreezeReceipt>(bytes, Json);
            return receipt is { Actor: not null, Command.Scope: not null } ? receipt : throw InvalidFreeze();
        }
        catch (JsonException) { throw InvalidFreeze(); }
    }

    private static OutpostEvidenceUnavailableException InvalidFreeze() => new("guild-freeze-evidence-invalid");
}
