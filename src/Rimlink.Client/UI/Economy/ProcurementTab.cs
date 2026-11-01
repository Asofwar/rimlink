using Google.Protobuf;
using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Economy;

/// <summary>Resource board, addressed contracts and cooperative supply goals; all mutations use the persisted economy outbox.</summary>
public sealed class ProcurementTab
{
    private bool busy, mine;
    private int page, total;
    private long loadedRevision = -1;
    private string status = "";
    private Vector2 scroll;
    private static string T(string key) => ("Rimlink.Procurement." + key).Translate();
    private static bool Pending => RimlinkSession.Outbox?.Entries.Any(e => e.Refusal is null && e.Kind is OutboxKind.ProcurementCreate or OutboxKind.ProcurementSupply or OutboxKind.ProcurementCancel) == true;
    private static bool Closed => !RimlinkSession.Active || RimlinkSession.State.Economy.InDebt || Outgoing.UnderThreat(Find.AnyPlayerHomeMap) || Game.Visits.VisitSession.InVisit;

    public void Draw(Rect rect, Map? map, IEnumerable<PlayerRef> players)
    {
        if (loadedRevision != RimlinkSession.State.Procurement.Revision && !busy) Refresh();
        bool newMine = mine;
        Widgets.CheckboxLabeled(new Rect(0f, rect.y, 180f, 26f), T("Mine"), ref newMine, disabled: busy);
        if (mine != newMine) { mine = newMine; page = 0; Refresh(); }
        if (Widgets.ButtonText(new Rect(184f, rect.y, 110f, 26f), "Rimlink.Economy.Refresh".Translate(), active: !busy)) Refresh();
        if (Widgets.ButtonText(new Rect(300f, rect.y, 140f, 26f), T("Create"), active: !busy && !Pending && !Closed))
            Find.WindowStack.Add(new CreateDialog(this, players.ToList()));
        if (Pending && Widgets.ButtonText(new Rect(446f, rect.y, 150f, 26f), T("Retry"), active: !busy)) Replay();
        if (page > 0 && Widgets.ButtonText(new Rect(rect.width - 90f, rect.y, 40f, 26f), "<", active: !busy)) { page--; Refresh(); }
        if ((page + 1) * 50 < total && Widgets.ButtonText(new Rect(rect.width - 44f, rect.y, 40f, 26f), ">", active: !busy)) { page++; Refresh(); }
        Widgets.Label(new Rect(0f, rect.y + 30f, rect.width, 44f), Pending ? T("Pending") : T("Rules"));
        var list = new Rect(0f, rect.y + 80f, rect.width, rect.height - 106f);
        var entries = RimlinkSession.State.Procurement.Entries;
        Widgets.BeginScrollView(list, ref scroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, entries.Count * 72f)));
        float y = 0f;
        foreach (var entry in entries)
        {
            Widgets.Label(new Rect(0f, y, list.width - 130f, 26f), $"#{entry.Id} {Kind(entry.Kind)}: {entry.DefName} {entry.Supplied}/{entry.Quantity} — {entry.Buyer?.Name}");
            Widgets.Label(new Rect(0f, y + 26f, list.width - 130f, 42f),
                $"{Reward(entry)}; {Status(entry.Status)}; {entry.ExpiresAt.ToDateTime().ToLocalTime():g}" + (entry.Supplier is null ? "" : $" → {entry.Supplier.Name}"));
            var chosen = entry;
            if (Widgets.ButtonText(new Rect(list.width - 125f, y, 105f, 28f), T("Details")))
                Find.WindowStack.Add(new SupplyDialog(this, chosen.Clone(), map));
            y += 72f;
        }
        Widgets.EndScrollView();
        Widgets.Label(new Rect(0f, rect.yMax - 24f, rect.width, 24f), busy ? "Rimlink.Economy.Working".Translate().ToString() : status);
    }

    private static string Kind(ProcurementKind k) => T("Kind." + k);
    private static string Status(ProcurementStatus s) => T("Status." + s);
    private static string Reward(ProcurementEntry e) => ("Rimlink.Procurement." + (e.Kind == ProcurementKind.BuyOrder ? "UnitPrice" : "Fund")).Translate(e.Reward);

    private void Refresh()
    {
        if (busy || RimlinkSession.Procurement is not { } client) return;
        busy = true;
        var connection = RimlinkSession.Connection;
        long revision = RimlinkSession.State.Procurement.Revision;
        var query = new ProcurementQuery { Mine = mine, Page = page };
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await client.List(query, CancellationToken.None);
                MainThread.Post(() =>
                {
                    if (RimlinkSession.Connection == connection) { RimlinkSession.State.Procurement.Load(result); total = result.Total; loadedRevision = revision; status = ""; }
                    busy = false;
                });
            }
            catch (Exception ex)
            {
                var refusal = RpcErrorDetail.Read(ex);
                MainThread.Post(() => { status = RefusalText.Of(refusal); loadedRevision = revision; busy = false; });
            }
        });
    }

    private void Mutate(IMessage request, Func<Rimlink.Client.Procurement.ProcurementClient, Task<ProcurementEntry>> call)
    {
        if (busy || Pending || RimlinkSession.Procurement is not { } client || RimlinkSession.Outbox is not { } outbox) return;
        busy = true;
        var entry = outbox.Add(request);
        var connection = RimlinkSession.Connection;
        _ = Task.Run(async () =>
        {
            try
            {
                await call(client);
                outbox.Remove(entry.Id);
                MainThread.Post(() => { if (connection == RimlinkSession.Connection) { RimlinkSession.State.Procurement.Invalidate(); RimlinkSession.State.Economy.MarkWealthStale(); status = T("Submitted"); } busy = false; });
            }
            catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
            {
                var refusal = RpcErrorDetail.Read(ex); outbox.Refuse(entry.Id, refusal);
                MainThread.Post(() => { status = RefusalText.Of(refusal); busy = false; });
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn("procurement unanswered: " + ex.Message);
                MainThread.Post(() => { status = T("Pending"); busy = false; });
            }
        });
    }

    private void Replay()
    {
        if (RimlinkSession.Outbox is not { } outbox || RimlinkSession.Connection is not { } c) return;
        busy = true;
        _ = Task.Run(async () =>
        {
            try { await outbox.Replay(new OutboxClients(new EconomyClient(c), null, null, Procurement: new(c)), CancellationToken.None); }
            catch (Exception ex) { RimlinkLog.Instance.Warn("procurement retry: " + ex.Message); }
            finally { MainThread.Post(() => { busy = false; RimlinkSession.State.Procurement.Invalidate(); RimlinkSession.State.Economy.MarkWealthStale(); }); }
        });
    }

    private sealed class CreateDialog(ProcurementTab owner, List<PlayerRef> players) : Window
    {
        private ProcurementKind kind;
        private PlayerRef? supplier;
        private string def = "Steel", quantityBuffer = "100", rewardBuffer = "1", hoursBuffer = "24";
        private int quantity = 100, reward = 1, hours = 24;
        public override Vector2 InitialSize => new(640f, 550f);
        public override void PreOpen() { base.PreOpen(); doCloseX = true; forcePause = true; absorbInputAroundWindow = true; }
        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0f, 0f, rect.width, 30f), T("Create"));
            int i = 0;
            foreach (ProcurementKind k in Enum.GetValues<ProcurementKind>())
            {
                var chosen = k;
                if (Widgets.RadioButtonLabeled(new Rect(i++ * rect.width / 3f, 34f, rect.width / 3f - 4f, 28f), Kind(k), kind == k)) kind = chosen;
            }
            Widgets.Label(new Rect(0f, 76f, 230f, 26f), T("Resource"));
            def = Widgets.TextField(new Rect(240f, 76f, 320f, 26f), def, 128);
            Widgets.Label(new Rect(0f, 110f, 230f, 26f), T("Quantity"));
            Widgets.TextFieldNumeric(new Rect(240f, 110f, 160f, 26f), ref quantity, ref quantityBuffer, 1, 1_000_000);
            Widgets.Label(new Rect(0f, 144f, 230f, 26f), T(kind == ProcurementKind.BuyOrder ? "UnitPriceLabel" : "FundLabel"));
            Widgets.TextFieldNumeric(new Rect(240f, 144f, 160f, 26f), ref reward, ref rewardBuffer, 1, 1_000_000_000);
            Widgets.Label(new Rect(0f, 178f, 230f, 26f), T("Hours"));
            Widgets.TextFieldNumeric(new Rect(240f, 178f, 160f, 26f), ref hours, ref hoursBuffer, 1, 720);
            if (kind == ProcurementKind.SupplyContract && Widgets.ButtonText(new Rect(0f, 216f, rect.width, 28f), supplier?.Name ?? T("Supplier")))
                Find.WindowStack.Add(new FloatMenu(players.Where(p => p.PlayerId != RimlinkSession.MyPlayerId).Select(p => new FloatMenuOption(p.Name, () => supplier = p)).ToList()));
            long fund = kind == ProcurementKind.BuyOrder ? (long)quantity * reward : reward;
            Widgets.Label(new Rect(0f, 254f, rect.width, 30f), "Rimlink.Procurement.Reserve".Translate(fund, RimlinkSession.State.Economy.Balance));
            Widgets.Label(new Rect(0f, 290f, rect.width, 130f), T("CreateRules"));
            bool allowed = !Closed && !Pending && !owner.busy && fund <= 1_000_000_000 && fund <= RimlinkSession.State.Economy.Balance
                && def.Length > 0 && (kind != ProcurementKind.SupplyContract || supplier is not null);
            if (Widgets.ButtonText(new Rect(0f, rect.height - 40f, rect.width, 32f), T("ReserveButton"), active: allowed))
            {
                var request = new ProcurementCreate { ClientRequestId = EconomyClient.NewRequestId(), Kind = kind, DefName = def.Trim(), Quantity = quantity,
                    Reward = reward, LifetimeHours = hours, SupplierPlayerId = kind == ProcurementKind.SupplyContract ? supplier!.PlayerId : 0 };
                owner.Mutate(request, c => c.Create(request, CancellationToken.None)); Close();
            }
        }
    }

    private sealed class SupplyDialog : Window
    {
        private readonly ProcurementTab owner;
        private readonly ProcurementEntry entry;
        private readonly Map? map;
        private readonly ThingPicker? picker;
        private Vector2 contributionsScroll;
        public SupplyDialog(ProcurementTab owner, ProcurementEntry entry, Map? map)
        {
            this.owner = owner; this.entry = entry; this.map = map;
            if (map is not null) picker = new ThingPicker(map, allowPawns: false, includeSilver: true);
            doCloseX = true; forcePause = true; absorbInputAroundWindow = true;
        }
        public override Vector2 InitialSize => new(760f, 690f);
        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0f, 0f, rect.width, 28f), $"#{entry.Id} {Kind(entry.Kind)} — {entry.DefName}: {entry.Supplied}/{entry.Quantity}");
            Widgets.Label(new Rect(0f, 30f, rect.width, 46f), $"{Reward(entry)}; {Status(entry.Status)}; {entry.ExpiresAt.ToDateTime().ToLocalTime():g}");
            Widgets.Label(new Rect(0f, 80f, rect.width, 106f), T(entry.Kind == ProcurementKind.CommunityGoal ? "GoalRules" : entry.Kind == ProcurementKind.SupplyContract ? "ContractRules" : "OrderRules"));
            var list = new Rect(0f, 192f, rect.width, 100f);
            Widgets.BeginScrollView(list, ref contributionsScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(100f, entry.Contributions.Count * 26f)));
            float y = 0f;
            foreach (var contribution in entry.Contributions)
            {
                Widgets.Label(new Rect(0f, y, list.width - 16f, 26f), "Rimlink.Procurement.Contribution".Translate(contribution.Supplier.Name, contribution.Quantity, contribution.Paid)); y += 26f;
            }
            Widgets.EndScrollView();
            bool open = entry.Status == ProcurementStatus.ProcurementOpen && entry.ExpiresAt.ToDateTime() > DateTime.UtcNow;
            bool own = entry.Buyer.PlayerId == RimlinkSession.MyPlayerId;
            if (!own && open && picker is not null) picker.Draw(new Rect(0f, 300f, rect.width, rect.height - 354f));
            bool maySupply = open && !own && (entry.Supplier is null || entry.Supplier.PlayerId == RimlinkSession.MyPlayerId)
                && !Closed && !Pending && !owner.busy && picker?.AnythingPicked == true;
            if (Widgets.ButtonText(new Rect(0f, rect.height - 40f, rect.width * 0.55f, 32f), T("Supply"), active: maySupply))
            {
                Outgoing.SupplyProcurement(map!, entry, picker!.Picks); Close();
            }
            if (own && entry.Status == ProcurementStatus.ProcurementOpen && Widgets.ButtonText(new Rect(rect.width * 0.58f, rect.height - 40f, rect.width * 0.42f, 32f), T("Cancel"), active: !Pending && !owner.busy))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("CancelConfirm"), () => {
                    var request = new ProcurementCancel { ClientRequestId = EconomyClient.NewRequestId(), ProcurementId = entry.Id };
                    owner.Mutate(request, c => c.Cancel(request, CancellationToken.None)); Close();
                }));
            }
        }
    }
}
