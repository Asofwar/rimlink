using System.Security.Cryptography;
using Google.Protobuf;
using RimWorld;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Outposts;

public sealed record WorkerCaptureResult(Rimlink.Protocol.V1.OutpostWorkerCapture? Worker, string? Refusal)
{
    public bool Eligible => Worker is not null && Refusal is null;
}

/// <summary>Main-thread snapshot of real game facts; the server still validates the selected profile against these facts.</summary>
public static class OutpostWorkerCapture
{
    public static WorkerCaptureResult Capture(Pawn pawn, OutpostProfile? profile)
    {
        var registry = PawnCustodyRegistry.CurrentRegistry;
        var envelope = registry?.Capture(pawn);
        if (!PawnCustodyRegistry.IsOwnGame || envelope is null || envelope.NonAuthoritativeMirror || envelope.Tombstone)
            return new(null, "Rimlink.Outposts.WorkerIdentity");
        bool adult = pawn.ageTracker.CurLifeStage.developmentalStage.Adult();
        bool owned = pawn.Faction == Faction.OfPlayer;
        bool free = pawn.IsFreeColonist;
        bool baseline = pawn.def == ThingDefOf.Human;
        bool plants = !pawn.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting) && !pawn.WorkTagIsDisabled(WorkTags.ManualDumb);
        bool mining = !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Mining);
        float Capacity(PawnCapacityDef def) => pawn.health.capacities.GetLevel(def);
        float consciousness = Capacity(PawnCapacityDefOf.Consciousness), manipulation = Capacity(PawnCapacityDefOf.Manipulation), moving = Capacity(PawnCapacityDefOf.Moving);
        if (!adult || !owned || !free || !baseline || pawn.Dead
            || !float.IsFinite(consciousness) || !float.IsFinite(manipulation) || !float.IsFinite(moving)
            || profile is not null && (pawn.Downed || pawn.InMentalState || Math.Min(consciousness, Math.Min(manipulation, moving)) <= 0
                || profile == OutpostProfile.Wood && !plants || profile == OutpostProfile.Steel && !mining
                || profile is not (OutpostProfile.Wood or OutpostProfile.Steel))) return new(null, "Rimlink.Outposts.WorkerIneligible");
        var blob = Economy.ThingBlobs.SerializePawn(pawn);
        return new(new Rimlink.Protocol.V1.OutpostWorkerCapture { PawnCustodyId = envelope.PawnCustodyId, AssignmentId = envelope.AssignmentId, Pawn = blob,
            BlobSha256 = ByteString.CopyFrom(SHA256.HashData(blob.ToByteArray())),
            Plants = pawn.skills.GetSkill(SkillDefOf.Plants).Level, Mining = pawn.skills.GetSkill(SkillDefOf.Mining).Level,
            Consciousness = consciousness, Manipulation = manipulation, Moving = moving,
            PlantsCapable = plants, MiningCapable = mining, AdultLifeStage = adult, FreeColonist = free,
            PlayerOwned = owned, BaselineHuman = baseline, Downed = pawn.Downed, MentalState = pawn.InMentalState }, null);
    }
}
