using HarmonyLib;
using UnityEngine;
using Verse;

namespace Rimlink.Client;

/// <summary>Mod entry: settings (server list) and the settings window.</summary>
public sealed class RimlinkMod : Mod
{
    public static RimlinkSettings Settings { get; private set; } = null!;

    public RimlinkMod(ModContentPack content) : base(content)
    {
        // First: the mods below Rimlink read their settings in their own constructors, which run after this one.
        UI.ServerSettings.Load();
        Settings = GetSettings<RimlinkSettings>();
        UI.ModpackActions.StartScan();
    }

    public override string SettingsCategory() => "Rimlink";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);
        listing.Label("Rimlink.Settings.Servers".Translate(Settings.Servers.Count));
        listing.End();
    }
}

public sealed class RimlinkSettings : ModSettings
{
    /// <summary>Saved servers as "name|host|port"; edited from the multiplayer window.</summary>
    public List<string> Servers = [];

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref Servers, "servers", LookMode.Value);
        Servers ??= [];
    }
}

[StaticConstructorOnStartup]
public static class RimlinkStartup
{
    public static readonly Harmony Harmony = new(ClientInfo.ModPackageId);

    static RimlinkStartup()
    {
        Harmony.PatchAll();
        Log.Message($"[Rimlink] loaded v{ClientInfo.Version}, protocol {ClientInfo.ProtocolVersion}");
    }
}
