using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Economy;

public sealed record ParcelRefusal(string Key, string Message, params object[] Args)
{
    // Keep the original CodeFor mapping even when a descriptor contains this phrase.
    public ErrorCode Code => Message.Contains("not in the server modpack") ? ErrorCode.DefNotInModpack : ErrorCode.ParcelTooBig;
}

/// <summary>Size and descriptor checks on item/pawn lists; the blobs themselves are never opened.</summary>
public sealed class ParcelValidator(IOptions<EconomyOptions> options, ModpackDefs defs)
{
    /// <summary>Null when acceptable, otherwise the typed, localizable refusal.</summary>
    public ParcelRefusal? Validate(ItemList? items, PawnList? pawns)
    {
        var o = options.Value;
        int itemCount = items?.Items.Count ?? 0, pawnCount = pawns?.Pawns.Count ?? 0;
        if (itemCount > o.MaxItemsPerParcel) return new(RefusalKeys.ParcelTooManyItems, $"too many item stacks ({itemCount} > {o.MaxItemsPerParcel})", itemCount, o.MaxItemsPerParcel);
        if (pawnCount > o.MaxPawnsPerParcel) return new(RefusalKeys.ParcelTooManyPawns, $"too many pawns ({pawnCount} > {o.MaxPawnsPerParcel})", pawnCount, o.MaxPawnsPerParcel);
        if (SizeOf(items, pawns) > o.MaxParcelBytes) return new(RefusalKeys.ParcelTooBig, $"parcel exceeds {o.MaxParcelBytes} bytes", o.MaxParcelBytes);

        foreach (var item in items?.Items ?? [])
        {
            if (item.Count < 1) return new(RefusalKeys.ParcelItemCount, $"item '{item.DefName}' has count {item.Count}", item.DefName, item.Count);
            if (string.IsNullOrEmpty(item.DefName) || item.ScribeXmlZstd.Length == 0) return new(RefusalKeys.ParcelItemNoData, "item without def or data");
            if (!defs.IsEmpty && !defs.Contains(item.DefName)) return new(RefusalKeys.ParcelDefNotInModpack, $"def '{item.DefName}' is not in the server modpack", item.DefName);
            if (!defs.IsEmpty && item.StuffDef.Length > 0 && !defs.Contains(item.StuffDef)) return new(RefusalKeys.ParcelDefNotInModpack, $"def '{item.StuffDef}' is not in the server modpack", item.StuffDef);
        }
        foreach (var pawn in pawns?.Pawns ?? [])
        {
            if (string.IsNullOrEmpty(pawn.KindDef) || pawn.ScribeXmlZstd.Length == 0) return new(RefusalKeys.ParcelPawnNoData, "pawn without kind or data");
            if (!defs.IsEmpty && !defs.Contains(pawn.KindDef)) return new(RefusalKeys.ParcelDefNotInModpack, $"def '{pawn.KindDef}' is not in the server modpack", pawn.KindDef);
        }
        return null;
    }

    public static int SizeOf(ItemList? items, PawnList? pawns) =>
        (items?.Items.Sum(i => i.ScribeXmlZstd.Length) ?? 0) + (pawns?.Pawns.Sum(p => p.ScribeXmlZstd.Length) ?? 0);
}
