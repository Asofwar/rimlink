using Grpc.Core;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Raid = Rimlink.Server.Storage.Entities.Raid;

namespace Rimlink.Server.Application.Raids;

/// <summary>Checks a defender's report against the raid it answers: every raider accounted for, loot within the cap, defs in the modpack.</summary>
public sealed class RaidReportValidator(IOptions<RaidOptions> options, ParcelValidator parcels)
{
    /// <summary>Returns the loot's silver value; throws the refusal otherwise.</summary>
    public long Validate(Raid raid, RaidReport report)
    {
        int count = PawnList.Parser.ParseFrom(raid.Pawns).Pawns.Count;
        var seen = new HashSet<int>();
        var survivors = new PawnList();
        foreach (var o in report.Outcomes)
        {
            if (o.Index < 0 || o.Index >= count || !seen.Add(o.Index))
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"outcome index {o.Index} is out of range or repeated"));
            if (o.Outcome is PawnOutcome.OutcomeFled or PawnOutcome.OutcomeAlive && raid.PawnMode == (int)PawnMode.PawnReal)
            {
                if (o.Blob is null || o.Blob.ScribeXmlZstd.Length == 0)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"surviving raider {o.Index} has no blob"));
                survivors.Pawns.Add(o.Blob);
            }
        }
        if (seen.Count != count) throw new RpcException(new Status(StatusCode.InvalidArgument, $"{count - seen.Count} raiders have no outcome"));
        if (report.GameTick < (raid.LandedTick ?? 0))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "the report's save tick is before the landing"));
        int captives = report.Captives?.Pawns.Count ?? 0;
        if (captives > 0 && !options.Value.AllowKidnap) throw new RpcException(new Status(StatusCode.InvalidArgument, "kidnapping is disabled on this server"));
        if (captives > 1) throw new RpcException(new Status(StatusCode.InvalidArgument, "at most one captive per raid"));

        if (parcels.Validate(report.Loot, report.Captives) is { } reason) throw RpcErrors.Failed(ParcelValidator.CodeFor(reason), reason);
        if (survivors.Pawns.Count > 0 && parcels.Validate(null, survivors) is { } survivorReason) throw RpcErrors.Failed(ParcelValidator.CodeFor(survivorReason), survivorReason);

        // Hundredths of silver on both sides, the same integer formula the client trims with: no stack slips under the cap by rounding.
        long lootX100 = report.Loot?.Items.Sum(i => i.MarketValueX100 * i.Count) ?? 0;
        if (lootX100 > raid.LootCap * 100) throw RpcErrors.Failed(ErrorCode.LootOverCap, $"loot worth {lootX100 / 100.0:0.##} silver exceeds this raid's cap of {raid.LootCap}");
        return lootX100 / 100;
    }
}
