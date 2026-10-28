using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Saves;

public class SaveUploadTests
{
    private static ServerFixture Server() => new ServerFixture().WithSetting("Auth:AllowPassword", "true");

    [Fact]
    public async Task UploadThenDownloadRoundTrips()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 5000, paddingBytes: 3_000_000);

        var info = await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova", tick: 5000));
        var (header, data) = await Download(saves, nova.SessionToken);

        Assert.Equal(1, info.Version);
        Assert.True(info.IsCurrent);
        Assert.Equal(5000, info.GameTick);
        Assert.Equal(save, data);
        Assert.Equal(5000, header.GameTick);
        Assert.True((await fixture.RegisterAndLogin("nova")).HasSave);
    }

    [Fact]
    public async Task DownloadWithoutSaveIsNotFound()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());

        using var call = saves.Download(new Empty(), Bearer(nova.SessionToken));
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task UploadWithWrongSeedIsSaveInvalidAndNothingStored()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave("OtherSeed", "Nova");

        var ex = await Assert.ThrowsAsync<RpcException>(() => Upload(saves, nova.SessionToken, save, HeaderFor(save, "OtherSeed", "Nova")));

        Assert.Equal(ErrorCode.SaveInvalid, ex.ErrorDetail()?.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.DataDir, "saves"), "*", SearchOption.AllDirectories));
        Assert.Empty((await saves.ListAsync(new Empty(), Bearer(nova.SessionToken))).Versions);
    }

    [Fact]
    public async Task UploadWithNamedFactionRenamesThePlayerFaction()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Sheratan Supernova");

        await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Sheratan Supernova"));

        Assert.Equal("Sheratan Supernova", (await fixture.RegisterAndLogin("nova")).Player.FactionName);
    }

    [Fact]
    public async Task UploadWithUnnamedFactionKeepsThePlayerFactionName()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, faction: null);

        var info = await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, ""));

        Assert.Equal(1, info.Version);
        Assert.Equal("Nova", (await fixture.RegisterAndLogin("nova")).Player.FactionName);
    }

    [Fact]
    public async Task HashMismatchIsRejected()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova");
        var header = HeaderFor(save, ServerFixture.WorldSeed, "Nova");
        header.Sha256 = ByteString.CopyFrom(new byte[32]);

        var ex = await Assert.ThrowsAsync<RpcException>(() => Upload(saves, nova.SessionToken, save, header));

        Assert.Equal(ErrorCode.SaveInvalid, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task OversizedHeaderIsRejected()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova");
        var header = HeaderFor(save, ServerFixture.WorldSeed, "Nova");
        header.TotalSize = 65L * 1024 * 1024;

        var ex = await Assert.ThrowsAsync<RpcException>(() => Upload(saves, nova.SessionToken, save, header));

        Assert.Equal(ErrorCode.SaveInvalid, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task CancelledUploadLeavesNoTempFileAndKeepsCurrent()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] first = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 1);
        await Upload(saves, nova.SessionToken, first, HeaderFor(first, ServerFixture.WorldSeed, "Nova", tick: 1));
        byte[] second = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 2, paddingBytes: 5_000_000);

        // Send the header and 3 of the chunks, then drop the call.
        await Assert.ThrowsAnyAsync<Exception>(() => Upload(saves, nova.SessionToken, second, HeaderFor(second, ServerFixture.WorldSeed, "Nova", tick: 2), stopAfterChunks: 3));
        await Task.Delay(200);

        string saveDir = Path.Combine(fixture.DataDir, "saves");
        Assert.Empty(Directory.GetFiles(saveDir, "*.tmp", SearchOption.AllDirectories));
        var versions = (await saves.ListAsync(new Empty(), Bearer(nova.SessionToken))).Versions;
        Assert.Single(versions);
        Assert.True(versions[0].IsCurrent);
        Assert.Equal(1, versions[0].GameTick);
    }

    [Fact]
    public async Task RollbackMarksVersionCurrentAndNotifiesPlayer()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        using var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(nova.SessionToken));
        foreach (long tick in new[] { 1L, 2L })
        {
            byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova", tick);
            await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova", tick));
        }

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(nova.Player.PlayerId, 1);
        }

        var (header, _) = await Download(saves, nova.SessionToken);
        Assert.Equal(1, header.GameTick);
        var versions = (await saves.ListAsync(new Empty(), Bearer(nova.SessionToken))).Versions;
        Assert.Equal([true, false], versions.OrderBy(v => v.Version).Select(v => v.IsCurrent));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ServerEvent? rolled = null;
        while (rolled is null && await events.ResponseStream.MoveNext(cts.Token))
            if (events.ResponseStream.Current.BodyCase == ServerEvent.BodyOneofCase.SaveRolledBack) rolled = events.ResponseStream.Current;
        Assert.Equal(1, rolled!.SaveRolledBack.Version);
    }

    [Fact]
    public async Task UploadSucceedsWhenAnOrphanFileOfThatNumberExists()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        // A crash between File.Move and the DB row would leave this behind.
        string dir = Path.Combine(fixture.DataDir, "saves", nova.Player.PlayerId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "1.rws.zst"), [1, 2, 3]);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 9);

        var info = await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova", tick: 9));

        Assert.Equal(1, info.Version);
        Assert.Equal(save, (await Download(saves, nova.SessionToken)).data);
    }

    [Fact]
    public async Task RollbackToTheCurrentVersionKeepsItCurrent()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 1);
        await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova", tick: 1));

        using (var scope = fixture.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(nova.Player.PlayerId, 1));
        }

        Assert.True(Assert.Single((await saves.ListAsync(new Empty(), Bearer(nova.SessionToken))).Versions).IsCurrent);
        Assert.True((await fixture.RegisterAndLogin("nova")).HasSave);
    }

    [Fact]
    public async Task FourthUploadWithinThirtySecondsIsResourceExhausted()
    {
        using var fixture = Server().WithRateLimits();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova");
        for (int i = 0; i < 3; i++) await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova")));

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }
}
