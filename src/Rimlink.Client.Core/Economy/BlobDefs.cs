using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

/// <summary>
/// The defs a parcel's blobs declare (item def and stuff, pawn kind) that this game does not have: checked before a blob is
/// loaded, so a parcel from a game with mods this one lacks names what is missing instead of failing on an anonymous blob.
/// Defs deeper in a blob (a pawn's apparel, a packed building's contents) are not declared and are not seen here.
/// </summary>
public static class BlobDefs
{
    /// <summary>
    /// "label (defName)" for each missing item or pawn kind, the def name alone for a missing stuff; each def once (labelled as its
    /// first blob, since stacks of one def differ in quality and hit points), in blob order.
    /// </summary>
    public static IReadOnlyList<string> Missing(ItemList? items, PawnList? pawns, Func<string, bool> thingDefExists, Func<string, bool> pawnKindExists)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>();
        void Add(string def, string name)
        {
            if (seen.Add(def)) missing.Add(name);
        }

        foreach (var item in items?.Items ?? Enumerable.Empty<ItemBlob>())
        {
            if (item.DefName.Length > 0 && !thingDefExists(item.DefName)) Add(item.DefName, item.Label.Length > 0 ? $"{item.Label} ({item.DefName})" : item.DefName);
            if (item.StuffDef.Length > 0 && !thingDefExists(item.StuffDef)) Add(item.StuffDef, item.StuffDef);
        }
        foreach (var pawn in pawns?.Pawns ?? Enumerable.Empty<PawnBlob>())
        {
            if (pawn.KindDef.Length > 0 && !pawnKindExists(pawn.KindDef)) Add("kind:" + pawn.KindDef, pawn.Name.Length > 0 ? $"{pawn.Name} ({pawn.KindDef})" : pawn.KindDef);
        }
        return missing;
    }
}
