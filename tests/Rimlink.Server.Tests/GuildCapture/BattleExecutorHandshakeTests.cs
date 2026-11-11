using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleExecutorHandshakeTests
{
    private sealed class Clock:TimeProvider
    {internal long Tick;internal DateTimeOffset Utc=DateTimeOffset.Parse("2030-01-01T00:00:00Z");public override long GetTimestamp()=>Tick;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override DateTimeOffset GetUtcNow()=>Utc;}
    private static BattleExecutorHelloBindingV1 Binding()=>new(){Version=1,ExecutorId="arbiter-1",RuntimeInstanceId=Guid.NewGuid().ToString("D"),
        ProfileDigest=ByteString.CopyFrom(SHA256.HashData("profile"u8)),InventorySha256=ByteString.CopyFrom(SHA256.HashData("inventory"u8)),ExecutorBuildSha256=ByteString.CopyFrom(SHA256.HashData("build"u8)),
        Lane=BattleExecutorLane.Execution,Entry=new(){Version=1,AtEntry=true},WorldInstanceId="world",RestoreBoundary=Guid.NewGuid().ToString("D")};
    [Fact]
    public void Complete_is_once_and_lost_response_replays_the_same_lease_without_publishing_twice()
    {
        var time=new Clock();var cache=new BattleExecutorHandshakes(time);var binding=Binding();var challenge=cache.BeginHeld(binding,"credential-generation");int published=0;
        var lease=new BattleExecutorLeaseMessage{ExecutorId="arbiter-1",LeaseId=Guid.NewGuid().ToString("D"),Fence=1,ProfileDigest=binding.ProfileDigest};
        var complete=new BattleExecutorHelloComplete{Binding=binding.Clone(),ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,ServerBootId=challenge.ServerBootId};
        Assert.Equal(lease,cache.CompleteHeld(complete,"credential-generation",()=>{published++;return lease;}));
        time.Utc=time.Utc.AddYears(10);Assert.Equal(lease,cache.CompleteHeld(complete,"credential-generation",()=>throw new Exception("must not republish")));Assert.Equal(1,published);
        var returned=cache.CompleteHeld(complete,"credential-generation",()=>throw new Exception());returned.Fence=99;
        Assert.Equal(1,cache.CompleteHeld(complete,"credential-generation",()=>throw new Exception()).Fence);
    }
    [Theory]
    [InlineData("runtime")][InlineData("profile")][InlineData("build")][InlineData("inventory")][InlineData("lane")][InlineData("target")][InlineData("nonce")][InlineData("boot")][InlineData("credential")][InlineData("expired")]
    public void A_changed_binding_or_stale_challenge_cannot_publish_a_lease(string damage)
    {
        var time=new Clock();var cache=new BattleExecutorHandshakes(time);var binding=Binding();var challenge=cache.BeginHeld(binding,"generation");
        var complete=new BattleExecutorHelloComplete{Binding=binding.Clone(),ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,ServerBootId=challenge.ServerBootId};string generation="generation";
        switch(damage){case "runtime":complete.Binding.RuntimeInstanceId=Guid.NewGuid().ToString("D");break;case "profile":complete.Binding.ProfileDigest=ByteString.CopyFrom(new byte[32]);break;
            case "build":complete.Binding.ExecutorBuildSha256=ByteString.CopyFrom(new byte[32]);break;case "inventory":complete.Binding.InventorySha256=ByteString.CopyFrom(new byte[32]);break;
            case "lane":complete.Binding.Lane=BattleExecutorLane.RecoveryDelivery;break;case "target":complete.Binding.BattleId=Guid.NewGuid().ToString("D");break;
            case "nonce":complete.Nonce=ByteString.CopyFrom(new byte[32]);break;case "boot":complete.ServerBootId=Guid.NewGuid().ToString("D");break;
            case "credential":generation="revoked";break;case "expired":time.Tick+=TimeSpan.FromSeconds(31).Ticks;break;}
        Assert.Throws<InvalidDataException>(()=>cache.CompleteHeld(complete,generation,()=>throw new Exception("must not publish")));
    }
    [Fact]
    public void Courier_delivery_never_replaces_the_live_execution_process_or_releases_its_cleanup_hold()
    {
        var clock=new Clock();var binding=Binding();var registry=new BattleExecutorRegistry(clock,binding.ExecutorId,binding.ProfileDigest.ToByteArray());string boot=Guid.NewGuid().ToString("D");
        var execution=registry.RegisterExecutionAuthenticated(binding,boot);registry.HeartbeatAuthenticated(execution,true);registry.HoldCleanup(execution);
        var courier=binding.Clone();courier.RuntimeInstanceId=Guid.NewGuid().ToString("D");courier.Lane=BattleExecutorLane.RecoveryDelivery;
        courier.Entry=null;courier.BattleId=Guid.NewGuid().ToString("D");courier.Attempt=1;courier.Fence=1;courier.StartDigest=ByteString.CopyFrom(SHA256.HashData("start"u8));courier.AssignmentTransitionId=Guid.NewGuid().ToString("D");
        var delivery=registry.RegisterRecoveryAuthenticated(courier,boot);registry.RequireCurrent(execution);registry.RequireCurrent(delivery);Assert.Null(registry.Available());
        Assert.Equal(binding.RuntimeInstanceId,registry.BindingOf(execution)!.RuntimeInstanceId);Assert.Equal(courier.RuntimeInstanceId,registry.BindingOf(delivery)!.RuntimeInstanceId);
        Assert.Throws<InvalidDataException>(()=>registry.ReleaseCleanup(delivery));Assert.Throws<InvalidDataException>(()=>registry.RequireExecution(delivery));
        registry.HeartbeatAuthenticated(delivery,false);Assert.Null(registry.Available());
        var impostor=binding.Clone();impostor.RuntimeInstanceId=courier.RuntimeInstanceId;Assert.Throws<InvalidDataException>(()=>registry.RegisterExecutionAuthenticated(impostor,boot));
    }
    [Fact]
    public void The_same_live_process_can_replace_only_its_transport_lease_without_clearing_busy_or_cleanup()
    {
        var clock=new Clock();var binding=Binding();var registry=new BattleExecutorRegistry(clock,binding.ExecutorId,binding.ProfileDigest.ToByteArray());string boot=Guid.NewGuid().ToString("D");
        var old=registry.RegisterExecutionAuthenticated(binding,boot);registry.HeartbeatAuthenticated(old,true);registry.HoldCleanup(old);
        var next=registry.RegisterExecutionAuthenticated(binding,boot);Assert.NotEqual(old.LeaseId,next.LeaseId);Assert.True(next.Fence>old.Fence);
        Assert.Throws<InvalidDataException>(()=>registry.HeartbeatAuthenticated(old,false));Assert.Null(registry.Available());
        registry.HeartbeatAuthenticated(next,false);Assert.Null(registry.Available());registry.ReleaseCleanup(next);Assert.Equal(next,registry.Available());
    }
    [Theory]
    [InlineData("missing")][InlineData("not-entry")][InlineData("game")][InlineData("session")][InlineData("context")][InlineData("ticks")][InlineData("callbacks")]
    public void Idle_execution_requires_an_empty_native_Entry_before_admission(string damage)
    {
        var binding=Binding();switch(damage){case "missing":binding.Entry=null;break;case "not-entry":binding.Entry.AtEntry=false;break;case "game":binding.Entry.GameLoaded=true;break;
            case "session":binding.Entry.PlayerSessionLoaded=true;break;case "context":binding.Entry.BattleContextBound=true;break;case "ticks":binding.Entry.TickRuntimeBound=true;break;
            case "callbacks":binding.Entry.PendingWorkCallbacks=1;break;}
        Assert.Throws<InvalidDataException>(()=>new BattleExecutorHandshakes(new Clock()).BeginHeld(binding,"credential"));
    }
    [Fact]
    public void Legacy_health_preflight_cannot_replace_a_real_process_even_after_its_transport_expires()
    {
        var clock=new Clock();var binding=Binding();var registry=new BattleExecutorRegistry(clock,binding.ExecutorId,binding.ProfileDigest.ToByteArray());
        var preflight=registry.RegisterAuthenticated(binding.ExecutorId,binding.ProfileDigest.ToByteArray());
        var execution=registry.RegisterExecutionAuthenticated(binding,Guid.NewGuid().ToString("D"));Assert.Throws<InvalidDataException>(()=>registry.RequireCurrent(preflight));
        clock.Tick+=TimeSpan.FromSeconds(31).Ticks;Assert.Throws<InvalidDataException>(()=>registry.RegisterAuthenticated(binding.ExecutorId,binding.ProfileDigest.ToByteArray()));
    }
    [Fact]
    public void A_server_restart_cannot_consume_an_old_challenge_and_the_cache_is_bounded()
    {
        var time=new Clock();var cache=new BattleExecutorHandshakes(time);var binding=Binding();var challenge=cache.BeginHeld(binding,"generation");
        var complete=new BattleExecutorHelloComplete{Binding=binding.Clone(),ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,ServerBootId=challenge.ServerBootId};
        Assert.Throws<InvalidDataException>(()=>new BattleExecutorHandshakes(time).CompleteHeld(complete,"generation",()=>throw new Exception()));
        for(int i=1;i<32;i++)cache.BeginHeld(binding,"generation");Assert.Throws<InvalidDataException>(()=>cache.BeginHeld(binding,"generation"));
        time.Tick+=TimeSpan.FromSeconds(31).Ticks;Assert.NotNull(cache.BeginHeld(binding,"generation"));
    }
}
