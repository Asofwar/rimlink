using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Influence;

public sealed class InfluenceSnapshotTests
{
    [Fact]
    public async Task PublicSnapshotContainsOnlyConsentedSourceDescriptors()
    {
        using var test = await InfluenceFixtures.Create();
        await test.SeedPublished(InfluenceLimits.MaxSources);
        var snapshot = await test.Snapshot();
        Assert.Equal(InfluenceLimits.MaxSources, snapshot.Sources.Count);
        Assert.True(snapshot.CalculateSize() <= InfluenceLimits.MaxSnapshotBytes);
        Assert.DoesNotContain(snapshot.Sources, s => s.Id == test.PeerHomeId || s.Id == test.HomeId);
        Assert.Equal(new[] { "id", "name", "palette_index" }, InfluenceAlliance.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name));
        Assert.True(Guid.TryParseExact(snapshot.WorldInstanceId, "N", out _));
        Assert.True(Guid.TryParseExact(snapshot.ProcessInstanceId, "N", out _));
    }

    [Fact]
    public async Task CorruptPublishedSourceCannotSilentlyDisappearFromTheCompleteSnapshot()
    {
        using var test = await InfluenceFixtures.Create();
        await test.SeedPublished(1);
        await test.Db(async db => { (await db.WorldObjects.SingleAsync(o => o.InfluenceEnabled)).InfluenceRootSurface = null; await db.SaveChangesAsync(); });
        await Assert.ThrowsAsync<RpcException>(() => test.Snapshot());
    }

    [Fact]
    public async Task SnapshotAndSettingsRequireASessionAndAnotherOwnersSettingIsPrivate()
    {
        using var test = await InfluenceFixtures.Create();
        var noSession = await Assert.ThrowsAsync<RpcException>(() => test.Client.GetAsync(new Empty()).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, noSession.StatusCode);
        var foreign = await Assert.ThrowsAsync<RpcException>(() => test.Client.GetSettingAsync(new InfluenceTarget { SettlementId = test.PeerHomeId }, GrpcTestExtensions.Bearer(test.Owner.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, foreign.StatusCode);
    }
}
