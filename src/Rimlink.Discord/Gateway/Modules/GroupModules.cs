using Discord;
using Discord.Interactions;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Gateway.Modules;

// A command group lives in one module: its moderator and admin subcommands sit together, the handlers check the level.

[Group("raids", "Raids between colonies"), DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class RaidsModule(CommandHandlers h, Permissions permissions, Strings s) : RimlinkModule(permissions, s)
{
    [SlashCommand("list", "Recent raids")]
    public Task List([Summary("player", "Only raids by or on this player"), Autocomplete(typeof(PlayerAutocomplete))] string? player = null) => Answer(c => h.RaidsList(c, player));

    [SlashCommand("stats", "Raid counts")]
    public Task Stats() => Answer(c => h.RaidStats(c));

    [SlashCommand("cancel", "Cancel a raid")]
    public Task Cancel([Summary("raid", "Raid number from /raids list")] long raid, [Summary("return", "Give landed raiders back to the attacker")] bool returnPawns = false,
        [Summary("reason", "Why")] string? reason = null) => Answer(c => h.RaidCancel(c, raid, returnPawns, reason));

    [SlashCommand("freeze", "Pause new raids")]
    public Task Freeze([Summary("reason", "Shown in the server channel")] string? reason = null) => Answer(c => h.RaidsFreeze(c, true, reason));

    [SlashCommand("unfreeze", "Allow raids again")]
    public Task Unfreeze() => Answer(c => h.RaidsFreeze(c, false, null));
}

[Group("live", "Live raids and visits"), DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class LiveModule(CommandHandlers h, Permissions permissions, Strings s) : RimlinkModule(permissions, s)
{
    [SlashCommand("list", "Recent live sessions")]
    public Task List([Summary("player", "Only sessions of this player"), Autocomplete(typeof(PlayerAutocomplete))] string? player = null) => Answer(c => h.LiveList(c, player));

    [SlashCommand("end", "End a live session")]
    public Task End([Summary("session", "Session number from /live list")] long session, [Summary("reason", "Why")] string? reason = null) =>
        Answer(c => h.LiveEnd(c, session, reason));
}

[Group("economy", "Silver and trading"), DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class EconomyModule(CommandHandlers h, Permissions permissions, Strings s) : RimlinkModule(permissions, s)
{
    [SlashCommand("stats", "Silver, lots and trades")]
    public Task Stats() => Answer(c => h.EconomyStats(c));

    [SlashCommand("freeze", "Pause trading")]
    public Task Freeze([Summary("reason", "Shown to the players")] string? reason = null) => Answer(c => h.EconomyFreeze(c, true, reason));

    [SlashCommand("unfreeze", "Open trading again")]
    public Task Unfreeze() => Answer(c => h.EconomyFreeze(c, false, null));
}

[Group("refund", "Undo a trade"), DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class RefundModule(CommandHandlers h, Permissions permissions, Strings s) : RimlinkModule(permissions, s)
{
    [SlashCommand("lot", "Return an open lot to its seller")]
    public Task Lot([Summary("lot", "Lot number")] long lot) => Answer(c => h.RefundLot(c, lot));

    [SlashCommand("parcel", "Send an undelivered gift back")]
    public Task Parcel([Summary("parcel", "Parcel number")] long parcel) => Answer(c => h.RefundParcel(c, parcel));
}
