using Rimlink.Protocol;

namespace Rimlink.Client.Mods;

/// <summary>
/// The running mods' <see cref="ModContentInfo"/>, scanned once in the background when the game starts. A mod whose folder
/// cannot be read is left out: it is reported without a files hash, so the server neither compares its files nor allows it.
/// </summary>
public sealed class ModContentCache
{
    private Task<IReadOnlyDictionary<string, ModContentInfo>> ready = Task.FromResult<IReadOnlyDictionary<string, ModContentInfo>>(new Dictionary<string, ModContentInfo>());

    public Task<IReadOnlyDictionary<string, ModContentInfo>> Ready => ready;

    public void Start(IEnumerable<(string PackageId, string Root)> mods)
    {
        var list = mods.ToList();
        ready = Task.Run<IReadOnlyDictionary<string, ModContentInfo>>(() =>
        {
            var result = new Dictionary<string, ModContentInfo>(StringComparer.Ordinal);
            foreach (var (id, root) in list)
            {
                try { result[id] = ModContent.Scan(root); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return result;
        });
    }
}
