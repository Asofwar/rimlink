using Grpc.Core;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Economy;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Economy;

/// <summary>
/// The economy window: market board, own lots, direct sending, wallet and mailbox. Opened from the comms console
/// (all tabs, items from under the trade beacons) or from a caravan at another player's settlement (Send tab only).
/// Network calls run in the background; every UI update comes back through <see cref="MainThread"/>.
/// </summary>
public sealed class Dialog_Economy : Window
{
    private enum Tab { Market, MyLots, Send, Wallet, Mailbox, Raids, Shops }

    private readonly Map? map;
    private readonly Caravan? caravan;
    private readonly PlayerRef? fixedRecipient;
    private Tab tab;
    private readonly ThingPicker? picker;
    private readonly List<Lot> marketLots = [];
    private int marketTotal, marketPage;
    private string marketSearch = "";
    private string status = "";
    private Vector2 marketScroll, lotsScroll, mailScroll, historyScroll;
    private PlayerRef? recipient;
    private int sendSilver, lotPrice = 100, depositSilver = 100, withdrawSilver = 100;
    private string sendSilverBuffer = "0", lotPriceBuffer = "100", depositBuffer = "100", withdrawBuffer = "100";
    private readonly List<WalletEntryMsg> history = [];
    private bool busy;
    private Vector2 raidsScroll, shopScroll;
    private PlayerRef? shopHost;
    private ShopList? shopList;

    public Dialog_Economy(Map map)
    {
        this.map = map;
        picker = new ThingPicker(map, allowPawns: true);
        Init();
        Refresh();
    }

    /// <summary>Caravan mode: only sending, to the owner of the settlement the caravan stands on.</summary>
    public Dialog_Economy(Caravan caravan, PlayerRef recipient)
    {
        this.caravan = caravan;
        fixedRecipient = recipient;
        this.recipient = recipient;
        tab = Tab.Send;
        Init();
    }

    private void Init()
    {
        doCloseX = true;
        draggable = true;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
    }

    public override Vector2 InitialSize => new(760f, 560f);

    private static EconomyState State => RimlinkSession.State.Economy;

