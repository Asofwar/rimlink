using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

/// <summary>Immutable ordinary transfer identity; never worker eligibility or a custody grant.</summary>
public static class OrdinaryCapture
{
    public static OrdinaryOutgoingCapture Create(IMessage request, string localOperationId, OutpostScopeMessage scope)
    {
        if (request.CalculateSize() > Limits.MaxMessageBytes) throw Invalid();
        var (kind, requestId, pawns) = Identity(request);
        if (pawns.CalculateSize() > Limits.EconomyMaxParcelBytes) throw Invalid();
        var capture = new OrdinaryOutgoingCapture { SchemaVersion = 1, Scope = scope.Clone(), LocalOperationId = localOperationId,
            RequestKind = kind, ClientRequestId = requestId, RequestIntentSha256 = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())) };
        foreach (var pawn in pawns.Pawns.Where(p => p.Registry is not null))
            capture.Pawns.Add(new OrdinaryPawnCapture { Pawn = pawn.Clone(), BlobSha256 = ByteString.CopyFrom(SHA256.HashData(pawn.ToByteArray())) });
        if (request is LiveProposal { Kind: LiveKind.LiveRaid } proposal)
            capture.DerivedRoute = new() { RequestKind = OrdinaryRequestKind.RaidLaunch, ClientRequestId = proposal.ClientRequestId,
                RequestIntentSha256 = ByteString.CopyFrom(SHA256.HashData(DerivedLiveRaid(proposal).ToByteArray())) };
        Validate(capture);
        return capture;
    }

    // The existing tagged live→raid mapping. Legacy untagged live: keys remain outside this codec.
    public static LaunchRequest DerivedLiveRaid(LiveProposal proposal) => new() { ClientRequestId = proposal.ClientRequestId,
        TargetPlayerId = proposal.HostPlayerId, PawnMode = PawnMode.PawnReal, Timing = proposal.TimingFallback,
        Arrival = proposal.Arrival, GameTick = proposal.GameTick, Pawns = proposal.Pawns.Clone() };

    public static (OrdinaryRequestKind Kind, string RequestId, PawnList Pawns) Identity(IMessage request) => request switch
    {
        TransferRequest r => (OrdinaryRequestKind.TransferSend, r.ClientRequestId, r.Pawns ?? new()),
        PostRequest r => (OrdinaryRequestKind.MarketPost, r.ClientRequestId, r.Pawn is null ? new() : new() { Pawns = { r.Pawn } }),
        TradeOpenRequest r => (OrdinaryRequestKind.TradeOpen, r.ClientRequestId, r.Pawns ?? new()),
        TradeSideRequest r => (OrdinaryRequestKind.TradeSide, r.ClientRequestId, r.Pawns ?? new()),
        LaunchRequest r => (OrdinaryRequestKind.RaidLaunch, r.ClientRequestId, r.Pawns ?? new()),
        LiveProposal r => (OrdinaryRequestKind.LivePropose, r.ClientRequestId, r.Pawns ?? new()),
        RaidReport r => (r.LiveSessionId > 0 && r.RaidId == 0 ? OrdinaryRequestKind.LiveReport : OrdinaryRequestKind.RaidReport,
            r.ClientRequestId, r.Captives ?? new()),
        _ => throw Invalid()
    };

    public static void Validate(OrdinaryOutgoingCapture capture)
    {
        if (capture.SchemaVersion != 1 || capture.CalculateSize() > OutpostRecoveryData.MaxMetadataBytes
            || !Scope(capture.Scope) || !GuidN(capture.LocalOperationId) || !GuidN(capture.ClientRequestId)
            || capture.RequestKind == OrdinaryRequestKind.Unspecified || !Enum.IsDefined(capture.RequestKind)
            || capture.RequestIntentSha256.Length != 32 || capture.Pawns.Count is < 1 or > OutpostRecoveryData.MaxEntries) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);
        var pawns = new PawnList();
        foreach (var item in capture.Pawns)
        {
            if (item.Pawn?.Registry is not { } registry || item.Pawn.ScribeXmlZstd.Length == 0
                || item.BlobSha256.Length != 32 || !SHA256.HashData(item.Pawn.ToByteArray()).AsSpan().SequenceEqual(item.BlobSha256.Span)
                || registry.SchemaVersion != 1 || !GuidD(registry.PawnCustodyId) || !ids.Add(registry.PawnCustodyId)
                || registry.AssignmentId.Length > 0 && !GuidD(registry.AssignmentId) || !capture.Scope.Equals(registry.Scope)
                || registry.Tombstone || registry.NonAuthoritativeMirror || !Text(registry.PawnReferenceToken, 256)
                || !references.Add(registry.PawnReferenceToken)) throw Invalid();
            pawns.Pawns.Add(item.Pawn);
        }
        if (pawns.CalculateSize() > Limits.EconomyMaxParcelBytes) throw Invalid();
        if (capture.DerivedRoute is { } derived && (capture.RequestKind != OrdinaryRequestKind.LivePropose
            || derived.RequestKind != OrdinaryRequestKind.RaidLaunch || derived.ClientRequestId != capture.ClientRequestId
            || derived.RequestIntentSha256.Length != 32 || derived.RequestIntentSha256.Equals(capture.RequestIntentSha256))) throw Invalid();
    }

    public static bool Matches(OrdinaryOutgoingCapture capture, OrdinaryRequestKind kind, string requestId, ReadOnlySpan<byte> digest)
    {
        Validate(capture);
        return capture.RequestKind == kind && capture.ClientRequestId == requestId && digest.SequenceEqual(capture.RequestIntentSha256.Span)
            || capture.DerivedRoute is { } derived && derived.RequestKind == kind && derived.ClientRequestId == requestId
                && digest.SequenceEqual(derived.RequestIntentSha256.Span);
    }

    private static bool GuidN(string id) => Guid.TryParseExact(id, "N", out var value) && id == value.ToString("N");
    private static bool GuidD(string id) => Guid.TryParseExact(id, "D", out var value) && id == value.ToString("D");
    private static bool Text(string value, int max) => value.Length is > 0 && value.Length <= max && !value.Any(char.IsControl);
    private static bool Scope(OutpostScopeMessage? scope) => scope is not null && Text(scope.WorldInstanceId, 128)
        && Text(scope.RestoreBoundary, 128) && scope.OwnerPlayerId > 0 && scope.SaveGeneration >= 0 && scope.SaveWriteEpoch >= 0;
    private static InvalidDataException Invalid() => new("invalid ordinary outgoing capture");
}
