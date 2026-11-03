using System.Data.Common;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RecoveryFixtures;

namespace Rimlink.Server.Tests.Saves;

public sealed class RecoveryReviewTests
{
    [Fact]
    public async Task TwoPreviouslyTrackedPlayersReceiveDifferentRollbackEpochs()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var original = await fixture.RegisterAndLogin("nova"); var login = await Login(fixture);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        foreach (int tick in new[] { 100, 200 })
        {
            byte[] bytes = Save(tick);
            await SaveFixtures.Upload(saves, login.SessionToken, bytes, Header(bytes, tick));
        }
        using var a = fixture.Services.CreateScope(); using var b = fixture.Services.CreateScope();
        foreach (var scope in new[] { a, b })
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.SingleAsync(p => p.Id == original.Player.PlayerId);
        Assert.True(await a.ServiceProvider.GetRequiredService<SaveStore>().Rollback(original.Player.PlayerId, 1));
        var middle = await Login(fixture);
        Assert.Equal(1, middle.SaveWriteEpoch);
        Assert.True(await b.ServiceProvider.GetRequiredService<SaveStore>().Rollback(original.Player.PlayerId, 2));
        using var check = fixture.Services.CreateScope();
        Assert.Equal(2, await check.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Where(p => p.Id == original.Player.PlayerId).Select(p => p.SaveWriteEpoch).SingleAsync());
        byte[] stale = Save(300, epoch: 1);
        var error = await Assert.ThrowsAsync<RpcException>(() => SaveFixtures.Upload(saves, middle.SessionToken, stale, Header(stale, 300)));
        Assert.Equal(ErrorCode.SaveWriteEpochMismatch, error.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task DownloadNeverGrantsANewEpochToThePreRollbackBytes()
    {
        var hold = new AuthorityReadHold();
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true")
            .WithServices(s => s.AddDbContext<RimlinkDbContext>(o => o.AddInterceptors(hold)));
        await fixture.RegisterAndLogin("nova"); var login = await Login(fixture);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        foreach (int tick in new[] { 100, 200 })
        {
            byte[] bytes = Save(tick); await SaveFixtures.Upload(saves, login.SessionToken, bytes, Header(bytes, tick));
        }
        hold.Armed = true;
        var download = SaveFixtures.Download(saves, login.SessionToken);
        await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task rollback = Task.Run(async () =>
        {
            using var scope = fixture.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(login.Player.PlayerId, 1));
        });
        try { await Task.WhenAny(rollback, Task.Delay(1000)); }
        finally { hold.Release.TrySetResult(); }
        var (header, _) = await download;
        await rollback;
        Assert.Equal(200, header.GameTick);
        Assert.Equal(0, header.SaveWriteEpoch);
    }

    private sealed class AuthorityReadHold : DbCommandInterceptor
    {
        public bool Armed;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("SELECT \"p\".\"SaveWriteEpoch\", \"p\".\"SaveGeneration\"", StringComparison.Ordinal))
            {
                Armed = false; Reached.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
