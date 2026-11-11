using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal static partial class GuildAuthorityEvidence
{
    internal static async Task<HashSet<string>> CleanedRuntimeHistoryAsync(RimlinkDbContext db,string executor,CancellationToken ct)
    {
        var rows=await db.Set<GuildBattle>().AsNoTracking().Where(b=>b.ExecutorId==executor).ToArrayAsync(ct);
        var operations=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind=="GuildBattlePrepared" || o.Kind=="GuildExecutorCleanup").ToArrayAsync(ct);
        var preparations=operations.Where(o=>o.Kind=="GuildBattlePrepared").Select(o=>JsonSerializer.Deserialize<GuildBattlePreparationReceipt>(o.Receipt,Json)??throw InvalidEntry()).ToDictionary(p=>p.BattleId,StringComparer.Ordinal);
        var cleanups=operations.Where(o=>o.Kind=="GuildExecutorCleanup").Select(o=>JsonSerializer.Deserialize<GuildExecutorCleanupReceipt>(o.Receipt,Json)??throw InvalidEntry())
            .Select(o=>BattleCleanupReceipt.Parser.ParseFrom(o.Receipt)).ToArray();
        if(rows.Any(b=>!preparations.ContainsKey(b.Id)))throw InvalidEntry();
        var result=new HashSet<string>(StringComparer.Ordinal);
        foreach(var group in rows.GroupBy(b=>preparations[b.Id].RuntimeAssignment.RuntimeInstanceId))
        {
            bool all=true;
            foreach(var battle in group)
            {
                if(battle.Active || battle.Phase!="Cleaned"){all=false;break;}
                var assignment=preparations[battle.Id].RuntimeAssignment;var receipts=cleanups.Where(r=>r.Scope.BattleId==battle.Id).ToArray();
                if(receipts.Length!=1)throw InvalidEntry();var receipt=receipts[0];BattleCleanupContracts.ValidateReceipt(receipt);
                if(receipt.ProofKind == BattleCleanupProofKind.SameProcessEntry && receipt.RuntimeInstanceId!=group.Key
                    || !(await CleanupScopeAsync(db,battle,receipt.Scope.RestoreBoundary,ct)).Equals(receipt.Scope)
                    || receipt.Terminal.AssignmentTransitionId!=assignment.TransitionId)throw InvalidEntry();
            }
            if(all)result.Add(group.Key);
        }
        return result;
    }
    private static Outposts.OutpostEvidenceUnavailableException InvalidEntry()=>new("battle-entry-cleaned-history-invalid");
}

internal sealed partial class BattleExecutorRegistry
{
    // Called only by fresh Entry Complete under S/B, after the verified cut proves there are no active battles.
    internal void ReadmitCleanedEntry(BattleExecutorHelloBindingV1 binding,IReadOnlySet<string> cleanedRuntimes)
    {
        lock(gate)
        {
            if(binding.Lane!=BattleExecutorLane.Execution || binding.Entry is null || binding.BattleId!="")throw Refused();
            if(executionBinding is null || !cleanedRuntimes.Contains(executionBinding.RuntimeInstanceId))return;
            current=null;executionBinding=null;executionBoot="";busy=false;cleanupHeld=false;startReplayOnly=false;
        }
    }
}
