using HarmonyLib;
using Rimlink.Protocol.V1;
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
        listing.GapLine();
        listing.Label("Rimlink.Settings.LetterPause".Translate(), tooltip: "Rimlink.Settings.LetterPauseTip".Translate());
        foreach (var mode in new[] { LetterPause.Never, LetterPause.Major, LetterPause.Any })
            if (listing.RadioButton(("Rimlink.Settings.LetterPause." + mode).Translate(), Settings.LetterPause == mode) && Settings.LetterPause != mode)
                Game.Live.LiveTempo.ChangeOwnThreshold(mode);
        listing.End();
    }
}

public sealed class RimlinkSettings : ModSettings
{
    /// <summary>Saved servers as "name|host|port"; edited from the multiplayer window.</summary>
    public List<string> Servers = [];

    /// <summary>Which of the host's letters pause a live session for this player (live UX spec §4).</summary>
    public LetterPause LetterPause = LetterPause.Major;

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref Servers, "servers", LookMode.Value);
        Scribe_Values.Look(ref LetterPause, "letterPause", LetterPause.Major);
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
