using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildSnapshotTests
{
    [Fact]
    public async Task Guild_only_bootstrap_has_a_sticky_family_marker_and_no_pawn_custody()
    {
        using var f = await GuildFixture.Create();
        Assert.False(await GuildSnapshotGuard.RequiresClientAsync(f.H.Db, 1, default));
        var metadata = Metadata(f);
        var guard = new OutpostSnapshotGuard(f.H.Db, f.H.Files);
        await Accept(f, guard, metadata, 2);
        await Accept(f, guard, metadata, 3);
        Assert.True(await GuildSnapshotGuard.RequiresClientAsync(f.H.Db, 1, default));
        Assert.Single(await f.H.Db.Set<GuildCaptureOperation>().Where(o => o.Kind == GuildSnapshotGuard.MarkerKind).ToArrayAsync());
        Assert.Empty(await f.H.Db.PawnCustody.ToArrayAsync()); Assert.Empty(await f.H.Db.OutpostOperations.ToArrayAsync());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task A_later_save_cannot_silently_drop_the_guild_family()
    {
        using var f = await GuildFixture.Create(); var guard = new OutpostSnapshotGuard(f.H.Db, f.H.Files);
        var metadata = Metadata(f); await Accept(f, guard, metadata, 2);
        metadata.GuildMetadata = null;
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => Accept(f, guard, metadata, 3));
        Assert.Equal(2, await f.H.Db.SaveVersions.Where(v => v.IsCurrent).Select(v => v.Number).SingleAsync());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task A_structurally_valid_receipt_without_a_server_attempt_does_not_accept_a_save()
    {
        using var f = await GuildFixture.Create(); var guard = new OutpostSnapshotGuard(f.H.Db, f.H.Files); var metadata = Metadata(f);
        metadata.GuildMetadata.Receipts.Add(new GuildDeliveryReceipt { SchemaVersion = 1, AttemptId = Guid.NewGuid().ToString("D"), ClaimId = Guid.NewGuid().ToString("D"),
            ParcelId = 1, ClientRequestId = Guid.NewGuid().ToString("D"), PayloadDigest = ByteString.CopyFrom(new byte[32]),
            AuthorizationTransitionId = Guid.NewGuid().ToString("D"), AuthorizationTransitionDigest = ByteString.CopyFrom(new byte[32]),
            AuthorizationIntentDigest = ByteString.CopyFrom(new byte[32]), BeforeSnapshot = new() { Scope = metadata.GuildMetadata.Scope.Clone(),
                SnapshotId = Guid.NewGuid().ToString("N"), RawSha256 = ByteString.CopyFrom(new byte[32]) } });
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => Accept(f, guard, metadata, 2));
        Assert.Empty(await f.H.Db.OutpostSnapshotLinks.ToArrayAsync());
    }

    internal static OutpostSaveMetadata Metadata(GuildFixture f) => new()
    {
        SchemaVersion = 1, Scope = new() { WorldInstanceId = f.Scope.WorldId, OwnerPlayerId = 1, RestoreBoundary = f.Scope.Boundary },
        GuildMetadata = new() { SchemaVersion = 1, Scope = new() { WorldInstanceId = f.Scope.WorldId, PlayerId = 1, RestoreBoundary = f.Scope.Boundary } }
    };

    internal static async Task<EvidenceSave> Accept(GuildFixture f, OutpostSnapshotGuard guard, OutpostSaveMetadata metadata, int number,long player=1)
    {
        string snapshot = Guid.NewGuid().ToString("N");
        byte[] packed = RecoveryFixtures.Save(number * 1000, snapshot, outposts: new() { Metadata = metadata });
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(SaveFixtures.Decompress(packed)), out byte[] hash);
        var version = new SaveVersion { PlayerId = player, Number = number, IsCurrent = true, GameTick = number * 1000,
            Size = packed.Length, Sha256 = SHA256.HashData(packed), RawSha256 = hash, RecoveryVersion = 1, SnapshotId = snapshot,
            RecoveryJson = JsonSerializer.Serialize(recovery), CreatedAt = DateTime.UtcNow };
        var header = RecoveryFixtures.Header(packed, version.GameTick); header.OutpostMetadata = metadata.Clone();
        header.GuildVersion = metadata.GuildMetadata?.SchemaVersion ?? 0;
        await using var lease = await f.H.Authority.AcquireAsync([player], default);
        await guard.ValidateHeldAsync(lease, player, header, recovery.Outposts, hash, default);
        var root = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        f.H.Files.WriteAtomically(f.H.Files.FullPath(player, number), packed);
        await f.H.Authority.CommitHeldAsync(lease, new(Guid.ParseExact(snapshot, "N").ToString("D"), metadata.Scope.WorldInstanceId, player, 0,
            metadata.Scope.RestoreBoundary, "SnapshotAccepted", "snapshot", new(root.HeadId, ByteString.CopyFrom(root.HeadDigest)), ByteString.CopyFrom(metadata.ToByteArray())), async (db, ct) =>
            {
                await db.SaveVersions.Where(v => v.PlayerId == player && v.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), ct);
                db.Add(version); await guard.StageAcceptedHeldAsync(lease, version, recovery.Outposts, ct);
            }, default);
        return new(version, header, recovery, packed);
    }
}
