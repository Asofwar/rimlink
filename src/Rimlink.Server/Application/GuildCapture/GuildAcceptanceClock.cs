using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal enum GuildSetupClockPurpose { EligibilitySetup=1,ScheduledWindowNotice=2 }
internal sealed record GuildClockBudget(int Version,long MaximumAdvanceTicks,int MaximumSteps)
{
    internal static GuildClockBudget V1=>new(1,30*TimeSpan.TicksPerDay,32);
}
internal sealed record GuildClockProjection(string RunId,long Revision,long TotalAdvanceTicks,GuildClockBudget Budget,bool Ready);
internal sealed record GuildSetupClockRequest(GuildMutationScope Scope,string StandId,string BootstrapDigest,string SourceIdentity,string RunId,string RequestId,
    long ExpectedRevision,string ExpectedHeadId,byte[] ExpectedHeadDigest,GuildSetupClockPurpose Purpose,long TargetUtcTicks,long[] Players,string CampaignId);
internal sealed record GuildClockRealObservation(DateTime Utc,long Timestamp,long Frequency);
internal sealed record GuildClockStepReceipt(int Version,Actor Operator,GuildSetupClockRequest Request,long Revision,long TotalAdvanceTicks,long DeltaTicks,
    string PreviousStepDigest,ClockSample Before,ClockSample ShiftedAtSameObservation,GuildClockRealObservation Real,GuildClockBudget Budget,string TransitionId);
internal interface IGuildAcceptanceClockControl
{
    GuildClockProjection ClockProjection {get;}
    void PublishClock(GuildClockStepReceipt receipt);
}

