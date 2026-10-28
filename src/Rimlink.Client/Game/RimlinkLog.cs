using Rimlink.Client.Net;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Routes core-layer logging into the game's log. Safe from any thread.</summary>
public sealed class RimlinkLog : ILog
{
    public static readonly RimlinkLog Instance = new();

    public void Info(string message) => Log.Message("[Rimlink] " + message);
    public void Warn(string message) => Log.Warning("[Rimlink] " + message);
    public void Error(string message) => Log.Error("[Rimlink] " + message);
}
