using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Economy;

public sealed class AuctionsTab
{
    private ThingPicker? picker;
    private bool create, mine, busy, hasMore;
    private int page, revision = -1, start = 100, buyout, hours = 24;
    private string startBuffer = "100", buyoutBuffer = "0", hoursBuffer = "24", status = "";
    private readonly Dictionary<long, string> bids = [];
    private List<Auction> rows = [];
    private Vector2 scroll;
    private DateTime refreshAt;

    public void Invalidate() => refreshAt = DateTime.MinValue;

    public void Draw(Rect rect, Map? map)
    {
        if (RimlinkSession.Economy is not { } economy) return;
        if (!busy && (DateTime.UtcNow >= refreshAt || revision != RimlinkSession.State.AuctionRevision)) Refresh(economy);
        float y = rect.y;
        if (Widgets.ButtonText(new Rect(0f, y, 140f, 28f), (create ? "Rimlink.Auctions.Board" : "Rimlink.Auctions.Create").Translate())) create = !create;
        if (Widgets.ButtonText(new Rect(146f, y, 140f, 28f), (mine ? "Rimlink.Auctions.All" : "Rimlink.Auctions.Mine").Translate())) { mine = !mine; page = 0; refreshAt = DateTime.MinValue; }
        int pending = RimlinkSession.Outbox?.Entries.Count(e => e.Kind is OutboxKind.AuctionPost or OutboxKind.AuctionBid or OutboxKind.AuctionCancel && e.Refusal is null) ?? 0;
        if (pending > 0 && Widgets.ButtonText(new Rect(292f, y, 260f, 28f), "Rimlink.Auctions.Retry".Translate(pending), active: !busy)) Replay(economy);
        Widgets.Label(new Rect(558f, y, rect.width - 558f, 28f), busy ? "Rimlink.Auctions.Loading".Translate().ToString() : status);
        if (status.Length > 0) TooltipHandler.TipRegion(new Rect(558f, y, rect.width - 558f, 28f), status);
        y += 34f;
        if (create)
        {
            if (map is null) { Widgets.Label(new Rect(0f, y, rect.width, 30f), "Rimlink.Trades.NeedHomeMap".Translate()); return; }
            picker ??= new ThingPicker(map, allowPawns: false);
            picker.Draw(new Rect(0f, y, rect.width * 0.58f, rect.yMax - y));
            float x = rect.width * 0.60f, width = rect.width - x;
            Number("Rimlink.Auctions.Start", ref start, ref startBuffer, 1, 1_000_000);
            Number("Rimlink.Auctions.Buyout", ref buyout, ref buyoutBuffer, 0, 1_000_000);
            Number("Rimlink.Auctions.Hours", ref hours, ref hoursBuffer, 1, 720);
            Text.Font = GameFont.Tiny;
            string rules = "Rimlink.Auctions.Rules".Translate();
            float rulesHeight = Text.CalcHeight(rules, width);
            Widgets.Label(new Rect(x, y, width, rulesHeight), rules);
            Text.Font = GameFont.Small;
            y += rulesHeight + 8f;
            bool closed = RimlinkSession.State.Economy.InDebt || Outgoing.UnderThreat(Find.AnyPlayerHomeMap) || pending > 0;
            if (Widgets.ButtonText(new Rect(x, y, width, 30f), "Rimlink.Auctions.Post".Translate(), active: !closed && picker.AnythingPicked))
            {
                if (buyout > 0 && buyout < start) { status = "Rimlink.Refusal.Auctions.Invalid".Translate(); return; }
                Outgoing.PostAuction(map, picker.Picks, start, buyout, hours * 3600);
                picker.Clear();
                refreshAt = DateTime.MinValue;
            }
            void Number(string key, ref int value, ref string buffer, int min, int max)
            {
                Widgets.Label(new Rect(x, y, width - 120f, 28f), key.Translate());
                Widgets.TextFieldNumeric(new Rect(rect.width - 115f, y, 110f, 28f), ref value, ref buffer, min, max);
                y += 34f;
            }
            return;
        }
        if (Widgets.ButtonText(new Rect(0f, y, 80f, 26f), "<", active: page > 0 && !busy)) { page--; refreshAt = DateTime.MinValue; }
        Widgets.Label(new Rect(88f, y, 100f, 26f), "Rimlink.Auctions.Page".Translate(page + 1));
        if (Widgets.ButtonText(new Rect(194f, y, 80f, 26f), ">", active: hasMore && !busy)) { page++; refreshAt = DateTime.MinValue; }
        y += 30f;
        var list = new Rect(0f, y, rect.width, rect.yMax - y);
        Widgets.BeginScrollView(list, ref scroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, rows.Count * 150f)));
        float cy = 0f;
        if (rows.Count == 0) Widgets.Label(new Rect(0f, 0f, list.width, 30f), "Rimlink.Auctions.None".Translate());
        foreach (var row in rows)
        {
            float w = list.width - 20f;
            string goods = string.Join(", ", row.Items.Items.Select(i => $"{i.Label} ×{i.Count}"));
            Widgets.Label(new Rect(0f, cy, w, 26f), $"#{row.Id}  {row.Seller.Name}: {goods}");
            TooltipHandler.TipRegion(new Rect(0f, cy, w, 26f), goods);
            string leader = row.Bidder?.Name ?? "Rimlink.Auctions.NoBids".Translate();
            Widgets.Label(new Rect(0f, cy + 27f, w, 26f), "Rimlink.Auctions.Current".Translate(row.StartPrice, row.Bid, leader, row.ExpiresAt.ToDateTime().ToLocalTime().ToString("g")));
            Widgets.Label(new Rect(0f, cy + 54f, w, 26f), ("Rimlink.Auctions.Status." + row.Status).Translate());
            bool open = row.Status == AuctionStatus.AuctionOpen && row.ExpiresAt.ToDateTime() > DateTime.UtcNow;
            bool waiting = RimlinkSession.Outbox?.Entries.Any(e => e.Refusal is null && TransferOutbox.Decode(e) switch
            {
                AuctionBidRequest b => b.AuctionId == row.Id,
                AuctionCancelRequest c => c.AuctionId == row.Id,
                _ => false,
            }) ?? false;
            if (open && row.Seller.PlayerId == RimlinkSession.MyPlayerId && row.Bidder is null)
            {
                if (Widgets.ButtonText(new Rect(0f, cy + 84f, 180f, 28f), "Rimlink.Auctions.Cancel".Translate(), active: !busy && !waiting))
                    Mutate(economy, new AuctionCancelRequest { ClientRequestId = EconomyClient.NewRequestId(), AuctionId = row.Id });
            }
            else if (open && row.Seller.PlayerId != RimlinkSession.MyPlayerId)
            {
                string amount = bids.GetValueOrDefault(row.Id) ?? Math.Max(row.StartPrice, row.Bid == long.MaxValue ? long.MaxValue : row.Bid + 1).ToString(CultureInfo.InvariantCulture);
                amount = Widgets.TextField(new Rect(0f, cy + 84f, 150f, 28f), amount, 19);
                bids[row.Id] = amount;
                bool allowed = !busy && !waiting && !RimlinkSession.State.Economy.InDebt && !Outgoing.UnderThreat(Find.AnyPlayerHomeMap);
                if (Widgets.ButtonText(new Rect(158f, cy + 84f, 150f, 28f), "Rimlink.Auctions.Bid".Translate(), active: allowed))
                {
                    if (long.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0)
                        Mutate(economy, new AuctionBidRequest { ClientRequestId = EconomyClient.NewRequestId(), AuctionId = row.Id, Amount = value });
                    else status = "Rimlink.Refusal.Auctions.Invalid".Translate();
                }
                if (row.BuyoutPrice > 0 && Widgets.ButtonText(new Rect(316f, cy + 84f, 220f, 28f), "Rimlink.Auctions.Buy".Translate(row.BuyoutPrice), active: allowed))
                {
                    var request = new AuctionBidRequest { ClientRequestId = EconomyClient.NewRequestId(), AuctionId = row.Id, Amount = row.BuyoutPrice, Buyout = true };
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Auctions.BuyConfirm".Translate(row.BuyoutPrice), () => Mutate(economy, request)));
                }
            }
            Widgets.DrawLineHorizontal(0f, cy + 140f, w);
            cy += 150f;
        }
        Widgets.EndScrollView();
    }

    private void Refresh(EconomyClient client)
    {
        busy = true;
        int requestedPage = page, observed = RimlinkSession.State.AuctionRevision;
        bool requestedMine = mine;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await client.Auctions(new AuctionQuery { Page = requestedPage, Mine = requestedMine }, CancellationToken.None);
                MainThread.Post(() => { rows = result.Auctions.ToList(); hasMore = result.HasMore; revision = observed; refreshAt = DateTime.UtcNow.AddSeconds(5); busy = false; });
            }
            catch (Exception ex) { Failed(ex); }
        });
    }
    private void Mutate(EconomyClient client, IMessage request)
    {
        if (busy || RimlinkSession.Outbox is not { } box) return;
        var entry = box.Add(request);
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (request is AuctionBidRequest bid) await client.BidAuction(bid, CancellationToken.None);
                else await client.CancelAuction((AuctionCancelRequest)request, CancellationToken.None);
                box.Remove(entry.Id);
                MainThread.Post(() => { status = "Rimlink.Auctions.Applied".Translate(); busy = false; refreshAt = DateTime.MinValue; RimlinkSession.State.Economy.MarkWealthStale(); });
            }
            catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex)) { box.Refuse(entry.Id, RpcErrorDetail.Read(ex)); Failed(ex); }
            catch (Exception ex) { Failed(ex, unanswered: true); }
        });
    }
    private void Replay(EconomyClient client)
    {
        if (RimlinkSession.Outbox is not { } box) return;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await box.Replay(client, CancellationToken.None);
                MainThread.Post(() => { busy = false; refreshAt = DateTime.MinValue; RimlinkSession.State.Economy.MarkWealthStale(); });
            }
            catch (Exception ex) { Failed(ex); }
        });
    }
    private void Failed(Exception ex, bool unanswered = false)
    {
        var refusal = RpcErrorDetail.Read(ex);
        MainThread.Post(() => { status = unanswered ? "Rimlink.Economy.Unanswered".Translate(RefusalText.Of(refusal)) : RefusalText.Of(refusal); busy = false; refreshAt = DateTime.UtcNow.AddSeconds(5); });
    }
}
