using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Influence;

public sealed class InfluenceConsentTests
{
    [Fact]
    public async Task PublicationRequiresExplicitSurfaceAndOnlyThenBecomesPublic()
    {
        using var test = await InfluenceFixtures.Create();
        Assert.Empty((await test.Snapshot()).Sources);
        var setting = await test.Setting();
        Assert.False(setting.HasRootSurface);
        var refused = await test.Send(InfluenceFixtures.Publication(setting, true));
        Assert.Equal(ErrorCode.InfluenceIneligible, refused.Error.Code);
        var confirmed = await test.ConfirmSurface();
        Assert.Null((await test.Send(InfluenceFixtures.Publication(confirmed, true))).Error);
        var snapshot = await test.Snapshot();
        Assert.Equal(test.HomeId, Assert.Single(snapshot.Sources).Id);
        Assert.Equal("North", Assert.Single(snapshot.Alliances).Name);
    }

    [Fact]
    public async Task DelayedEnableCannotUndoEvenAnIdempotentLookingDisable()
    {
        using var test = await InfluenceFixtures.Create();
        var setting = await test.ConfirmSurface();
        var delayed = InfluenceFixtures.Publication(setting, true);
        var disable = await test.Send(InfluenceFixtures.Publication(setting, false));
        Assert.True(disable.AppliedVersion > setting.Version);
        var result = await test.Send(delayed);
        Assert.Equal(ErrorCode.InfluenceConflict, result.Error.Code);
        Assert.Equal(result, await test.Send(delayed));
        Assert.Empty((await test.Snapshot()).Sources);
    }

    [Fact]
    public async Task AcknowledgedEnableReplayAfterDisableDoesNotPublishAgainOrAuditTwice()
    {
        using var test = await InfluenceFixtures.Create();
        var request = InfluenceFixtures.Publication(await test.ConfirmSurface(), true);
        var first = await test.Send(request);
        Assert.Null(first.Error);
        Assert.Null((await test.Send(InfluenceFixtures.Publication(await test.Setting(), false))).Error);
        long before = 0;
        await test.Db(async db => before = await db.AdminActions.LongCountAsync());
        Assert.Equal(first, await test.Send(request));
        Assert.Empty((await test.Snapshot()).Sources);
        await test.Db(async db => Assert.Equal(before, await db.AdminActions.LongCountAsync()));
    }

    [Fact]
    public async Task NonSurfaceAndEveryChangedContextAreRejected()
    {
        using var test = await InfluenceFixtures.Create();
        var notSurface = await test.ConfirmSurface(root: false);
        Assert.Equal(ErrorCode.InfluenceIneligible, (await test.Send(InfluenceFixtures.Publication(notSurface, true))).Error.Code);
        var surface = await test.ConfirmSurface();
        foreach (int part in Enumerable.Range(0, 3))
        {
            var request = InfluenceFixtures.Publication(surface, true);
            if (part == 0) request.Publication.Context.WorldInstanceId = Guid.NewGuid().ToString("N");
            if (part == 1) request.Publication.Context.SaveGeneration++;
            if (part == 2) request.Publication.Context.AllianceId++;
            Assert.Equal(ErrorCode.InfluenceConflict, (await test.Send(request)).Error.Code);
        }
        Assert.Empty((await test.Snapshot()).Sources);
    }
}
