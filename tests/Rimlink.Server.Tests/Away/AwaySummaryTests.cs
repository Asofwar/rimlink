using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Raid = Rimlink.Server.Storage.Entities.Raid;
using ShopItem = Rimlink.Server.Storage.Entities.ShopItem;
using MessageKind = Rimlink.Server.Storage.Entities.MessageKind;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.HygieneFixtures;

namespace Rimlink.Server.Tests.Away;

/// <summary>The "while you were away" summary from existing tables, strictly after the baseline (spec §3).</summary>
public class AwaySummaryTests
{
    private static readonly DateTime Since = DateTime.UtcNow.AddDays(-3).Date.AddHours(12);

    private sealed record People(ServerFixture Fixture, long Nova, long NovaAccount, long Mira);

    /// <summary>Nova and Mira exist; Nova's last session ended at <see cref="Since"/>; nobody is logged in.</summary>
    private static async Task<People> Baseline(ServerFixture fixture)
    {
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        var sessions = fixture.Services.GetRequiredService<SessionRegistry>();
        foreach (long id in new[] { nova.Player.PlayerId, mira.Player.PlayerId }) sessions.End(sessions.ByPlayer(id)!.Token);
        await Eventually(async () => await Db(fixture, db => db.Accounts.CountAsync(a => a.LastSessionEndAt != null)) == 2);
        await SetActivity(fixture, nova.Player.PlayerId, Since.AddHours(-1), Since);
        long account = await fixture.AccountIdOf("nova");
        return new People(fixture, nova.Player.PlayerId, account, mira.Player.PlayerId);
    }

