using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;

namespace Rimlink.Server.Grpc;

public sealed class AdminGrpcService(AdminCommandRunner runner) : Protocol.V1.Admin.AdminBase
{
    public override async Task<AdminReply> Execute(AdminCommand request, ServerCallContext context) =>
        new() { Text = await runner.Execute(Actor.From(context.GetSession()), request) };
}
