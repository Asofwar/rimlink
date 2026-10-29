namespace Rimlink.Client.Mods;

/// <summary>
/// Which settings file a mod class uses this launch. Mods of the active set use the server's copy, except those whose Mod class
/// was created before Rimlink's: they read the player's own file before the redirect existed, so they keep it for later writes and
/// for the hash sent at login (which then differs, and "Fix and restart" puts Rimlink before them).
/// </summary>
public sealed class SettingsRedirect(ServerSettingsStore store, ServerSettingsSet set, IReadOnlyCollection<SettingsKey> readBeforeRimlink)
{
    private readonly HashSet<SettingsKey> keys = set.Mods.ToHashSet();
    private readonly HashSet<SettingsKey> early = readBeforeRimlink.ToHashSet();

    public ServerSettingsSet Set => set;

    public string Resolve(SettingsKey key, string original) =>
        keys.Contains(key) && !early.Contains(key) ? store.PathFor(set.Server, key) : original;
}
