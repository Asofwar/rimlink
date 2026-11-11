using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Outposts;
using Xunit;
using Authority = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildAuthorityCommitTests
{
    [Fact]
    public async Task Explicit_V3_activation_preserves_every_legacy_transition_and_replays_once()
    {
        using var f = new AuthorityHarness(); var (service, command) = await Prepared(f);
        var prefix = await f.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => OutpostRestoreEvidence.Encode(t));
        var first = await service.ActivateDomainFormatAsync(Actor.Api, command, default);
        Assert.False(first.Replay); Assert.Equal(OutpostDomainFormat.V3, await Format(f));
        foreach (var t in await f.Db.OutpostTransitions.AsNoTracking().Where(t => prefix.Keys.Contains(t.Id)).ToArrayAsync())
            Assert.Equal(prefix[t.Id], OutpostRestoreEvidence.Encode(t));
        var replay = await service.ActivateDomainFormatAsync(Actor.Api, command, default);
        Assert.True(replay.Replay); Assert.Equal(first.Head, replay.Head);
        Assert.Equal(3, await f.Db.OutpostTransitions.CountAsync());
        Assert.False(f.Feature.IsAvailable);
    }

    [Theory]
    [InlineData("BeforePending", false)]
    [InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)]
    [InlineData("AfterExternalCommit", true)]
    public async Task Activation_crash_recovers_exactly_old_or_new_format(string checkpoint, bool committed)
    {
        using var f = new AuthorityHarness(); var (service, command) = await Prepared(f);
        f.Authority.CommitCheckpoint = p => { if (p == checkpoint) throw new Interruption(); };
        await Assert.ThrowsAsync<Interruption>(() => service.ActivateDomainFormatAsync(Actor.Api, command, default));
        f.Authority.CommitCheckpoint = null; f.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? OutpostDomainFormat.V3 : OutpostDomainFormat.V2, await Format(f));
        var repeated = await service.ActivateDomainFormatAsync(Actor.Api, command, default);
        Assert.Equal(committed, repeated.Replay); Assert.Equal(OutpostDomainFormat.V3, await Format(f));
        Assert.Equal(3, await f.Db.OutpostTransitions.CountAsync());
    }

    [Fact]
    public async Task Nonempty_unprotected_guild_state_before_activation_stops_the_old_authority()
    {
        using var f = new AuthorityHarness(); var (service, command) = await Prepared(f);
        f.Db.Add(new GuildCaptureOperation { Id = "uncommitted", ActorId = 1, RequestId = "request" }); await f.Db.SaveChangesAsync();
        var oldHead = await f.Db.OutpostAuthority.AsNoTracking().Select(r => r.HeadId).SingleAsync();
        Assert.Equal(OutpostStartupState.Maintenance, (await f.Authority.OpenAsync(default)).State);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.ActivateDomainFormatAsync(Actor.Api, command, default));
        Assert.Equal(oldHead, await f.Db.OutpostAuthority.AsNoTracking().Select(r => r.HeadId).SingleAsync());
    }

    private static async Task<(OutpostAdminService, OutpostAdminCommand)> Prepared(AuthorityHarness f)
    {
        var first = await OutpostAdminActivationTests.Prepare(f);
        var service = OutpostAdminActivationTests.Service(f);
        await service.ActivateDomainFormatAsync(Actor.Api, first, default);
        await OutpostMainObservationTests.Prepare(f, f.MainObservation);
        var preview = await service.PreviewActivateGuildDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "activate guild authority", default);
        Assert.Empty(preview.Blockers); Assert.NotNull(preview.Command);
        Assert.Equal(3, preview.Command.Intent.Version);
        return (service, preview.Command with { Confirmed = true });
    }

    private static async Task<OutpostDomainFormat> Format(AuthorityHarness f)
    {
        f.Db.ChangeTracker.Clear(); var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        return (await Authority.ReadStoredChainAsync(f.Db, f.Files, row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), true, default)).Format;
    }

    private sealed class Interruption : Exception;
}
