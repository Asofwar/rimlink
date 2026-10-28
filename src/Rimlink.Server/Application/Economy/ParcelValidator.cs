using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Economy;

/// <summary>Size and descriptor checks on item/pawn lists; the blobs themselves are never opened.</summary>
public sealed class ParcelValidator(IOptions<EconomyOptions> options, ModpackDefs defs)
{
    /// <summary>Null when acceptable, otherwise the reason (see <see cref="CodeFor"/> for its error code).</summary>
    public string? Validate(ItemList? items, PawnList? pawns)
    {
        var o = options.Value;
        int itemCount = items?.Items.Count ?? 0, pawnCount = pawns?.Pawns.Count ?? 0;
        if (itemCount > o.MaxItemsPerParcel) return $"too many item stacks ({itemCount} > {o.MaxItemsPerParcel})";
        if (pawnCount > o.MaxPawnsPerParcel) return $"too many pawns ({pawnCount} > {o.MaxPawnsPerParcel})";
        if (SizeOf(items, pawns) > o.MaxParcelBytes) return $"parcel exceeds {o.MaxParcelBytes} bytes";

        foreach (var item in items?.Items ?? [])
        {
            if (item.Count < 1) return $"item '{item.DefName}' has count {item.Count}";
            if (string.IsNullOrEmpty(item.DefName) || item.ScribeXmlZstd.Length == 0) return "item without def or data";
            if (!defs.IsEmpty && !defs.Contains(item.DefName)) return $"def '{item.DefName}' is not in the server modpack";
            if (!defs.IsEmpty && item.StuffDef.Length > 0 && !defs.Contains(item.StuffDef)) return $"def '{item.StuffDef}' is not in the server modpack";
        }
        foreach (var pawn in pawns?.Pawns ?? [])
        {
            if (string.IsNullOrEmpty(pawn.KindDef) || pawn.ScribeXmlZstd.Length == 0) return "pawn without kind or data";
            if (!defs.IsEmpty && !defs.Contains(pawn.KindDef)) return $"def '{pawn.KindDef}' is not in the server modpack";
        }
        return null;
    }

    public static ErrorCode CodeFor(string reason) =>
        reason.Contains("not in the server modpack") ? ErrorCode.DefNotInModpack : ErrorCode.ParcelTooBig;

    public static int SizeOf(ItemList? items, PawnList? pawns) =>
        (items?.Items.Sum(i => i.ScribeXmlZstd.Length) ?? 0) + (pawns?.Pawns.Sum(p => p.ScribeXmlZstd.Length) ?? 0);
}
