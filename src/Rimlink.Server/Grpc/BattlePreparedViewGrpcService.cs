using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;

namespace Rimlink.Server.Grpc;

internal sealed partial class GuildCaptureGrpcService
{
    public override async Task ReadPreparedEntry(GuildWorldScope request, IServerStreamWriter<BattlePreparedEntryPart> stream, ServerCallContext context)
    {
        var result = await OfferCall(context, () => views.ReadEntryAsync(context.GetSession(), request, context.CancellationToken));
        if (result is not { } bundle) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await stream.WriteAsync(new() { Entry = bundle.Entry }, ct);
        for (int offset = 0; offset < bundle.Inventory.Length; offset += BattleArtifactStore.ChunkBytes)
        {
            int count = Math.Min(BattleArtifactStore.ChunkBytes, bundle.Inventory.Length - offset);
            await stream.WriteAsync(new() { Inventory = ByteString.CopyFrom(bundle.Inventory, offset, count) }, ct);
        }
    }

    public override Task<BattlePreparedViewReply> ReadPreparedView(BattlePreparedViewRequest request,ServerCallContext context)=>OfferCall(context,
        ()=>views.ReadAsync(context.GetSession(),request,context.CancellationToken));
    public override Task<BattlePreparedInitialReply> ReadPreparedInitial(BattlePreparedInitialRequest request,ServerCallContext context)=>OfferCall(context,
        ()=>views.ReadInitialAsync(context.GetSession(),request,context.CancellationToken));
}
