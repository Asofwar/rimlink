using Grpc.Core;
using Google.Protobuf;
using System.Security.Cryptography;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RecoveryFixtures;

namespace Rimlink.Server.Tests.Saves;

public sealed class SaveRecoveryTests
{
    [Fact]
    public async Task RepeatingAnOlderSnapshotAcknowledgesItWithoutPromotingIt()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        await fixture.RegisterAndLogin("nova"); var login = await Login(fixture);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] first = Save(100), second = Save(200);
        var original = await SaveFixtures.Upload(saves, login.SessionToken, first, Header(first, 100));
        var current = await SaveFixtures.Upload(saves, login.SessionToken, second, Header(second, 200));
        byte[] recompressed;
        using (var compressor = new ZstdSharp.Compressor(9)) recompressed = compressor.Wrap(SaveFixtures.Decompress(first)).ToArray();

        var repeat = await SaveFixtures.Upload(saves, login.SessionToken, recompressed, Header(recompressed, 100));

        Assert.Equal(original.Version, repeat.Version); Assert.False(repeat.IsCurrent);
        Assert.Equal(ByteString.CopyFrom(SHA256.HashData(SaveFixtures.Decompress(first))),original.RawSha256);
        Assert.Equal(original.RawSha256,repeat.RawSha256);Assert.Equal(0u,repeat.OutpostVersion);Assert.Empty(repeat.OutpostRestoreBoundary);
        var versions = await saves.ListAsync(new Empty(), Bearer(login.SessionToken));
        Assert.Equal(2, versions.Versions.Count);
        Assert.Equal(original.RawSha256,versions.Versions.Single(v=>v.Version==original.Version).RawSha256);
        Assert.Equal(current.Version, versions.Versions.Single(v => v.IsCurrent).Version);
    }

    [Fact]
    public async Task AStoredIdentityCannotNameDifferentBytes()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        await fixture.RegisterAndLogin("nova"); var login = await Login(fixture);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        string id = Guid.NewGuid().ToString("N"); byte[] first = Save(100, id), changed = Save(200, id);
        await SaveFixtures.Upload(saves, login.SessionToken, first, Header(first, 100));

        var error = await Assert.ThrowsAsync<RpcException>(() => SaveFixtures.Upload(saves, login.SessionToken, changed, Header(changed, 200)));

        Assert.Equal(ErrorCode.SaveInvalid, error.ErrorDetail()?.Code);
        Assert.Single((await saves.ListAsync(new Empty(), Bearer(login.SessionToken))).Versions);
    }

    [Fact]
    public async Task HeaderCannotClaimReceiptsOfAnotherSnapshot()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        await fixture.RegisterAndLogin("nova"); var login = await Login(fixture);
        byte[] bytes = Save(100); var header = Header(bytes, 100); header.SnapshotId = Guid.NewGuid().ToString("N");
        var error = await Assert.ThrowsAsync<RpcException>(() => SaveFixtures.Upload(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), login.SessionToken, bytes, header));
        Assert.Equal(ErrorCode.SaveInvalid, error.ErrorDetail()?.Code);
    }
}
