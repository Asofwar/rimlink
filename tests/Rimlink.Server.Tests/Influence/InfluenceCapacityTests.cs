using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Influence;

public sealed class InfluenceCapacityTests
{
    [Fact]
    public async Task TwoApplicantsForTheLastPlaceCannotExceedTheCap()
    {
        using var test = await InfluenceFixtures.Create();
        var own = await test.ConfirmSurface();
        var peer = await test.ConfirmSurface(peer: true);
        await test.SeedPublished(InfluenceLimits.MaxSources - 1);
        var results = await Task.WhenAll(test.Send(InfluenceFixtures.Publication(own, true)), test.Send(InfluenceFixtures.Publication(peer, true), peer: true));
        Assert.Single(results, r => r.Error is null);
        Assert.Equal(ErrorCode.InfluenceCapacity, Assert.Single(results, r => r.Error is not null).Error.Code);
        Assert.Equal(InfluenceLimits.MaxSources, (await test.Snapshot()).Sources.Count);
    }

    [Fact]
    public async Task OfflineSourcesFillCapacityAndDeniedIntentCannotSucceedLater()
    {
        using var test = await InfluenceFixtures.Create();
        var setting = await test.ConfirmSurface();
        await test.SeedPublished(InfluenceLimits.MaxSources);
        var request = InfluenceFixtures.Publication(setting, true);
        var denied = await test.Send(request);
        Assert.Equal(ErrorCode.InfluenceCapacity, denied.Error.Code);
        await test.Db(async db => { db.Remove(await db.WorldObjects.FirstAsync(o => o.InfluenceEnabled)); await db.SaveChangesAsync(); });
        Assert.Equal(denied, await test.Send(request));
        Assert.Null((await test.Send(InfluenceFixtures.Publication(setting, true))).Error);
        Assert.Equal(InfluenceLimits.MaxSources, (await test.Snapshot()).Sources.Count);
    }

    [Fact]
    public async Task OverflowRestoredFromABackupRefusesTheWholeSnapshotButAllowsDisable()
    {
        using var test = await InfluenceFixtures.Create();
        var own = await test.ConfirmSurface();
        Assert.Null((await test.Send(InfluenceFixtures.Publication(own, true))).Error);
        await test.SeedPublished(InfluenceLimits.MaxSources);
        var error = await Assert.ThrowsAsync<RpcException>(() => test.Snapshot());
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Null((await test.Send(InfluenceFixtures.Publication(await test.Setting(), false))).Error);
        Assert.Equal(InfluenceLimits.MaxSources, (await test.Snapshot()).Sources.Count);
    }
}
