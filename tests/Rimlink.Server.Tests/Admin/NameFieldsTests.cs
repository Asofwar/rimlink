using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Admin;

/// <summary>Admin lists carry player names next to the ids, so the panel and the bot need no extra lookups.</summary>
public class NameFieldsTests
{
    private static async Task<(ServerFixture Fixture, long Nova, long Mira)> Seeded()
    {
        var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true").WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);
        long nova = (await fixture.RegisterAndLogin("nova", playerName: "Nova")).Player.PlayerId;
        long mira = (await fixture.RegisterAndLogin("mira", playerName: "Mira")).Player.PlayerId;

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        var now = DateTime.UtcNow;
        db.Raids.Add(new Raid { AttackerPlayerId = nova, TargetPlayerId = mira, State = RaidStateDb.Queued, CreatedAt = now, ExpiresAt = now.AddDays(7) });
        db.LiveSessions.Add(new LiveSession { HostPlayerId = mira, GuestPlayerId = nova, State = LiveStateDb.Ended, CreatedAt = now, EndedAt = now, EndReason = 0 });
        db.Lots.Add(new Lot { SellerPlayerId = nova, BuyerPlayerId = mira, Price = 10, CreatedAt = now, ExpiresAt = now.AddDays(30), Status = LotStatusDb.Sold, ClosedAt = now });
        db.Lots.Add(new Lot { SellerPlayerId = mira, Price = 20, CreatedAt = now, ExpiresAt = now.AddDays(30), Status = LotStatusDb.Open });
        db.Parcels.Add(new Parcel { ToPlayerId = mira, FromPlayerId = nova, Kind = ParcelKindDb.Gift, CreatedAt = now });
        db.Parcels.Add(new Parcel { ToPlayerId = mira, FromPlayerId = null, Kind = ParcelKindDb.Admin, CreatedAt = now });
        db.Parcels.Add(new Parcel { ToPlayerId = mira, FromPlayerId = 999999, Kind = ParcelKindDb.Gift, CreatedAt = now });
        await db.SaveChangesAsync();
        return (fixture, nova, mira);
    }

    [Fact]
    public async Task RaidsCarryAttackerAndTargetNames()
    {
        var (fixture, nova, _) = await Seeded();
        using var _f = fixture;

        var raid = (await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/raids")).EnumerateArray().Single();

        Assert.Equal(nova, raid.GetProperty("attackerPlayerId").GetInt64());
        Assert.Equal("Nova", raid.GetProperty("attackerName").GetString());
        Assert.Equal("Mira", raid.GetProperty("targetName").GetString());
    }

    [Fact]
    public async Task LiveSessionsCarryHostAndGuestNames()
    {
        var (fixture, _, _) = await Seeded();
        using var _f = fixture;

        var live = (await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/live")).EnumerateArray().Single();

        Assert.Equal("Mira", live.GetProperty("hostName").GetString());
        Assert.Equal("Nova", live.GetProperty("guestName").GetString());
    }

    [Fact]
    public async Task OpenLotsCarrySellerNames()
    {
        var (fixture, _, mira) = await Seeded();
        using var _f = fixture;

        // The list holds open lots only (the sold one is not in it), so a lot has a seller and never a buyer.
        var lot = (await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/economy/lots")).EnumerateArray().Single();

        Assert.Equal(mira, lot.GetProperty("sellerPlayerId").GetInt64());
        Assert.Equal("Mira", lot.GetProperty("sellerName").GetString());
    }

    [Fact]
    public async Task ParcelsCarrySenderAndReceiverNames()
    {
        var (fixture, _, mira) = await Seeded();
        using var _f = fixture;

        var parcels = (await fixture.Rest().GetFromJsonAsync<JsonElement>($"/api/admin/economy/parcels/{mira}")).EnumerateArray().ToList();

        Assert.All(parcels, p => Assert.Equal(mira, p.GetProperty("toPlayerId").GetInt64()));
        Assert.All(parcels, p => Assert.Equal("Mira", p.GetProperty("toName").GetString()));
        Assert.Equal("Nova", parcels.Single(p => p.GetProperty("fromPlayerId").ValueKind == JsonValueKind.Number && p.GetProperty("fromPlayerId").GetInt64() != 999999).GetProperty("fromName").GetString());
        Assert.Equal(JsonValueKind.Null, parcels.Single(p => p.GetProperty("fromPlayerId").ValueKind == JsonValueKind.Null).GetProperty("fromName").ValueKind);
        // A sender that no longer exists keeps its id and has no name.
        Assert.Equal(JsonValueKind.Null, parcels.Single(p => p.GetProperty("fromPlayerId").ValueKind == JsonValueKind.Number && p.GetProperty("fromPlayerId").GetInt64() == 999999).GetProperty("fromName").ValueKind);
    }
}
