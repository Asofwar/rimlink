using System.Reflection;
using RimWorld;
using Verse;
using ZstdSharp;

namespace Rimlink.Client.Game;

/// <summary>The world's storyteller, difficulty and (Custom) difficulty values, which every colony plays with when the server locks them.</summary>
public sealed record ServerStorytellerSettings(StorytellerDef Def, DifficultyDef Difficulty, Difficulty? Values);

/// <summary>
/// Storyteller lock (integrity spec §6): the server's <c>WorldParams</c> storyteller and difficulty hold on the new-colony page,
/// on the in-game storyteller page and on every load of a colony. The admin's <see cref="Difficulty"/> travels as zstd Scribe XML.
/// Main thread only.
/// </summary>
public static class ServerStoryteller
{
    private const string Node = "difficulty";
    private static readonly FieldInfo[] ValueFields = typeof(Difficulty).GetFields(BindingFlags.Public | BindingFlags.Instance);
    private static Google.Protobuf.ByteString? cachedBytes;
    private static Difficulty? cachedValues;
    private static string? warned;

    /// <summary>Null when nothing is locked: no session, no world yet, Free policy, or a def this modlist does not have.</summary>
    public static ServerStorytellerSettings? Current()
    {
        if (!RimlinkSession.Active || RimlinkSession.World is not { Exists: true, StorytellerLocked: true } world) return null;
        var p = world.Params;
        var def = DefDatabase<StorytellerDef>.GetNamedSilentFail(p.StorytellerDef);
        var difficulty = DefDatabase<DifficultyDef>.GetNamedSilentFail(p.DifficultyDef);
        if (def is null || difficulty is null)
        {
            string key = p.StorytellerDef + "/" + p.DifficultyDef;
            if (warned != key) Log.Warning($"[Rimlink] the server's storyteller {p.StorytellerDef} / {p.DifficultyDef} is not loaded here; it is not enforced");
            warned = key;
            return null;
        }
        Difficulty? values = p.DifficultyValues.Length > 0 ? Values(p.DifficultyValues) : difficulty.isCustom ? null : new Difficulty(difficulty);
        return new ServerStorytellerSettings(def, difficulty, values);
    }

    /// <summary>Puts the server's settings on <paramref name="target"/>; true when anything changed (the costs are recached then).</summary>
    public static bool Apply(Storyteller target, ServerStorytellerSettings s)
    {
        bool changed = false;
        if (target.def != s.Def)
        {
            target.def = s.Def;
            target.Notify_DefChanged();
            changed = true;
        }
        if (target.difficultyDef != s.Difficulty) { target.difficultyDef = s.Difficulty; changed = true; }
        if (s.Values is { } values)
        {
            target.difficulty ??= new Difficulty();
            changed |= CopyValues(values, target.difficulty);
        }
        if (changed) RecacheDifficultyCosts();
        return changed;
    }

    /// <summary>Copies every value field; true when one differed. The cached instance is never handed out, so pages cannot edit it.</summary>
    public static bool CopyValues(Difficulty from, Difficulty to)
    {
        bool changed = false;
        foreach (var f in ValueFields)
        {
            object? a = f.GetValue(from), b = f.GetValue(to);
            if (Equals(a, b)) continue;
            f.SetValue(to, a);
            changed = true;
        }
        return changed;
    }

    /// <summary>As the vanilla in-game storyteller page does on close: costs and recipes that depend on the difficulty.</summary>
    public static void RecacheDifficultyCosts()
    {
        foreach (var def in DefDatabase<ThingDef>.AllDefs.Where(d => d.costListForDifficulty != null)) def.costListForDifficulty.RecacheApplies();
        RecipeDefGenerator.ResetRecipeIngredientsForDifficulty();
    }

    /// <summary>The admin's values when the world is created (<see cref="WorldSetup.CollectParams"/>).</summary>
    public static byte[] Serialize(Difficulty difficulty)
    {
        string path = TempPath();
        try
        {
            Scribe.saver.InitSaving(path, "rimlinkDifficulty");
            try { Scribe_Deep.Look(ref difficulty, Node); }
            finally { Scribe.saver.FinalizeSaving(); }
            using var compressor = new Compressor(3);
            return compressor.Wrap(File.ReadAllBytes(path)).ToArray();
        }
        finally { File.Delete(path); }
    }

    private static Difficulty? Values(Google.Protobuf.ByteString bytes)
    {
        if (cachedBytes == bytes) return cachedValues;
        string path = TempPath();
        try
        {
            using (var decompressor = new Decompressor()) File.WriteAllBytes(path, decompressor.Unwrap(bytes.ToByteArray()).ToArray());
            Difficulty? loaded = null;
            Scribe.loader.InitLoading(path);
            try { Scribe_Deep.Look(ref loaded, Node); }
            finally { Scribe.loader.FinalizeLoading(); }
            cachedBytes = bytes;
            cachedValues = loaded;
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Warning("[Rimlink] the server's difficulty values could not be read; only the difficulty is enforced: " + ex.Message);
            cachedBytes = bytes;
            cachedValues = null;
            return null;
        }
        finally { File.Delete(path); }
    }

    private static string TempPath()
    {
        string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink", "blobs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".xml");
    }
}
