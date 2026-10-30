using Grpc.Core;
using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Incidents;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Economy;

/// <summary>
/// The events tab of the economy window (paid incidents spec §4): a target, an event from the server's catalog, its level and
/// drop pods where allowed, the server's quote, "Order"; below, the player's own orders with their state and reason.
/// </summary>
public sealed class EventsTab
{
    private static readonly Color ClosedColor = new(1f, 0.45f, 0.4f);

    private PlayerRef? target;
    private string entryId = "";
    private int level = 2;
    private bool pods;
    private string? quotedFor;
    private string priceLine = "";
    private bool canOrder;
    // Kept until the server answered for sure: a retry after a lost answer replays the same order instead of paying twice.
    private string requestId = Guid.NewGuid().ToString("N");
    private Vector2 catalogScroll, ordersScroll;
    private string status = "";
    private bool busy;

    public void Draw(Rect rect, IEnumerable<PlayerRef> knownPlayers)
    {
        var incidents = RimlinkSession.State.Incidents;
        var entries = incidents.Catalog.Entries;
        if (entries.Count == 0)
        {
            Widgets.Label(new Rect(0f, rect.y, rect.width, 28f), "Rimlink.Incident.Unavailable".Translate());
            return;
        }
        var social = RimlinkSession.State.Social;
        if (Widgets.ButtonText(new Rect(0f, rect.y, 260f, 28f), target?.Name ?? "Rimlink.Economy.PickRecipient".Translate().ToString()))
        {
            var options = knownPlayers.Where(p => !social.IsBlocked(p.PlayerId)).Select(p => new FloatMenuOption(p.Name, () => target = p)).ToList();
            if (options.Count == 0) options.Add(new FloatMenuOption("Rimlink.Economy.NobodyKnown".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }
        GUI.color = Color.gray;
        Widgets.Label(new Rect(270f, rect.y + 4f, rect.width - 270f, 24f), busy ? "Rimlink.Economy.Working".Translate().ToString() : status);
        GUI.color = Color.white;

        float top = rect.y + 34f, listHeight = (rect.height - 34f) * 0.55f;
        var listRect = new Rect(0f, top, 360f, listHeight);
        Widgets.BeginScrollView(listRect, ref catalogScroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, entries.Count * 28f)));
        float y = 0f;
        foreach (var e in entries)
        {
            var row = new Rect(0f, y, listRect.width - 16f, 26f);
            if (e.Id == entryId) Widgets.DrawHighlightSelected(row);
            else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            Widgets.Label(new Rect(4f, y + 2f, row.width - 90f, 24f), IncidentLabels.Of(e.IncidentDef, e.FactionDef, e.Id));
            GUI.color = Color.gray;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(row.width - 88f, y, 84f, 26f), ("Rimlink.Incident.Kind." + e.Kind).Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(row)) entryId = e.Id;
            y += 28f;
        }
        Widgets.EndScrollView();

