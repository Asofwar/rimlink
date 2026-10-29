using Discord;
using Discord.Interactions;
using Rimlink.Discord.Commands;

namespace Rimlink.Discord.Gateway.Modules;

/// <summary>The confirm/cancel buttons of irreversible commands; <see cref="Confirmations"/> decides who may press and runs it once.</summary>
public sealed class ConfirmModule(Confirmations confirmations) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("confirm:*")]
    public Task Confirm(string id) => Press(id, true);

    [ComponentInteraction("cancel:*")]
    public Task Cancel(string id) => Press(id, false);

    private async Task Press(string id, bool confirm)
    {
        await DeferAsync(ephemeral: true);
        var reply = await confirmations.Press(id, Context.User.Id, confirm);
        await FollowupAsync(reply.Text, ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}
