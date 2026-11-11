using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleExecutorConnectionTests
{
    private sealed class Wire(Func<HttpRequestMessage,IMessage> reply) : HttpMessageHandler
    {
        public readonly List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Assert.Equal("RimlinkExecutor "+new string('s',32),Assert.Single(request.Headers.GetValues("authorization")));
            Assert.DoesNotContain(request.Headers.GetValues("authorization"),v=>v.StartsWith("Bearer ",StringComparison.Ordinal));
            Paths.Add(request.RequestUri!.AbsolutePath);byte[] bytes=reply(request).ToByteArray(),frame=new byte[bytes.Length+5];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1,4),bytes.Length);bytes.CopyTo(frame,5);
            var response=new HttpResponseMessage(HttpStatusCode.OK){Version=new(2,0),Content=new ByteArrayContent(frame)};
            response.Content.Headers.ContentType=new("application/grpc");response.TrailingHeaders.Add("grpc-status","0");return Task.FromResult(response);
        }
    }
    private static BattleExecutorSettings Settings()=>new(){ExecutorId="arbiter-1",WorldInstanceId="world",RestoreBoundary="7aa8a533-90c8-43ab-961f-84f1fb875e5a",Endpoint="https://localhost:27555",CertificateSha256=new string('a',64),ProfileDigest=new string('b',64)};
    private static BattleExecutorLeaseMessage Lease()=>new(){ExecutorId="arbiter-1",LeaseId=Guid.NewGuid().ToString("D"),Fence=1,ProfileDigest=ByteString.CopyFrom(Convert.FromHexString(new string('b',64)))};
    [Fact]
    public async Task Dedicated_channel_authenticates_without_creating_a_player_session_and_checks_every_returned_lease()
    {
        var lease=Lease();using var wire=new Wire(r=>r.RequestUri!.AbsolutePath.EndsWith("/Poll",StringComparison.Ordinal)?new BattleExecutorWorkReply{Lease=lease.Clone()}:lease);
        using var connection=new BattleExecutorConnection(Settings(),new string('s',32),new ConnectionTransport(new("http://localhost"),wire));
        await connection.Register(default);await connection.Heartbeat(false,default);var reply=await connection.Poll(default);
        Assert.Equal(lease,reply.Lease);Assert.Equal(3,wire.Paths.Count);Assert.All(wire.Paths,p=>Assert.StartsWith("/rimlink.v1.BattleExecutor/",p));
        var observed=connection.Lease!;observed.Fence=100;Assert.Equal(1,connection.Lease!.Fence);
        lease.Fence=2;await Assert.ThrowsAsync<InvalidDataException>(()=>connection.Heartbeat(false,default));
    }
    [Fact]
    public async Task Wrong_profile_or_world_cannot_install_work_in_another_game()
    {
        var lease=Lease();var work=new BattleExecutorWorkReply{Lease=lease.Clone(),Preparing=new(){Scope=new(){WorldInstanceId="other"}}};
        using var wire=new Wire(r=>r.RequestUri!.AbsolutePath.EndsWith("/Poll",StringComparison.Ordinal)?work:lease);
        using var connection=new BattleExecutorConnection(Settings(),new string('s',32),new ConnectionTransport(new("http://localhost"),wire));
        await connection.Register(default);await Assert.ThrowsAsync<InvalidDataException>(()=>connection.Poll(default));
        lease.ProfileDigest=ByteString.CopyFrom(new byte[32]);
        using var second=new BattleExecutorConnection(Settings(),new string('s',32),new ConnectionTransport(new("http://localhost"),wire));
        await Assert.ThrowsAsync<InvalidDataException>(()=>second.Register(default));Assert.Null(second.Lease);
    }
    [Theory]
    [InlineData("boundary")][InlineData("valid")][InlineData("digest")][InlineData("nonce")][InlineData("runtime")][InlineData("boot")][InlineData("lane")]
    public async Task Process_handshake_checks_the_challenge_and_the_exact_server_issued_binding(string damage)
    {
        var binding=new BattleExecutorHelloBindingV1{Version=1,ExecutorId="arbiter-1",RuntimeInstanceId=Guid.NewGuid().ToString("D"),WorldInstanceId="world",
            RestoreBoundary=Settings().RestoreBoundary,Lane=BattleExecutorLane.Execution,Entry=new(){Version=1,AtEntry=true},ProfileDigest=Lease().ProfileDigest,
            InventorySha256=ByteString.CopyFrom(SHA256.HashData("inventory"u8)),ExecutorBuildSha256=ByteString.CopyFrom(SHA256.HashData("build"u8))};
        if(damage=="boundary")binding.RestoreBoundary=Guid.NewGuid().ToString("D");
        var challenge=new BattleExecutorHelloChallenge{ChallengeId=Guid.NewGuid().ToString("D"),Nonce=ByteString.CopyFrom(SHA256.HashData("nonce"u8)),ServerBootId=Guid.NewGuid().ToString("D"),
            BindingDigest=ByteString.CopyFrom(Rimlink.Protocol.BattleExecutorHelloContracts.Digest(binding)),ValidForMs=30000};
        var lease=Lease();lease.Binding=binding.Clone();lease.ServerBootId=challenge.ServerBootId;lease.ValidForMs=30000;
        switch(damage){case "digest":challenge.BindingDigest=ByteString.CopyFrom(new byte[32]);break;case "nonce":challenge.Nonce=ByteString.Empty;break;
            case "runtime":lease.Binding.RuntimeInstanceId=Guid.NewGuid().ToString("D");break;case "boot":lease.ServerBootId=Guid.NewGuid().ToString("D");break;
            case "lane":lease.Binding.Lane=BattleExecutorLane.RecoveryDelivery;break;}
        using var wire=new Wire(r=>r.RequestUri!.AbsolutePath.EndsWith("/HelloBegin",StringComparison.Ordinal)?challenge:lease);
        using var connection=new BattleExecutorConnection(Settings(),new string('s',32),new ConnectionTransport(new("http://localhost"),wire));
        if(damage=="valid"){await connection.Register(binding,default);Assert.Equal(lease,connection.Lease);Assert.Equal(new[]{"/rimlink.v1.BattleExecutor/HelloBegin","/rimlink.v1.BattleExecutor/HelloComplete"},wire.Paths);}
        else{await Assert.ThrowsAsync<InvalidDataException>(()=>connection.Register(binding,default));Assert.Null(connection.Lease);}
    }
    [Fact]
    public void TLS_pin_is_fixed_and_never_trusts_first_seen_or_a_different_certificate()
    {
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=local-test",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        string pin=Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        Assert.True(BattleExecutorConnection.MatchesCertificate(certificate,pin));Assert.False(BattleExecutorConnection.MatchesCertificate(certificate,new string('0',64)));
        Assert.False(BattleExecutorConnection.MatchesCertificate(null,pin));
    }
    [Theory]
    [InlineData("valid")][InlineData("lease")][InlineData("digest")][InlineData("elapsed")][InlineData("position")]
    public async Task RuntimeStarted_opens_only_the_same_lease_and_exact_native_proof(string damage)
    {
        var lease=Lease();lease.StartReplayOnly=true;var start=BattleManifestContractsTests.Manifest();
        var proof=new BattleRuntimeStartedProofV1{Version=1,Scope=new(){WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence},
            RuntimeInstanceId=start.RuntimeInstanceId,AssignmentTransitionId=start.PreparationTransitionId,StartDigest=ByteString.CopyFrom(new byte[32]),StartRequestId=Guid.NewGuid().ToString("D"),
            InitialSnapshot=start.InitialSnapshot.Clone(),InitialTick=start.InitialTick,GameLoaded=true,TickRuntimeBound=true,ReceiptJournalSha256=ByteString.CopyFrom(new byte[32]),ReceiptJournalBytes=40};
        var active=lease.Clone();active.StartReplayOnly=false;var reply=new BattleRuntimeStartedReply{Executor=active,ProofDigest=ByteString.CopyFrom(SHA256.HashData(proof.ToByteArray())),Position=1,ElapsedSinceStartMs=15000};
        switch(damage){case "lease":reply.Executor.LeaseId=Guid.NewGuid().ToString("D");break;case "digest":reply.ProofDigest=ByteString.CopyFrom(new byte[32]);break;case "elapsed":reply.ElapsedSinceStartMs=-1;break;case "position":reply.Position=0;break;}
        using var wire=new Wire(r=>r.RequestUri!.AbsolutePath.EndsWith("/RuntimeStarted",StringComparison.Ordinal)?reply:lease);
        using var connection=new BattleExecutorConnection(Settings(),new string('s',32),new ConnectionTransport(new("http://localhost"),wire));await connection.Register(default);
        if(damage=="valid"){Assert.Equal(reply,await connection.RuntimeStarted(proof,default));Assert.False(connection.Lease!.StartReplayOnly);await connection.Heartbeat(true,default);Assert.False(connection.Lease!.StartReplayOnly);}
        else{await Assert.ThrowsAsync<InvalidDataException>(()=>connection.RuntimeStarted(proof,default));Assert.Equal(lease,connection.Lease);}
    }
}
