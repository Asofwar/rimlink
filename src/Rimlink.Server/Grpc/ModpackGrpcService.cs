using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;

namespace Rimlink.Server.Grpc;

public sealed class ModpackGrpcService(ModpackService modpack) : Protocol.V1.Modpack.ModpackBase
{
    public override async Task<ModpackList> Get(Empty request, ServerCallContext context)
    {
        var list = new ModpackList();
        list.Entries.AddRange(await modpack.Get());
        return list;
    }

    public override async Task<Empty> Set(ModpackList request, ServerCallContext context)
    {
        await modpack.Set(request.Entries, context.GetSession());
        return new Empty();
    }
}
