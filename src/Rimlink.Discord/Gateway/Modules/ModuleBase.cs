using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Gateway.Modules;

/// <summary>
/// Thin Discord.Net shell around <see cref="CommandHandlers"/>: builds the <see cref="Caller"/> from the member's roles and sends
/// the <see cref="Reply"/>. The answer is deferred first — a server call may take longer than Discord's three seconds — so a
/// public query answers in the channel and everything else only to the caller, decided per command.
/// </summary>
public abstract class RimlinkModule(Permissions permissions, Strings s) : InteractionModuleBase<SocketInteractionContext>
{
    protected Caller Me()
    {
        var roles = Context.User is SocketGuildUser member ? member.Roles.Select(r => r.Id) : [];
        return new Caller(Context.User.Id, Context.User.Username, permissions.LevelOf(roles));
    }

    protected async Task Answer(Func<Caller, Task<Reply>> command, bool publicQuery = false)
    {
        await DeferAsync(ephemeral: !publicQuery);
        var reply = await command(Me());
        string text = reply.Text.Length <= OutboundQueue.MessageLimit ? reply.Text : reply.Text[..(OutboundQueue.MessageLimit - 1)] + "…";
        MessageComponent? buttons = reply.Confirm is { } c
            ? new ComponentBuilder()
                .WithButton(s.T("cmd.confirm.yes"), $"confirm:{c.Id}", ButtonStyle.Danger)
                .WithButton(s.T("cmd.confirm.no"), $"cancel:{c.Id}", ButtonStyle.Secondary)
                .Build()
            : null;
        await FollowupAsync(text, ephemeral: !publicQuery, allowedMentions: AllowedMentions.None, components: buttons);
    }
}
