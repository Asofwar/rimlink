using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class SaveTransferTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public SaveTransferTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }

    private sealed class PasswordCredentials(string login, string password) : ICredentialProvider
    {
        public Task<LoginRequest> BuildLogin(CancellationToken cancellationToken) =>
            Task.FromResult(new LoginRequest { Password = new PasswordCredential { Login = login, Password = password } });
    }

    private sealed class NoPrompt : IPinPrompt
    {
        public Task<bool> Confirm(ServerEndpoint endpoint, string fingerprint, string? previous) => Task.FromResult(true);
    }

    private static async Task<RimlinkConnection> Connected(ServerFixture fixture)
    {
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var connection = new RimlinkConnection(new ServerEndpoint("test", 1), new PasswordCredentials("nova", "pw"), new NoPrompt(), new ConsoleLog(),
            new ConnectionTransport(fixture.Server.BaseAddress, fixture.Server.CreateHandler()));
        await connection.ConnectAsync(CancellationToken.None);
        return connection;
    }

    /// <summary>The .rws the game writes: plain XML (the fixture's save is zstd, so unwrap it).</summary>
    private string WriteRws(string name, string seed, string faction, long tick)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, SaveFixtures.Decompress(SaveFixtures.MakeSave(seed, faction, tick, paddingBytes: 2_500_000)));
        return path;
    }

    [Fact]
    public async Task UploadThenDownloadRoundTripsThroughServer()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        await using var connection = await Connected(fixture);
        var transfer = new SaveTransfer(connection);
        string rws = WriteRws("current.rws", ServerFixture.WorldSeed, "Nova", tick: 777);

        var info = await transfer.UploadAsync(rws, new SaveMeta(ServerFixture.WorldSeed, "Nova", 777), CancellationToken.None);
        string downloaded = await transfer.DownloadAsync(Path.Combine(dir, "restored", "current.rws"), CancellationToken.None);

        Assert.Equal(1, info.Version);
        Assert.Equal(777, info.GameTick);
        Assert.Equal(File.ReadAllBytes(rws), File.ReadAllBytes(downloaded));
        Assert.Equal(Path.Combine(dir, "restored", "current.rws"), downloaded);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "restored"), "*.tmp"));
    }

    [Fact]
    public async Task UploadFailureLeavesLocalFileAndThrowsSaveRejected()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        await using var connection = await Connected(fixture);
        var transfer = new SaveTransfer(connection);
        string rws = WriteRws("current.rws", "WrongSeed", "Nova", tick: 1);
        byte[] before = File.ReadAllBytes(rws);

        var ex = await Assert.ThrowsAsync<RpcException>(() => transfer.UploadAsync(rws, new SaveMeta("WrongSeed", "Nova", 1), CancellationToken.None));

        Assert.Equal(ErrorCode.SaveInvalid, ex.ErrorDetail()?.Code);
        Assert.Equal(before, File.ReadAllBytes(rws));
    }

    [Fact]
    public async Task DownloadWithoutServerSaveReturnsNull()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        await using var connection = await Connected(fixture);

        Assert.Null(await new SaveTransfer(connection).DownloadAsync(Path.Combine(dir, "restored", "current.rws"), CancellationToken.None));
    }

    [Fact]
    public void PendingUploadFlagRoundTrips()
    {
        var pending = new PendingUpload(dir);

        Assert.False(pending.IsSet);
        pending.Set();
        Assert.True(pending.IsSet);
        Assert.True(new PendingUpload(dir).IsSet);
        pending.Clear();
        Assert.False(new PendingUpload(dir).IsSet);
    }
}
