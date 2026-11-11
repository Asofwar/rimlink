using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleControlTransport
{
    internal async Task<BattleCompletedViewReply> ReadCompletedViewAsync(Session session,BattleCompletedViewRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>1024 || request.Scope is null || request.StartDigest.Length!=32
            || !Guid.TryParseExact(request.Scope.BattleId,"D",out _) || session.GuildVersion!=GuildResourceContracts.CurrentVersion)throw Refused();
        request=request.Clone();
        await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);
        var scope=request.Scope;
        if(scope.WorldInstanceId!=held.Cut.WorldId || scope.RestoreBoundary!=held.Cut.Boundary)throw Refused();
        BattleCommanderIdentity.RequireStillCurrent(sessions,session);
        var battle=await held.Db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b=>b.Id==scope.BattleId,ct)??throw Refused();
        if(battle.StartManifest is null || battle.StartDigest is null || !battle.StartDigest.AsSpan().SequenceEqual(request.StartDigest.Span)
            || battle.WorldId!=scope.WorldInstanceId || battle.Boundary!=scope.RestoreBoundary)throw Refused();
        var start=BattleManifestContracts.ReadPrepared(battle.StartManifest);
        if(start.Attempt!=scope.Attempt || start.Fence!=scope.Fence)throw Refused();
        var commander = start.Attacker.PlayerId == session.PlayerId ? start.Attacker
            : !start.Defender.DefenderAi && start.Defender.PlayerId == session.PlayerId ? start.Defender : null;
        if (commander is null && (!start.Defender.DefenderAi || !await held.Db.Set<GuildCampaign>().AnyAsync(
            c => c.Id == battle.CampaignId && c.DefenderCommanderId == session.PlayerId && c.DefenderAllianceId == start.DefenderAllianceId, ct))) throw Refused();
        long alliance = commander?.AllianceId ?? start.DefenderAllianceId;
        var member=await held.Db.Set<AllianceMember>().AsNoTracking()
            .SingleOrDefaultAsync(m=>m.PlayerId==session.PlayerId && m.AllianceId==alliance,ct);
        if(member is null || commander is not null && member.JoinedAt.Ticks!=commander.MembershipJoinedUtcTicks
            || !await held.Db.Players.AsNoTracking().AnyAsync(p=>p.Id==session.PlayerId && p.SaveWriteEpoch==session.SaveWriteEpoch,ct))throw Refused();
        BattleCommanderIdentity.RequireStillCurrent(sessions,session);
        if(battle.Phase=="StartCommitted" && battle.Active && battle.FinalDigest is null)return new();
        if (battle.FinalDigest is null && battle.Phase is "CancelledAwaitingCleanup" or "Cleaned")
        {
            var aborted = await GuildAuthorityEvidence.CleanupTargetAsync(held.Db, battle, ct);
            var stopped = new BattleCompletedViewReply { Abort = aborted.Terminal };
            _ = BattleManifestContracts.ReadCompletedView(stopped, start);
            return stopped;
        }
        if(battle.Phase is not("AppliedAwaitingCleanup" or "Cleaned") || battle.FinalManifest is null || battle.FinalDigest is null
            || battle.Receipt is null || battle.View is null || battle.View.Length>1048576 || battle.Receipt.Length>4096
            || !battle.FinalDigest.AsSpan().SequenceEqual(BattleManifestContracts.Digest(BattleDocumentKind.CommittedFinal,battle.FinalManifest)))throw Refused();
        var reply=new BattleCompletedViewReply{Receipt=BattleAppliedReceipt.Parser.ParseFrom(battle.Receipt),
            CommittedFinal=ByteString.CopyFrom(battle.FinalManifest),View=ByteString.CopyFrom(battle.View)};
        _=BattleManifestContracts.ReadCompletedView(reply,start)??throw Refused();
        return reply;
    }
}