internal static class GuildAcceptanceClockRules
{
    internal const string Kind="GuildAcceptanceClockStep";
    internal static GuildSetupClockRequest Freeze(GuildSetupClockRequest request)
    {
        if(request is null || request.Scope is null || !Uuid(request.RequestId) || !Uuid(request.RunId) || !Uuid(request.StandId) || !Uuid(request.ExpectedHeadId)
            || !Hash(request.SourceIdentity) || !Hash(request.BootstrapDigest) || request.ExpectedRevision<0 || request.ExpectedHeadDigest is not{Length:32}
            || !Enum.IsDefined(request.Purpose) || request.Players is not{Length:>0 and <=2} || request.Players.Any(p=>p<=0)
            || !request.Players.SequenceEqual(request.Players.Distinct().Order()) || request.CampaignId is null || (request.TargetUtcTicks<=0 || request.TargetUtcTicks>DateTime.MaxValue.Ticks))throw Invalid();
        if(request.Purpose==GuildSetupClockPurpose.EligibilitySetup ? request.CampaignId!="" : !Uuid(request.CampaignId))throw Invalid();
        return request with{ExpectedHeadDigest=request.ExpectedHeadDigest.ToArray(),Players=request.Players.ToArray()};
    }
    internal static long Delta(GuildSetupClockRequest request,GuildClockProjection projection,ClockSample before,bool activeBattle,DateTime? earliestSlot)
    {
        if(!projection.Ready || projection.RunId!=request.RunId || projection.Revision!=request.ExpectedRevision || projection.Budget!=GuildClockBudget.V1
            || projection.Revision>=projection.Budget.MaximumSteps || activeBattle || before.Utc.Kind!=DateTimeKind.Utc || before.Frequency!=TimeSpan.TicksPerSecond
            || earliestSlot is{} slot && request.TargetUtcTicks>slot.Ticks)throw Invalid();
        long delta=checked(request.TargetUtcTicks-before.Utc.Ticks);
        if(delta<=0 || delta>7*TimeSpan.TicksPerDay || projection.TotalAdvanceTicks<0 || delta>projection.Budget.MaximumAdvanceTicks-projection.TotalAdvanceTicks)throw Invalid();
        return delta;
    }
    internal static string Digest(GuildClockStepReceipt value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static bool Hash(string value)=>value is{Length:64}&&value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    internal static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty&&id.ToString("D")==value;
    internal static OutpostEvidenceUnavailableException Invalid()=>new("guild-acceptance-clock-refused");
}

internal static partial class GuildAuthorityEvidence
{
    internal static async Task<GuildClockStepReceipt[]> ClockJournalAsync(RimlinkDbContext db,CancellationToken ct)
    {
        var raw=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind==GuildAcceptanceClockRules.Kind).Select(o=>o.Receipt).ToArrayAsync(ct);
        return raw.Select(ReadClockStep).OrderBy(r=>r.Revision).ToArray();
    }
    private static GuildClockStepReceipt ReadClockStep(byte[] raw)=>JsonSerializer.Deserialize<GuildClockStepReceipt>(raw,Json)??throw GuildAcceptanceClockRules.Invalid();
    private static void ValidateClockSteps(GuildCaptureOperation[] operations,IReadOnlyDictionary<string,OutpostTransition> transitions)
    {
        var entries=operations.Where(o=>o.Kind==GuildAcceptanceClockRules.Kind).Select(o=>(Operation:o,Receipt:ReadClockStep(o.Receipt))).OrderBy(e=>e.Receipt.Revision).ToArray();
        if(entries.Length==0)return;
        var identity=operations.Where(o=>o.Kind==AcceptanceBindingKind).Select(o=>(JsonSerializer.Deserialize<GuildAcceptanceBindingReceipt>(o.Receipt,Json)??throw GuildAcceptanceClockRules.Invalid()).Identity).SingleOrDefault()??throw GuildAcceptanceClockRules.Invalid();
        long revision=0,total=0;string previousDigest="",previousTransition="";
        foreach(var entry in entries)
        {
            var op=entry.Operation;var r=entry.Receipt;var request=GuildAcceptanceClockRules.Freeze(r.Request);
            if(r.Before is null || r.ShiftedAtSameObservation is null || r.Real is null)throw GuildAcceptanceClockRules.Invalid();
            if(r.Version!=1 || r.Operator is not{AccountId:0,PlayerId:0,Role:Role.Admin} || r.Revision!=++revision || revision>32
                || r.Budget!=GuildClockBudget.V1 || request.ExpectedRevision!=revision-1 || request.RequestId!=op.RequestId || op.ActorId!=0
                || r.TransitionId!=op.TransitionId || !transitions.TryGetValue(r.TransitionId,out var transition) || transition.Actor!=r.Operator.Name
                || request.Scope.WorldId!=transition.WorldId || request.Scope.Boundary!=transition.RestoreBoundary || request.Scope.WorldId!=identity.WorldId || request.Scope.Boundary!=identity.Boundary || request.StandId!=identity.StandId
                || request.BootstrapDigest!=identity.BootstrapDigest || request.SourceIdentity!=identity.SourceIdentity || r.PreviousStepDigest!=previousDigest
                || transition.OldHeadId!=request.ExpectedHeadId || transition.PredecessorId is not{} predecessor || !transitions.TryGetValue(predecessor,out var prior)
                || !prior.Digest.AsSpan().SequenceEqual(request.ExpectedHeadDigest) || !IntentDigest(new{actor=r.Operator,request}).AsSpan().SequenceEqual(op.IntentDigest)
                || r.Before.Utc.Kind!=DateTimeKind.Utc || r.Before.Frequency!=TimeSpan.TicksPerSecond || r.Before.ProcessId!=r.ShiftedAtSameObservation.ProcessId
                || r.ShiftedAtSameObservation.Frequency!=r.Before.Frequency || r.DeltaTicks is <=0 or >7*TimeSpan.TicksPerDay
                || r.ShiftedAtSameObservation.Utc!=r.Before.Utc.AddTicks(r.DeltaTicks) || r.ShiftedAtSameObservation.Utc.Ticks!=request.TargetUtcTicks
                || r.ShiftedAtSameObservation.Timestamp!=checked(r.Before.Timestamp+r.DeltaTicks) || r.Real.Utc.Kind!=DateTimeKind.Utc || r.Real.Frequency<=0
                || r.TotalAdvanceTicks!=checked(total+r.DeltaTicks) || r.TotalAdvanceTicks>r.Budget.MaximumAdvanceTicks)throw GuildAcceptanceClockRules.Invalid();
            if(previousTransition!="")RequireEarlier(transitions,previousTransition,r.TransitionId);
            total=r.TotalAdvanceTicks;previousDigest=GuildAcceptanceClockRules.Digest(r);previousTransition=r.TransitionId;
        }
    }
}
