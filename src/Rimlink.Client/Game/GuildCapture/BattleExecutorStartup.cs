using Rimlink.Client.GuildCapture;
using Rimlink.Client.UI;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

internal static class BattleExecutorStartup
{
    private static string? configuration;
    private static bool started;
    private static BattleExecutorOperator? runtime;
    public static bool Selected {get;private set;}
    public static string Status=>runtime?.Status??(started?"RecoveryRequired":"Loading");
    internal static bool Select(IReadOnlyList<string> arguments)
    {
        Selected=arguments.Any(a=>a.StartsWith("-rimlinkExecutorConfig=",StringComparison.OrdinalIgnoreCase));
        if(!Selected)return false;
        try{configuration=BattleExecutorSettings.ConfigurationArgument(arguments)??throw new InvalidDataException();}
        catch(Exception error)when(error is InvalidDataException or ArgumentException)
        {started=true;Log.Error("[Rimlink] executor configuration argument refused: "+error.GetType().Name);}
        return true;
    }
    internal static void Frame()
    {
        if(!Selected || started || Current.ProgramState!=ProgramState.Entry || Current.Game is not null || LongEventHandler.AnyEventNowOrWaiting)return;
        if(!ModpackActions.Content.Ready.IsCompletedSuccessfully)return;
        started=true;
        _=Task.Run(async()=>
        {
            try{runtime=new(BattleExecutorSettings.Read(configuration!));await runtime.Run();}
            catch(Exception error)
            {
                if(runtime is not null)runtime.Status="RecoveryRequired";
                MainThread.Post(()=>Log.Error("[Rimlink] executor requires recovery; immutable assignment and artifacts retained: "+error));
            }
        });
    }
}
