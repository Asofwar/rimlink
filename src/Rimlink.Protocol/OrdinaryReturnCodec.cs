using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;
namespace Rimlink.Protocol;

/// <summary>The original command and complete physical return; wallet funds and guest escrow never become local goods.</summary>
public static class OrdinaryReturnCodec
{
    public static IMessage Original(OrdinaryCancelRequest request)
    {
        if (request?.Capture is null || request.OriginalRequest.Length is < 1 or > Limits.MaxMessageBytes) throw Invalid();
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(request.OriginalRequest.ToByteArray(), false), Limits.MaxMessageBytes, 32);
        return request.Capture.RequestKind switch {
            OrdinaryRequestKind.TransferSend => TransferRequest.Parser.ParseFrom(input),
            OrdinaryRequestKind.MarketPost => PostRequest.Parser.ParseFrom(input),
            OrdinaryRequestKind.TradeOpen => TradeOpenRequest.Parser.ParseFrom(input),
            OrdinaryRequestKind.TradeSide => TradeSideRequest.Parser.ParseFrom(input),
            OrdinaryRequestKind.RaidLaunch => LaunchRequest.Parser.ParseFrom(input),
            OrdinaryRequestKind.LivePropose => LiveProposal.Parser.ParseFrom(input),
            OrdinaryRequestKind.RaidReport or OrdinaryRequestKind.LiveReport => RaidReport.Parser.ParseFrom(input),
            _ => throw Invalid() };
    }
    public static OrdinaryReturnPayload Payload(OrdinaryCancelRequest request)
    {
        if (request is null || request.CalculateSize() > Limits.MaxMessageBytes || request.Capture is null || request.Scope is null
            || !request.Scope.Equals(request.Capture.Scope) || !Guid.TryParseExact(request.ReturnAttemptId, "D", out var id)
            || id == Guid.Empty || request.ReturnAttemptId != id.ToString("D")) throw Invalid();
        IMessage original = Original(request);
        if (!OrdinaryCapture.Create(original, request.Capture.LocalOperationId, request.Scope).Equals(request.Capture)
            || !SHA256.HashData(request.OriginalRequest.Span).AsSpan().SequenceEqual(request.Capture.RequestIntentSha256.Span)) throw Invalid();
        var result = original switch {
            TransferRequest r => new OrdinaryReturnPayload { Items = r.Items?.Clone() ?? new(), Pawns = r.Pawns?.Clone() ?? new() },
            PostRequest r => new OrdinaryReturnPayload { Items = r.Items?.Clone() ?? new(), Pawns = r.Pawn is null ? new() : new() { Pawns = { r.Pawn.Clone() } } },
            TradeOpenRequest r => new OrdinaryReturnPayload { Items = r.Items?.Clone() ?? new(), Pawns = r.Pawns?.Clone() ?? new() },
            TradeSideRequest r => new OrdinaryReturnPayload { Items = r.Items?.Clone() ?? new(), Pawns = r.Pawns?.Clone() ?? new() },
            LaunchRequest r => new OrdinaryReturnPayload { Items = new(), Pawns = r.Pawns?.Clone() ?? new() },
            LiveProposal r => new OrdinaryReturnPayload { Items = new(), Pawns = r.Pawns?.Clone() ?? new() },
            RaidReport r => new OrdinaryReturnPayload { Items = r.Loot?.Clone() ?? new(), Pawns = r.Captives?.Clone() ?? new() },
            _ => throw Invalid() };
        if (result.CalculateSize() > Limits.EconomyMaxParcelBytes) throw Invalid();
        return result;
    }
    private static InvalidDataException Invalid() => new("invalid ordinary return intent");
}
