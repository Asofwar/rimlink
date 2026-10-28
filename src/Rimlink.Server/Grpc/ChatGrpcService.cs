using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Chat;

namespace Rimlink.Server.Grpc;

public sealed class ChatGrpcService(ChatService chat) : Protocol.V1.Chat.ChatBase
{
    public override async Task<Empty> Send(SendRequest request, ServerCallContext context)
    {
        await chat.Send(context.GetSession(), request.Text, request.ToPlayerId);
        return new Empty();
    }

    public override async Task<ChatHistory> History(Empty request, ServerCallContext context)
    {
        var history = new ChatHistory();
        history.Messages.AddRange(await chat.History(context.GetSession()));
        return history;
    }
}
