using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleRunningInboxTests
{
    private static BattleRunningWork Work(BattlePreparedManifestV1 start,long position)
    {
        var value=new BattleRunningWork{Scope=new(){WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence},
            StartDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray())),Position=position,DeliveryId=Guid.NewGuid().ToString("D")};
        byte[] command=new BattleCommandV1{Version=1,FighterId=start.Entities[0].FighterId,Draft=true}.ToByteArray();
        value.Commands.Add(new BattleQueuedCommand{Side=BattleFighterSide.Attacker,Sequence=1,Command=ByteString.CopyFrom(command),CommandDigest=ByteString.CopyFrom(SHA256.HashData(command)),
            GrantId=start.Attacker.ControlGrantId,ControlEpoch=1,ConnectionEpoch=1,DeliveryId=value.DeliveryId});
        value.Controls.Add(new BattleRuntimeControlState{Side=BattleFighterSide.Attacker,GrantId=start.Attacker.ControlGrantId,ControlEpoch=1,ConnectionEpoch=1,Present=true});
        value.Controls.Add(new BattleRuntimeControlState{Side=BattleFighterSide.Defender});return value;
    }
    [Fact]
    public void Delivery_then_cut_then_projection_run_sequentially_and_old_replies_cannot_restore_control()
    {
        var start=BattleManifestContractsTests.Manifest();var inbox=new BattleRunningInbox(start);var events=new List<string>();var work=Work(start,3);
        work.Controls[0].Present=false;work.Cuts.Add(new BattleControlCut{CutId=Guid.NewGuid().ToString("D"),Side=BattleFighterSide.Attacker,ThroughConnectionEpoch=1,SequenceWatermark=1});
        Assert.True(inbox.Apply(work,_=>events.Add("dispatch"),_=>events.Add("cut"),_=>events.Add("projection")));
        Assert.Equal(new[]{"dispatch","cut","projection"},events);events.Clear();
        Assert.False(inbox.Apply(Work(start,2),_=>events.Add("stale"),_=>events.Add("stale"),_=>events.Add("stale")));Assert.Empty(events);
        Assert.True(inbox.Apply(work,_=>events.Add("retry"),_=>{},_=>{}));Assert.Single(events);
        var conflict=work.Clone();conflict.Controls[0].Present=true;Assert.Throws<InvalidDataException>(()=>inbox.Apply(conflict,_=>{},_=>{},_=>{}));
    }
    [Theory]
    [InlineData("digest")][InlineData("scope")][InlineData("delivery")][InlineData("command")][InlineData("cut")]
    public void Every_field_is_validated_before_any_game_callback(string damage)
    {
        var start=BattleManifestContractsTests.Manifest();var inbox=new BattleRunningInbox(start);var work=Work(start,1);int calls=0;
        switch(damage){case "digest":work.StartDigest=ByteString.CopyFrom(new byte[32]);break;case "scope":work.Scope.Attempt++;break;
            case "delivery":work.Commands[0].DeliveryId=Guid.NewGuid().ToString("D");break;case "command":work.Commands[0].CommandDigest=ByteString.CopyFrom(new byte[32]);break;
            case "cut":work.Cuts.Add(new BattleControlCut{CutId=Guid.NewGuid().ToString("D"),Side=BattleFighterSide.Attacker,ThroughConnectionEpoch=1,SequenceWatermark=1});break;}
        Assert.Throws<InvalidDataException>(()=>inbox.Apply(work,_=>calls++,_=>calls++,_=>calls++));Assert.Equal(0,calls);
    }
    [Fact]
    public void A_partial_native_callback_failure_requires_recovery_instead_of_reapplying_a_batch()
    {
        var start=BattleManifestContractsTests.Manifest();var inbox=new BattleRunningInbox(start);var work=Work(start,1);
        Assert.Throws<IOException>(()=>inbox.Apply(work,_=>throw new IOException("native journal unavailable"),_=>{},_=>{}));
        Assert.Throws<InvalidDataException>(()=>inbox.Apply(work,_=>{},_=>{},_=>{}));
    }
}
