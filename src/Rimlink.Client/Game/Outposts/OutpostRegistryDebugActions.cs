using System.Text.Json;
using LudeonTK;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Outposts;

/// <summary>Offline dev probes in an isolated saved game; never supply server receipts or bypass a live session.</summary>
public static class OutpostRegistryDebugActions
{
    private static PawnCustodyRegistry Registry => PawnCustodyRegistry.CurrentRegistry!;
    private static void RequireLab()
    {
        if (RimlinkSession.Active || !Prefs.DevMode || !PawnCustodyRegistry.IsOwnGame)
            throw new InvalidOperationException("custody lab requires an offline own game in dev mode");
    }
    private static Pawn FirstPawn() => Find.CurrentMap.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();

    [DebugAction("Rimlink", "Custody lab bootstrap", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Bootstrap()
    {
        RequireLab();
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        component.RecoveryVersion = SaveRecoveryData.CurrentVersion; component.SnapshotId = Guid.NewGuid().ToString("N");
        component.OutpostRecovery = new() { Metadata = new() { SchemaVersion = 1,
            Scope = new() { WorldInstanceId = "registry-lab", OwnerPlayerId = 1, SaveGeneration = component.SaveGeneration,
                SaveWriteEpoch = component.SaveWriteEpoch, RestoreBoundary = "registry-lab-boundary" } } };
        foreach (var pawn in Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned))
            Registry.Bootstrap(pawn, component.OutpostRecovery.Metadata.Scope);
        Validate();
    }

    [DebugAction("Rimlink", "Custody lab validate", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Validate()
    {
        RequireLab(); var result = Registry.ValidateOwnGame();
        var pawns = Find.CurrentMap.mapPawns.FreeColonistsSpawned.Select(p => new {
            thing = p.GetUniqueLoadID(), custody = Registry.Capture(p)?.PawnCustodyId,
            wood = OutpostWorkerCapture.Capture(p, OutpostProfile.Wood).Eligible,
            steel = OutpostWorkerCapture.Capture(p, OutpostProfile.Steel).Eligible }).ToArray();
        Log.Message("[Rimlink] custody-lab " + JsonSerializer.Serialize(new { kind = "validate", result.RecoveryRequired,
            entries = result.Registry.Count, issues = result.Issues, branch = Registry.GameBranchId, pawns }));
    }

    [DebugAction("Rimlink", "Custody lab blob roundtrip", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void RoundTrip()
    {
        RequireLab(); var pawn = FirstPawn(); var map = pawn.Map; var cell = pawn.Position;
        string before = pawn.GetUniqueLoadID(); var envelope = Registry.Capture(pawn) ?? throw new InvalidOperationException("bootstrap first");
        var blob = Economy.ThingBlobs.SerializePawn(pawn);
        var mirror = Economy.ThingBlobs.DeserializePawnRaw(blob);
        bool mirrorOnly = Registry.Capture(mirror)?.NonAuthoritativeMirror == true && !Registry.ValidateOwnGame().RecoveryRequired;
        // Drop the mirror reference: Destroy would affect the live same-id pawn through vanilla equality.
        Economy.PawnExit.Leave(pawn, new(Guid.NewGuid().ToString("D"), "offline-lab-roundtrip"));
        var returned = Economy.ThingBlobs.DeserializePawn(blob, newIds: true);
        Economy.RelationLedger.Rejoin(returned);
        returned.thingIDNumber = Find.UniqueIDsManager.GetNextThingID(); Economy.ThingBlobs.RenameOwnVerbs(returned);
        Registry.Bind(returned, envelope); GenSpawn.Spawn(returned, cell, map);
        bool stable = Registry.Capture(returned)?.PawnCustodyId == envelope.PawnCustodyId;
        var result = Registry.ValidateOwnGame();
        Log.Message("[Rimlink] custody-lab " + JsonSerializer.Serialize(new { kind = "roundtrip", before,
            after = returned.GetUniqueLoadID(), stable, mirrorOnly, result.RecoveryRequired, entries = result.Registry.Count }));
        if (!stable || !mirrorOnly || before == returned.GetUniqueLoadID() || result.RecoveryRequired)
            throw new InvalidOperationException("custody roundtrip probe failed");
    }

    [DebugAction("Rimlink", "Custody lab inject duplicate", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Duplicate()
    {
        RequireLab(); var pawn = FirstPawn(); var envelope = Registry.Capture(pawn) ?? throw new InvalidOperationException("bootstrap first");
        var copy = Economy.ThingBlobs.DeserializePawn(Economy.ThingBlobs.SerializePawn(pawn), newIds: true);
        Registry.Bind(copy, envelope); GenSpawn.Spawn(copy, pawn.Position, pawn.Map);
        var result = Registry.ValidateOwnGame();
        Log.Message("[Rimlink] custody-lab " + JsonSerializer.Serialize(new { kind = "duplicate", result.RecoveryRequired, issues = result.Issues }));
        if (!result.RecoveryRequired) throw new InvalidOperationException("distinct objects with one UUID were accepted");
    }
}