    private static async Task Seed(ServerFixture fixture, Action<RimlinkDbContext> add)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        add(db);
        await db.SaveChangesAsync();
    }

    private static async Task<AwaySummary> SummaryOfNova(ServerFixture fixture)
    {
        var login = await fixture.RegisterAndLogin("nova");
        return await new Protocol.V1.Away.AwayClient(fixture.CreateChannel()).SummaryAsync(new Empty(), Bearer(login.SessionToken));
    }

    private static DateTime At(double hours) => Since.AddHours(hours);

    [Fact]
    public async Task RaidsOnMeListQueuedCancelledAndExpired()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        await Seed(fixture, db =>
        {
            db.Raids.Add(new Raid { AttackerPlayerId = w.Mira, TargetPlayerId = w.Nova, State = RaidStateDb.Queued, CreatedAt = At(1), ExpiresAt = At(200) });
            db.Raids.Add(new Raid { AttackerPlayerId = w.Mira, TargetPlayerId = w.Nova, State = RaidStateDb.Cancelled, CreatedAt = At(-10), ResolvedAt = At(2), ExpiresAt = At(200) });
            db.Raids.Add(new Raid { AttackerPlayerId = w.Mira, TargetPlayerId = w.Nova, State = RaidStateDb.Expired, CreatedAt = At(-10), ResolvedAt = At(3), ExpiresAt = At(3) });
            db.Raids.Add(new Raid { AttackerPlayerId = w.Mira, TargetPlayerId = w.Nova, State = RaidStateDb.Queued, CreatedAt = At(-5), ExpiresAt = At(200) }); // before since
        });

        var summary = await SummaryOfNova(fixture);

        Assert.Equal(["expired", "cancelled", "queued"], summary.RaidsOnMe.Select(r => r.State));
        Assert.All(summary.RaidsOnMe, r => Assert.Equal("Mira", r.Other.Name));
        Assert.Equal(Since, summary.Since.ToDateTime());
    }

    [Fact]
    public async Task MyRaidsCarryTheOutcome()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        string outcome = JsonFormatter.Default.Format(new RaidSummary { Dead = 2, Captured = 1, LootValue = 500 });
        await Seed(fixture, db =>
        {
            db.Raids.Add(new Raid { AttackerPlayerId = w.Nova, TargetPlayerId = w.Mira, State = RaidStateDb.Resolved, CreatedAt = At(-20), ResolvedAt = At(4), ExpiresAt = At(200), SummaryJson = outcome });
            db.Raids.Add(new Raid { AttackerPlayerId = w.Nova, TargetPlayerId = w.Mira, State = RaidStateDb.Landed, CreatedAt = At(1), ExpiresAt = At(200) }); // not over yet
        });

        var raid = Assert.Single((await SummaryOfNova(fixture)).MyRaids);

        Assert.Equal(("resolved", "Mira", 2, 1, 500L), (raid.State, raid.Other.Name, raid.Summary.Dead, raid.Summary.Captured, raid.Summary.LootValue));
    }

    [Fact]
    public async Task VisitsParcelsAndLotSalesAreListed()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        await Seed(fixture, db =>
        {
            db.Visits.Add(new Visit { HostPlayerId = w.Nova, VisitorPlayerId = w.Mira, StartedAt = At(1) });
            db.Parcels.Add(new Server.Storage.Entities.Parcel { ToPlayerId = w.Nova, FromPlayerId = w.Mira, Kind = ParcelKindDb.Gift, Silver = 30, CreatedAt = At(2) });
            db.Parcels.Add(new Server.Storage.Entities.Parcel { ToPlayerId = w.Nova, Kind = ParcelKindDb.Admin, CreatedAt = At(3) });
            db.Lots.Add(new Server.Storage.Entities.Lot { SellerPlayerId = w.Nova, BuyerPlayerId = w.Mira, Price = 250, Items = EconomyFixtures.Items(("Steel", 50)).ToByteArray(),
                CreatedAt = At(-30), ExpiresAt = At(300), Status = LotStatusDb.Sold, ClosedAt = At(4) });
            db.Lots.Add(new Server.Storage.Entities.Lot { SellerPlayerId = w.Nova, Price = 99, CreatedAt = At(1), ExpiresAt = At(300), Status = LotStatusDb.Open });
        });

        var summary = await SummaryOfNova(fixture);

        Assert.Equal("Mira", Assert.Single(summary.Visits).Visitor.Name);
        Assert.Equal(["admin", "gift"], summary.Parcels.Select(p => p.Kind));
        Assert.Equal(("Mira", 30L), (summary.Parcels[1].From.Name, summary.Parcels[1].Silver));
        Assert.Null(summary.Parcels[0].From);
        var sale = Assert.Single(summary.LotSales);
        Assert.Equal((250L, "Mira"), (sale.Price, sale.Buyer.Name));
        Assert.Contains("Steel", sale.Summary);
    }

    [Fact]
    public async Task ShopSalesAreCountedWithSilver()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        var shopItem = new ShopItem { HostPlayerId = w.Nova, ThingId = 7, DefName = "Beer", Count = 10, Price = 12, Label = "beer" };
        var lot = new Server.Storage.Entities.Lot { SellerPlayerId = w.Nova, BuyerPlayerId = w.Mira, Price = 250, CreatedAt = At(-30), ExpiresAt = At(300), Status = LotStatusDb.Sold, ClosedAt = At(4) };
        await Seed(fixture, db => { db.ShopItems.Add(shopItem); db.Lots.Add(lot); });
        await Seed(fixture, db =>
        {
            db.PendingRemovals.Add(new PendingRemoval { HostPlayerId = w.Nova, ThingId = 7, Count = 3, CreatedAt = At(2) });
            db.PendingRemovals.Add(new PendingRemoval { HostPlayerId = w.Nova, ThingId = 7, Count = 2, CreatedAt = At(5) });
            db.WalletEntries.Add(new WalletEntry { PlayerId = w.Nova, Delta = 36, Kind = "sale", RefId = shopItem.Id, At = At(2) });
            db.WalletEntries.Add(new WalletEntry { PlayerId = w.Nova, Delta = 24, Kind = "sale", RefId = shopItem.Id, At = At(5) });
            db.WalletEntries.Add(new WalletEntry { PlayerId = w.Nova, Delta = 250, Kind = "sale", RefId = lot.Id, At = At(4) });
        });

        var summary = await SummaryOfNova(fixture);

        Assert.Equal((5, 60L), (summary.ShopItemsSold, summary.ShopSilver));
    }

    [Fact]
    public async Task MentionsIncludeDirectMessagesAndWholeWordNames()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        await Seed(fixture, db =>
        {
            db.ChatMessages.Add(new ChatMessage { FromPlayerId = w.Mira, Text = "where is @Nova?", Kind = MessageKind.Player, SentAt = At(1) });
            db.ChatMessages.Add(new ChatMessage { FromPlayerId = w.Mira, ToPlayerId = w.Nova, Text = "psst", Kind = MessageKind.Player, SentAt = At(2) });
            db.ChatMessages.Add(new ChatMessage { Text = "nova come back", Kind = MessageKind.External, ExternalAuthor = "Mod", SentAt = At(3) });
            db.ChatMessages.Add(new ChatMessage { FromPlayerId = w.Mira, Text = "a supernova!", Kind = MessageKind.Player, SentAt = At(4) });
            db.ChatMessages.Add(new ChatMessage { FromPlayerId = w.Nova, Text = "Nova here", Kind = MessageKind.Player, SentAt = At(5) });
            db.ChatMessages.Add(new ChatMessage { Text = "Nova joined the server", Kind = MessageKind.System, SentAt = At(6) });
            db.ChatMessages.Add(new ChatMessage { FromPlayerId = w.Mira, Text = "Nova earlier", Kind = MessageKind.Player, SentAt = At(-1) });
        });

        var summary = await SummaryOfNova(fixture);

        Assert.Equal(["nova come back", "psst", "where is @Nova?"], summary.Mentions.Select(m => m.Text));
        Assert.Equal(("Mod", false), (summary.Mentions[0].Author, summary.Mentions[0].Direct));
        Assert.True(summary.Mentions[1].Direct);
        Assert.Equal("Mira", summary.Mentions[2].From.Name);
    }

    [Fact]
    public async Task AdminActionsOnMeAreListed()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        await Seed(fixture, db =>
        {
            db.AdminActions.Add(new AdminAction { ActorAccountId = 0, ActorName = "api", Action = "wallet", TargetAccountId = w.NovaAccount, Details = "+100: gift", At = At(1) });
            db.AdminActions.Add(new AdminAction { ActorAccountId = 0, ActorName = "api", Action = "kick", TargetAccountId = 999, Details = "other", At = At(2) });
        });

        var action = Assert.Single((await SummaryOfNova(fixture)).AdminActions);

        Assert.Equal(("wallet", "api", "+100: gift"), (action.Action, action.Actor, action.Details));
    }

    [Fact]
    public async Task ColonyRestoreIsIncluded()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        var sessions = fixture.Services.GetRequiredService<SessionRegistry>();
        sessions.End(sessions.ByPlayer(nova.PlayerId)!.Token);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(nova.PlayerId, "auto", Actor.System("system:hygiene"));

        var summary = await SummaryOfNova(fixture);

        Assert.True(summary.ColonyRestore.Restored);
        Assert.Equal("Nova Home", summary.ColonyRestore.Name);
    }

    [Fact]
    public async Task ListsCapAndCountTheRest()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        await Seed(fixture, db =>
        {
            for (int i = 1; i <= 13; i++) db.Parcels.Add(new Server.Storage.Entities.Parcel { ToPlayerId = w.Nova, Kind = ParcelKindDb.Gift, CreatedAt = At(i) });
            for (int i = 1; i <= 7; i++) db.ChatMessages.Add(new ChatMessage { FromPlayerId = w.Mira, ToPlayerId = w.Nova, Text = $"m{i}", Kind = MessageKind.Player, SentAt = At(i) });
        });

        var summary = await SummaryOfNova(fixture);

        Assert.Equal((10, 3), (summary.Parcels.Count, summary.ParcelsMore));
        Assert.Equal((5, 2), (summary.Mentions.Count, summary.MentionsMore));
        Assert.Equal("m7", summary.Mentions[0].Text);
    }

    [Fact]
    public async Task RowsAtSinceAreExcluded()
    {
        using var fixture = HygieneServer();
        var w = await Baseline(fixture);
        await Seed(fixture, db => db.Parcels.Add(new Server.Storage.Entities.Parcel { ToPlayerId = w.Nova, Kind = ParcelKindDb.Gift, CreatedAt = Since }));

        Assert.Empty((await SummaryOfNova(fixture)).Parcels);
    }

    [Fact]
    public async Task FirstLoginGivesAnEmptySummary()
    {
        using var fixture = HygieneServer();
        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel())
            .RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" });

        var summary = await new Protocol.V1.Away.AwayClient(fixture.CreateChannel()).SummaryAsync(new Empty(), Bearer(reply.SessionToken));

        Assert.Null(summary.Since);
        Assert.Empty(summary.Parcels);
        Assert.Empty(summary.Mentions);
    }

    [Fact]
    public async Task SummaryNeedsASession()
    {
        using var fixture = HygieneServer();

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await new Protocol.V1.Away.AwayClient(fixture.CreateChannel()).SummaryAsync(new Empty()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }
}
