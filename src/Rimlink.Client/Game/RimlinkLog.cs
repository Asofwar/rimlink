using Rimlink.Client.Net;
using Rimlink.Client.Diagnostics;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Routes core-layer logging into the game's log. Safe from any thread.</summary>
public sealed class RimlinkLog : ILog
{
    public static readonly RimlinkLog Instance = new();
    public static readonly DiagnosticJournal Diagnostics = new();

    public void Info(string message) { Diagnostics.RecordLog(DiagnosticSeverity.Info, message); Log.Message("[Rimlink] " + message); }
    public void Warn(string message) { Diagnostics.RecordLog(DiagnosticSeverity.Warning, message); Log.Warning("[Rimlink] " + message); }
    public void Error(string message) { Diagnostics.RecordLog(DiagnosticSeverity.Error, message); Log.Error("[Rimlink] " + message); }
}
