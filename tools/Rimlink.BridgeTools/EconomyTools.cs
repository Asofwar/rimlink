using Grpc.Core;
using RimBridgeServer.Sdk;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Economy;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>The economy window's actions: send, post a lot, the market, buy, deposit. Items are "def:count,def:count".</summary>
public sealed class EconomyTools
{
    [Tool("rimlink/send", Description = "Send items, pawns and/or silver from the wallet to another player",
        ResultDescription = "success/started; the transfer runs the usual save-upload-send pipeline")]
    public Task<object> Send(IRimBridgeContext ctx,
        [ToolParameter(Description = "Receiver name or id", Required = true)] string player,
        [ToolParameter(Description = "Items as def:count, comma-separated")] string? items = null,
        [ToolParameter(Description = "Comma-separated pawn ids or names")] string? pawns = null,
        [ToolParameter(Description = "Silver from the wallet")] int silver = 0,
        [ToolParameter(Description = "Take items from anywhere on the map, not only under the beacons")] bool anywhere = false) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (Targets.Player(player) is not { } to) return Bridge.Fail($"unknown player '{player}'");
        var map = Find.CurrentMap;
        List<Outgoing.Pick> picks = [];
        if (!string.IsNullOrWhiteSpace(items))
        {
            if (Targets.Items(map, items, anywhere, out string? itemError) is not { } found) return Bridge.Fail(itemError!);
            picks = found;
        }
        List<Pawn> chosen = [];
        if (!string.IsNullOrWhiteSpace(pawns))
        {
            if (Targets.Pawns(pawns, out string? pawnError) is not { } p) return Bridge.Fail(pawnError!);
            chosen = p;
        }
        if (picks.Count == 0 && chosen.Count == 0 && silver <= 0) return Bridge.Fail("nothing to send: give items, pawns or silver");
        Outgoing.SendItems(map, to.PlayerId, to.Name, picks, chosen, silver);
        return Targets.Started($"transfer to {to.Name} started", new { items = picks.Sum(x => x.Count), pawns = chosen.Count, silver });
    });

    [Tool("rimlink/post_lot", Description = "Post a market lot: items or one pawn, for a price in silver",
        ResultDescription = "success/started")]
    public Task<object> PostLot(IRimBridgeContext ctx,
        [ToolParameter(Description = "Price in silver", Required = true)] int price,
        [ToolParameter(Description = "Items as def:count, comma-separated")] string? items = null,
        [ToolParameter(Description = "One pawn id or name")] string? pawn = null,
        [ToolParameter(Description = "Take items from anywhere on the map, not only under the beacons")] bool anywhere = false) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (price <= 0) return Bridge.Fail("price must be positive");
        if (string.IsNullOrWhiteSpace(items) == string.IsNullOrWhiteSpace(pawn)) return Bridge.Fail("give exactly one of items or pawn");
        var map = Find.CurrentMap;
        if (!string.IsNullOrWhiteSpace(pawn))
        {
            if (Targets.Pawns(pawn, out string? pawnError) is not { Count: 1 } one) return Bridge.Fail(pawnError ?? "give one pawn");
            Outgoing.PostLot(map, [], one[0], price);
            return Targets.Started($"lot with {one[0].LabelShort} for {price} posted");
        }
        if (Targets.Items(map, items!, anywhere, out string? error) is not { } picks) return Bridge.Fail(error!);
        Outgoing.PostLot(map, picks, null, price);
        return Targets.Started($"lot of {picks.Sum(x => x.Count)} things for {price} posted");
    });

    [Tool("rimlink/deposit", Description = "Deposit silver from under the beacons (or anywhere in dev mode) into the wallet",
        ResultDescription = "success/started")]
    public Task<object> Deposit(IRimBridgeContext ctx,
        [ToolParameter(Description = "Silver to deposit", Required = true)] int silver,
        [ToolParameter(Description = "Take silver from anywhere on the map; needs dev mode")] bool anywhere = false) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (silver <= 0) return Bridge.Fail("silver must be positive");
        if (anywhere && !Prefs.DevMode) return Bridge.Fail("anywhere needs dev mode");
        bool before = BlobDebugActions.PickAnywhere;
        BlobDebugActions.PickAnywhere = anywhere;
        try { Outgoing.Deposit(Find.CurrentMap, silver); }
        finally { BlobDebugActions.PickAnywhere = before; }
        return Targets.Started($"deposit of {silver} started");
    });

    [Tool("rimlink/market", Description = "List market lots, optionally of one seller",
        ResultDescription = "success and lots [{id, seller, price, items, pawn}]")]
    public async Task<object> Market(IRimBridgeContext ctx,
        [ToolParameter(Description = "Seller name or id")] string? seller = null)
    {
        var check = await Bridge.OnMain<object?>(ctx, () => RimlinkSession.Active ? null : Bridge.Fail("no Rimlink session"));
        if (check is not null) return check;
        long sellerId = 0;
        if (!string.IsNullOrWhiteSpace(seller))
        {
            var found = await Bridge.OnMain(ctx, () => Targets.Player(seller) ?? (seller == RimlinkSession.MyPlayerId.ToString() ? RimlinkSession.Login!.Player : null));
            if (found is null) return Bridge.Fail($"unknown player '{seller}'");
            sellerId = found.PlayerId;
        }
        try
        {
            var lots = await RimlinkSession.Economy!.Lots(new MarketQuery { SellerPlayerId = sellerId }, CancellationToken.None);
            return new
            {
                success = true,
                total = lots.Total,
                lots = lots.Lots.Select(l => new
                {
                    id = l.Id,
                    seller = l.Seller?.Name,
                    price = l.Price,
                    items = l.Items?.Items.Select(i => $"{i.DefName} x{i.Count}").ToList(),
                    pawn = l.Pawn?.Name,
                }).ToList(),
            };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex));
        }
    }

    [Tool("rimlink/buy", Description = "Buy a market lot; the purchase arrives as a mailbox parcel",
        ResultDescription = "success and the parcel id")]
    public async Task<object> Buy(IRimBridgeContext ctx,
        [ToolParameter(Description = "Lot id from rimlink/market", Required = true)] long lotId)
    {
        var check = await Bridge.OnMain<object?>(ctx, () => RimlinkSession.Active ? null : Bridge.Fail("no Rimlink session"));
        if (check is not null) return check;
        var economy = RimlinkSession.Economy!;
        try
        {
            var parcel = await economy.Buy(lotId, CancellationToken.None);
            // As the economy window does after a purchase: wallet, mailbox and own lots afresh.
            var wallet = await economy.Wallet(CancellationToken.None);
            var pending = await economy.Pending(CancellationToken.None);
            var mine = await economy.Lots(new MarketQuery { SellerPlayerId = RimlinkSession.MyPlayerId }, CancellationToken.None);
            await Bridge.OnMain(ctx, () => { RimlinkSession.State.Economy.Load(wallet, pending, mine); return 0; });
            return new { success = true, parcelId = parcel.Id, balance = wallet.Balance };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex));
        }
    }
}
