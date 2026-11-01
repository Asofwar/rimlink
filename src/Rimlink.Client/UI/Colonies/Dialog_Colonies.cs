using RimWorld;
using Rimlink.Client.Colonies;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Live;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Colonies;

public sealed class Dialog_Colonies : Window
{
    private ColonyAccessList list = new();
    private Vector2 scroll;
    private bool busy;
    private string? error;
    private readonly Dictionary<long, string> joinIds = [];

    public Dialog_Colonies()
    {
        doCloseX = true;
        draggable = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        Refresh();
    }

    public override Vector2 InitialSize => new(720f, 540f);
    private static ColonyClient? Client => RimlinkSession.Connection is { } c ? new ColonyClient(c) : null;

    public override void DoWindowContents(Rect rect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0, 0, rect.width - 170, 35), "Rimlink.Colonies.Title".Translate());
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0, 40, rect.width, 65), "Rimlink.Colonies.Limits".Translate());
        GUI.enabled = !busy;
        if (Widgets.ButtonText(new Rect(rect.width - 160, 0, 150, 30), "Rimlink.Colonies.Refresh".Translate())) Refresh();
        if (Widgets.ButtonText(new Rect(0, 108, 210, 30), "Rimlink.Colonies.Invite".Translate())) Invite();
        if (error is not null) Widgets.Label(new Rect(220, 108, rect.width - 220, 50), error);
        var area = new Rect(0, 165, rect.width, rect.height - 165);
        if (list.Entries.Count == 0) Widgets.Label(area, (busy ? "Rimlink.Economy.Working" : "Rimlink.Colonies.Empty").Translate());
        var content = new Rect(0, 0, area.width - 16, Math.Max(area.height, list.Entries.Count * 90f));
        Widgets.BeginScrollView(area, ref scroll, content);
        float y = 0;
        foreach (var access in list.Entries)
        {
            bool own = access.Owner.PlayerId == RimlinkSession.MyPlayerId;
            string other = own ? access.Member.Name : access.Owner.Name;
            Widgets.Label(new Rect(0, y, content.width, 25), "Rimlink.Colonies.Access".Translate(other, (access.Accepted ? "Rimlink.Colonies.Accepted" : "Rimlink.Colonies.Pending").Translate(), access.PawnIds.Count));
            if (!access.Accepted && !own)
            {
                if (Widgets.ButtonText(new Rect(0, y + 28, 140, 28), "Rimlink.Colonies.Accept".Translate())) Run(c => c.Answer(access.Owner.PlayerId, true, CancellationToken.None));
            }
            else if (own && access.Accepted)
            {
                if (Widgets.ButtonText(new Rect(0, y + 28, 200, 28), "Rimlink.Colonies.Assign".Translate())) Assign(access);
            }
            else if (!own && access.Accepted && access.PawnIds.Count > 0)
            {
                if (Widgets.ButtonText(new Rect(0, y + 28, 200, 28), "Rimlink.Colonies.Join".Translate())) Join(access);
            }
            if (Widgets.ButtonText(new Rect(content.width - 170, y + 28, 165, 28), (own ? "Rimlink.Colonies.Revoke" : "Rimlink.Colonies.Leave").Translate()))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Colonies.RevokeConfirm".Translate(other), () => Run(c => c.Revoke(access.Owner.PlayerId, access.Member.PlayerId, CancellationToken.None)), destructive: true));
            y += 90;
        }
        Widgets.EndScrollView();
        GUI.enabled = true;
    }

    private void Invite()
    {
        var choices = RimlinkSession.State.Players.Online.Where(p => p.PlayerId != RimlinkSession.MyPlayerId && list.Entries.All(e => e.Member.PlayerId != p.PlayerId || e.Owner.PlayerId != RimlinkSession.MyPlayerId))
            .Select(p => new FloatMenuOption(p.Name, () => Run(c => c.Invite(p.PlayerId, CancellationToken.None)))).ToList();
        if (choices.Count > 0) Find.WindowStack.Add(new FloatMenu(choices));
    }

    private void Assign(ColonyAccess access)
    {
        if (Find.AnyPlayerHomeMap is not { } map || Game.Visits.VisitSession.InVisit || LiveHost.Active || LiveHost.Starting)
        {
            error = "Rimlink.Colonies.NeedHome".Translate();
            return;
        }
        Find.WindowStack.Add(new Dialog_ColonyPawns(map.mapPawns.FreeColonistsSpawned.ToList(), access.PawnIds, ids => Run(c => c.Assign(access.Member.PlayerId, ids, CancellationToken.None))));
    }

    private void Join(ColonyAccess access)
    {
        if (Game.Visits.VisitSession.InVisit || LiveHost.Active || LiveHost.Starting || LiveGuest.Session is not null || Find.AnyPlayerHomeMap is null)
        {
            error = "Rimlink.Colonies.NeedHome".Translate();
            return;
        }
        if (!joinIds.TryGetValue(access.Owner.PlayerId, out var requestId)) joinIds[access.Owner.PlayerId] = requestId = Guid.NewGuid().ToString("N");
        Run(async c =>
        {
            var session = await c.Join(access.Owner.PlayerId, requestId, CancellationToken.None);
            MainThread.Post(() =>
            {
                joinIds.Remove(access.Owner.PlayerId);
                if (session.State is LiveState.LiveStarting or LiveState.LiveRunning or LiveState.LiveWaiting) { LiveGuest.OnStarted(session); Close(); }
            });
        });
    }

    private void Refresh() => Run(_ => Task.CompletedTask);

    private void Run(Func<ColonyClient, Task> call)
    {
        if (busy || Client is not { } client) return;
        busy = true;
        error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await call(client);
                var updated = await client.List(CancellationToken.None);
                MainThread.Post(() => { list = updated; busy = false; });
            }
            catch (Exception ex)
            {
                var refusal = Rimlink.Client.Net.RpcErrorDetail.Read(ex);
                MainThread.Post(() => { busy = false; error = RefusalText.Of(refusal); });
            }
        });
    }
}

