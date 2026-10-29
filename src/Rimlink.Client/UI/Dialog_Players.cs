using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>
/// Who is online: a direct message, a trade offer, a block. Blocked players are marked; those offline are listed below so
/// they can be unblocked.
/// </summary>
public sealed class Dialog_Players : Window
{
    private Vector2 scroll;

    public Dialog_Players()
    {
        doCloseX = true;
        draggable = true;
        preventCameraMotion = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(480f, 420f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Rimlink.Players.Title".Translate(RimlinkSession.State.Players.Online.Count));
        Text.Font = GameFont.Small;
        var social = RimlinkSession.State.Social;
        var players = RimlinkSession.State.Players.Online.OrderBy(p => p.Name).ToList();
        var offlineBlocked = social.Blocks.Where(b => players.All(p => p.PlayerId != b.PlayerId)).OrderBy(b => b.Name).ToList();
        int rows = players.Count + (offlineBlocked.Count > 0 ? offlineBlocked.Count + 1 : 0);
        var listRect = new Rect(0f, 44f, inRect.width, inRect.height - 44f);
        Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, rows * 30f)));
        float y = 0f, w = listRect.width - 16f;
        foreach (var p in players)
        {
            bool blocked = social.IsBlocked(p.PlayerId);
            if (blocked) GUI.color = Color.gray;
            string name = blocked ? "Rimlink.Social.BlockedName".Translate(p.Name).ToString() : $"{p.Name}  ({p.FactionName})";
            Widgets.Label(new Rect(0f, y + 3f, w - 266f, 24f), name);
            GUI.color = Color.white;
            if (p.PlayerId != RimlinkSession.MyPlayerId)
            {
                if (!blocked && Widgets.ButtonText(new Rect(w - 262f, y, 86f, 26f), "Rimlink.Players.Message".Translate()))
                {
                    var chat = Find.WindowStack.WindowOfType<Dialog_Chat>() ?? Open<Dialog_Chat>();
                    chat.SetTarget(p.PlayerId, p.Name);
                }
                if (!blocked && Widgets.ButtonText(new Rect(w - 172f, y, 80f, 26f), "Rimlink.Players.Trade".Translate())) Trade(p);
                if (Widgets.ButtonText(new Rect(w - 88f, y, 88f, 26f), (blocked ? "Rimlink.Social.Unblock" : "Rimlink.Social.Block").Translate())) ToggleBlock(p, blocked);
            }
            y += 30f;
        }
        if (offlineBlocked.Count > 0)
        {
            Widgets.Label(new Rect(0f, y + 3f, w, 24f), "Rimlink.Social.BlockedOffline".Translate());
            y += 30f;
            foreach (var b in offlineBlocked)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, y + 3f, w - 96f, 24f), b.Name);
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(w - 88f, y, 88f, 26f), "Rimlink.Social.Unblock".Translate())) ToggleBlock(b, blocked: true);
                y += 30f;
            }
        }
        Widgets.EndScrollView();
    }

    private static void Trade(PlayerRef p)
    {
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap is not { IsPlayerHome: true } map || Game.Visits.VisitSession.InVisit)
        {
            Messages.Message("Rimlink.Trades.NeedHomeMap".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Find.WindowStack.Add(new Economy.Dialog_TradeSide(map, null, p.Name, p.PlayerId));
    }

    private static void ToggleBlock(PlayerRef p, bool blocked)
    {
        if (blocked) { Send(c => c.Unblock(p.PlayerId, CancellationToken.None), "Rimlink.Social.Unblocked".Translate(p.Name)); return; }
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Social.BlockConfirm".Translate(p.Name),
            () => Send(c => c.Block(p.PlayerId, CancellationToken.None), "Rimlink.Social.Blocked".Translate(p.Name)), destructive: true));
    }

    private static void Send(Func<Social.SocialClient, Task<BlockList>> call, string success)
    {
        if (RimlinkSession.Social is not { } client) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var list = await call(client);
                MainThread.Post(() => { RimlinkSession.State.Social.SetBlocks(list); Messages.Message(success, MessageTypeDefOf.NeutralEvent, historical: false); });
            }
            catch (Exception ex)
            {
                string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => Messages.Message("Rimlink.Economy.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false));
            }
        });
    }

    private static T Open<T>() where T : Window, new()
    {
        var window = new T();
        Find.WindowStack.Add(window);
        return window;
    }
}
