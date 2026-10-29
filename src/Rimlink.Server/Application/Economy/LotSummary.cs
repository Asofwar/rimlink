using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Economy;

/// <summary>One line naming what a lot holds, for admin lists and the admin feed: "Steel ×50, Component ×3" or the pawn's label.</summary>
public static class LotSummary
{
    public const int MaxLength = 200;

    public static string Of(ItemList? items, PawnBlob? pawn)
    {
        string text = items is { Items.Count: > 0 }
            ? string.Join(", ", items.Items.Select(Describe))
            : pawn is null ? "" : string.IsNullOrEmpty(pawn.Label) ? pawn.Name : pawn.Label;
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    private static string Describe(ItemBlob item)
    {
        string label = string.IsNullOrEmpty(item.Label) ? item.DefName : item.Label;
        return item.Count == 1 ? label : $"{label} ×{item.Count}";
    }
}
