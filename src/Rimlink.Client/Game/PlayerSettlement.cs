using RimWorld.Planet;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game.Raids;
using Rimlink.Client.Game.Visits;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Another player's settlement on the world map. Not saved: rebuilt from the server on every login.</summary>
public sealed class PlayerSettlement : WorldObject
{
    public long ObjectId;
    public string OwnerName = "";
    public string SettlementName = "";
    public uint ColorArgb = 0xFF4A90D9;
    public long OwnerPlayerId;
    public string FactionName = "";
    /// <summary>0 unknown, 1–5 by the server's bounds (<see cref="WorldInfo.WealthTiers"/>); never the exact wealth.</summary>
    public int WealthTier;
    /// <summary>The day of the owner's last activity as the server knew it when it sent the object (UTC midnight).</summary>
    public DateTime? OwnerLastSeen;
    public int? ColonyGameDays;
    private DateTime? seenOnlineDay;

    /// <summary>Copies what the server sends about the settlement and its owner (on spawn and on every update).</summary>
    public void Apply(Protocol.V1.WorldObject o)
    {
        OwnerPlayerId = o.Owner.PlayerId;
        OwnerName = o.Owner.Name;
        FactionName = o.Owner.FactionName;
        SettlementName = o.Name;
        ColorArgb = o.Owner.ColorArgb;
        WealthTier = o.WealthTier;
        OwnerLastSeen = o.OwnerLastSeen?.ToDateTime();
        ColonyGameDays = o.HasColonyGameDays ? o.ColonyGameDays : null;
    }

    /// <summary>From the online list; remembers today while the owner is seen online, for "last seen" after they leave.</summary>
    public bool OwnerOnline
    {
        get
        {
            bool online = RimlinkSession.State.Players.Online.Any(p => p.PlayerId == OwnerPlayerId);
            if (online) seenOnlineDay = DateTime.UtcNow.Date;
            return online;
        }
    }

    private DateTime? LastSeenDay => seenOnlineDay is { } seen && (OwnerLastSeen is not { } known || seen > known) ? seen : OwnerLastSeen;

    public override string Label => SettlementName.Length > 0 ? SettlementName : OwnerName;

    public override Color ExpandingIconColor => new(
        ((ColorArgb >> 16) & 0xFF) / 255f,
        ((ColorArgb >> 8) & 0xFF) / 255f,
        (ColorArgb & 0xFF) / 255f,
        ((ColorArgb >> 24) & 0xFF) / 255f);

    public override string GetInspectString()
    {
        var lines = new List<string>
        {
            FactionName.Length > 0 ? "Rimlink.Settlement.InspectFaction".Translate(OwnerName, FactionName) : "Rimlink.Settlement.Inspect".Translate(OwnerName),
        };
        if (OwnerOnline) lines.Add("Rimlink.Settlement.Online".Translate());
        else if (LastSeenDay is { } day)
        {
            int days = Math.Max(0, (int)(DateTime.UtcNow.Date - day.Date).TotalDays);
            lines.Add(days == 0 ? "Rimlink.Settlement.SeenToday".Translate() : days == 1 ? "Rimlink.Settlement.SeenYesterday".Translate() : "Rimlink.Settlement.SeenDaysAgo".Translate(days));
        }
        if (ColonyGameDays is { } age) lines.Add("Rimlink.Settlement.Age".Translate(age));
        if (WealthTier > 0) lines.Add("Rimlink.Settlement.Wealth".Translate(WealthTier, TierRange(WealthTier)));
        return string.Join("\n", lines);
    }

    private static string TierRange(int tier)
    {
        var bounds = RimlinkSession.World?.WealthTiers;
        if (bounds is null || bounds.Count != 4) return "?";
        return tier switch
        {
            1 => "Rimlink.Settlement.RangeBelow".Translate(Short(bounds[0])),
            5 => "Rimlink.Settlement.RangeAbove".Translate(Short(bounds[3])),
            _ => "Rimlink.Settlement.RangeBetween".Translate(Short(bounds[tier - 2]), Short(bounds[tier - 1])),
        };
    }

    private static string Short(long value) => value >= 1_000_000
        ? "Rimlink.Settlement.Millions".Translate((value / 1_000_000.0).ToString("0.#"))
        : "Rimlink.Settlement.Thousands".Translate((value / 1000.0).ToString("0.#"));

    public override bool SelectableNow => true;

    /// <summary>Right-click with a caravan selected: travel here and hand things over on arrival.</summary>
    public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
    {
        foreach (var option in base.GetFloatMenuOptions(caravan)) yield return option;
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkTransfer.CanTransfer(caravan, this),
                     () => new CaravanArrivalAction_RimlinkTransfer(this),
                     "Rimlink.Economy.CaravanTransfer".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkRaid.CanRaid(caravan, this),
                     () => new CaravanArrivalAction_RimlinkRaid(this),
                     "Rimlink.Raid.CaravanRaid".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkVisit.CanVisit(this),
                     () => new CaravanArrivalAction_RimlinkVisit(this),
                     "Rimlink.Visit.CaravanVisit".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => Live.CaravanArrivalAction_RimlinkLive.CanPropose(this, Rimlink.Protocol.V1.LiveKind.LiveRaid),
                     () => new Live.CaravanArrivalAction_RimlinkLive(this, Rimlink.Protocol.V1.LiveKind.LiveRaid),
                     "Rimlink.Live.CaravanRaid".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => Live.CaravanArrivalAction_RimlinkLive.CanPropose(this, Rimlink.Protocol.V1.LiveKind.LiveVisit),
                     () => new Live.CaravanArrivalAction_RimlinkLive(this, Rimlink.Protocol.V1.LiveKind.LiveVisit),
                     "Rimlink.Live.CaravanVisit".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
    }

