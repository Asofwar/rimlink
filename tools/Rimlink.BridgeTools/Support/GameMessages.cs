using HarmonyLib;
using RimWorld;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>
/// The game refuses a player action with an on-screen RejectInput message, not with a return value. Main thread only.
/// A repeated text is not added again but re-stamped (<see cref="Message.ResetTimer"/>), so every live message is
/// checked by its frame, not only the last one.
/// </summary>
public static class GameMessages
{
    /// <summary>The frame counter messages are stamped with; take it before the action.</summary>
    public static int Frame => RealTime.frameCount;

    public static string? RejectionSince(int frame)
    {
        var live = (List<Message>)AccessTools.Field(typeof(Messages), "liveMessages").GetValue(null)!;
        return live.Where(m => m.startingFrame >= frame && m.def == MessageTypeDefOf.RejectInput)
            .OrderBy(m => m.startingFrame)
            .LastOrDefault()?.text;
    }
}
