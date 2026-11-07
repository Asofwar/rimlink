using System.Diagnostics;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Rimlink.Server.Tests.Outposts;

// Owned process termination is ordering evidence, not a filesystem power-loss guarantee.
public sealed class OutpostAdminBootstrapCrashTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Killed_v2_initializer_retains_exact_original_command_and_one_initial_effect(string point, bool committed)
    {
        using var f = new AuthorityHarness(); var command = await OutpostAdminBootstrapTests.Command(f);
        byte[] original = OutpostAdminIntentCodec.Encode(command.Intent);
        await File.WriteAllBytesAsync(Path.Combine(f.Root, "bootstrap-command.bin"), original);
        // No parent attachment may make the child's otherwise cold termination look safe.
        await f.MainObservation.DrainAsync(default); await f.Db.Database.CloseConnectionAsync();
        try
        {
            var termination = await OutpostFaults.KillAtAsync(f.Root, point, workerTest:
                "Rimlink.Server.Tests.Outposts.OutpostAdminBootstrapCrashTests.Owned_v2_initialize_crash_worker");
            Assert.True(termination.WorkerExitedAfterRoot && termination.WorkerExitedAfterStreams, termination.ToString());
            var startup = await f.Authority.OpenAsync(default);
            var expected = committed ? OutpostStartupState.Ready : point == "AfterPending" ? OutpostStartupState.Maintenance : OutpostStartupState.RequiresInitialization;
            Assert.True(startup.State == expected, $"Expected {expected}; actual {startup}; native={f.SqliteFailure.LastFailure}; open={f.Authority.LastOpenFailure}; termination={termination}");
            Assert.Equal(committed ? 1 : 0, await f.Db.OutpostAuthority.CountAsync());
            Assert.Equal(committed ? 1 : 0, await f.Db.OutpostAdminCommands.CountAsync());
            var result = await OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default);
            Assert.Equal(committed, result.Replay); Assert.Equal(command.Intent.Allocation.NextHeadId, result.Head.Id);
            Assert.Single(await f.Db.OutpostTransitions.AsNoTracking().ToListAsync());
            Assert.Single(await f.Db.OutpostAdminCommands.AsNoTracking().ToListAsync());
            Assert.Single(await f.Db.AdminActions.AsNoTracking().ToListAsync());
            Assert.Equal(original, (await f.Db.OutpostAdminCommands.AsNoTracking().SingleAsync()).Command);
            var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
            var folded = await OutpostAuthority.ReadStoredChainAsync(f.Db, f.Files, row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), true, default);
            Assert.Equal(OutpostDomainFormat.V2, folded.Format);
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
            var again = await OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default);
            Assert.True(again.Replay); Assert.Equal(result.Head, again.Head); Assert.False(f.Feature.IsAvailable);
        }
        catch
        {
            f.KeepForDiagnostics = true; output.WriteLine("Owned bootstrap fixture retained: " + f.Root); throw;
        }
    }

    [Fact]
    public async Task Owned_v2_initialize_crash_worker()
    {
        string? root = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_ROOT");
        if (root is null) return;
        string point = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_POINT")!;
        string ready = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_READY")!;
        using var f = new AuthorityHarness(root);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, "bootstrap-command.bin"));
        var intent = OutpostAdminIntentCodec.Decode(bytes);
        f.Authority.CommitCheckpoint = stage =>
        {
            if (stage != point) return;
            using var worker = Process.GetCurrentProcess();
            File.WriteAllText(ready + ".pending", $"{worker.Id}|{OutpostFaults.StartIdentity(worker)}");
            File.Move(ready + ".pending", ready);
            Thread.Sleep(Timeout.Infinite);
        };
        await OutpostAdminActivationTests.Service(f).InitializeAsync(intent.Actor, new(intent, OutpostAdminIntentCodec.Digest(intent), true), default);
        throw new InvalidOperationException("initialize-crash-worker-missed-checkpoint");
    }
}
