using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
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
        if (!options.Value.Enabled) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, "raids are disabled on this server");
        if (freeze.Frozen) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, "raids are paused: " + (freeze.Reason ?? "maintenance"));
    }
}

/// <summary>Who may raid whom right now (spec §3): peaceful mode, newbie and wealth protection, cooldowns, active-raid limit; and the loot cap.</summary>
public sealed class RaidRules(IOptions<RaidOptions> options, RimlinkDbContext db)
{
    private static readonly RaidStateDb[] Final = [RaidStateDb.Resolved, RaidStateDb.Expired, RaidStateDb.Cancelled];

    /// <summary>Peaceful, or asked to leave peaceful mode less than the delay ago.</summary>
    public static bool IsPeaceful(Player p, DateTime now) => p.Peaceful || (p.PeacefulOffAt is { } until && until > now);

    public async Task<(ErrorCode Code, string Message)?> CheckLaunch(Player attacker, Player target, DateTime now)
    {
        var o = options.Value;
        if (IsPeaceful(attacker, now)) return (ErrorCode.PeacefulMode, "you are in peaceful mode");
        if (IsPeaceful(target, now)) return (ErrorCode.PeacefulMode, $"{target.Name} is in peaceful mode");
        if (target.FirstSaveAt is null || now - target.FirstSaveAt.Value < TimeSpan.FromDays(o.NewbieProtectionDays))
            return (ErrorCode.TargetProtected, $"{target.Name}'s colony is too new to be raided");
        if (target.WealthTotal < o.MinColonyWealth)
            return (ErrorCode.TargetProtected, $"{target.Name}'s colony is too poor to be raided");

        int active = await db.Raids.CountAsync(r => r.AttackerPlayerId == attacker.Id && !Final.Contains(r.State));
        if (active >= o.MaxActiveRaidsPerAttacker) return (ErrorCode.RaidActive, "you already have a raid under way");

        var targetSince = now - TimeSpan.FromHours(o.TargetCooldownHours);
        if (await db.Raids.AnyAsync(r => r.TargetPlayerId == target.Id && r.State != RaidStateDb.Cancelled && r.CreatedAt >= targetSince))
            return (ErrorCode.RaidCooldown, $"{target.Name} was raided less than {o.TargetCooldownHours} hours ago");
        var pairSince = now - TimeSpan.FromHours(o.PairCooldownHours);
        if (await db.Raids.AnyAsync(r => r.TargetPlayerId == target.Id && r.AttackerPlayerId == attacker.Id && r.State != RaidStateDb.Cancelled && r.CreatedAt >= pairSince))
            return (ErrorCode.RaidCooldown, $"you raided {target.Name} less than {o.PairCooldownHours} hours ago");
        return null;
    }

    /// <summary>Silver worth of loot the defender may lose: min(items wealth × percent, absolute cap), halved for Snapshot and again for Instant.</summary>
    public long LootCap(Player target, PawnMode mode, RaidTiming timing)
    {
        var o = options.Value;
        double cap = Math.Min(target.WealthItems * o.LootCapPercent / 100.0, o.LootCapSilver);
        if (mode == PawnMode.PawnSnapshot) cap *= 0.5;
        if (timing == RaidTiming.TimingInstant) cap *= 0.5;
        return (long)cap;
    }
}
