namespace Rimlink.Client.Diagnostics;

public enum DiagnosticSeverity { Info, Warning, Error }
public sealed record DiagnosticLogEntry(DateTime Utc, DiagnosticSeverity Severity);

/// <summary>Free-form messages may contain credentials even when they look harmless: retain severity and time only.</summary>
public sealed class DiagnosticJournal
{
    public const int MaxEntries = 128;
    private readonly Queue<DiagnosticLogEntry> entries = new();
    private readonly object sync = new();

    public void RecordLog(DiagnosticSeverity severity, string message)
    {
        lock (sync)
        {
            if (entries.Count == MaxEntries) entries.Dequeue();
            entries.Enqueue(new(DateTime.UtcNow, Enum.IsDefined(severity) ? severity : DiagnosticSeverity.Info));
        }
    }

    public IReadOnlyList<DiagnosticLogEntry> Snapshot() { lock (sync) return entries.ToArray(); }
}
