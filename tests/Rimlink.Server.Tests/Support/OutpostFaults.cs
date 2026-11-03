using System.Diagnostics;
using Rimlink.Server.Tests.Outposts;

namespace Rimlink.Server.Tests.Support;

// Kills only the process tree started by this fixture after a positive owned checkpoint.
// Process termination checks ordering/reopen behavior, not power-loss durability.
internal static class OutpostFaults
{
    public static async Task KillAtAsync(string root, string point, Func<Task>? inspectWhileHeld = null)
    {
        string ready = Path.Combine(root, "checkpoint-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(OutpostCrashTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=Rimlink.Server.Tests.Outposts.OutpostCrashTests.Owned_crash_worker");
        start.Environment["RIMLINK_OUTPOST_FAULT_ROOT"] = root;
        start.Environment["RIMLINK_OUTPOST_FAULT_POINT"] = point;
        start.Environment["RIMLINK_OUTPOST_FAULT_READY"] = ready;
        using var child = Process.Start(start) ?? throw new IOException("crash-worker-not-started");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
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
            if (inspectWhileHeld is not null) await inspectWhileHeld();
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
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
        }
    }
}