        var entry = entries.FirstOrDefault(e => e.Id == entryId);
        DrawChoice(new Rect(372f, top, rect.width - 372f, listHeight), entry);
        DrawOrders(new Rect(0f, top + listHeight + 8f, rect.width, rect.height - 34f - listHeight - 8f), incidents.Mine);
    }

    private void DrawChoice(Rect rect, CatalogEntry? entry)
    {
        if (entry is null)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 26f), "Rimlink.Incident.PickEvent".Translate());
            return;
        }
        float y = rect.y;
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(rect.x, y, rect.width, 32f), IncidentLabels.Of(entry.IncidentDef, entry.FactionDef, entry.Id));
        Text.Font = GameFont.Small;
        y += 36f;
        bool hostile = entry.Kind == IncidentKind.IncidentHostile;
        if (hostile)
        {
            for (int l = 1; l <= 4; l++)
                if (Widgets.RadioButtonLabeled(new Rect(rect.x + (l - 1) * 108f, y, 104f, 26f), "Rimlink.Incident.Level".Translate(l), level == l)) level = l;
            y += 30f;
            if (entry.PodsAllowed) Widgets.CheckboxLabeled(new Rect(rect.x, y, 240f, 26f), "Rimlink.Incident.Pods".Translate(), ref pods);
            y += 30f;
        }
        bool withPods = hostile && entry.PodsAllowed && pods;
        int withLevel = hostile ? level : 0;
        if (target is not null)
        {
            string key = $"{target.PlayerId}/{entry.Id}/{withLevel}/{withPods}";
            if (quotedFor != key) Quote(key, target.PlayerId, entry.Id, withLevel, withPods);
        }
        Widgets.Label(new Rect(rect.x, y, rect.width, 26f), target is null ? "Rimlink.Economy.PickRecipient".Translate().ToString() : priceLine);
        y += 32f;

        var economy = RimlinkSession.State.Economy;
        bool enabled = target is not null && canOrder && !economy.InDebt && !busy;
        var orderRect = new Rect(rect.x, y, 160f, 30f);
        if (!enabled) GUI.color = Color.gray;
        if (Widgets.ButtonText(orderRect, "Rimlink.Incident.Order".Translate(), active: enabled) && enabled)
            Order(target!, entry, withLevel, withPods);
        GUI.color = Color.white;
        if (economy.InDebt)
        {
            GUI.color = ClosedColor;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(orderRect.xMax + 6f, y, rect.width - orderRect.width - 6f, 30f), "Rimlink.Economy.InDebt".Translate(-economy.Balance));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }
    }

    private void DrawOrders(Rect rect, List<Incident> mine)
    {
        Widgets.Label(new Rect(0f, rect.y, rect.width, 26f), "Rimlink.Incident.MyOrders".Translate(mine.Count));
        var list = new Rect(0f, rect.y + 28f, rect.width, rect.height - 28f);
        Widgets.BeginScrollView(list, ref ordersScroll, new Rect(0f, 0f, list.width - 16f, Math.Max(list.height, mine.Count * 26f)));
        float y = 0f;
        foreach (var i in mine)
        {
            string level = i.Kind == IncidentKind.IncidentHostile ? $" ({"Rimlink.Incident.Level".Translate(i.Level)})" : "";
            string reason = i.Reason.Length > 0 ? ": " + i.Reason : "";
            Widgets.Label(new Rect(16f, y, list.width - 32f, 24f),
                $"→ {i.Target?.Name ?? "?"}: {IncidentLabels.Of(i.IncidentDef, i.FactionDef, i.EntryId)}{level}, {i.Price} — {("Rimlink.Incident.State." + i.State).Translate()}{reason}");
            y += 26f;
        }
        Widgets.EndScrollView();
    }

    private void Quote(string key, long targetId, string entry, int withLevel, bool withPods)
    {
        quotedFor = key;
        canOrder = false;
        priceLine = "Rimlink.Economy.Working".Translate();
        if (RimlinkSession.Incidents is not { } client) return;
        var request = new QuoteRequest { TargetPlayerId = targetId, EntryId = entry, Level = withLevel, Pods = withPods };
        _ = Task.Run(async () =>
        {
            try
            {
                var quote = await client.Quote(request, CancellationToken.None);
                MainThread.Post(() =>
                {
                    if (quotedFor != key) return;
                    priceLine = "Rimlink.Incident.Price".Translate(quote.Price, quote.Warned ? "Rimlink.Incident.Warned".Translate().ToString() : "");
                    canOrder = true;
                });
            }
            catch (Exception ex)
            {
                var refusal = Rimlink.Client.Net.RpcErrorDetail.Read(ex);
                MainThread.Post(() => { if (quotedFor == key) priceLine = RefusalText.Of(refusal); });
            }
        });
    }

    private void Order(PlayerRef to, CatalogEntry entry, int withLevel, bool withPods)
    {
        if (RimlinkSession.Incidents is not { } client) return;
        busy = true;
        var request = new OrderIncidentRequest { ClientRequestId = requestId, TargetPlayerId = to.PlayerId, EntryId = entry.Id, Level = withLevel, Pods = withPods };
        string label = IncidentLabels.Of(entry.IncidentDef, entry.FactionDef, entry.Id);
        _ = Task.Run(async () =>
        {
            try
            {
                var order = await client.Order(request, CancellationToken.None);
                MainThread.Post(() =>
                {
                    var mine = RimlinkSession.State.Incidents.Mine;
                    if (!mine.Any(i => i.Id == order.Id)) mine.Insert(0, order);
                    requestId = Guid.NewGuid().ToString("N");
                    quotedFor = null; // the target's queue and cooldown changed
                    status = "Rimlink.Incident.Ordered".Translate(label, to.Name);
                    busy = false;
                });
            }
            catch (Exception ex)
            {
                var refusal = Rimlink.Client.Net.RpcErrorDetail.Read(ex);
                bool definitive = ex is RpcException rpc && rpc.StatusCode is StatusCode.FailedPrecondition or StatusCode.InvalidArgument or StatusCode.NotFound;
                MainThread.Post(() =>
                {
                    if (definitive) requestId = Guid.NewGuid().ToString("N");
                    status = RefusalText.Of(refusal);
                    busy = false;
                });
            }
        });
    }

}
