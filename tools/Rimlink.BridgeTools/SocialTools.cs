using Grpc.Core;
using RimBridgeServer.Sdk;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Economy;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>Gifts with a decision, the block list, the gift/trade flags and two-way trades, as the letters, the Players window and the trades tab do them.</summary>
public sealed class SocialTools
{
    [Tool("rimlink/gift_answer", Description = "Accept or refuse a gift waiting for a decision (status.social.awaitingGifts)",
        ResultDescription = "success; an accepted gift drops at the next mailbox pass, a refused one goes back to its sender")]
    public async Task<object> GiftAnswer(IRimBridgeContext ctx,
        [ToolParameter(Description = "Parcel id of the gift", Required = true)] long parcelId,
        [ToolParameter(Description = "true to accept, false to refuse", Required = true)] bool accept)
    {
        if (await NoSession(ctx) is { } no) return no;
        try
        {
            await RimlinkSession.Economy!.Answer(parcelId, accept, CancellationToken.None);
            return new { success = true, parcelId, accepted = accept };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex));
        }
    }

    [Tool("rimlink/block", Description = "Block or unblock a player (DMs, gifts, trades and their public chat lines)",
        ResultDescription = "success and the block list")]
    public async Task<object> Block(IRimBridgeContext ctx,
        [ToolParameter(Description = "Player name or id", Required = true)] string player,
        [ToolParameter(Description = "Unblock instead")] bool unblock = false)
    {
        if (await NoSession(ctx) is { } no) return no;
        var target = await Bridge.OnMain(ctx, () => Targets.Player(player) ?? RimlinkSession.State.Social.Blocks.FirstOrDefault(b => b.Name == player || b.PlayerId.ToString() == player));
        if (target is null) return Bridge.Fail($"unknown player '{player}'");
        try
        {
            var social = RimlinkSession.Social!;
            var list = unblock ? await social.Unblock(target.PlayerId, CancellationToken.None) : await social.Block(target.PlayerId, CancellationToken.None);
            await Bridge.OnMain(ctx, () => { RimlinkSession.State.Social.SetBlocks(list); return 0; });
            return new { success = true, blocks = list.Players.Select(p => p.Name).ToList() };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex));
        }
    }

    [Tool("rimlink/trades_flags", Description = "Set the flags 'accept gifts without asking' and 'not accepting trades' (true/false; omit to keep)",
        ResultDescription = "success and the player's flags")]
    public async Task<object> TradesFlags(IRimBridgeContext ctx,
        [ToolParameter(Description = "true or false")] string? acceptGifts = null,
        [ToolParameter(Description = "true or false")] string? tradesClosed = null)
    {
        if (await NoSession(ctx) is { } no) return no;
        var social = RimlinkSession.Social!;
        try
        {
            PlayerFlags? flags = null;
            if (bool.TryParse(acceptGifts, out bool gifts)) flags = await social.SetAcceptGifts(gifts, CancellationToken.None);
            if (bool.TryParse(tradesClosed, out bool closed)) flags = await social.SetTradesClosed(closed, CancellationToken.None);
            if (flags is null) return Bridge.Fail("give acceptGifts and/or tradesClosed as true or false");
            await Bridge.OnMain(ctx, () => { RimlinkSession.State.Raids.Apply(new ServerEvent { PlayerFlagsChanged = new PlayerFlagsChanged { Flags = flags } }); return 0; });
            return new { success = true, acceptGifts = flags.AcceptGifts, tradesClosed = flags.TradesClosed };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex));
        }
    }

    [Tool("rimlink/trade_open", Description = "Offer a trade: items/pawns from under the beacons (or anywhere) and silver from the wallet go into escrow",
        ResultDescription = "success/started; the offer runs the usual save-upload pipeline, then status.social.trades has it")]
    public Task<object> TradeOpen(IRimBridgeContext ctx,
        [ToolParameter(Description = "Partner name or id", Required = true)] string player,
        [ToolParameter(Description = "Items as def:count, comma-separated")] string? items = null,
        [ToolParameter(Description = "Comma-separated pawn ids or names")] string? pawns = null,
        [ToolParameter(Description = "Silver from the wallet")] int silver = 0,
        [ToolParameter(Description = "Note to the partner (up to 200 characters)")] string? note = null,
        [ToolParameter(Description = "Take items from anywhere on the map, not only under the beacons")] bool anywhere = false) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (Targets.Player(player) is not { } to) return Bridge.Fail($"unknown player '{player}'");
        if (Goods(items, pawns, anywhere, out var picks, out var chosen) is { } error) return error;
        return Targets.Act(() => Outgoing.OpenTrade(Find.CurrentMap, to.PlayerId, to.Name, picks, chosen, silver, note ?? ""),
            $"trade offer to {to.Name} started", new { items = picks.Sum(x => x.Count), pawns = chosen.Count, silver });
    });

    [Tool("rimlink/trade_side", Description = "Change our side of an open trade: add goods (or replace them with returnGoods), set the side's silver total and note",
        ResultDescription = "success/started")]
    public Task<object> TradeSide(IRimBridgeContext ctx,
        [ToolParameter(Description = "Trade id", Required = true)] long tradeId,
        [ToolParameter(Description = "Items as def:count, comma-separated")] string? items = null,
        [ToolParameter(Description = "Comma-separated pawn ids or names")] string? pawns = null,
        [ToolParameter(Description = "The side's silver total; -1 keeps it")] int silver = -1,
        [ToolParameter(Description = "The side's note; omit to keep it")] string? note = null,
        [ToolParameter(Description = "Take back what is already offered (it returns by drop pod)")] bool returnGoods = false,
        [ToolParameter(Description = "Take items from anywhere on the map, not only under the beacons")] bool anywhere = false) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (RimlinkSession.State.Social.Trades.FirstOrDefault(t => t.Id == tradeId) is not { } trade) return Bridge.Fail($"no trade {tradeId} in status");
        if (Goods(items, pawns, anywhere, out var picks, out var chosen) is { } error) return error;
        var mine = Mine(trade);
        long total = silver >= 0 ? silver : mine.Silver;
        return Targets.Act(() => Outgoing.SetTradeSide(Find.CurrentMap, trade, picks, chosen, total, note ?? mine.Note, returnGoods),
            $"side of trade {tradeId} change started", new { items = picks.Sum(x => x.Count), pawns = chosen.Count, silver = total, returnGoods });
    });

    [Tool("rimlink/trade_confirm", Description = "Confirm the trade at the revision this client knows (status.social.trades[].revision)",
        ResultDescription = "success and the trade's status after the call")]
    public Task<object> TradeConfirm(IRimBridgeContext ctx, [ToolParameter(Description = "Trade id", Required = true)] long tradeId) =>
        Call(ctx, tradeId, (client, trade) => client.Confirm(trade.Id, trade.Revision, CancellationToken.None));

    [Tool("rimlink/trade_cancel", Description = "Cancel an open trade without the confirmation dialog; each side goes back to its owner",
        ResultDescription = "success and the trade's status after the call")]
    public Task<object> TradeCancel(IRimBridgeContext ctx, [ToolParameter(Description = "Trade id", Required = true)] long tradeId) =>
        Call(ctx, tradeId, (client, trade) => client.Cancel(trade.Id, CancellationToken.None));

    private static async Task<object> Call(IRimBridgeContext ctx, long tradeId, Func<Rimlink.Client.Social.TradesClient, Trade, Task<Trade>> call)
    {
        if (await NoSession(ctx) is { } no) return no;
        var known = await Bridge.OnMain(ctx, () => RimlinkSession.State.Social.Trades.FirstOrDefault(t => t.Id == tradeId));
        if (known is null) return Bridge.Fail($"no trade {tradeId} in status");
        try
        {
            var trade = await call(RimlinkSession.Trades!, known);
            await Bridge.OnMain(ctx, () => { RimlinkSession.State.Social.Upsert(trade); return 0; });
            return new { success = true, tradeId, status = trade.Status.ToString(), revision = trade.Revision };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex));
        }
    }

    private static object? Goods(string? items, string? pawns, bool anywhere, out List<Outgoing.Pick> picks, out List<Pawn> chosen)
    {
        picks = [];
        chosen = [];
        if (!string.IsNullOrWhiteSpace(items))
        {
            if (Targets.Items(Find.CurrentMap, items, anywhere, out string? itemError) is not { } found) return Bridge.Fail(itemError!);
            picks = found;
        }
        if (!string.IsNullOrWhiteSpace(pawns))
        {
            if (Targets.Pawns(pawns, out string? pawnError) is not { } p) return Bridge.Fail(pawnError!);
            chosen = p;
        }
        return null;
    }

    public static TradeSide Mine(Trade t) => (t.Initiator?.PlayerId == RimlinkSession.MyPlayerId ? t.InitiatorSide : t.PartnerSide) ?? new TradeSide();

    public static TradeSide Theirs(Trade t) => (t.Initiator?.PlayerId == RimlinkSession.MyPlayerId ? t.PartnerSide : t.InitiatorSide) ?? new TradeSide();

    private static Task<object?> NoSession(IRimBridgeContext ctx) => Bridge.OnMain<object?>(ctx, () => RimlinkSession.Active ? null : Bridge.Fail("no Rimlink session"));
}
