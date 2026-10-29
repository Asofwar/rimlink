using RimWorld;
using Rimlink.Protocol.V1;
using Verse;
using Verse.AI;

namespace Rimlink.Client.Game.Live;

/// <summary>Turns a guest's order into the vanilla call on the host's map (spec §5). Every refusal is a reason the guest sees.</summary>
public static class OrderExecutor
{
    /// <summary>Raiders have no drafter: the guest's draft state is kept here so the mirror can show it and offer the attack controls.</summary>
    public static HashSet<int> VirtualDrafted { get; } = [];

    public static OrderResult Apply(Order o, long guestId, LiveKind kind, Map map)
    {
        string? reason = Execute(o, guestId, kind, map);
        return new OrderResult { Seq = o.Seq, Ok = reason is null, Reason = reason ?? "" };
    }

    private static string? Execute(Order o, long guestId, LiveKind kind, Map map)
    {
        switch (o.OCase)
        {
            case Order.OOneofCase.SetSpeed: return LiveTempo.SetGuestSpeed(o.SetSpeed);
            case Order.OOneofCase.Pause: return LiveTempo.SetGuestPause(o.Pause);
        }
        var owners = MapComponent_LiveOwners.Of(map);
        var pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == o.PawnId);
        if (pawn is null) return "no such pawn on the map";
        if (owners.OwnerOf(pawn) != guestId) return "not your pawn";
        if (pawn.Dead) return "dead";
        if (pawn.Downed && o.OCase != Order.OOneofCase.Draft) return "downed";
        if (pawn.InMentalState) return "in a mental state";
        try
        {
            switch (o.OCase)
            {
                case Order.OOneofCase.Draft:
                    if (pawn.drafter is null)
                    {
                        if (kind != LiveKind.LiveRaid) return "cannot be drafted";
                        if (o.Draft) VirtualDrafted.Add(pawn.thingIDNumber); else VirtualDrafted.Remove(pawn.thingIDNumber);
                        if (!o.Draft) pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                        return null;
                    }
                    pawn.drafter.Drafted = o.Draft;
                    return null;
                case Order.OOneofCase.MoveTo:
                {
                    var cell = new IntVec3(o.MoveTo.X, 0, o.MoveTo.Z);
                    if (!cell.InBounds(map) || !cell.Walkable(map)) return "cannot walk there";
                    if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return "unreachable";
                    var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                    job.playerForced = true;
                    return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) ? null : "refused";
                }
                case Order.OOneofCase.Attack:
                {
                    var target = TargetOf(o.Attack, map);
                    if (!target.IsValid) return "no such target";
                    if (kind == LiveKind.LiveVisit && (target.Thing?.Faction is not { } targetFaction || !targetFaction.HostileTo(Faction.OfPlayer))) return "visitors fight only the host's enemies";
                    Job job;
                    if (pawn.equipment?.Primary is { def.IsRangedWeapon: true } && target.HasThing || (!target.HasThing && pawn.equipment?.Primary is { def.IsRangedWeapon: true }))
                    {
                        job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                        job.verbToUse = pawn.TryGetAttackVerb(target.Thing, allowManualCastWeapons: true);
                        if (job.verbToUse is null) return "no attack verb";
                    }
                    else
                    {
                        if (!target.HasThing) return "melee needs a target";
                        job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                    }
                    job.playerForced = true;
                    return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) ? null : "refused";
                }
                case Order.OOneofCase.TakeJob:
                {
                    var def = DefDatabase<JobDef>.GetNamedSilentFail(o.TakeJob.JobDef);
                    if (def is null) return "unknown job " + o.TakeJob.JobDef;
                    if (LiveRules.Forbidden(kind, def) is { } forbidden) return forbidden;
                    var a = TargetOf(o.TakeJob.A, map);
                    var b = TargetOf(o.TakeJob.B, map);
                    var c = TargetOf(o.TakeJob.C, map);
                    var job = JobMaker.MakeJob(def, a, b, c);
                    if (o.TakeJob.Count > 0) job.count = o.TakeJob.Count;
                    job.playerForced = o.TakeJob.Force;
                    if (a.HasThing && !pawn.CanReserveAndReach(a, PathEndMode.Touch, Danger.Deadly)) return "cannot reach or reserve the target";
                    return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) ? null : "refused";
                }
                case Order.OOneofCase.WorkPriority:
                {
                    if (kind != LiveKind.LiveVisit) return "raiders do not work";
                    var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(o.WorkPriority.WorkDef);
                    if (def is null) return "unknown work type";
                    if (pawn.workSettings is null || pawn.WorkTypeIsDisabled(def)) return "incapable";
                    pawn.workSettings.SetPriority(def, Math.Clamp(o.WorkPriority.Priority, 0, 4));
                    return null;
                }
                case Order.OOneofCase.Schedule:
                {
                    if (kind != LiveKind.LiveVisit) return "raiders keep no schedule";
                    var def = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(o.Schedule.AssignmentDef);
                    if (def is null || pawn.timetable is null) return "unknown assignment";
                    pawn.timetable.SetAssignment(Math.Clamp(o.Schedule.Hour, 0, 23), def);
                    return null;
                }
                case Order.OOneofCase.Equip:
                {
                    var thing = ThingAt(o.Equip, map);
                    if (thing is null || !thing.def.IsWeapon) return "no such weapon";
                    var job = JobMaker.MakeJob(JobDefOf.Equip, thing);
                    job.playerForced = true;
                    return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) ? null : "refused";
                }
                case Order.OOneofCase.Wear:
                {
                    var thing = ThingAt(o.Wear, map);
                    if (thing is not Apparel apparel) return "no such apparel";
                    var job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
                    job.playerForced = true;
                    return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) ? null : "refused";
                }
                case Order.OOneofCase.Drop:
                {
                    if (pawn.equipment?.Primary is { } weapon && weapon.thingIDNumber == o.Drop)
                    {
                        var job = JobMaker.MakeJob(JobDefOf.DropEquipment, weapon);
                        job.playerForced = true;
                        return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) ? null : "refused";
                    }
                    if (pawn.apparel?.WornApparel.FirstOrDefault(x => x.thingIDNumber == o.Drop) is { } worn)
                    {
                        var job = JobMaker.MakeJob(JobDefOf.RemoveApparel, worn);
                        job.playerForced = true;
                        return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) ? null : "refused";
                    }
                    if (pawn.inventory?.innerContainer.FirstOrDefault(x => x.thingIDNumber == o.Drop) is { } carried)
                        return pawn.inventory.innerContainer.TryDrop(carried, pawn.Position, map, ThingPlaceMode.Near, out _) ? null : "could not drop";
                    return "not carried";
                }
                default:
                    return "unknown order";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Rimlink] live order {o.OCase} failed: {ex}");
            return "error: " + ex.Message;
        }
    }

    private static LocalTargetInfo TargetOf(Target? t, Map map)
    {
        if (t is null) return LocalTargetInfo.Invalid;
        if (t.TCase == Target.TOneofCase.Cell) return new IntVec3(t.Cell.X, 0, t.Cell.Z);
        if (t.TCase == Target.TOneofCase.ThingId) return ThingAt(t.ThingId, map) is { } thing ? thing : LocalTargetInfo.Invalid;
        return LocalTargetInfo.Invalid;
    }

    private static Thing? ThingAt(long id, Map map) =>
        map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id) ?? map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id);
}
