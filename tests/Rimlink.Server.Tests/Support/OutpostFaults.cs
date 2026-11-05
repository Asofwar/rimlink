using System.Diagnostics;
using Rimlink.Server.Tests.Outposts;

namespace Rimlink.Server.Tests.Support;

// Kills only the process tree started by this fixture after a positive owned checkpoint.
// Process termination checks ordering/reopen behavior, not power-loss durability.
internal static class OutpostFaults
{
    public sealed record Termination(bool WorkerExitedAfterRoot, bool WorkerExitedAfterStreams,
        string WorkerAfterRoot = "", string WorkerAfterStreams = "");

    internal static string StartIdentity(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // /proc/pid/stat field 22 is kernel start ticks since boot. Process.StartTime adds a separately sampled/cached wall-clock boot time.
        string stat = File.ReadAllText($"/proc/{process.Id}/stat");
        int end = stat.LastIndexOf(')');
        if (end < 0) throw new IOException("crash-worker-invalid-kernel-identity");
        string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong ticks))
            throw new IOException("crash-worker-invalid-kernel-identity");
        return ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<Termination> KillAtAsync(string root, string point, Func<Task>? inspectWhileHeld = null,
        string? workerTest = null, IReadOnlyDictionary<string, string>? workerEnvironment = null)
    {
        string ready = Path.Combine(root, "checkpoint-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(OutpostCrashTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + (workerTest ?? "Rimlink.Server.Tests.Outposts.OutpostCrashTests.Owned_crash_worker"));
        start.Environment["RIMLINK_OUTPOST_FAULT_ROOT"] = root;
        start.Environment["RIMLINK_OUTPOST_FAULT_POINT"] = point;
        start.Environment["RIMLINK_OUTPOST_FAULT_READY"] = ready;
        if (workerEnvironment is not null)
            foreach (var entry in workerEnvironment) start.Environment[entry.Key] = entry.Value;
        using var child = Process.Start(start) ?? throw new IOException("crash-worker-not-started");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        Process? worker = null;
        bool workerExitedAfterRoot = false;
        bool workerExitedAfterStreams = false;
        string workerAfterRoot = "";
        string workerAfterStreams = "";
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (child.HasExited)
                    throw new IOException("crash-worker-exited-before-checkpoint: " + await stdout + await stderr);
                if (deadline.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("crash-worker-checkpoint-timeout");
                await Task.Delay(25);
            }
            string[] identity = File.ReadAllText(ready).Split('|');
            worker = Process.GetProcessById(int.Parse(identity[0]));
            if (StartIdentity(worker) != identity[1])
                throw new IOException("crash-worker-identity-changed");
            _ = worker.SafeHandle; // Bind the observed owned process identity before termination.
            if (inspectWhileHeld is not null) await inspectWhileHeld();
            // Leave the launcher alive to reap its worker. Killing the launcher first leaves
            // an orphaned zombie on Linux; root exit/pipe EOF do not prove descendant exit.
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            workerExitedAfterRoot = worker.HasExited;
            workerAfterRoot = ObserveWorker(worker);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            await stdout;
            await stderr;
            workerExitedAfterStreams = worker?.HasExited ?? false;
            workerAfterStreams = ObserveWorker(worker);
            worker?.Dispose();
        }
        return new(workerExitedAfterRoot, workerExitedAfterStreams, workerAfterRoot, workerAfterStreams);
    }

    private static string ObserveWorker(Process? worker)
    {
        if (worker is null) return "not-observed";
        if (!OperatingSystem.IsLinux()) return worker.HasExited ? "exited" : "not-exited";
        try
        {
            string stat = File.ReadAllText($"/proc/{worker.Id}/stat");
            int end = stat.LastIndexOf(')');
            return end < 0 ? "invalid-proc-stat" : $"pid={worker.Id}; {stat[(end + 1)..].Trim()}";
        }
        catch (FileNotFoundException) { return "proc-missing"; }
        catch (DirectoryNotFoundException) { return "proc-missing"; }
    }
}
