using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

/// <summary>One battle and one authentication. Request IDs and capability bytes survive replies lost on this link.</summary>
public sealed class BattleCommanderLink
{
    private readonly RimlinkConnection connection;
    private readonly LoginReply login;
    private readonly BattlePreparedManifestV1 start;
    private readonly BattleScopeMessage scope;
    private readonly BattleFighterSide side;
    private readonly ByteString digest;
    private readonly ByteString capability = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
    private readonly string offerId = Id(), readyId = Id(), grantId = Id(), attachId = Id(), channelId = Id();
    private BattleControlAccess? access;
    private bool offerAcknowledged;
    public bool Current => ReferenceEquals(connection.Login, login) && connection.State == ConnectionState.Connected;
    public long ChannelEpoch => access?.ConnectionEpoch ?? 0;

    public BattleCommanderLink(RimlinkConnection connection, BattleViewerMarkerV1 marker, BattlePreparedManifestV1 start, BattleFighterSide side, bool startCommitted = false)
    {
        this.connection = connection; login = connection.Login ?? throw Invalid();
        this.start = start.Clone(); scope = marker.Battle.Clone(); this.side = side; digest = marker.ManifestDigest;
        var home = marker.Home.Scope;
        bool aiHandoff = startCommitted && side == BattleFighterSide.Defender && start.Defender.DefenderAi;
        if (login.GuildVersion != GuildResourceContracts.CurrentVersion || login.Player.PlayerId != home.PlayerId
            || login.SaveGeneration != home.SaveGeneration || login.SaveWriteEpoch != home.SaveWriteEpoch
            || connection.Endpoint.Id != marker.EndpointId || login.OutpostRestoreBoundary != home.RestoreBoundary
            || scope.WorldInstanceId != start.WorldInstanceId || scope.RestoreBoundary != start.RestoreBoundary || scope.BattleId != start.BattleId
            || scope.Attempt != start.Attempt || scope.Fence != start.Fence || side is not (BattleFighterSide.Attacker or BattleFighterSide.Defender)
            || !aiHandoff && (side == BattleFighterSide.Attacker ? start.Attacker.PlayerId : start.Defender.PlayerId) != home.PlayerId
            || !digest.Span.SequenceEqual(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest, start.ToByteArray()))) throw Invalid();
    }

    public async Task Ready(CancellationToken ct)
    {
        if (side != BattleFighterSide.Attacker) throw Invalid();
        if (access is not null) return;
        if (!offerAcknowledged)
        {
            var offer = await Call(d => connection.GuildCaptureApi.ReadReadyOfferAsync(new() { ClientRequestId = offerId, Scope = scope },
                deadline: d, cancellationToken: ct).ResponseAsync, ct);
            if (!scope.Equals(offer.Scope) || !digest.Equals(offer.ManifestDigest) || !offer.Manifest.Span.SequenceEqual(start.ToByteArray())
                || offer.ProposalSequence != start.ProposalSequence || !Guid.TryParseExact(offer.DeliveryId, "D", out _) || offer.Nonce.Length != 32)
                throw Invalid();
            var ack = await Call(d => connection.GuildCaptureApi.AcknowledgeReadyOfferAsync(new() { Scope = scope, DeliveryId = offer.DeliveryId, Nonce = offer.Nonce },
                deadline: d, cancellationToken: ct).ResponseAsync, ct);
            if (ack.DeliveryId != offer.DeliveryId || ack.ReceivedAtUtcTicks <= 0) throw Invalid();
            offerAcknowledged = true;
        }
        var reply = await Call(d => connection.GuildCaptureApi.AttackerReadyAsync(new() { ClientRequestId = readyId, Scope = scope,
            ProposalSequence = start.ProposalSequence, ManifestDigest = digest, CapabilityDigest = CapabilityDigest() },
            deadline: d, cancellationToken: ct).ResponseAsync, ct);
        if (!reply.ManifestDigest.Equals(digest) || reply.ControlEpoch != start.ProposalSequence) throw Invalid();
        SetAccess(reply.GrantId);
    }

    public async Task Attach(long priorChannelEpoch, CancellationToken ct)
    {
        if (access is null)
        {
            var grant = await Call(d => connection.GuildCaptureApi.ReconcileControlAsync(new() { ClientRequestId = grantId, Scope = scope,
                StartDigest = digest, CapabilityDigest = CapabilityDigest() }, deadline: d, cancellationToken: ct).ResponseAsync, ct);
            if (grant.Side != side || grant.Epoch <= 0 || !grant.StartDigest.Equals(digest)) throw Invalid();
            SetAccess(grant.GrantId);
        }
        var request = new BattleAttachControlRequest { ClientRequestId = attachId, Access = access!.Clone() };
        if (priorChannelEpoch > 0) request.ExpectedPriorEpoch = priorChannelEpoch;
        var channel = await Call(d => connection.GuildCaptureApi.AttachControlAsync(request, deadline: d, cancellationToken: ct).ResponseAsync, ct);
        if (channel.Side != side || channel.ControlEpoch <= 0 || channel.ConnectionEpoch <= priorChannelEpoch) throw Invalid();
        access.ConnectionEpoch = channel.ConnectionEpoch;
    }

    public async Task<ByteString> Read(long after, CancellationToken ct)
    {
        var request = new BattleRunningViewRequest { Access = Attached(), AfterSequence = after };
        return (await Call(d => connection.GuildCaptureApi.ReadRunningViewAsync(request, deadline: d, cancellationToken: ct).ResponseAsync, ct)).View;
    }

    public async Task<BattleCompletedViewReply?> ReadResult(CancellationToken ct)
    {
        var reply = await Call(d => connection.GuildCaptureApi.ReadCompletedViewAsync(new() { Scope = scope, StartDigest = digest },
            deadline: d, cancellationToken: ct).ResponseAsync, ct);
        return BattleManifestContracts.ReadCompletedView(reply, start) is not null || reply.Abort is not null ? reply : null;
    }

    public async Task<BattleSubmitCommandReply> Send(BattleCommandV1 command, CancellationToken ct)
    {
        var request = new BattleSubmitCommandRequest { ClientRequestId = Id(), Access = Attached(), Command = command.ToByteString() };
        _ = BattleCommandContracts.Read(request.Command.Span, start, side);
        async Task<BattleSubmitCommandReply> Submit() => await Call(d => connection.GuildCaptureApi.SubmitCommandAsync(request,
            deadline: d, cancellationToken: ct).ResponseAsync, ct);
        BattleSubmitCommandReply reply;
        try { reply = await Submit(); }
        catch (RpcException ex) when (Current && ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        { reply = await Submit(); }
        if (reply.Side != side || reply.Sequence is <1 or >24000 || !reply.CommandDigest.Span.SequenceEqual(SHA256.HashData(request.Command.Span)))
            throw Invalid();
        return reply;
    }

    private async Task<T> Call<T>(Func<DateTime, Task<T>> rpc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Current) throw new OperationCanceledException("battle control authentication changed");
        var reply = await connection.ForAuthentication(login, () => rpc(DateTime.UtcNow.AddSeconds(8)));
        if (!Current) throw new OperationCanceledException("battle control authentication changed");
        return reply;
    }

    private ByteString CapabilityDigest() => ByteString.CopyFrom(SHA256.HashData(capability.Span));
    private BattleControlAccess Attached() => access is { ConnectionEpoch: >0 } ? access.Clone() : throw Invalid();
    private void SetAccess(string grant)
    {
        if (!Guid.TryParseExact(grant, "D", out var id) || id == Guid.Empty) throw Invalid();
        access = new() { Scope = scope.Clone(), StartDigest = digest, GrantId = grant, Capability = capability, ClientChannelId = channelId };
    }
    private static string Id() => Guid.NewGuid().ToString("D");
    private static InvalidDataException Invalid() => new("battle-commander-link-invalid");
}
