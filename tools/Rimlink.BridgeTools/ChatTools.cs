using Grpc.Core;
using RimBridgeServer.Sdk;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Protocol.V1;

namespace Rimlink.BridgeTools;

public sealed class ChatTools
{
    [Tool("rimlink/chat", Description = "The latest chat lines this client has received", ResultDescription = "success and messages [{id, from, to, text, at}]")]
    public Task<object> Chat(IRimBridgeContext ctx,
        [ToolParameter(Description = "How many of the latest lines")] int count = 20) => Bridge.OnMain<object>(ctx, () =>
    {
        if (!RimlinkSession.Active) return Bridge.Fail("no Rimlink session");
        var all = RimlinkSession.State.Chat.Messages;
        return new
        {
            success = true,
            messages = all.Skip(Math.Max(0, all.Count - Math.Max(1, count)))
                .Select(m => new { id = m.Id, from = m.From?.Name, to = m.ToPlayerId, text = m.Text, at = m.At?.ToDateTime() })
                .ToList(),
        };
    });

    [Tool("rimlink/chat_send", Description = "Send a chat line, public or to one player", ResultDescription = "success")]
    public async Task<object> ChatSend(IRimBridgeContext ctx,
        [ToolParameter(Description = "Text to send", Required = true)] string text,
        [ToolParameter(Description = "Receiver name or id; empty for public chat")] string? to = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Bridge.Fail("text is empty");
        var target = await Bridge.OnMain<object>(ctx, () =>
        {
            if (!RimlinkSession.Active) return Bridge.Fail("no Rimlink session");
            if (string.IsNullOrWhiteSpace(to)) return 0L;
            return Targets.Player(to) is { } p ? p.PlayerId : Bridge.Fail($"unknown player '{to}'");
        });
        if (target is not long toId) return target;
        try
        {
            await RimlinkSession.Connection!.Chat.SendAsync(new SendRequest { Text = text, ToPlayerId = toId });
            return new { success = true, to = toId };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex));
        }
    }
}
