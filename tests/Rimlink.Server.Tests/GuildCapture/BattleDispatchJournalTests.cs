using System.Security.Cryptography;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleDispatchJournalTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"rimlink-dispatch-"+Guid.NewGuid().ToString("N"));
    private readonly BattleJournalScope scope=new(Guid.NewGuid().ToString("D"),"world",Guid.NewGuid().ToString("D"),"arbiter",1,SHA256.HashData("start"u8));
    private static BattleJournalControl Control(long connection=1,long epoch=1)=>new(Guid.NewGuid().ToString("D"),epoch,SHA256.HashData("session"u8),connection);
    private BattleCommandJournal Open()=>BattleCommandJournal.OpenHeld(root,scope);
    private string PathOf()=>Path.Combine(root,scope.BattleId+".commands");
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}

    [Fact]
    public void Controlled_command_binds_immutable_grant_session_and_channel_before_returning_a_sequence()
    {
        var journal=Open();var control=Control();string request=Guid.NewGuid().ToString("D");
        Assert.Equal(1,journal.AppendControlled(1,10,request,"move"u8,control));
        var reopened=Open();Assert.Equal(1,reopened.AppendControlled(1,10,request,"move"u8,control));
        Assert.Throws<InvalidDataException>(()=>reopened.AppendControlled(1,10,request,"move"u8,control with{Epoch=2}));
        Assert.Throws<InvalidDataException>(()=>reopened.Append(1,10,request,"move"u8));
        var value=reopened.Read(1,1);Assert.Equal(control.GrantId,value.Control!.GrantId);Assert.Equal(control.SessionDigest,value.Control.SessionDigest);
        value.Control.SessionDigest[0]^=1;Assert.Equal(control.SessionDigest,reopened.Read(1,1).Control!.SessionDigest);
        using var file=File.Open(PathOf(),FileMode.Open,FileAccess.Read,FileShare.None);
    }
    [Fact]
    public void Dispatch_is_durable_before_reply_and_a_lost_reply_replays_the_exact_batch()
    {
        var journal=Open();journal.AppendControlled(1,10,Guid.NewGuid().ToString("D"),"move"u8,Control());
        journal.AppendControlled(2,20,Guid.NewGuid().ToString("D"),"draft"u8,Control());
        string id=Guid.NewGuid().ToString("D");BattleJournalKey[] keys=[new(1,1),new(2,1)];
        journal.Dispatch(id,keys);var reopened=Open();Assert.Equal(id,reopened.Read(1,1).DeliveryId);
        Assert.Equal(keys,reopened.Dispatch(id,keys).Keys);
        Assert.Throws<InvalidDataException>(()=>reopened.Dispatch(id,[new(2,1),new(1,1)]));
        Assert.Throws<InvalidDataException>(()=>reopened.Dispatch(Guid.NewGuid().ToString("D"),[new(1,1)]));
        reopened.Acknowledge(1,1,40,"executed"u8);Assert.Equal(keys,Open().Dispatch(id,keys).Keys);
    }
    [Fact]
    public void Revoke_cannot_claim_that_an_already_dispatched_command_was_not_executed()
    {
        var journal=Open();journal.AppendControlled(1,10,Guid.NewGuid().ToString("D"),"move"u8,Control());
        journal.Dispatch(Guid.NewGuid().ToString("D"),[new(1,1)]);
        var cut=journal.BeginCut(1,1,Guid.NewGuid().ToString("D"));var reopened=Open();
        Assert.Null(reopened.Read(1,1).Acknowledgement);Assert.True(reopened.CutPending(1));
        Assert.Throws<InvalidDataException>(()=>reopened.AcknowledgeCut(cut.Id));Assert.Throws<InvalidDataException>(()=>reopened.Seal());
        reopened.Acknowledge(1,1,42,"executed-before-cut"u8);reopened.AcknowledgeCut(cut.Id);
        Assert.Equal(42,Open().Read(1,1).AcceptedTick);Assert.False(Open().CutPending(1));
        Assert.Equal(reopened.Seal().Digest,Open().ValidateSeal().Digest);
    }
    [Fact]
    public void New_channel_waits_for_cut_application_and_side_sequence_never_resets()
    {
        var journal=Open();var old=Control();string request=Guid.NewGuid().ToString("D");journal.AppendControlled(1,10,request,"move"u8,old);
        var cut=journal.BeginCut(1,1,Guid.NewGuid().ToString("D"));var next=Control(2,2);
        Assert.Throws<InvalidDataException>(()=>journal.AppendControlled(1,10,Guid.NewGuid().ToString("D"),"move"u8,next));
        Assert.Throws<InvalidDataException>(()=>journal.Dispatch(Guid.NewGuid().ToString("D"),[new(1,1)]));
        journal.Acknowledge(1,1,null,"not-executed-control-revoked"u8);journal.AcknowledgeCut(cut.Id);
        Assert.Equal(1,journal.AppendControlled(1,10,request,"move"u8,old));
        Assert.Throws<InvalidDataException>(()=>journal.AppendControlled(1,10,Guid.NewGuid().ToString("D"),"move"u8,old));
        Assert.Equal(2,journal.AppendControlled(1,10,Guid.NewGuid().ToString("D"),"move"u8,next));
    }
    [Fact]
    public void Durable_position_orders_replies_and_exact_replays_do_not_move_the_cut_backwards()
    {
        var journal=Open();Assert.Equal(0,journal.Position);
        var control=Control();string request=Guid.NewGuid().ToString("D");journal.AppendControlled(1,10,request,"move"u8,control);Assert.Equal(1,journal.Position);
        journal.Dispatch(Guid.NewGuid().ToString("D"),[new(1,1)]);Assert.Equal(2,journal.Position);
        var cut=journal.BeginCut(1,1,Guid.NewGuid().ToString("D"));Assert.Equal(3,journal.Position);
        journal.Acknowledge(1,1,42,"executed"u8);journal.AcknowledgeCut(cut.Id);Assert.Equal(5,journal.Position);
        journal.Seal();Assert.Equal(6,journal.Position);var reopened=Open();Assert.Equal(6,reopened.Position);
        Assert.Equal(1,reopened.AppendControlled(1,10,request,"move"u8,control));Assert.Equal(6,reopened.Position);
    }

    [Fact]
    public void One_channel_is_persisted_and_replacement_waits_for_the_old_cut()
    {
        var journal=Open();var control=Control();byte[] transport=SHA256.HashData("http2 connection"u8);string request=Guid.NewGuid().ToString("D"),client=Guid.NewGuid().ToString("D");
        var first=journal.AttachChannel(1,10,request,client,control.GrantId,control.Epoch,control.SessionDigest,transport,null);
        Assert.Equal(1,first.Control.ConnectionEpoch);Assert.Equal(first.ClientChannelId,Open().Channel(1)!.ClientChannelId);
        var replay=Open().AttachChannel(1,10,request,client,control.GrantId,control.Epoch,control.SessionDigest,transport,null);Assert.Equal(1,replay.Control.ConnectionEpoch);
        Assert.Throws<InvalidDataException>(()=>Open().AttachChannel(1,10,request,client,control.GrantId,control.Epoch,control.SessionDigest,SHA256.HashData("other socket"u8),null));
        Assert.Throws<InvalidDataException>(()=>journal.AttachChannel(1,10,Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),control.GrantId,2,control.SessionDigest,transport,1));
        var cut=journal.BeginCut(1,1,Guid.NewGuid().ToString("D"));journal.AcknowledgeCut(cut.Id);
        var next=journal.AttachChannel(1,10,Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),control.GrantId,2,control.SessionDigest,transport,1);
        Assert.Equal(2,next.Control.ConnectionEpoch);Assert.Equal(2,Open().Channel(1)!.Control.ConnectionEpoch);
        Assert.Equal(1,journal.AttachChannel(1,10,request,client,control.GrantId,control.Epoch,control.SessionDigest,transport,null).Control.ConnectionEpoch);
        Assert.Equal(2,journal.Channel(1)!.Control.ConnectionEpoch);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void A_torn_dispatch_or_cut_record_is_retained_for_recovery(bool torn)
    {
        var journal=Open();journal.AppendControlled(1,10,Guid.NewGuid().ToString("D"),"move"u8,Control());journal.Dispatch(Guid.NewGuid().ToString("D"),[new(1,1)]);
        journal.BeginCut(1,1,Guid.NewGuid().ToString("D"));byte[] bytes=File.ReadAllBytes(PathOf());if(torn)bytes=bytes[..^1];else bytes[^1]^=1;File.WriteAllBytes(PathOf(),bytes);
        Assert.Throws<InvalidDataException>(()=>Open());Assert.Equal(bytes,File.ReadAllBytes(PathOf()));
    }
}
