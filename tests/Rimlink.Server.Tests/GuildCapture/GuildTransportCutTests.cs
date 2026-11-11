using System.Data;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildTransportCutTests
{
    [Fact]
    public async Task Verified_cut_is_published_only_for_a_committed_V3_head()
    {
        using var f = await GuildFixture.Create();
        var cut = Assert.IsType<GuildVerifiedCut>(f.H.Feature.GuildCut);
        var head = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(head.HeadId, cut.HeadId); Assert.Equal(head.HeadDigest, cut.HeadDigest.ToByteArray());
        Assert.Equal(head.WorldId, cut.WorldId); Assert.Equal(head.RestoreBoundary, cut.Boundary);
        await f.Place(0);
        Assert.NotEqual(cut.HeadId, f.H.Feature.GuildCut!.HeadId);
    }

    [Theory]
    [InlineData("BeforePending")][InlineData("AfterPending")][InlineData("AfterDomainCommit")][InlineData("AfterExternalCommit")]
    public async Task Interrupted_financial_commit_closes_transport_until_verified_reconciliation(string point)
    {
        using var f = await GuildFixture.Create();
        f.H.Authority.CommitCheckpoint = p =>
        {
            if (p != point) return;
            Assert.Null(f.H.Feature.GuildCut); throw new Interrupted();
        };
        await Assert.ThrowsAsync<Interrupted>(() => f.Place(0)); Assert.Null(f.H.Feature.GuildCut);
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.NotNull(f.H.Feature.GuildCut);
    }

    [Fact]
    public async Task A_failed_stored_evidence_check_never_leaves_old_transport_admission_live()
    {
        using var f = await GuildFixture.Create(); await f.Place(0); Assert.NotNull(f.H.Feature.GuildCut);
        await f.H.Db.Database.ExecuteSqlRawAsync("UPDATE guild_outposts SET Stock = 1");
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Null(f.H.Feature.GuildCut);
    }

    [Fact]
    public async Task Cancelling_a_running_verification_keeps_the_verified_head_available_to_transport()
    {
        using var f = await GuildFixture.Create();
        var head = Assert.IsType<GuildVerifiedCut>(f.H.Feature.GuildCut).HeadId;
        using var cancellation = new CancellationTokenSource();
        await using (var lease = await f.H.Authority.AcquireAsync([], default))
        {
            var connection = f.H.Db.Database.GetDbConnection();
            StateChangeEventHandler cancel = (_, change) =>
            {
                if (change.CurrentState == ConnectionState.Open && f.H.Feature.GuildCut is null) cancellation.Cancel();
            };
            connection.StateChange += cancel;
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.H.Authority.CheckHeldAsync(lease, cancellation.Token));
            }
            finally { connection.StateChange -= cancel; }
        }
        var transport = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, f.Battles);
        await using var admitted = await transport.HoldAsync(default);
        Assert.Equal(head, admitted.Cut.HeadId);
    }

    [Fact]
    public async Task Cancellation_before_the_pending_write_rolls_back_the_mutation_and_keeps_transport_available()
    {
        using var f = await GuildFixture.Create();
        var head = Assert.IsType<GuildVerifiedCut>(f.H.Feature.GuildCut).HeadId;
        using var cancellation = new CancellationTokenSource();
        f.H.Authority.CommitCheckpoint = point =>
        {
            if (point != "BeforePending") return;
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.PlaceAsync(Actor.Api, f.Scope,
            Guid.NewGuid().ToString("D"), 0, GuildResource.Steel, "cancelled placement", cancellation.Token));
        Assert.Empty(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().ToArrayAsync());
        var transport = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, f.Battles);
        await using var admitted = await transport.HoldAsync(default);
        Assert.Equal(head, admitted.Cut.HeadId);
    }

    private sealed class Interrupted : Exception;
}
