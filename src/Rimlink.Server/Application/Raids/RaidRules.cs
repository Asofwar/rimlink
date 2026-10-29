using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Raids;

/// <summary>Refuses raid operations while raids are disabled or frozen by an admin (visits and the shop are unaffected).</summary>
public sealed class RaidGate(IOptions<RaidOptions> options, RaidFreezeState freeze)
{
    public void RequireOpen()
    {
        if (!options.Value.Enabled) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, RefusalKeys.RaidsDisabled, "raids are disabled on this server");
        if (freeze.Frozen) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, RefusalKeys.RaidsPaused, "raids are paused: " + (freeze.Reason ?? "maintenance"), freeze.Reason ?? "maintenance");
    }
}

/// <summary>Who may raid whom right now (spec §3): peaceful mode, newbie and wealth protection, cooldowns, active-raid limit; and the loot cap.</summary>
public sealed class RaidRules(IOptions<RaidOptions> options, RimlinkDbContext db, ServerWealth wealth)
{
    private static readonly RaidStateDb[] Final = [RaidStateDb.Resolved, RaidStateDb.Expired, RaidStateDb.Cancelled];

    /// <summary>Peaceful, or asked to leave peaceful mode less than the delay ago.</summary>
    public static bool IsPeaceful(Player p, DateTime now) => p.Peaceful || (p.PeacefulOffAt is { } until && until > now);

    private static readonly IncidentStateDb[] Refunded = [IncidentStateDb.Failed, IncidentStateDb.Expired, IncidentStateDb.Cancelled];

    public async Task<(ErrorCode Code, string Key, string Message, object[] Args)?> CheckLaunch(Player attacker, Player target, DateTime now)
    {
        var o = options.Value;
        if (await CheckTarget(attacker, target, now) is { } refusal) return refusal;

        int active = await db.Raids.CountAsync(r => r.AttackerPlayerId == attacker.Id && !Final.Contains(r.State));
        if (active >= o.MaxActiveRaidsPerAttacker) return (ErrorCode.RaidActive, RefusalKeys.RaidsActive, "you already have a raid under way", []);
        var pairSince = now - TimeSpan.FromHours(o.PairCooldownHours);
        if (await db.Raids.AnyAsync(r => r.TargetPlayerId == target.Id && r.AttackerPlayerId == attacker.Id && r.State != RaidStateDb.Cancelled && r.CreatedAt >= pairSince))
            return (ErrorCode.RaidCooldown, RefusalKeys.RaidsPairCooldown, $"you raided {target.Name} less than {o.PairCooldownHours} hours ago", [target.Name, o.PairCooldownHours]);
        return null;
    }

    /// <summary>
    /// What protects a target from anything harmful another player sends (a raid or a paid hostile/weather incident): peaceful
    /// mode on either side, newbie protection, the poor-colony threshold and the target cooldown, which raids and harmful paid
    /// incidents share (refunded orders do not count).
    /// </summary>
    public async Task<(ErrorCode Code, string Key, string Message, object[] Args)?> CheckTarget(Player attacker, Player target, DateTime now)
    {
        var o = options.Value;
        if (IsPeaceful(attacker, now)) return (ErrorCode.PeacefulMode, RefusalKeys.RaidsPeacefulSelf, "you are in peaceful mode", []);
        if (IsPeaceful(target, now)) return (ErrorCode.PeacefulMode, RefusalKeys.RaidsPeacefulTarget, $"{target.Name} is in peaceful mode", [target.Name]);
        // Players from before this column existed have saves but no stamp: their oldest save is the first one.
        var firstSave = target.FirstSaveAt ?? await db.SaveVersions.Where(v => v.PlayerId == target.Id).MinAsync(v => (DateTime?)v.CreatedAt);
        if (firstSave is null || now - firstSave.Value < TimeSpan.FromDays(o.NewbieProtectionDays))
            return (ErrorCode.TargetProtected, RefusalKeys.RaidsTargetNew, $"{target.Name}'s colony is too new to be raided", [target.Name]);
        // Silver in the wallet, lots and parcels in transit count: a colony cannot hide its wealth on the server.
        if ((decimal)target.WealthTotal + await wealth.Of(target.Id) < o.MinColonyWealth)
            return (ErrorCode.TargetProtected, RefusalKeys.RaidsTargetPoor, $"{target.Name}'s colony is too poor to be raided", [target.Name]);

        var targetSince = now - TimeSpan.FromHours(o.TargetCooldownHours);
        int gift = (int)IncidentKind.IncidentKind;
        if (await db.Raids.AnyAsync(r => r.TargetPlayerId == target.Id && r.State != RaidStateDb.Cancelled && r.CreatedAt >= targetSince)
            || await db.IncidentOrders.AnyAsync(i => i.TargetPlayerId == target.Id && i.Kind != gift && !Refunded.Contains(i.State) && i.CreatedAt >= targetSince))
            return (ErrorCode.RaidCooldown, RefusalKeys.RaidsTargetCooldown, $"{target.Name} was raided less than {o.TargetCooldownHours} hours ago", [target.Name, o.TargetCooldownHours]);
        return null;
    }

    /// <summary>
    /// Silver worth of loot the defender may lose: min((items wealth + server-held wealth) × percent, absolute cap), halved for
    /// Snapshot and again for Instant.
    /// </summary>
    public long LootCap(Player target, long serverWealth, PawnMode mode, RaidTiming timing)
    {
        var o = options.Value;
        double cap = Math.Min(((double)target.WealthItems + serverWealth) * o.LootCapPercent / 100.0, o.LootCapSilver);
        if (mode == PawnMode.PawnSnapshot) cap *= 0.5;
        if (timing == RaidTiming.TimingInstant) cap *= 0.5;
        return (long)cap;
    }
}
