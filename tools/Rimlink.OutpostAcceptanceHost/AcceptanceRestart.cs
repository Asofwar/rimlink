using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceRestartOrigin(DateTimeOffset StartUtc, string PreviousRunId, string PreviousSha256, long DowntimeTicks);
internal static class AcceptanceRestart
{
    internal static void CheckTip(string evidence, string standId, string previousRun)
    {
        var runs = new Dictionary<string, (string Hash, AcceptanceRestartOrigin? Origin)>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(evidence, "run-*.clock.jsonl"))
        {
            AcceptancePaths.File(path);
            using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            string line = reader.ReadLine() ?? throw new InvalidDataException("acceptance-restart-empty-origin");
            if (line.Length > 16 * 1024) throw new InvalidDataException("acceptance-restart-origin-limit");
            var entry = JsonSerializer.Deserialize<Entry>(line, AcceptanceStandFiles.Json) ?? throw new InvalidDataException("acceptance-restart-origin-invalid");
            string id = entry.Identity.RunId;
            if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty || Path.GetFileName(path) != "run-" + id + ".clock.jsonl"
                || runs.Count >= 4096) throw new InvalidDataException("acceptance-restart-run-inventory-invalid");
            var verified = Read(path, standId, id, TimeSpan.Zero);
            var origin = entry.Data.GetProperty("restart").Deserialize<AcceptanceRestartOrigin>(AcceptanceStandFiles.Json);
            if (!runs.TryAdd(id, (verified.PreviousSha256, origin))) throw new InvalidDataException("acceptance-restart-duplicate-run");
        }
        var children = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, row) in runs)
            if (row.Origin is { } origin && (!runs.TryGetValue(origin.PreviousRunId, out var parent) || parent.Hash != origin.PreviousSha256
                || !children.TryAdd(origin.PreviousRunId, id))) throw new InvalidDataException("acceptance-restart-journal-branch-or-hash");
        var roots = runs.Where(r => r.Value.Origin is null).Select(r => r.Key).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("acceptance-restart-ambiguous-root");
        string tip = roots[0]; var visited = new HashSet<string>(StringComparer.Ordinal) { tip };
        while (children.TryGetValue(tip, out string? child))
        {
            if (!visited.Add(child)) throw new InvalidDataException("acceptance-restart-journal-cycle");
            tip = child;
        }
        if (visited.Count != runs.Count || tip != previousRun) throw new InvalidDataException("acceptance-restart-latest-closed-tip-required");
    }
    private sealed record Entry(int Version, string Kind, AcceptanceRunIdentity Identity, DateTimeOffset RealUtc,
        long RealTimestamp, long RealFrequency, JsonElement Data);

    internal static AcceptanceRestartOrigin Read(string path, string standId, string runId, TimeSpan downtime)
    {
        if (downtime < TimeSpan.Zero || downtime > TimeSpan.FromDays(30)) throw new InvalidDataException("acceptance-restart-downtime-invalid");
        AcceptancePaths.File(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 32 * 1024 * 1024) throw new InvalidDataException("acceptance-restart-journal-size");
        string hash = Convert.ToHexStringLower(SHA256.HashData(file)); file.Position = 0;
        using var reader = new StreamReader(file, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        Entry? last = null; AcceptanceRunIdentity? owner = null; int count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length is < 1 or > 16 * 1024 || ++count > 12292) throw new InvalidDataException("acceptance-restart-journal-limit");
            var entry = JsonSerializer.Deserialize<Entry>(line, AcceptanceStandFiles.Json) ?? throw new InvalidDataException("acceptance-restart-journal-invalid");
            owner ??= entry.Identity;
            if (entry.Version != 1 || entry.Identity != owner || owner.StandId != standId || owner.RunId != runId
                || owner.SourceIdentity != OutpostProductQualification.SourceIdentity || owner.ProcessId <= 0
                || entry.RealFrequency <= 0 || entry.RealUtc.Offset != TimeSpan.Zero
                || count == 1 && entry.Kind != "Origin" || count > 1 && (entry.Kind == "Origin" || last?.Kind == "Closed")
                || last is not null && (entry.RealFrequency != last.RealFrequency || entry.RealTimestamp < last.RealTimestamp))
                throw new InvalidDataException("acceptance-restart-journal-chain-invalid");
            last = entry;
        }
        if (last?.Kind != "Closed" || count < 3) throw new InvalidDataException("acceptance-restart-needs-closed-run");
        var reading = last.Data.GetProperty("time").Deserialize<AcceptanceClockReading>(AcceptanceStandFiles.Json)
            ?? throw new InvalidDataException("acceptance-restart-last-reading-invalid");
        var sample = last.Data.GetProperty("sample").Deserialize<ClockSample>(AcceptanceStandFiles.Json)
            ?? throw new InvalidDataException("acceptance-restart-last-sample-invalid");
        if (reading.Utc.Offset != TimeSpan.Zero || sample.Utc.Kind != DateTimeKind.Utc || reading.Frequency != TimeSpan.TicksPerSecond
            || sample.Frequency != reading.Frequency || sample.Timestamp > reading.Timestamp)
            throw new InvalidDataException("acceptance-restart-last-coordinates-invalid");
        var lastUtc = reading.Utc > sample.Utc ? reading.Utc : new DateTimeOffset(sample.Utc);
        return new(lastUtc.Add(downtime), runId, hash, downtime.Ticks);
    }
    internal static void CheckCheckpoints(DateTimeOffset startUtc, IEnumerable<DateTime> checkpoints)
    {
        if (startUtc.Offset != TimeSpan.Zero || checkpoints.Any(c => c.Kind != DateTimeKind.Utc || c > startUtc.UtcDateTime))
            throw new InvalidDataException("acceptance-restart-behind-domain-checkpoint");
    }
}
