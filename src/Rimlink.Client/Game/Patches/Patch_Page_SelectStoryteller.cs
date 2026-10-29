using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>The new-colony storyteller page holds the server's storyteller and difficulty when the server locks them.</summary>
[HarmonyPatch(typeof(Page_SelectStoryteller))]
public static class Patch_Page_SelectStoryteller
{
    [HarmonyPrefix, HarmonyPatch(nameof(Page_SelectStoryteller.DoWindowContents))]
    public static void DoWindowContents(Page_SelectStoryteller __instance) => Hold(__instance);

    [HarmonyPostfix, HarmonyPatch(nameof(Page_SelectStoryteller.DoWindowContents))]
    public static void Label(Rect rect)
    {
        if (ServerStoryteller.Current() is not null) StorytellerLockLabel.Draw(rect);
    }

    /// <summary>Also right before the page builds the game's storyteller, so a click and Next in one frame cannot slip through.</summary>
    [HarmonyPrefix, HarmonyPatch("CanDoNext")]
    public static void CanDoNext(Page_SelectStoryteller __instance) => Hold(__instance);

    private static void Hold(Page_SelectStoryteller page)
    {
        if (ServerStoryteller.Current() is not { } s) return;
        var t = Traverse.Create(page);
        t.Field("storyteller").SetValue(s.Def);
        t.Field("difficulty").SetValue(s.Difficulty);
        if (s.Values is not { } values) return;
        var own = t.Field("difficultyValues").GetValue<Difficulty>();
        if (own is null) t.Field("difficultyValues").SetValue(own = new Difficulty());
        ServerStoryteller.CopyValues(values, own);
    }
}

/// <summary>Changing the storyteller in game is undone every frame when the server locks it.</summary>
[HarmonyPatch(typeof(Page_SelectStorytellerInGame), nameof(Page_SelectStorytellerInGame.DoWindowContents))]
public static class Patch_Page_SelectStorytellerInGame
{
    public static void Postfix(Rect rect)
    {
        if (ServerStoryteller.Current() is not { } s || Current.Game?.storyteller is not { } storyteller) return;
        ServerStoryteller.Apply(storyteller, s);
        StorytellerLockLabel.Draw(rect);
    }
}

internal static class StorytellerLockLabel
{
    public static void Draw(Rect rect)
    {
        Text.Font = GameFont.Small;
        // In the strip above the bottom buttons, as on the locked world page.
        GUI.color = new Color(1f, 0.85f, 0.4f);
        Widgets.Label(new Rect(rect.x, rect.yMax - 72f, rect.width, 24f), "Rimlink.Storyteller.Locked".Translate(RimlinkSession.Endpoint?.Display ?? ""));
        GUI.color = Color.white;
    }
}
