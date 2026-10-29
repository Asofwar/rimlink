using System.Globalization;
using Rimlink.Discord.Api;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Commands;

/// <summary>
/// Every slash command as a plain method (Discord bot spec §5). The level check is the first thing each one does, before any
/// request; server errors become short texts (no answer, no such thing, the token lacks a scope) instead of error dumps.
/// Public queries answer in the channel, everything else only to the caller.
/// </summary>
public sealed class CommandHandlers(RimlinkApi api, PlayerResolver players, Confirmations confirmations, Strings s)
{
    private const string Read = "read", Moderate = "moderate", AdminScope = "admin";
    private const int ListLimit = 20;

    // --- Everyone ---

    public Task<Reply> Online(Caller caller, CancellationToken ct = default) => Run(caller, Level.Everyone, Read, false, async () =>
    {
        var online = (await api.Players("", 200, ct)).Items.Where(p => p.Online).ToList();
        return online.Count == 0 ? s.T("cmd.online.none") : s.T("cmd.online.list", online.Count, string.Join(", ", online.Select(p => Formatter.Escape(p.Name))));
    });

    public Task<Reply> Status(Caller caller, CancellationToken ct = default) => Run(caller, Level.Everyone, Read, false, async () =>
    {
        var st = await api.Status(ct);
        var parts = new List<string> { s.T("cmd.status", Formatter.Escape(st.Version), st.PlayersOnline, st.MaxPlayers) };
        if (!st.WorldExists) parts.Add(s.T("cmd.status.noworld"));
        if (st.Economy is { Frozen: true } economy) parts.Add(s.T("cmd.status.economy", Formatter.Escape(economy.Reason ?? s.T("reason.none"))));
        if (st.Raids is { Frozen: true } raids) parts.Add(s.T("cmd.status.raids", Formatter.Escape(raids.Reason ?? s.T("reason.none"))));
        return string.Join("\n", parts);
    });

    public Task<Reply> Market(Caller caller, string? seller, CancellationToken ct = default) => Run(caller, Level.Everyone, Read, false, async () =>
    {
        long? sellerId = seller is null ? null : (await Resolve(seller, ct)).Id;
        var lots = await api.Lots(sellerId, ct);
        return lots.Count == 0
            ? s.T("cmd.market.none")
            : string.Join("\n", lots.Take(ListLimit).Select(l => s.T("cmd.market.lot", l.Id, Formatter.Escape(l.SellerName), Formatter.Escape(l.Summary), l.Price)));
    });

    public Task<Reply> Player(Caller caller, string name, CancellationToken ct = default) => Run(caller, Level.Everyone, Moderate, false, async () =>
    {
        var card = await api.Player((await Resolve(name, ct)).Id, ct);
        var parts = new List<string> { s.T(card.Online ? "cmd.player.online" : "cmd.player.offline") };
        parts.Add(card.Settlements.Count == 0 ? s.T("cmd.player.nocolony") : s.T("cmd.player.colonies", string.Join(", ", card.Settlements.Select(c => Formatter.Escape(c.Name)))));
        if (card.Peaceful) parts.Add(s.T("cmd.player.peaceful"));
        return s.T("cmd.player.card", Formatter.Escape(card.Name), Formatter.Escape(card.FactionName ?? ""), string.Join(", ", parts));
    });

    // --- Moderator ---

    public Task<Reply> Kick(Caller caller, string player, string? reason, CancellationToken ct = default) => Run(caller, Level.Moderator, Moderate, true, async () =>
        Formatter.Escape(await api.Kick(caller.Actor, (await Resolve(player, ct)).Id, reason, ct)));

    public Task<Reply> Mute(Caller caller, string player, int minutes, CancellationToken ct = default) => Done(caller, Level.Moderator, Moderate, async () =>
        await api.Mute(caller.Actor, (await Resolve(player, ct)).Id, minutes, ct));

    public Task<Reply> Unmute(Caller caller, string player, CancellationToken ct = default) => Done(caller, Level.Moderator, Moderate, async () =>
        await api.Unmute(caller.Actor, (await Resolve(player, ct)).Id, ct));

    public Task<Reply> Broadcast(Caller caller, string text, CancellationToken ct = default) => Done(caller, Level.Moderator, Moderate, () =>
        api.Broadcast(caller.Actor, text, ct));

    public Task<Reply> Bans(Caller caller, CancellationToken ct = default) => Run(caller, Level.Moderator, Moderate, true, async () =>
    {
        var bans = await api.Bans(ct);
        return bans.Count == 0 ? s.T("cmd.bans.none") : string.Join("\n", bans.Take(ListLimit).Select(b => s.T("cmd.bans.ban", b.Id,
            Formatter.Escape(b.PlayerName ?? b.SteamId?.ToString(CultureInfo.InvariantCulture) ?? b.Ip ?? "?"), Formatter.Escape(b.Reason),
            b.ExpiresAt is { } until ? s.T("cmd.bans.until", Date(until)) : s.T("cmd.bans.permanent"))));
    });

