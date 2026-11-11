using HarmonyLib;
using RimWorld;
using Rimlink.Client.Game.GuildCapture;
using Rimlink.Client.GuildCapture;
using Verse;

namespace Rimlink.Client.Game.Patches;

// The viewer never simulates; an executor only receives a permit for one authorized native tick.
[HarmonyPatch(typeof(TickManager),nameof(TickManager.DoSingleTick))]
public static class Patch_TickManager_DoSingleTick_GuildBattle
{
    public static bool Prefix(ref bool __state)
    {
        __state=false;if(!BattleGameContext.Active)return !BattleExecutorStartup.Selected;
        if(BattleGameContext.Role!=BattleMapRole.Executor)return false;
        return __state=BattleTickController.Current?.BeforeTick()==true;
    }
    public static void Postfix(bool __state){if(__state)BattleTickController.Current?.AfterTick();}
    public static Exception? Finalizer(Exception? __exception){if(__exception is not null)BattleTickController.Current?.Fail(__exception);return __exception;}
}

// This also covers direct debug tick calls, while the ordinary update is skipped to keep a viewer's clock unchanged.
[HarmonyPatch(typeof(TickManager),nameof(TickManager.TickManagerUpdate))]
public static class Patch_TickManager_Update_GuildBattle
{
    public static bool Prefix()=>!BattleGameContext.Active || BattleGameContext.Role==BattleMapRole.Executor;
}

// Random personal incidents cannot be generated in a fixed battle baseline.
[HarmonyPatch(typeof(Storyteller),nameof(Storyteller.StorytellerTick))]
public static class Patch_Storyteller_Tick_GuildBattle
{
    public static bool Prefix()=>!BattleGameContext.Active;
}

// The executor must keep observing wall time and network grace even when the game is paused or in Entry.
[HarmonyPatch(typeof(Root),nameof(Root.Update))]
public static class Patch_Root_Update_GuildBattle
{
    public static void Postfix(){BattleExecutorStartup.Frame();BattleTickController.Current?.Frame();}
}
