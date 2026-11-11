using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleControlTransport(BattleExecutorRegistry executors,BattleTransportAdmission admission,BattleArtifactStore artifacts,
    SessionRegistry sessions,TimeProvider time,BattleTransportCache cache,OutpostClock clock)
{
    private async Task<GuildBattle> RequireBattleAsync(BattleTransportAdmission.Lease held,BattleScopeMessage scope,CancellationToken ct,
        bool allowStartedAbort = false)
    {
        if(scope is null || !Guid.TryParseExact(scope.BattleId,"D",out _) || !Guid.TryParseExact(scope.RestoreBoundary,"D",out _))throw Refused();
        var battle=await held.Db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b=>b.Id==scope.BattleId,ct)??throw Refused();
        if(scope.WorldInstanceId!=held.Cut.WorldId || scope.RestoreBoundary!=held.Cut.Boundary || battle.WorldId!=scope.WorldInstanceId
            || battle.Boundary!=scope.RestoreBoundary || battle.Attempt!=scope.Attempt || battle.Fence!=scope.Fence || !battle.Active
            || battle.StartManifest is null || battle.StartDigest is not{Length:32} || battle.FinalDigest is not null)throw Refused();
        if (allowStartedAbort && battle.Phase == "CancelledAwaitingCleanup") return battle;
        if (battle.Phase != "StartCommitted") throw Refused();
        cache.BindHeld(artifacts,new(battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,battle.StartDigest),battle.StartManifest);
        return battle;
    }
    private void RequireExecutor(BattleExecutorLease executor)
    {
        if(executors.RequireExecutionFor(cache.Start)!=executor)throw Refused();
    }
    private async Task<GuildBattleControlGrant> RequirePlayerAsync(BattleTransportAdmission.Lease held,Session session,BattleControlAccess access,CancellationToken ct)
    {
        if(access is null || access.CalculateSize()>1024 || access.StartDigest.Length!=32 || access.Capability.Length!=32 || !Guid.TryParseExact(access.GrantId,"D",out _)
            || !Guid.TryParseExact(access.ClientChannelId,"D",out var client) || client==Guid.Empty || access.ConnectionEpoch<0)throw Refused();
        var battle=await RequireBattleAsync(held,access.Scope,ct);
        if(!access.StartDigest.Span.SequenceEqual(battle.StartDigest))throw Refused();
        executors.RequireExecutionFor(cache.Start);
        var grant=await held.Db.Set<GuildBattleControlGrant>().AsNoTracking().SingleOrDefaultAsync(g=>g.Id==access.GrantId && g.BattleId==battle.Id,ct)??throw Refused();
        if(grant.PlayerId!=session.PlayerId || !CryptographicOperations.FixedTimeEquals(grant.CapabilityDigest,SHA256.HashData(access.Capability.Span))
            || !await EligibleAsync(held.Db,grant,session,ct))throw Refused();
        return grant;
    }
    private async Task<bool> EligibleAsync(RimlinkDbContext db,GuildBattleControlGrant grant,Session? session,CancellationToken ct)
    {
        var start=cache.Start;long player=grant.PlayerId;
        if(!grant.Active || grant.ExpiresAtUtc<=clock.Sample().Utc || session is null || session.PlayerId!=player || session.GuildVersion!=1
            || session.Cts.IsCancellationRequested || !ReferenceEquals(sessions.ByPlayer(player),session) || grant.Side is not(1 or 2))return false;
        var campaign=await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c=>c.Id==start.CampaignId,ct);
        long alliance=grant.Side==1?campaign.AttackerAllianceId:campaign.DefenderAllianceId;
        if(!campaign.Active || campaign.Phase!="StartCommitted" || player!=(grant.Side==1?campaign.AttackerCommanderId:campaign.DefenderCommanderId))return false;
        if(!cache.Bindings.TryGetValue(grant.Id,out var binding))
        {
            if(grant.Side==1 && grant.Id==start.Attacker.ControlGrantId && grant.Epoch==start.Attacker.ControlEpoch)
                binding=(start.Attacker.SessionDigest.ToByteArray(),start.Attacker.MembershipJoinedUtcTicks);
            else
            {
                var operation=await db.Set<GuildCaptureOperation>().FromSqlInterpolated($"SELECT * FROM guild_capture_operations WHERE Kind = 'GuildBattleControlReconciled' AND json_extract(CAST(Receipt AS TEXT), '$.GrantId') = {grant.Id}").AsNoTracking().SingleOrDefaultAsync(ct);
                if(operation is null)return false;
                var receipt=GuildAuthorityEvidence.ReadControlReceipt(operation.Receipt);
                if(receipt.BattleId!=start.BattleId || receipt.PlayerId!=player || receipt.Side!=grant.Side || receipt.Epoch!=grant.Epoch)return false;
                binding=(Convert.FromHexString(receipt.Intent.SessionDigest),receipt.MembershipJoinedUtcTicks);
            }
            if(cache.Bindings.Count>=1024)throw Refused();cache.Bindings.Add(grant.Id,binding);
        }
        if(!BattleCommanderIdentity.Digest(session).Span.SequenceEqual(binding.Session))return false;
        var member=await db.Set<AllianceMember>().AsNoTracking().SingleOrDefaultAsync(m=>m.PlayerId==player && m.AllianceId==alliance,ct);
        var owner=await db.Players.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==player,ct);
        if(member is null || member.JoinedAt.Ticks!=binding.Joined || owner is null || owner.SaveWriteEpoch!=session.SaveWriteEpoch || owner.EconomyFrozenAt is not null
            || grant.Side==1 && RaidRules.IsPeaceful(owner,clock.Sample().Utc) || await db.Wallets.AnyAsync(w=>w.PlayerId==player && w.Balance<0,ct)
            || !await db.WorldObjects.AnyAsync(o=>o.OwnerPlayerId==player && o.Type==WorldObjectTypes.Settlement,ct)
            || !await db.SaveVersions.AnyAsync(s=>s.PlayerId==player && s.IsCurrent && s.SaveGeneration==owner.SaveGeneration && s.SaveWriteEpoch==owner.SaveWriteEpoch,ct)
            || await db.ArchivedColonies.AnyAsync(a=>a.PlayerId==player && a.SaveGeneration==owner.SaveGeneration && a.State==ArchiveState.Archived,ct)
            || await db.Set<GuildDeliveryAttempt>().AnyAsync(a=>a.RecipientId==player && a.Active,ct))return false;
        return !await EconomyGuard.UnderThreatHeldAsync(db,player);
    }
    private BattleJournalChannel RequireChannel(Session session,string connection,BattleControlAccess access, GuildBattleControlGrant grant)
    {
        var channel=cache.Journal.Channel(grant.Side)??throw Refused();
        if(channel.ActorId!=session.PlayerId || channel.ClientChannelId!=access.ClientChannelId || channel.Control.ConnectionEpoch!=access.ConnectionEpoch
            || channel.Control.GrantId!=grant.Id || channel.Control.Epoch!=grant.Epoch || !channel.Control.SessionDigest.AsSpan().SequenceEqual(BattleCommanderIdentity.Digest(session).Span)
            || !channel.TransportDigest.AsSpan().SequenceEqual(TransportDigest(connection)) || cache.Journal.ChannelClosed(grant.Side,channel.Control.ConnectionEpoch))throw Refused();
        return channel;
    }
    private static byte[] TransportDigest(string connection)
    {
        if(string.IsNullOrWhiteSpace(connection) || connection.Length>1024)throw Refused();
        return SHA256.HashData(Encoding.UTF8.GetBytes("Rimlink.BattleHttp2\0"+connection));
    }
    private static OutpostEvidenceUnavailableException Refused()=>new("battle-control-transport-refused");
}