public sealed class Dialog_ColonyPawns : Window
{
    private readonly List<Pawn> pawns;
    private readonly HashSet<long> selected;
    private readonly Action<long[]> apply;
    private Vector2 scroll;

    public Dialog_ColonyPawns(List<Pawn> pawns, IEnumerable<long> previous, Action<long[]> apply)
    {
        this.pawns = pawns;
        selected = previous.Intersect(pawns.Select(p => (long)p.thingIDNumber)).ToHashSet();
        this.apply = apply;
        doCloseX = true;
        absorbInputAroundWindow = true;
    }
    public override Vector2 InitialSize => new(440, 440);
    public override void DoWindowContents(Rect rect)
    {
        Widgets.Label(new Rect(0, 0, rect.width, 45), "Rimlink.Colonies.AssignHelp".Translate());
        var area = new Rect(0, 50, rect.width, rect.height - 95);
        Widgets.BeginScrollView(area, ref scroll, new Rect(0, 0, area.width - 16, Math.Max(area.height, pawns.Count * 30)));
        float y = 0;
        foreach (var pawn in pawns)
        {
            bool value = selected.Contains(pawn.thingIDNumber);
            Widgets.CheckboxLabeled(new Rect(0, y, area.width - 20, 28), pawn.LabelShort, ref value);
            if (value) selected.Add(pawn.thingIDNumber); else selected.Remove(pawn.thingIDNumber);
            y += 30;
        }
        Widgets.EndScrollView();
        GUI.enabled = selected.Count <= 32;
        if (Widgets.ButtonText(new Rect(0, rect.height - 35, rect.width, 30), "Rimlink.Colonies.Apply".Translate()))
        {
            apply(selected.Order().ToArray());
            Close();
        }
        GUI.enabled = true;
    }
}