    /// <summary>Transport pods aimed here: the pawns inside raid the settlement on arrival.</summary>
    public override IEnumerable<FloatMenuOption> GetTransportersFloatMenuOptions(IEnumerable<IThingHolder> pods, Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        foreach (var option in base.GetTransportersFloatMenuOptions(pods, launchAction)) yield return option;
        foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                     () => TransportersArrivalAction_RimlinkRaid.CanRaid(pods, this),
                     () => new TransportersArrivalAction_RimlinkRaid(this),
                     "Rimlink.Raid.PodRaid".Translate(OwnerName),
                     launchAction, Tile))
        {
            yield return option;
        }
        foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkVisit.CanVisit(this),
                     () => new TransportersArrivalAction_RimlinkVisit(this),
                     "Rimlink.Visit.PodVisit".Translate(OwnerName),
                     launchAction, Tile))
        {
            yield return option;
        }
        foreach (var kind in new[] { Rimlink.Protocol.V1.LiveKind.LiveRaid, Rimlink.Protocol.V1.LiveKind.LiveVisit })
        {
            foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                         () => Live.TransportersArrivalAction_RimlinkLive.CanPropose(pods, this, kind),
                         () => new Live.TransportersArrivalAction_RimlinkLive(this, kind),
                         (kind == Rimlink.Protocol.V1.LiveKind.LiveRaid ? "Rimlink.Live.PodRaid" : "Rimlink.Live.PodVisit").Translate(OwnerName),
                         launchAction, Tile))
            {
                yield return option;
            }
        }
    }

    /// <summary>A caravan already standing here can open the transfer window again.</summary>
    public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
    {
        foreach (var gizmo in base.GetCaravanGizmos(caravan)) yield return gizmo;
        if (caravan.Tile == Tile && CaravanArrivalAction_RimlinkRaid.CanRaid(caravan, this))
        {
            yield return new Command_Action
            {
                defaultLabel = "Rimlink.Raid.CaravanRaidGizmo".Translate(),
                defaultDesc = "Rimlink.Raid.CaravanRaid".Translate(OwnerName),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", reportFailure: false),
                action = () => CaravanArrivalAction_RimlinkRaid.Open(caravan, this),
            };
        }
        if (caravan.Tile == Tile && CaravanArrivalAction_RimlinkVisit.CanVisit(this))
        {
            yield return new Command_Action
            {
                defaultLabel = "Rimlink.Visit.CaravanVisitGizmo".Translate(),
                defaultDesc = "Rimlink.Visit.CaravanVisit".Translate(OwnerName),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Install", reportFailure: false),
                action = () => VisitSession.Begin(RimlinkSession.State.Objects.TryGet(ObjectId)!.Owner, caravan.PawnsListForReading.ToList()),
            };
        }
        foreach (var kind in new[] { Rimlink.Protocol.V1.LiveKind.LiveRaid, Rimlink.Protocol.V1.LiveKind.LiveVisit })
        {
            if (caravan.Tile != Tile || !Live.CaravanArrivalAction_RimlinkLive.CanPropose(this, kind)) continue;
            var k = kind;
            yield return new Command_Action
            {
                defaultLabel = (k == Rimlink.Protocol.V1.LiveKind.LiveRaid ? "Rimlink.Live.CaravanRaidGizmo" : "Rimlink.Live.CaravanVisitGizmo").Translate(),
                defaultDesc = (k == Rimlink.Protocol.V1.LiveKind.LiveRaid ? "Rimlink.Live.CaravanRaid" : "Rimlink.Live.CaravanVisit").Translate(OwnerName),
                icon = ContentFinder<Texture2D>.Get(k == Rimlink.Protocol.V1.LiveKind.LiveRaid ? "UI/Commands/Attack" : "UI/Commands/Install", reportFailure: false),
                action = () => Live.CaravanArrivalAction_RimlinkLive.Open(caravan, this, k),
            };
        }
        if (caravan.Tile != Tile || !CaravanArrivalAction_RimlinkTransfer.CanTransfer(caravan, this)) yield break;
        yield return new Command_Action
        {
            defaultLabel = "Rimlink.Economy.CaravanTransferGizmo".Translate(),
            defaultDesc = "Rimlink.Economy.CaravanTransfer".Translate(OwnerName),
            icon = ContentFinder<Texture2D>.Get("UI/Commands/Trade", reportFailure: false),
            action = () => CaravanArrivalAction_RimlinkTransfer.Open(caravan, this),
        };
    }
}