    public Task<Reply> Actions(Caller caller, int limit = ListLimit, CancellationToken ct = default) => Run(caller, Level.Moderator, Moderate, true, async () =>
    {
        var actions = await api.Actions(Math.Clamp(limit, 1, 50), ct);
        return actions.Count == 0 ? s.T("cmd.actions.none") : string.Join("\n", actions.Select(a => s.T("cmd.actions.line", Date(a.At),
            Formatter.Escape(a.ActorName ?? "api"), Formatter.Escape(a.Action), a.TargetName is { } t ? " → " + Formatter.Escape(t) : "", Formatter.Escape(a.Details))));
    });

    public Task<Reply> RaidsList(Caller caller, string? player, CancellationToken ct = default) => Run(caller, Level.Moderator, Read, true, async () =>
    {
        long? id = player is null ? null : (await Resolve(player, ct)).Id;
        var raids = await api.Raids(id, ct);
        return raids.Count == 0 ? s.T("cmd.raids.none") : string.Join("\n", raids.Take(ListLimit).Select(r => s.T("cmd.raids.line", r.Id, r.AttackerPlayerId, r.TargetPlayerId, r.State)));
    });

    public Task<Reply> RaidStats(Caller caller, CancellationToken ct = default) => Run(caller, Level.Moderator, Read, true, async () =>
    {
        var r = await api.RaidStats(ct);
        return s.T("cmd.raids.stats", r.Queued, r.Announced, r.Landed, r.ResolvedToday, r.Expired, r.Cancelled, Frozen(r.Frozen, r.FreezeReason));
    });

    public Task<Reply> LiveList(Caller caller, string? player, CancellationToken ct = default) => Run(caller, Level.Moderator, Read, true, async () =>
    {
        long? id = player is null ? null : (await Resolve(player, ct)).Id;
        var sessions = await api.Live(id, ct);
        return sessions.Count == 0 ? s.T("cmd.live.none") : string.Join("\n", sessions.Take(ListLimit).Select(l => s.T("cmd.live.line", l.Id, l.Kind, l.HostPlayerId, l.GuestPlayerId, l.State)));
    });

    public Task<Reply> EconomyStats(Caller caller, CancellationToken ct = default) => Run(caller, Level.Moderator, Read, true, async () =>
    {
        var e = await api.EconomyStats(ct);
        return s.T("cmd.economy.stats", e.SilverInWallets, e.OpenLots, e.PendingParcels, e.TradesToday, Frozen(e.Frozen, e.FreezeReason));
    });

    // --- Admin ---

    public Task<Reply> Ban(Caller caller, string player, double? days, string? reason, CancellationToken ct = default) => Run(caller, Level.Admin, AdminScope, true, async () =>
        s.T("cmd.ban.done", (await api.Ban(caller.Actor, (await Resolve(player, ct)).Id, days, reason ?? "banned", ct)).Id));

    public Task<Reply> Pardon(Caller caller, long banId, CancellationToken ct = default) => Done(caller, Level.Admin, AdminScope, () => api.Pardon(caller.Actor, banId, ct));

    public Task<Reply> Wallet(Caller caller, string player, long delta, string? reason, CancellationToken ct = default) => Run(caller, Level.Admin, AdminScope, true, async () =>
    {
        var target = await Resolve(player, ct);
        return s.T("cmd.wallet.done", Formatter.Escape(target.Name), (await api.Wallet(caller.Actor, target.Id, delta, reason, ct)).Balance);
    });

    public Task<Reply> RefundLot(Caller caller, long id, CancellationToken ct = default) => Done(caller, Level.Admin, AdminScope, () => api.RefundLot(caller.Actor, id, ct));

    public Task<Reply> RefundParcel(Caller caller, long id, CancellationToken ct = default) => Done(caller, Level.Admin, AdminScope, () => api.RefundParcel(caller.Actor, id, ct));

    public Task<Reply> EconomyFreeze(Caller caller, bool frozen, string? reason, CancellationToken ct = default) => Done(caller, Level.Admin, AdminScope, () => api.FreezeEconomy(caller.Actor, frozen, reason, ct));

    public Task<Reply> RaidsFreeze(Caller caller, bool frozen, string? reason, CancellationToken ct = default) => Done(caller, Level.Admin, AdminScope, () => api.FreezeRaids(caller.Actor, frozen, reason, ct));

    public Task<Reply> RaidCancel(Caller caller, long id, bool returnPawns, string? reason, CancellationToken ct = default) =>
        Done(caller, Level.Admin, AdminScope, () => api.CancelRaid(caller.Actor, id, reason, returnPawns, ct));

