namespace Rimlink.Server.Application.Modpack;

/// <summary>
/// ThingDef/PawnKindDef names of the server modpack, used to refuse item and pawn blobs the modpack cannot contain.
/// Empty = the check is off. Loaded from the modpack_defs table at startup and replaced on Modpack.SetDefs.
/// </summary>
public sealed class ModpackDefs
{
    private volatile HashSet<string> names = [];

    public bool IsEmpty => names.Count == 0;

    public int Count => names.Count;

    public bool Contains(string defName) => names.Contains(defName);

    public void Replace(IEnumerable<string> defNames) => names = new HashSet<string>(defNames, StringComparer.Ordinal);
}
