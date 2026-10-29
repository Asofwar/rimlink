using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>
/// The main-menu option lists as drawn: the bridge's layout snapshot does not see <see cref="OptionListingUtility"/>.
/// A postfix records every option drawn in a frame (by label; OnGUI draws each list more than once per frame).
/// </summary>
public static class MenuCapture
{
    private static readonly object Sync = new();
    private static bool patched;
    private static int frame = -1;
    private static List<ListableOption> current = [];
    private static List<ListableOption> last = [];
    private static int lastFrame = -1;

    /// <summary>Main thread. Patches once; the capture fills from the next drawn frame on.</summary>
    public static void Ensure()
    {
        if (patched) return;
        new Harmony("rimlink.bridgetools").Patch(
            AccessTools.Method(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing)),
            postfix: new HarmonyMethod(typeof(MenuCapture), nameof(Postfix)));
        patched = true;
    }

    public static void Postfix(List<ListableOption> optList)
    {
        if (Current.ProgramState != ProgramState.Entry) return;
        lock (Sync)
        {
            if (Time.frameCount != frame)
            {
                if (current.Count > 0) { last = current; lastFrame = frame; }
                current = [];
                frame = Time.frameCount;
            }
            foreach (var option in optList)
                if (!current.Any(o => o.label == option.label)) current.Add(option);
        }
    }

    /// <summary>The options of the last completely drawn frame, or empty when the menu has not been drawn for a while.</summary>
    public static IReadOnlyList<ListableOption> Last
    {
        get
        {
            lock (Sync) return lastFrame >= Time.frameCount - 3 ? last : [];
        }
    }
}

/// <summary>The new-colony (and any other) <see cref="Page"/>: its Next/Back are protected, so they are invoked by reflection.</summary>
public static class Pages
{
    /// <summary>Main thread.</summary>
    public static Page? Top() => Find.WindowStack.Windows.OfType<Page>().LastOrDefault();

    public static bool CanBack(Page page) => (bool)AccessTools.Method(page.GetType(), "CanDoBack").Invoke(page, null)!;

    /// <summary>What the Next button does. A refusal is reported by the page as a message; the newest one is returned.</summary>
    public static bool TryNext(Page page, out string? refusal)
    {
        refusal = null;
        if (page is Page_CreateWorldParams)
        {
            // Its CanDoNext queues the generation as a long event and returns false; the page closes itself when it is done.
            AccessTools.Method(page.GetType(), "CanDoNext").Invoke(page, null);
            return true;
        }
        int before = GameMessages.Frame;
        if ((bool)AccessTools.Method(page.GetType(), "CanDoNext").Invoke(page, null)!)
        {
            AccessTools.Method(page.GetType(), "DoNext").Invoke(page, null);
            return true;
        }
        refusal = GameMessages.RejectionSince(before) ?? "the page refused to go on";
        return false;
    }

    public static bool TryBack(Page page)
    {
        if (!CanBack(page)) return false;
        AccessTools.Method(page.GetType(), "DoBack").Invoke(page, null);
        return true;
    }
}
