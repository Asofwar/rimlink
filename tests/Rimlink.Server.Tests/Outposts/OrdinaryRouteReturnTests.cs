using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OrdinaryReturnTests
{
    [Theory]
    [InlineData("trade-open")][InlineData("trade-side")][InlineData("raid-launch")]
    [InlineData("live-visit")][InlineData("live-raid")][InlineData("raid-report")][InlineData("live-report")]
    public async Task Each_ordinary_route_can_fence_an_unapplied_exact_removal_without_returning_guest_outcomes(string route)
    {
        using var h = await CustodyHarness.Returned();
        string key = Guid.NewGuid().ToString("N");
        var pawns = new PawnList { Pawns = { h.Blob.Clone() } };
        var items = new ItemList { Items = { EconomyFixtures.Item("Steel", 13) } };
        IMessage original = route switch
        {
            "trade-open" => new TradeOpenRequest { ClientRequestId = key, PartnerId = 2, GameTick = 1000, Items = items, Pawns = pawns, Silver = 25 },
            "trade-side" => new TradeSideRequest { ClientRequestId = key, TradeId = 81, BaseRevision = 3, GameTick = 1000, Items = items, Pawns = pawns, Silver = 25 },
            "raid-launch" => new LaunchRequest { ClientRequestId = key, TargetPlayerId = 2, GameTick = 1000, PawnMode = PawnMode.PawnReal, Pawns = pawns },
            "live-visit" or "live-raid" => new LiveProposal { ClientRequestId = key, HostPlayerId = 2, GameTick = 1000,
                Kind = route == "live-raid" ? LiveKind.LiveRaid : LiveKind.LiveVisit, Pawns = pawns },
            _ => new RaidReport { ClientRequestId = key, RaidId = route == "raid-report" ? 81 : 0,
                LiveSessionId = route == "live-report" ? 81 : 0, GameTick = 1000, Loot = items, Captives = pawns,
                Outcomes = { new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeAlive,
                    Blob = new PawnBlob { Name = "Original guest is not local return", ScribeXmlZstd = ByteString.CopyFromUtf8("guest") } } } }
        };
        var cancel = await Removed(h, original);
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var returns = scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
        Assert.Equal(OrdinaryOperationStatus.Unknown, (await returns.ResolveAsync(1, new() { Operation = cancel }, default)).Status);
        var result = await returns.BeginReturnAsync(1, cancel, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, result.Status);
        Assert.Equal(h.Blob, Assert.Single(result.ReturnProof.Payload.Pawns.Pawns));
        bool mixed = route is "trade-open" or "trade-side" or "raid-report" or "live-report";
        if (mixed)
        {
            var item = Assert.Single(result.ReturnProof.Payload.Items.Items);
            Assert.Equal("Steel", item.DefName); Assert.Equal(13, item.Count);
        }
        else Assert.Empty(result.ReturnProof.Payload.Items.Items);
        Assert.Equal(result, await returns.BeginReturnAsync(1, cancel, default));
        Assert.Single(await h.Db.OutpostOrdinaryReturns.ToListAsync());
        Assert.Equal("LocalReturnPending", (await h.Row()).LocationKind);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }
}

internal static class OrdinarySourceAssertions
{
    // Uses the actual accepted XML capture, not a synthetic WorkerCapture or a normalized request hash.
    public static async Task Applied(CustodyHarness h, IServiceProvider services, IMessage original, long sourceId, long actor = 1)
    {
        var identity = OrdinaryCapture.Identity(original);
        byte[] digest = SHA256.HashData(original.ToByteArray());
        var versions = await h.Db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == actor).OrderBy(v => v.Number).ToListAsync();
        var version = versions.First(v => SaveStore.RecoveryOf(v).Outposts.Metadata.OrdinaryOutgoing.Any(c =>
            c.RequestKind == identity.Kind && c.ClientRequestId == identity.RequestId && c.RequestIntentSha256.Span.SequenceEqual(digest)));
        var metadata = SaveStore.RecoveryOf(version).Outposts.Metadata;
        var capture = metadata.OrdinaryOutgoing.Single(c => c.RequestKind == identity.Kind && c.ClientRequestId == identity.RequestId);
        var proof = new OutpostSnapshotProof { Scope = metadata.Scope.Clone(), SnapshotId = version.SnapshotId!, RawSha256 = ByteString.CopyFrom(version.RawSha256!) };
        var cancel = new OrdinaryCancelRequest { Scope = metadata.Scope.Clone(), Capture = capture.Clone(), OriginalRequest = original.ToByteString(),
            RemovalSnapshot = proof.Clone(), CanonicalSnapshot = proof.Clone(), ReturnAttemptId = Guid.NewGuid().ToString("D") };
        var service = services.GetRequiredService<OrdinaryReturnService>();
        var result = await service.BeginReturnAsync(actor, cancel, default);
        Assert.Equal(OrdinaryOperationStatus.Applied, result.Status); Assert.Equal(sourceId, result.SourceId); Assert.Null(result.ReturnProof);
        Assert.Equal(result, await service.ResolveAsync(actor, new() { Operation = cancel }, default));
        Assert.Empty(await h.Db.OutpostOrdinaryReturns.ToListAsync());
    }
}
