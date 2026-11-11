using System.Net.Security;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Rimlink.Protocol;

namespace Rimlink.Client.GuildCapture;

public sealed class BattleExecutorConnection : IDisposable
{
    private readonly BattleExecutorSettings settings;
    private readonly GrpcChannel channel;
    private BattleExecutorLeaseMessage? lease;
    public BattleExecutorLeaseMessage? Lease => lease?.Clone();
    public Protocol.V1.BattleExecutor.BattleExecutorClient Api { get; }

    public BattleExecutorConnection(BattleExecutorSettings settings,string credential) : this(settings,credential,null) { }
    internal BattleExecutorConnection(BattleExecutorSettings settings,string credential,ConnectionTransport? transport)
    {
        this.settings=settings;
        if (credential.Length is <32 or >256 || credential.Any(c=>c is <'!' or >'~') || !Uri.TryCreate(settings.Endpoint,UriKind.Absolute,out var endpoint)
            || endpoint.Scheme!="https" || settings.ProfileDigest.Length!=64 || settings.CertificateSha256.Length!=64) throw Invalid();
        if(settings.AcceptanceStandId!="")new GuildAcceptanceSelection(settings.AcceptanceStandId,settings.CertificateSha256).RequireEndpoint(endpoint);
        var connection=transport??new ConnectionTransport(endpoint,new SocketsHttpHandler
        {
            AllowAutoRedirect=false,EnableMultipleHttp2Connections=false,ConnectTimeout=TimeSpan.FromSeconds(10),
            KeepAlivePingDelay=TimeSpan.FromSeconds(10),KeepAlivePingTimeout=TimeSpan.FromSeconds(5),
            SslOptions=new SslClientAuthenticationOptions { RemoteCertificateValidationCallback=(_,certificate,_,_)=>MatchesCertificate(certificate,settings.CertificateSha256) }
        });
        var authentication=CallCredentials.FromInterceptor((_,metadata)=> { metadata.Add("authorization","RimlinkExecutor "+credential);return Task.CompletedTask; });
        bool insecure=connection.Address.Scheme=="http";
        channel=GrpcChannel.ForAddress(connection.Address,new GrpcChannelOptions
        {
            HttpHandler=connection.Handler,DisposeHttpClient=true,
            Credentials=ChannelCredentials.Create(insecure?ChannelCredentials.Insecure:ChannelCredentials.SecureSsl,authentication),
            UnsafeUseInsecureChannelCallCredentials=insecure,MaxSendMessageSize=262144+16384,MaxReceiveMessageSize=1048576+16384
        });
        Api=new(channel);
    }
    public async Task Register(BattleExecutorHelloBindingV1 binding,CancellationToken ct)
    {
        if(lease is not null)throw Invalid();await Negotiate(binding,ct);
    }
    public async Task Reconnect(BattleExecutorHelloBindingV1 binding,CancellationToken ct)
    {
        if(lease?.Binding is not{} prior || prior.RuntimeInstanceId!=binding.RuntimeInstanceId || prior.Lane!=binding.Lane
            || prior.WorldInstanceId!=binding.WorldInstanceId || prior.RestoreBoundary!=binding.RestoreBoundary || !prior.ProfileDigest.Equals(binding.ProfileDigest))throw Invalid();
        await Negotiate(binding,ct);
    }
    private async Task Negotiate(BattleExecutorHelloBindingV1 binding,CancellationToken ct)
    {
        binding=binding.Clone();binding.GuildRuntime=settings.AcceptanceStandId==""?null:new GuildAcceptanceSelection(settings.AcceptanceStandId,settings.CertificateSha256).Target;
        binding=BattleExecutorHelloContracts.Read(binding.ToByteArray());
        if(binding.ExecutorId!=settings.ExecutorId || binding.WorldInstanceId!=settings.WorldInstanceId || binding.RestoreBoundary!=settings.RestoreBoundary
            || !binding.ProfileDigest.Span.SequenceEqual(Convert.FromHexString(settings.ProfileDigest)))throw Invalid();
        var challenge=await Api.HelloBeginAsync(binding,Options(ct));
        if(!Guid.TryParseExact(challenge.ChallengeId,"D",out var id) || id==Guid.Empty || !Guid.TryParseExact(challenge.ServerBootId,"D",out id) || id==Guid.Empty
            || challenge.Nonce.Length!=32 || challenge.ValidForMs is <1 or >30000 || !challenge.BindingDigest.Span.SequenceEqual(BattleExecutorHelloContracts.Digest(binding)))throw Invalid();
        if (challenge.LocalProcessRequired)
        {
            if (!OperatingSystem.IsWindows() || !new Uri(settings.Endpoint).IsLoopback) throw Invalid();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(challenge.ValidForMs));
            using var pipe = new NamedPipeClientStream(".", "Rimlink.Executor." + challenge.ChallengeId, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(deadline.Token);
            await pipe.WriteAsync(challenge.Nonce.Memory, deadline.Token);
            byte[] accepted = new byte[1];
            await pipe.ReadExactlyAsync(accepted, deadline.Token);
            if (accepted[0] != 1) throw Invalid();
        }
        var reply=await Api.HelloCompleteAsync(new(){Binding=binding,ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,ServerBootId=challenge.ServerBootId},Options(ct));
        RequireLease(reply,false);
        if(!binding.Equals(reply.Binding) || reply.ServerBootId!=challenge.ServerBootId || reply.ValidForMs!=30000 || reply.StartReplayOnly!=(binding.RetainedContext?.Phase==BattleExecutorRetainedPhase.StartAcknowledgementPending))throw Invalid();
        lease=reply.Clone();
    }
    // Legacy health preflight has no Execution binding and cannot receive Product work.
    public async Task Register(CancellationToken ct)
    {
        if (lease is not null || settings.AcceptanceStandId!="") throw Invalid();
        var reply=await Api.RegisterAsync(new(){ExecutorId=settings.ExecutorId,ProfileDigest=ByteString.CopyFrom(Convert.FromHexString(settings.ProfileDigest))},Options(ct));
        RequireLease(reply,false);lease=reply.Clone();
    }
    public async Task Heartbeat(bool hasUnfinishedProcess,CancellationToken ct)
    {
        var sent=Lease??throw Invalid();
        var reply=await Api.HeartbeatAsync(new(){Lease=sent,HasUnfinishedProcess=hasUnfinishedProcess},Options(ct));
        RequireLease(reply,false);var comparable=reply.Clone();comparable.StartReplayOnly=sent.StartReplayOnly;
        // A delayed heartbeat cannot downgrade or acknowledge RuntimeStarted, or replace a newer negotiated lease.
        if(!comparable.Equals(sent))throw Invalid();
    }
    public async Task<BattleRuntimeStartedReply> RuntimeStarted(BattleRuntimeStartedProofV1 proof,CancellationToken ct)
    {
        proof=BattleRuntimeStartedContracts.Read(proof.ToByteArray());var sent=Lease??throw Invalid();
        var reply=await Api.RuntimeStartedAsync(new(){Executor=sent,Proof=proof},Options(ct));var expected=sent.Clone();expected.StartReplayOnly=false;
        if(reply.Executor is null || !expected.Equals(reply.Executor) || reply.Position<=0 || reply.ElapsedSinceStartMs<0
            || !reply.ProofDigest.Span.SequenceEqual(SHA256.HashData(proof.ToByteArray())))throw Invalid();
        lease=reply.Executor.Clone();return reply;
    }
    public async Task<BattleExecutorWorkReply> Poll(CancellationToken ct)
    {
        var reply=await Api.PollAsync(new(){Lease=Lease??throw Invalid()},Options(ct));RequireLease(reply.Lease,true);
        if (reply.Preparing is { } work && work.Scope?.WorldInstanceId!=settings.WorldInstanceId) throw Invalid();
        return reply;
    }
    public CallOptions Options(CancellationToken ct)=>new(deadline:DateTime.UtcNow.AddSeconds(15),cancellationToken:ct);
    private void RequireLease(BattleExecutorLeaseMessage value,bool exact)
    {
        if (value is null || value.ExecutorId!=settings.ExecutorId || !Guid.TryParseExact(value.LeaseId,"D",out var id) || id==Guid.Empty || value.Fence<=0
            || !value.ProfileDigest.Span.SequenceEqual(Convert.FromHexString(settings.ProfileDigest)) || exact && !value.Equals(lease)) throw Invalid();
    }
    internal static bool MatchesCertificate(X509Certificate? certificate,string pin)
    {
        if (certificate is null || pin.Length!=64) return false;
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()),Convert.FromHexString(pin)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return false; }
    }
    public void Dispose()=>channel.Dispose();
    private static InvalidDataException Invalid()=>new("battle-executor-connection-identity-invalid");
}
