using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Saves;

public class SaveGenerationTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true").WithSetting("Auth:AdminLogins:0", "admin");

    private static async Task Reset(ServerFixture fixture, LoginReply admin)
    {
        var command = new AdminCommand { Name = "reset" };
        command.Args.Add("Nova");
        await new Protocol.V1.Admin.AdminClient(fixture.CreateChannel()).ExecuteAsync(command, Bearer(admin.SessionToken));
    }

    [Fact]
    public async Task ResetRefusesOldLocalSaveButAcceptsNewColonyAndIncrementsAgain()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        var bytes = MakeSave(ServerFixture.WorldSeed, "Nova");
        var header = HeaderFor(bytes, ServerFixture.WorldSeed, "Nova");
        Assert.Equal(0, nova.SaveGeneration);
        await Upload(saves, nova.SessionToken, bytes, header);
        await Reset(fixture, admin);
        nova = await fixture.RegisterAndLogin("nova");
        Assert.False(nova.HasSave);
        Assert.Equal(1, nova.SaveGeneration);
        var ex = await Assert.ThrowsAsync<RpcException>(() => Upload(saves, nova.SessionToken, bytes, header));
        Assert.Equal(ErrorCode.SaveGenerationMismatch, ex.ErrorDetail()?.Code);
        Assert.Empty((await saves.ListAsync(new Empty(), Bearer(nova.SessionToken))).Versions);

        header.SaveGeneration = nova.SaveGeneration;
        Assert.Equal(1, (await Upload(saves, nova.SessionToken, bytes, header)).Version);
        Assert.Equal(1, (await fixture.RegisterAndLogin("nova")).SaveGeneration);
        await Reset(fixture, admin);
        Assert.Equal(2, (await fixture.RegisterAndLogin("nova")).SaveGeneration);
    }

    [Fact]
    public async Task ResetDuringUploadRefusesItsOldGenerationAtCommit()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bytes = MakeSave(ServerFixture.WorldSeed, "Nova");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks()
        {
            yield return bytes;
            read.SetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SaveStore>();
        var upload = store.Store(nova.Player.PlayerId, HeaderFor(bytes, ServerFixture.WorldSeed, "Nova"), Chunks(), CancellationToken.None);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await Reset(fixture, admin); }
        finally { resume.SetResult(); }
        var ex = await Assert.ThrowsAsync<RpcException>(() => upload);
        Assert.Equal(ErrorCode.SaveGenerationMismatch, ex.ErrorDetail()?.Code);
        Assert.False((await fixture.RegisterAndLogin("nova")).HasSave);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.DataDir, "saves"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FirstSaveWithoutServerVersionIsStillRecoverableInSameGeneration()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        Assert.False(nova.HasSave);
        var bytes = MakeSave(ServerFixture.WorldSeed, "Nova");
        Assert.Equal(1, (await Upload(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), nova.SessionToken,
            bytes, HeaderFor(bytes, ServerFixture.WorldSeed, "Nova"))).Version);
    }

    [Fact]
    public async Task ResetDoesNotRestoreAnEarlierHygieneArchiveAtLoginOrByAdmin()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).SettleAsync(new SettleRequest { Tile = 12, Name = "Old home" }, Bearer(nova.SessionToken));
        var bytes = MakeSave(ServerFixture.WorldSeed, "Nova");
        await Upload(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), nova.SessionToken, bytes, HeaderFor(bytes, ServerFixture.WorldSeed, "Nova"));
        fixture.Services.GetRequiredService<SessionRegistry>().End(nova.SessionToken);
        long archiveId;
        using (var scope = fixture.Services.CreateScope())
            archiveId = Assert.IsType<ArchiveOutcome.Archived>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>()
                .Archive(nova.Player.PlayerId, "admin", Actor.Api)).ArchiveId;
        // A login can track the old Player before it waits for reset's storage lock.
        using var restoreScope = fixture.Services.CreateScope();
        var trackedPlayer = await restoreScope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.SingleAsync(p => p.Id == nova.Player.PlayerId);
        Assert.Equal(0, trackedPlayer.SaveGeneration);
        await Reset(fixture, admin);
        var login = await fixture.RegisterAndLogin("nova");
        Assert.Equal(1, login.SaveGeneration);
        Assert.False(login.HasSave);
        Assert.False(login.HasSettlement);
        Assert.Null(login.ColonyRestore);
        Assert.IsType<RestoreOutcome.NotFound>(await restoreScope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archiveId, "api"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(fixture.DataDir, "archive"), "*.zst", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RecreateWorldAlsoInvalidatesPlayersWhoseFirstSaveNeverReachedServer()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        Assert.False(nova.HasSave);
        await new Protocol.V1.Admin.AdminClient(fixture.CreateChannel()).ExecuteAsync(new AdminCommand { Name = "recreateworld" }, Bearer(admin.SessionToken));
        using var scope = fixture.Services.CreateScope();
        var generations = await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Select(p => p.SaveGeneration).ToListAsync();
        Assert.Equal(2, generations.Count);
        Assert.All(generations, generation => Assert.Equal(1, generation));
    }
}
