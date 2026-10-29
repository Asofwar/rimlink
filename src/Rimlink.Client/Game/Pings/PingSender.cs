using Rimlink.Client.Game.Live;
using Rimlink.Client.Game.Visits;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Pings;

/// <summary>
/// The ping key (live UX spec §2): on a shared map (the live mirror, the host's map during a live session, a visit copy) it pings the
/// other player at the cell under the mouse; on one's own map otherwise and on the world map it offers the players online.
/// </summary>
public static class PingSender
{
    [DefOf]
    public static class Keys
    {
        public static KeyBindingDef Rimlink_Ping = null!;

        static Keys() => DefOfHelper.EnsureInitializedInCtor(typeof(Keys));
    }

    /// <summary>The visitor whose visit started last: the first offer on one's own map.</summary>
    private static PlayerRef? lastVisitor;
    private static bool subscribed;

    public static void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        RimlinkSession.State.Raids.VisitStarted += v => lastVisitor = v.Visitor;
        RimlinkSession.State.Pinged += p => PingMarkers.Show(p.Tile, p.Cell is { } c ? new IntVec3(c.X, 0, c.Z) : null, p.From?.Name ?? "?", received: true);
    }

    /// <summary>The other player of the shared map this game shows, if it shows one.</summary>
    public static PlayerRef? Counterpart()
    {
        if (MirrorMode.Active && MirrorMode.Session?.Host is { } host) return host;
        if (LiveHost.Active && LiveHost.Session?.Guest is { } guest) return guest;
        if (VisitSession.Active && VisitSession.HostId != 0) return new PlayerRef { PlayerId = VisitSession.HostId, Name = VisitSession.HostName };
        return null;
    }

    /// <summary>From the game component's OnGUI: the key, unless a text field or a window has the input (typing "g" in chat).</summary>
    public static void OnGUI()
    {
        if (Event.current.type != EventType.KeyDown || !RimlinkSession.Active || Current.ProgramState != ProgramState.Playing) return;
        if (!Keys.Rimlink_Ping.KeyDownEvent || GUIUtility.keyboardControl != 0 || Find.WindowStack.WindowsPreventCameraMotion) return;
        Event.current.Use();
        if (WorldRendererUtility.WorldSelected)
        {
            int tile = GenWorld.MouseTile();
            if (tile >= 0) Offer(tile, null);
            return;
        }
        if (Find.CurrentMap is not { } map) return;
        var cell = Verse.UI.MouseCell();
        if (!cell.InBounds(map)) return;
        if (Counterpart() is { } other) Send(other, map.Tile, cell);
        else Offer(map.Tile, cell);
    }

    private static void Offer(int tile, IntVec3? cell)
    {
        var first = Counterpart() ?? lastVisitor;
        var others = RimlinkSession.State.Players.Online.Where(p => p.PlayerId != RimlinkSession.MyPlayerId)
            .OrderBy(p => p.PlayerId == first?.PlayerId ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (others.Count == 0)
        {
            Messages.Message("Rimlink.Ping.NoOne".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Find.WindowStack.Add(new FloatMenu(others.Select(p => new FloatMenuOption("Rimlink.Ping.To".Translate(p.Name), () => Send(p, tile, cell))).ToList()));
    }

    public static void Send(PlayerRef to, int tile, IntVec3? cell)
    {
        if (RimlinkSession.Connection is not { } connection) return;
        PingMarkers.Show(tile, cell, "", received: false);
        var request = new PingRequest { ToPlayerId = to.PlayerId, Tile = tile, Cell = cell is { } c ? new MapCell { X = c.x, Z = c.z } : null };
        _ = Task.Run(async () =>
        {
            try { await connection.Chat.PingAsync(request); }
            catch (Exception ex)
            {
                string reason = RefusalText.Of(ex);
                MainThread.Post(() => Messages.Message("Rimlink.Ping.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false));
            }
        });
    }
}
