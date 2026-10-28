namespace Rimlink.Client.Net;

/// <summary>Logging seam so the core never references Verse.Log.</summary>
public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class ConsoleLog : ILog
{
    public void Info(string message) => Console.WriteLine("[Rimlink] " + message);
    public void Warn(string message) => Console.WriteLine("[Rimlink] WARN " + message);
    public void Error(string message) => Console.Error.WriteLine("[Rimlink] ERROR " + message);
}
