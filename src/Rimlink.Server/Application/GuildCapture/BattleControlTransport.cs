using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleControlTransport
{
    internal async Task<BattleControlChannelReply> AttachAsync(Session session,string connection,BattleAttachControlRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>2048 || request.Access?.ConnectionEpoch!=0)throw Refused();
        request=request.Clone();await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);
        var grant=await RequirePlayerAsync(held,session,request.Access,ct);await ReconcileHeldAsync(held,ct);
        var old=cache.Journal.Channel(grant.Side);
        if(old is not null && old.RequestId!=request.ClientRequestId && !cache.Journal.ChannelClosed(grant.Side,old.Control.ConnectionEpoch))
        {CloseChannel(old);throw Refused();}
        var channel=cache.Journal.AttachChannel(grant.Side,session.PlayerId,request.ClientRequestId,request.Access.ClientChannelId,grant.Id,grant.Epoch,
            BattleCommanderIdentity.Digest(session).ToByteArray(),TransportDigest(connection),request.HasExpectedPriorEpoch?request.ExpectedPriorEpoch:null);
        if(cache.Journal.Channel(grant.Side)?.Control.ConnectionEpoch!=channel.Control.ConnectionEpoch || cache.Journal.ChannelClosed(grant.Side,channel.Control.ConnectionEpoch))throw Refused();
        cache.Presence[(grant.Side,channel.Control.ConnectionEpoch)]=time.GetTimestamp();
        return new(){Side=(BattleFighterSide)grant.Side,ControlEpoch=grant.Epoch,ConnectionEpoch=channel.Control.ConnectionEpoch,Position=cache.Journal.Position};
    }
    internal async Task<BattleSubmitCommandReply> SubmitAsync(Session session,string connection,BattleSubmitCommandRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>BattleCommandContracts.MaximumBytes+2048 || request.Command.Length>BattleCommandContracts.MaximumBytes)throw Refused();
        request=request.Clone();await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);
        var grant=await RequirePlayerAsync(held,session,request.Access,ct);await ReconcileHeldAsync(held,ct);
        var channel=RequireChannel(session,connection,request.Access,grant);
        _=BattleCommandContracts.Read(request.Command.Span,cache.Start,(BattleFighterSide)grant.Side);
        var prior=cache.Journal.FindRequest(request.ClientRequestId);
        if(prior is null)
        {
            if(artifacts.DescriptorHeld(cache.Start.BattleId,cache.Start.Fence,"result") is not null)throw Refused();
            var queue=cache.Accepted[grant.Side-1];long now=time.GetTimestamp();
            while(queue.TryPeek(out long stamp) && time.GetElapsedTime(stamp,now)>=TimeSpan.FromSeconds(1))queue.Dequeue();
            if(queue.Count>=20)throw Refused();
        }
        long sequence=cache.Journal.AppendControlled(grant.Side,session.PlayerId,request.ClientRequestId,request.Command.Span,channel.Control);
        if(prior is null)cache.Accepted[grant.Side-1].Enqueue(time.GetTimestamp());
        cache.Presence[(grant.Side,channel.Control.ConnectionEpoch)]=time.GetTimestamp();
        var accepted=cache.Journal.Read(grant.Side,sequence);
        return new(){Side=(BattleFighterSide)grant.Side,Sequence=sequence,CommandDigest=ByteString.CopyFrom(SHA256.HashData(accepted.Command)),
            Receipt=accepted.Acknowledgement is null?null:BattleCommandReceipt.Parser.ParseFrom(accepted.Acknowledgement)};
    }
    internal async Task<BattleTransportPosition> HeartbeatAsync(Session session,string connection,BattleControlAccess access,CancellationToken ct)
    {
        if(access is null)throw Refused();access=access.Clone();await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);
        var grant=await RequirePlayerAsync(held,session,access,ct);await ReconcileHeldAsync(held,ct);var channel=RequireChannel(session,connection,access,grant);
        cache.Presence[(grant.Side,channel.Control.ConnectionEpoch)]=time.GetTimestamp();return new(){Position=cache.Journal.Position};
    }
    internal async Task<BattleRunningViewReply> ReadViewAsync(Session session,string connection,BattleRunningViewRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>2048 || request.AfterSequence is <0 or >30001)throw Refused();
        request=request.Clone();await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);
        var grant=await RequirePlayerAsync(held,session,request.Access,ct);await ReconcileHeldAsync(held,ct);
        var channel=RequireChannel(session,connection,request.Access,grant);
        cache.Presence[(grant.Side,channel.Control.ConnectionEpoch)]=time.GetTimestamp();
        return new(){View=cache.Views.ReadAfter(request.AfterSequence)};
    }
    internal async Task<BattleRunningWork> PollAsync(BattleExecutorLease executor,BattleScopeMessage scope,CancellationToken ct,ByteString? view=null)
    {
        if(view?.Length>BattleManifestContracts.RunningViewBytes)throw Refused();
        executors.RequireCurrent(executor);await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);
        var battle = await RequireBattleAsync(held, scope, ct, allowStartedAbort: true);
        if (battle.Phase == "CancelledAwaitingCleanup")
            return new() { Scope = scope.Clone(), StartDigest = ByteString.CopyFrom(battle.StartDigest!),
                Aborted = await BattleExecutorWorkService.CancellationWorkAsync(held, executors, executor, battle, ct) };
        RequireExecutor(executor);
        await ReconcileHeldAsync(held,ct);var journal=cache.Journal;var delivery=journal.OutstandingDispatch();
        if(view is {IsEmpty:false})
        {
            if(journal.RuntimeStarted is null)throw Refused();
            cache.Views.Apply(view,requireFull:true);
        }
        if(delivery is null && journal.RuntimeStarted is not null && artifacts.DescriptorHeld(battle.Id,battle.Fence,"result") is null)
        {
            var keys=new[]{1,2}.SelectMany(side=>journal.PendingCommands(side)).Where(c=>c.DeliveryId is null && !journal.CutPending(c.Side)
                && c.Control is not null && !journal.ChannelClosed(c.Side,c.Control.ConnectionEpoch)).OrderBy(c=>c.Sequence).ThenBy(c=>c.Side).Take(8).Select(c=>new BattleJournalKey(c.Side,c.Sequence)).ToArray();
            if(keys.Length>0)delivery=journal.Dispatch(Guid.NewGuid().ToString("D"),keys);
        }
        var reply=new BattleRunningWork{Scope=scope.Clone(),StartDigest=ByteString.CopyFrom(battle.StartDigest!),Position=journal.Position,DeliveryId=delivery?.Id??""};
        if(delivery is not null)foreach(var key in delivery.Keys)
        {
            var command=journal.Read(key.Side,key.Sequence);if(command.Acknowledgement is not null)continue;
            reply.Commands.Add(new BattleQueuedCommand{Side=(BattleFighterSide)command.Side,Sequence=command.Sequence,Command=ByteString.CopyFrom(command.Command),
                CommandDigest=ByteString.CopyFrom(SHA256.HashData(command.Command)),GrantId=command.Control!.GrantId,ControlEpoch=command.Control.Epoch,
                ConnectionEpoch=command.Control.ConnectionEpoch,DeliveryId=delivery.Id});
        }
        foreach(int side in new[]{1,2})
        {
            var channel=journal.Channel(side);bool present=channel is not null && !journal.ChannelClosed(side,channel.Control.ConnectionEpoch);
            reply.Controls.Add(new BattleRuntimeControlState{Side=(BattleFighterSide)side,GrantId=channel?.Control.GrantId??"",ControlEpoch=channel?.Control.Epoch??0,
                ConnectionEpoch=channel?.Control.ConnectionEpoch??0,Present=present});
            if(journal.PendingCut(side) is {} cut)reply.Cuts.Add(new BattleControlCut{CutId=cut.Id,Side=(BattleFighterSide)side,ThroughConnectionEpoch=cut.ThroughConnectionEpoch,SequenceWatermark=cut.Watermark});
        }
        return reply;
    }
    internal async Task<BattleTransportPosition> AcknowledgeAsync(BattleExecutorCommandsAck request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>16384 || request.Executor is null || request.Receipts.Count>8 || request.AppliedCutIds.Count>2
            || request.Receipts.Select(r=>(r.Side,r.Sequence)).Distinct().Count()!=request.Receipts.Count || request.AppliedCutIds.Distinct().Count()!=request.AppliedCutIds.Count)throw Refused();
        request=request.Clone();var executor=BattleResultStaging.Lease(request.Executor);executors.RequireCurrent(executor);
        await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);await RequireBattleAsync(held,request.Scope,ct);RequireExecutor(executor);
        foreach(var receipt in request.Receipts)
        {
            _=BattleCommandContracts.ReadReceipt(receipt.ToByteArray());
            var command=cache.Journal.Read((int)receipt.Side,receipt.Sequence);
            if(command.DeliveryId is null || command.DeliveryId!=request.DeliveryId || !receipt.CommandDigest.Span.SequenceEqual(SHA256.HashData(command.Command))
                || receipt.HasAcceptedTick && receipt.AcceptedTick is <0 or >30000 || receipt.Disposition is not(BattleCommandDisposition.Executed or BattleCommandDisposition.Refused or BattleCommandDisposition.NotExecuted)
                || receipt.Disposition==BattleCommandDisposition.Executed && (!receipt.HasAcceptedTick || receipt.Reason!=BattleCommandReason.None)
                || receipt.Disposition==BattleCommandDisposition.NotExecuted && receipt.HasAcceptedTick || !Enum.IsDefined(receipt.Reason)
                || command.Acknowledgement is not null && !command.Acknowledgement.AsSpan().SequenceEqual(receipt.ToByteArray()))throw Refused();
        }
        foreach(string cut in request.AppliedCutIds)_=cache.Journal.ReadCut(cut);
        foreach(var receipt in request.Receipts)cache.Journal.Acknowledge((int)receipt.Side,receipt.Sequence,receipt.HasAcceptedTick?receipt.AcceptedTick:null,receipt.ToByteArray());
        foreach(string cut in request.AppliedCutIds)cache.Journal.AcknowledgeCut(cut);
        return new(){Position=cache.Journal.Position};
    }
    private async Task ReconcileHeldAsync(BattleTransportAdmission.Lease held,CancellationToken ct)
    {
        foreach(int side in new[]{1,2})
        {
            var channel=cache.Journal.Channel(side);if(channel is null || cache.Journal.ChannelClosed(side,channel.Control.ConnectionEpoch))continue;
            var grant=await held.Db.Set<GuildBattleControlGrant>().AsNoTracking().SingleOrDefaultAsync(g=>g.Id==channel.Control.GrantId,ct);
            bool alive=cache.Presence.TryGetValue((side,channel.Control.ConnectionEpoch),out long stamp)
                && time.GetElapsedTime(stamp,time.GetTimestamp()) is var elapsed && elapsed>=TimeSpan.Zero && elapsed<=TimeSpan.FromSeconds(30);
            if(!alive || grant is null || !await EligibleAsync(held.Db,grant,sessions.ByPlayer(channel.ActorId),ct))CloseChannel(channel);
        }
    }
    private void CloseChannel(BattleJournalChannel channel)
    {
        int side=channel.Side;if(cache.Journal.ChannelClosed(side,channel.Control.ConnectionEpoch))return;
        cache.Journal.BeginCut(side,channel.Control.ConnectionEpoch,Guid.NewGuid().ToString("D"));
        foreach(var command in cache.Journal.PendingCommands(side).Where(c=>c.DeliveryId is null))
        {
            var receipt=new BattleCommandReceipt{Side=(BattleFighterSide)side,Sequence=command.Sequence,CommandDigest=ByteString.CopyFrom(SHA256.HashData(command.Command)),
                Disposition=BattleCommandDisposition.NotExecuted,Reason=BattleCommandReason.ControlRevoked};
            cache.Journal.Acknowledge(side,command.Sequence,null,receipt.ToByteArray());
        }
    }
}