    private void Refresh()
    {
        var economy = RimlinkSession.Economy;
        if (economy is null) return;
        busy = true;
        long me = RimlinkSession.MyPlayerId;
        string search = marketSearch;
        int page = marketPage;
        _ = Task.Run(async () =>
        {
            try
            {
                var lots = await economy.Lots(new MarketQuery { Text = search, Page = page }, CancellationToken.None);
                var mine = await economy.Lots(new MarketQuery { SellerPlayerId = me }, CancellationToken.None);
                var wallet = await economy.Wallet(CancellationToken.None);
                var pending = await economy.Pending(CancellationToken.None);
                var entries = await economy.History(CancellationToken.None);
                var raids = RimlinkSession.Raids;
                RaidList? incoming = null, own = null;
                PlayerFlags? flags = null;
                if (raids is not null)
                {
                    try { incoming = await raids.Pending(CancellationToken.None); own = await raids.Mine(CancellationToken.None); flags = await raids.Flags(CancellationToken.None); }
                    catch (RpcException ex) { RimlinkLog.Instance.Warn("raids not loaded: " + EconomyClient.MessageOf(ex)); }
                }
                MainThread.Post(() =>
                {
                    if (incoming is not null && own is not null && flags is not null) RimlinkSession.State.Raids.Load(incoming, own, flags);
                    marketLots.Clear();
                    marketLots.AddRange(lots.Lots);
                    marketTotal = lots.Total;
                    State.Load(wallet, pending, mine);
                    history.Clear();
                    history.AddRange(entries.Entries);
                    status = "";
                    busy = false;
                });
            }
            catch (Exception ex)
            {
                string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => { status = reason; busy = false; });
            }
        });
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (!RimlinkSession.Active) { Close(); return; }
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "Rimlink.Economy.Title".Translate());
        Text.Font = GameFont.Small;

        if (fixedRecipient is null)
        {
            float x = 0f;
            foreach (Tab t in Enum.GetValues(typeof(Tab)))
            {
                var r = new Rect(x, 36f, 94f, 28f);
                if (tab == t) Widgets.DrawHighlightSelected(r);
                if (Widgets.ButtonText(r, ("Rimlink.Economy.Tab." + t).Translate())) tab = t;
                x += 98f;
            }
            if (Widgets.ButtonText(new Rect(inRect.width - 70f, 36f, 70f, 28f), "Rimlink.Economy.Refresh".Translate())) Refresh();
        }

        var body = new Rect(0f, 72f, inRect.width, inRect.height - 100f);
        switch (tab)
        {
            case Tab.Market: DrawMarket(body); break;
            case Tab.MyLots: DrawMyLots(body); break;
            case Tab.Send: DrawSend(body); break;
            case Tab.Wallet: DrawWallet(body); break;
            case Tab.Mailbox: DrawMailbox(body); break;
            case Tab.Raids: DrawRaids(body); break;
            case Tab.Shops: DrawShops(body); break;
        }

        GUI.color = status.Length > 0 ? Color.yellow : Color.gray;
        Widgets.Label(new Rect(0f, inRect.height - 24f, inRect.width, 24f), busy ? "Rimlink.Economy.Working".Translate().ToString() : status);
        GUI.color = Color.white;
    }

    private void DrawMarket(Rect rect)
    {
        marketSearch = Widgets.TextField(new Rect(0f, rect.y, 260f, 26f), marketSearch, 64);
        if (Widgets.ButtonText(new Rect(266f, rect.y, 80f, 26f), "Rimlink.Economy.Search".Translate())) { marketPage = 0; Refresh(); }
        Widgets.Label(new Rect(360f, rect.y, 200f, 26f), "Rimlink.Economy.LotsCount".Translate(marketTotal));
        if (marketPage > 0 && Widgets.ButtonText(new Rect(rect.width - 170f, rect.y, 80f, 26f), "<")) { marketPage--; Refresh(); }
        if ((marketPage + 1) * 50 < marketTotal && Widgets.ButtonText(new Rect(rect.width - 84f, rect.y, 80f, 26f), ">")) { marketPage++; Refresh(); }

        var list = new Rect(0f, rect.y + 32f, rect.width, rect.height - 32f);
        Widgets.BeginScrollView(list, ref marketScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, marketLots.Count * 30f)));
        float y = 0f;
        foreach (var lot in marketLots)
        {
            bool mine = lot.Seller?.PlayerId == RimlinkSession.MyPlayerId;
            Widgets.Label(new Rect(0f, y + 3f, list.width - 320f, 26f), $"{Describe(lot)} — {lot.Seller?.Name}");
            Widgets.Label(new Rect(list.width - 310f, y + 3f, 110f, 26f), "Rimlink.Economy.Price".Translate(lot.Price));
            if (!mine && Widgets.ButtonText(new Rect(list.width - 196f, y, 90f, 26f), "Rimlink.Economy.Buy".Translate()))
            {
                long id = lot.Id;
                Call(e => e.Buy(id, CancellationToken.None), "Rimlink.Economy.Bought".Translate());
            }
            if (mine && Widgets.ButtonText(new Rect(list.width - 196f, y, 90f, 26f), "Rimlink.Economy.Withdraw".Translate()))
            {
                long id = lot.Id;
                Call(e => e.WithdrawLot(id, CancellationToken.None), "Rimlink.Economy.Withdrawn".Translate());
            }
            y += 30f;
        }
        Widgets.EndScrollView();
    }

    private void DrawMyLots(Rect rect)
    {
        var left = new Rect(0f, rect.y, rect.width * 0.55f, rect.height);
        var right = new Rect(left.xMax + 10f, rect.y, rect.width - left.width - 10f, rect.height);
        picker!.Draw(new Rect(left.x, left.y, left.width, left.height - 70f));
        Widgets.Label(new Rect(left.x, left.yMax - 64f, 120f, 26f), "Rimlink.Economy.PriceLabel".Translate());
        Widgets.TextFieldNumeric(new Rect(left.x + 124f, left.yMax - 64f, 120f, 26f), ref lotPrice, ref lotPriceBuffer, 1, 1_000_000);
        if (Widgets.ButtonText(new Rect(left.x, left.yMax - 30f, 200f, 28f), "Rimlink.Economy.PostLot".Translate()) && picker.AnythingPicked)
        {
            var picks = picker.Picks;
            var pawns = picker.Pawns;
            if (pawns.Count > 1 || (pawns.Count == 1 && picks.Count > 0)) { status = "Rimlink.Economy.OneLotRule".Translate(); return; }
            ConfirmPawns(pawns, () => { Outgoing.PostLot(map!, picks, pawns.FirstOrDefault(), lotPrice); picker.Clear(); });
        }

        Widgets.Label(new Rect(right.x, right.y, right.width, 26f), "Rimlink.Economy.MyLots".Translate(State.MyLots.Count));
        var list = new Rect(right.x, right.y + 30f, right.width, right.height - 30f);
        Widgets.BeginScrollView(list, ref lotsScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, State.MyLots.Count * 30f)));
        float y = 0f;
        foreach (var lot in State.MyLots.ToList())
        {
            Widgets.Label(new Rect(0f, y + 3f, list.width - 120f, 26f), $"{Describe(lot)} — {lot.Price}");
            if (Widgets.ButtonText(new Rect(list.width - 110f, y, 94f, 26f), "Rimlink.Economy.Withdraw".Translate()))
            {
                long id = lot.Id;
                Call(e => e.WithdrawLot(id, CancellationToken.None), "Rimlink.Economy.Withdrawn".Translate());
            }
            y += 30f;
        }
        Widgets.EndScrollView();
    }

    private void DrawSend(Rect rect)
    {
        string who = recipient?.Name ?? "Rimlink.Economy.PickRecipient".Translate();
        if (fixedRecipient is null && Widgets.ButtonText(new Rect(0f, rect.y, 260f, 28f), who))
        {
            var options = KnownPlayers().Select(p => new FloatMenuOption(p.Name, () => recipient = p)).ToList();
            if (options.Count == 0) options.Add(new FloatMenuOption("Rimlink.Economy.NobodyKnown".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }
        else if (fixedRecipient is not null) Widgets.Label(new Rect(0f, rect.y + 3f, 400f, 28f), "Rimlink.Economy.SendingTo".Translate(fixedRecipient.Name));

        if (caravan is null)
        {
            picker!.Draw(new Rect(0f, rect.y + 34f, rect.width, rect.height - 104f));
            Widgets.Label(new Rect(0f, rect.yMax - 64f, 160f, 26f), "Rimlink.Economy.SilverFromWallet".Translate(State.Balance));
            Widgets.TextFieldNumeric(new Rect(164f, rect.yMax - 64f, 120f, 26f), ref sendSilver, ref sendSilverBuffer, 0, (int)Math.Min(int.MaxValue, State.Balance));
        }
        else
        {
            caravanPicker ??= new CaravanPicker(caravan);
            caravanPicker.Draw(new Rect(0f, rect.y + 34f, rect.width, rect.height - 70f));
        }

        if (Widgets.ButtonText(new Rect(0f, rect.yMax - 30f, 200f, 28f), "Rimlink.Economy.SendButton".Translate()))
        {
            if (recipient is null) { status = "Rimlink.Economy.PickRecipient".Translate(); return; }
            if (caravan is null)
            {
                var picks = picker!.Picks;
                var pawns = picker.Pawns;
                if (picks.Count == 0 && pawns.Count == 0 && sendSilver == 0) { status = "Rimlink.Economy.NothingPicked".Translate(); return; }
                var to = recipient;
                ConfirmPawns(pawns, () => { Outgoing.SendItems(map!, to.PlayerId, to.Name, picks, pawns, sendSilver); picker.Clear(); sendSilver = 0; sendSilverBuffer = "0"; });
            }
            else
            {
                var to = recipient;
                var things = caravanPicker!.Things;
                var pawns = caravanPicker.Pawns;
                if (things.Count == 0 && pawns.Count == 0) { status = "Rimlink.Economy.NothingPicked".Translate(); return; }
                ConfirmPawns(pawns, () => { Outgoing.SendFromCaravan(caravan, to.PlayerId, to.Name, things, pawns); Close(); });
            }
        }
    }

    private CaravanPicker? caravanPicker;

    private void DrawWallet(Rect rect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, rect.y, rect.width, 32f), "Rimlink.Economy.Balance".Translate(State.Balance));
        Text.Font = GameFont.Small;
        float y = rect.y + 40f;
        Widgets.Label(new Rect(0f, y + 3f, 200f, 26f), "Rimlink.Economy.DepositLabel".Translate());
        Widgets.TextFieldNumeric(new Rect(204f, y, 120f, 26f), ref depositSilver, ref depositBuffer, 1, 1_000_000);
        if (Widgets.ButtonText(new Rect(330f, y, 160f, 26f), "Rimlink.Economy.Deposit".Translate())) Outgoing.Deposit(map!, depositSilver);
        y += 32f;
        Widgets.Label(new Rect(0f, y + 3f, 200f, 26f), "Rimlink.Economy.WithdrawLabel".Translate());
        Widgets.TextFieldNumeric(new Rect(204f, y, 120f, 26f), ref withdrawSilver, ref withdrawBuffer, 1, 1_000_000);
        if (Widgets.ButtonText(new Rect(330f, y, 160f, 26f), "Rimlink.Economy.WithdrawSilver".Translate()))
        {
            int amount = withdrawSilver;
            Call(e => e.WithdrawSilver(new WithdrawRequest { ClientRequestId = EconomyClient.NewRequestId(), Silver = amount }, CancellationToken.None), "Rimlink.Economy.WithdrawQueued".Translate(amount));
        }
        y += 40f;
        Widgets.Label(new Rect(0f, y, rect.width, 26f), "Rimlink.Economy.History".Translate());
        y += 28f;
        var list = new Rect(0f, y, rect.width, rect.yMax - y);
        Widgets.BeginScrollView(list, ref historyScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, history.Count * 24f)));
        float hy = 0f;
        foreach (var e in history)
        {
            Widgets.Label(new Rect(0f, hy, list.width - 16f, 22f), $"{e.At?.ToDateTime().ToLocalTime():dd.MM HH:mm}  {e.Delta:+#;-#;0}  {e.Kind}");
            hy += 24f;
        }
        Widgets.EndScrollView();
    }

    private void DrawMailbox(Rect rect)
    {
        Widgets.Label(new Rect(0f, rect.y, rect.width - 200f, 26f), "Rimlink.Economy.PendingParcels".Translate(State.Pending.Count));
        if (State.Pending.Count > 0 && Widgets.ButtonText(new Rect(rect.width - 190f, rect.y, 190f, 26f), "Rimlink.Economy.AcceptNow".Translate()))
        {
            Incoming.DeliverPending();
        }
        var list = new Rect(0f, rect.y + 32f, rect.width, rect.height - 32f);
        Widgets.BeginScrollView(list, ref mailScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, State.Pending.Count * 30f)));
        float y = 0f;
        foreach (var p in State.Pending)
        {
            Widgets.Label(new Rect(0f, y + 3f, list.width - 16f, 26f), Describe(p));
            y += 30f;
        }
        Widgets.EndScrollView();
    }

    private void DrawRaids(Rect rect)
    {
        var raids = RimlinkSession.State.Raids;
        // A pending switch-off (still peaceful until then) shows unticked; ticking it again cancels the switch-off.
        var offAt = raids.Flags.PeacefulOffAt?.ToDateTime();
        bool peaceful = raids.Flags.Peaceful && offAt is null, closed = raids.Flags.VisitsClosed;
        bool wantPeaceful = peaceful, wantClosed = closed;
        Widgets.CheckboxLabeled(new Rect(0f, rect.y, 300f, 26f), "Rimlink.Raids.Peaceful".Translate(), ref wantPeaceful);
        Widgets.CheckboxLabeled(new Rect(320f, rect.y, 300f, 26f), "Rimlink.Raids.CloseVisits".Translate(), ref wantClosed);
        if (wantPeaceful != peaceful && RimlinkSession.Raids is { } raidsClient) SetFlag(() => raidsClient.SetPeaceful(wantPeaceful, CancellationToken.None));
        if (wantClosed != closed && RimlinkSession.Raids is { } raidsClient2) SetFlag(() => raidsClient2.SetVisitsClosed(wantClosed, CancellationToken.None));
        bool autoAccept = raids.Flags.AutoAcceptLive, wantAutoAccept = autoAccept;
        Widgets.CheckboxLabeled(new Rect(0f, rect.y + 28f, 620f, 26f), "Rimlink.Live.AutoAccept".Translate(), ref wantAutoAccept);
        if (wantAutoAccept != autoAccept && RimlinkSession.Live is { } liveClient) SetFlag(() => liveClient.SetAutoAccept(wantAutoAccept, CancellationToken.None));
        if (offAt is { } until && until > DateTime.UtcNow) // the server sends nothing when the delay runs out
            Widgets.Label(new Rect(0f, rect.y + 56f, rect.width, 22f), "Rimlink.Raids.PeacefulOffAt".Translate(until.ToLocalTime().ToString("g")));

        var list = new Rect(0f, rect.y + 84f, rect.width, rect.height - 84f);
        var liveState = RimlinkSession.State.Live;
        int rows = raids.Incoming.Count + raids.Mine.Count + 6;
        Widgets.BeginScrollView(list, ref raidsScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, rows * 28f)));
        float y = 0f, w = list.width - 16f;
        if (liveState.Current is { } session)
        {
            string kind = (session.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid" : "Rimlink.Live.KindVisit").Translate();
            Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Live.ListCurrent".Translate(kind, session.Host?.Name ?? "?", session.Guest?.Name ?? "?", ("Rimlink.Live.State." + session.State).Translate()));
            y += 28f;
        }
        if (liveState.PendingEnds.Count > 0)
        {
            Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Live.ListPendingEnds".Translate(liveState.PendingEnds.Count));
            y += 28f;
        }
        Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Raids.Incoming".Translate(raids.Incoming.Count));
        y += 28f;
        if (raids.Incoming.Count == 0) { Widgets.Label(new Rect(16f, y, w, 26f), "Rimlink.Raids.None".Translate()); y += 28f; }
        foreach (var r in raids.Incoming)
        {
            string when = r.State == RaidState.RaidAnnounced ? "Rimlink.Raids.LandsAt".Translate(((int)Math.Max(0, r.LandAtTick - Find.TickManager.TicksGame)).ToStringTicksToPeriod()).ToString() : StateLabel(r.State);
            Widgets.Label(new Rect(16f, y, w, 26f), $"{r.Attacker?.Name ?? "?"}: {r.Pawns.Pawns.Count} {("Rimlink.Raids.Raiders").Translate()}, {when}");
            y += 28f;
        }
        y += 8f;
        Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Raids.Mine".Translate(raids.Mine.Count));
        y += 28f;
        if (raids.Mine.Count == 0) { Widgets.Label(new Rect(16f, y, w, 26f), "Rimlink.Raids.None".Translate()); y += 28f; }
        foreach (var r in raids.Mine)
        {
            string summary = r.Summary is { } s && r.State == RaidState.RaidResolved
                ? " " + "Rimlink.Raids.Summary".Translate(s.Dead, s.Captured, s.Fled + s.Alive, s.LootValue)
                : "";
            Widgets.Label(new Rect(16f, y, w, 26f), $"→ {r.Target?.Name ?? "?"}: {r.Pawns.Pawns.Count} {("Rimlink.Raids.Raiders").Translate()}, {StateLabel(r.State)}{summary}");
            y += 28f;
        }
        Widgets.EndScrollView();
    }

    private static string StateLabel(RaidState state) => ("Rimlink.Raids.State." + state).Translate();

    private void SetFlag(Func<Task<PlayerFlags>> call)
    {
        if (busy) return;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var flags = await call();
                MainThread.Post(() => { RimlinkSession.State.Raids.Apply(new ServerEvent { PlayerFlagsChanged = new PlayerFlagsChanged { Flags = flags } }); busy = false; });
            }
            catch (Exception ex)
            {
                string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => { status = reason; busy = false; });
            }
        });
    }

    private void DrawShops(Rect rect)
    {
        if (Widgets.ButtonText(new Rect(0f, rect.y, 240f, 26f), shopHost?.Name ?? "Rimlink.Shops.Pick".Translate().ToString()))
        {
            var options = KnownPlayers().Select(p => new FloatMenuOption(p.Name, () => LoadShop(p))).ToList();
            if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
        }
        Widgets.Label(new Rect(250f, rect.y, rect.width - 250f, 26f), "Rimlink.Shops.Hint".Translate());
        var list = new Rect(0f, rect.y + 32f, rect.width, rect.height - 32f);
        int count = shopList?.Items.Count ?? 0;
        Widgets.BeginScrollView(list, ref shopScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, (count + 1) * 28f)));
        float y = 0f;
        if (shopHost is not null && count == 0) Widgets.Label(new Rect(0f, y, list.width - 16f, 26f), "Rimlink.Shops.Empty".Translate(shopHost.Name));
        foreach (var item in shopList?.Items ?? [])
        {
            Widgets.Label(new Rect(0f, y, list.width - 16f, 26f), "Rimlink.Shops.Item".Translate(item.Label, item.Count, item.Price));
            y += 28f;
        }
        Widgets.EndScrollView();
    }

    private void LoadShop(PlayerRef host)
    {
        var shop = RimlinkSession.Shop;
        if (shop is null) return;
        shopHost = host;
        shopList = null;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var list = await shop.Of(host.PlayerId, CancellationToken.None);
                MainThread.Post(() => { shopList = list; busy = false; });
            }
            catch (Exception ex)
            {
                string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => { status = reason; busy = false; });
            }
        });
    }

    private void Call(Func<EconomyClient, Task<Parcel>> call, string success)
    {
        var economy = RimlinkSession.Economy;
        if (economy is null) return;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await call(economy);
                MainThread.Post(() => { Messages.Message(success, MessageTypeDefOf.PositiveEvent, historical: false); Refresh(); });
            }
            catch (Exception ex)
            {
                string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => { status = reason; busy = false; });
            }
        });
    }

    private static void ConfirmPawns(IReadOnlyList<Pawn> pawns, Action go)
    {
        if (pawns.Any(p => p.RaceProps.Humanlike))
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Economy.ConfirmPawn".Translate(string.Join(", ", pawns.Select(p => p.LabelShort))), go, destructive: true));
        else go();
    }

    private static IEnumerable<PlayerRef> KnownPlayers()
    {
        long me = RimlinkSession.MyPlayerId;
        var seen = new Dictionary<long, PlayerRef>();
        foreach (var o in RimlinkSession.State.Objects.All) if (o.Owner is not null) seen[o.Owner.PlayerId] = o.Owner;
        foreach (var p in RimlinkSession.State.Players.Online) seen[p.PlayerId] = p;
        return seen.Values.Where(p => p.PlayerId != me).OrderBy(p => p.Name);
    }

    public static string Describe(Lot lot) => lot.Pawn is { KindDef.Length: > 0 } ? lot.Pawn.Label : Describe(lot.Items);

    public static string Describe(Parcel p)
    {
        var parts = new List<string>();
        if (p.Items.Items.Count > 0) parts.Add(Describe(p.Items));
        foreach (var pawn in p.Pawns.Pawns) parts.Add(pawn.Label);
        if (p.Silver > 0) parts.Add("Rimlink.Economy.SilverAmount".Translate(p.Silver));
        string from = p.From is not null ? p.From.Name : ("Rimlink.Economy.Kind." + p.Kind).Translate();
        return $"{from}: {string.Join(", ", parts)}";
    }

    private static string Describe(ItemList items) => string.Join(", ", items.Items.Select(i => i.Count > 1 ? $"{i.Label} ×{i.Count}" : i.Label));
}