    public Task<Reply> LiveEnd(Caller caller, long id, string? reason, CancellationToken ct = default) => Done(caller, Level.Admin, AdminScope, () => api.EndLive(caller.Actor, id, reason, ct));

    public Task<Reply> Peaceful(Caller caller, string player, bool on, CancellationToken ct = default) => Done(caller, Level.Admin, AdminScope, async () =>
        await api.Peaceful(caller.Actor, (await Resolve(player, ct)).Id, on, ct));

    public Task<Reply> Saves(Caller caller, string player, CancellationToken ct = default) => Run(caller, Level.Admin, AdminScope, true, async () =>
    {
        var saves = await api.Saves((await Resolve(player, ct)).Id, ct);
        return saves.Count == 0 ? s.T("cmd.saves.none") : string.Join("\n", saves.Take(ListLimit).Select(v => s.T("cmd.saves.line", v.Version,
            v.IsCurrent ? s.T("cmd.saves.current") : "", v.GameTick, v.Size / 1024, Date(v.CreatedAt))));
    });

    // --- Admin, confirmed ---

    public Task<Reply> Shutdown(Caller caller, int seconds, string? reason, CancellationToken ct = default) => Confirm(caller,
        () => Task.FromResult(s.T("cmd.confirm.shutdown", Math.Max(0, seconds), Formatter.Escape(reason ?? s.T("reason.none")))),
        () => api.Shutdown(caller.Actor, Math.Max(0, seconds), reason, CancellationToken.None));

    public Task<Reply> Rollback(Caller caller, string player, int version, CancellationToken ct = default)
    {
        PlayerInfo? target = null;
        return Confirm(caller,
            async () => s.T("cmd.confirm.rollback", Formatter.Escape((target = await Resolve(player, ct)).Name), version),
            () => api.Rollback(caller.Actor, target!.Id, version, CancellationToken.None));
    }

    public Task<Reply> Reset(Caller caller, string player, CancellationToken ct = default)
    {
        PlayerInfo? target = null;
        return Confirm(caller,
            async () => s.T("cmd.confirm.reset", Formatter.Escape((target = await Resolve(player, ct)).Name)),
            () => api.Reset(caller.Actor, target!.Id, CancellationToken.None));
    }

    public Task<Reply> RecreateWorld(Caller caller, CancellationToken ct = default) => Confirm(caller,
        () => Task.FromResult(s.T("cmd.confirm.recreate")),
        () => api.RecreateWorld(caller.Actor, CancellationToken.None));

    // --- plumbing ---

    /// <summary>A text an operation stops with instead of a server error (an unknown or ambiguous player name).</summary>
    private sealed class ReplyException(string text) : Exception(text);

    private async Task<PlayerInfo> Resolve(string name, CancellationToken ct)
    {
        var (player, error) = await players.Resolve(name, ct);
        return player ?? throw new ReplyException(error!);
    }

    private async Task<Reply> Run(Caller caller, Level level, string scope, bool ephemeral, Func<Task<string>> call)
    {
        if (caller.Level < level) return new Reply(s.T("cmd.denied"), true);
        try
        {
            return new Reply(await call(), ephemeral);
        }
        catch (ReplyException ex)
        {
            return new Reply(ex.Message, true);
        }
        catch (RimlinkApiException ex)
        {
            return new Reply(ErrorText(ex, scope), true);
        }
    }

    private Task<Reply> Done(Caller caller, Level level, string scope, Func<Task> call) => Run(caller, level, scope, true, async () =>
    {
        await call();
        return s.T("cmd.done");
    });

    /// <summary>Admin only: the description is built (and the player resolved) now; the call runs when the caller confirms.</summary>
    private async Task<Reply> Confirm(Caller caller, Func<Task<string>> describe, Func<Task> call)
    {
        if (caller.Level < Level.Admin) return new Reply(s.T("cmd.denied"), true);
        string description;
        try
        {
            description = await describe();
        }
        catch (ReplyException ex)
        {
            return new Reply(ex.Message, true);
        }
        catch (RimlinkApiException ex)
        {
            return new Reply(ErrorText(ex, AdminScope), true);
        }

        var confirmation = confirmations.Create(caller, description, async () =>
        {
            try
            {
                await call();
                return s.T("cmd.done");
            }
            catch (RimlinkApiException ex)
            {
                return ErrorText(ex, AdminScope);
            }
        });
        return new Reply(description, true, confirmation);
    }

    private string ErrorText(RimlinkApiException ex, string scope) => ex.Status switch
    {
        0 => s.T("cmd.noanswer"),
        403 => s.T("cmd.forbidden", scope),
        404 => s.T("cmd.notfound"),
        _ => Formatter.Escape(ex.Detail),
    };

    private string Frozen(bool frozen, string? reason) => frozen ? s.T("cmd.frozen", Formatter.Escape(reason ?? s.T("reason.none"))) : "";

    private static string Date(DateTime at) => at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
