using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Economy;

public sealed class OutgoingEpochTests
{
    [Fact]
    public async Task APreviouslyAuthenticatedSessionCannotCommitOutflowAfterRollback()
    {
        var accessor = new HttpContextAccessor();
        using var fixture = EconomyFixtures.EconomyServer().WithServices(services => services.AddSingleton<IHttpContextAccessor>(accessor));
        var login = await fixture.RegisterAndLogin("nova");
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Players.Where(p => p.Id == login.Player.PlayerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, 1));
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Items[SessionInterceptor.ItemKey] = new Session
        {
            Token = "fixture-token", AccountId = 1, PlayerId = login.Player.PlayerId, Role = Rimlink.Server.Storage.Entities.Role.Player,
            PlayerName = "nova", Ip = "127.0.0.1", SaveWriteEpoch = 0,
        };

        var error = await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<TransferService>().StampOutgoing(login.Player.PlayerId, 100));

        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }
}