/// <summary>Caravan inventory and members as checkboxes (caravan mode of the economy window).</summary>
public sealed class CaravanPicker(Caravan caravan)
{
    private readonly HashSet<Thing> things = [];
    private readonly HashSet<Pawn> pawns = [];
    private Vector2 scroll;

    public IReadOnlyList<Thing> Things => things.Where(t => !t.Destroyed).ToList();
    public IReadOnlyList<Pawn> Pawns => pawns.Where(p => !p.Destroyed).ToList();

    public void Draw(Rect rect)
    {
        var items = CaravanInventoryUtility.AllInventoryItems(caravan);
        var members = caravan.PawnsListForReading;
        Widgets.BeginScrollView(rect, ref scroll, new Rect(0f, 0f, rect.width - 16f, Math.Max(rect.height, (items.Count + members.Count + 2) * 28f)));
        float y = 0f, w = rect.width - 16f;
        Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Economy.CaravanItems".Translate());
        y += 28f;
        foreach (var t in items)
        {
            bool on = things.Contains(t);
            Widgets.CheckboxLabeled(new Rect(0f, y, w, 26f), $"{t.LabelCapNoCount} ×{t.stackCount}", ref on);
            if (on) things.Add(t); else things.Remove(t);
            y += 28f;
        }
        Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Economy.Pawns".Translate());
        y += 28f;
        foreach (var p in members)
        {
            bool on = pawns.Contains(p);
            Widgets.CheckboxLabeled(new Rect(0f, y, w, 26f), p.LabelCap, ref on);
            if (on) pawns.Add(p); else pawns.Remove(p);
            y += 28f;
        }
        Widgets.EndScrollView();
    }
}
