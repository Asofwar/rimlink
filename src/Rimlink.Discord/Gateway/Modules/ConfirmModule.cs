using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Rimlink.Discord.Commands;

namespace Rimlink.Discord.Gateway.Modules;

/// <summary>
/// The confirm/cancel buttons of irreversible commands; <see cref="Confirmations"/> decides who may press (the owner, still an
/// admin) and runs it once.
/// </summary>
public sealed class ConfirmModule(Confirmations confirmations, Permissions permissions) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("confirm:*")]
    public Task Confirm(string id) => Press(id, true);

    [ComponentInteraction("cancel:*")]
    public Task Cancel(string id) => Press(id, false);

    private async Task Press(string id, bool confirm)
    {
        await DeferAsync(ephemeral: true);
        var level = permissions.LevelOf(Context.User is SocketGuildUser member ? member.Roles.Select(r => r.Id) : []);
        var reply = await confirmations.Press(id, Context.User.Id, level, confirm);
        await FollowupAsync(reply.Text, ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}
