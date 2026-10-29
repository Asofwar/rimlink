using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;

namespace Rimlink.Server.Grpc;

public sealed class ModpackGrpcService(ModpackService modpack, ModpackDefs defs) : Protocol.V1.Modpack.ModpackBase
{
    public override async Task<Empty> SetDefs(DefList request, ServerCallContext context)
    {
        var session = context.GetSession();
        await modpack.SetDefs(request.DefNames, session.Role, session.PlayerName, defs);
        return new Empty();
    }

    public override async Task<ModpackList> Get(Empty request, ServerCallContext context)
    {
        var list = new ModpackList();
        list.Entries.AddRange(await modpack.Get());
        return list;
    }

    public override async Task<Empty> SetConfigs(ModConfigList request, ServerCallContext context)
    {
        var session = context.GetSession();
        await modpack.SetConfigs(request.Configs, session.Role, session.PlayerName);
        return new Empty();
    }

    public override async Task<ModConfigList> GetConfigs(Empty request, ServerCallContext context)
    {
        if (context.GetSession().Role != Server.Storage.Entities.Role.Admin) throw RpcErrors.PermissionDenied("admin only");
        var list = new ModConfigList();
        list.Configs.AddRange(await modpack.GetConfigs());
        return list;
    }

    public override async Task<Empty> Set(ModpackList request, ServerCallContext context)
    {
        var session = context.GetSession();
        await modpack.Set(request.Entries, session.Role, session.PlayerName);
        return new Empty();
    }
}
