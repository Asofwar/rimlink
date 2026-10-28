using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;

namespace Rimlink.Server.Grpc;

public sealed class SavesGrpcService(SaveStore store) : Protocol.V1.Saves.SavesBase
{
    public override async Task<SaveInfo> Upload(IAsyncStreamReader<SaveChunk> requestStream, ServerCallContext context)
    {
        var session = context.GetSession();
        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.PartCase != SaveChunk.PartOneofCase.Header)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "first message must be the save header"));
        var header = requestStream.Current.Header;

        try
        {
            var version = await store.Store(session.PlayerId, header, DataChunks(requestStream, context.CancellationToken), context.CancellationToken);
            return SaveStore.ToInfo(version);
        }
        catch (SaveRejected ex)
        {
            throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.SaveInvalid, ex.Message);
        }
    }

    public override async Task Download(Empty request, IServerStreamWriter<SaveChunk> responseStream, ServerCallContext context)
    {
        var session = context.GetSession();
        var current = await store.OpenCurrent(session.PlayerId) ?? throw new RpcException(new Status(StatusCode.NotFound, "no save stored for this player"));
        var (version, content) = current;

        await using (content)
        {
            await responseStream.WriteAsync(new SaveChunk
            {
                Header = new SaveHeader { GameTick = version.GameTick, TotalSize = version.Size, Sha256 = ByteString.CopyFrom(version.Sha256) },
            }, context.CancellationToken);

            byte[] buffer = new byte[Limits.SaveChunkBytes];
            int read;
            while ((read = await content.ReadAsync(buffer, context.CancellationToken)) > 0)
            {
                await responseStream.WriteAsync(new SaveChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, context.CancellationToken);
            }
        }
    }

    public override async Task<SaveVersions> List(Empty request, ServerCallContext context)
    {
        var reply = new SaveVersions();
        reply.Versions.AddRange((await store.List(context.GetSession().PlayerId)).Select(SaveStore.ToInfo));
        return reply;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> DataChunks(IAsyncStreamReader<SaveChunk> stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await stream.MoveNext(cancellationToken))
        {
            if (stream.Current.PartCase != SaveChunk.PartOneofCase.Data) throw new SaveRejected("unexpected message in save stream");
            if (stream.Current.Data.Length > Limits.SaveChunkBytes) throw new SaveRejected("chunk larger than 1 MB");
            yield return stream.Current.Data.Memory;
        }
    }
}
