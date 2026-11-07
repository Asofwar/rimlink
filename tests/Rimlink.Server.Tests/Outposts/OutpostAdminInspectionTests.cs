using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminInspectionTests
{
    [Fact]
    public async Task Inspect_proves_committed_format_without_mutating_any_file_audit_or_runtime_state()
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        var before = Files(f); var startup = f.Feature.Startup;
        var result = await OutpostAdminActivationTests.Service(f).InspectAsync(Actor.Api, default);
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(OutpostAuthorityObservationKind.Committed, result.Kind);
        Assert.NotNull(result.Committed); Assert.Equal(row.HeadId, result.Committed.HeadId);
        Assert.Equal(row.HeadDigest, result.Committed.HeadDigest);
        Assert.Equal(OutpostDomainFormat.V1, result.Committed.DomainFormat);
        Assert.Equal((await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default)).ProfileId, result.Committed.Schema.ProfileId);
        Assert.Equal(before, Files(f)); Assert.Equal(startup, f.Feature.Startup); Assert.False(f.Feature.IsAvailable);
        Assert.Empty(await f.Db.AdminActions.ToListAsync()); Assert.Empty(await f.Db.OutpostAdminCommands.ToListAsync());
    }

    [Theory]
    [InlineData("pending")][InlineData("hot-journal")][InlineData("missing-journal")][InlineData("missing-anchor")][InlineData("missing-save-lock")]
    public async Task Unsafe_observation_is_unavailable_and_never_reconciles_or_creates_missing_files(string problem)
    {
        using var f = new AuthorityHarness(); var command = await Prepare(f);
        if (problem == "pending")
        {
            f.Authority.CommitCheckpoint = point => { if (point == "AfterPending") throw new Stop(); };
            await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(f).ActivateDomainFormatAsync(Actor.Api, command, default));
            f.Authority.CommitCheckpoint = null; f.Db.ChangeTracker.Clear();
        }
        if (problem == "hot-journal")
        {
            byte[] bytes = File.ReadAllBytes(f.Anchor.DatabasePath + "-journal");
            byte[] magic = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7]; magic.CopyTo(bytes, 0);
            File.WriteAllBytes(f.Anchor.DatabasePath + "-journal", bytes);
        }
        if (problem == "missing-journal") File.Delete(f.Anchor.DatabasePath + "-journal");
        if (problem == "missing-anchor") File.Delete(f.Anchor.DatabasePath);
        if (problem == "missing-save-lock") File.Delete(Path.Combine(f.Data.Root, "save-storage.lock"));
        var before = Files(f); var startup = f.Feature.Startup;
        var service = OutpostAdminActivationTests.Service(f);
        Assert.Equal(OutpostAuthorityObservationKind.Unavailable, (await service.InspectAsync(Actor.Api, default)).Kind);
        var preview = await service.PreviewActivateDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "inspect only", default);
        Assert.Null(preview.Command); Assert.NotEmpty(preview.Blockers);
        Assert.Equal(before, Files(f)); Assert.Equal(startup, f.Feature.Startup);
        Assert.Empty(await f.Db.OutpostAdminCommands.ToListAsync()); Assert.Empty(await f.Db.AdminActions.ToListAsync());
    }

    [Fact]
    public async Task Inspect_does_not_create_transient_main_database_sidecars()
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        await f.Db.Database.CloseConnectionAsync();
        var created = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var observedControl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string controlName = "inspection-watch-" + Guid.NewGuid().ToString("N");
        using var watcher = new FileSystemWatcher(f.Data.Root) { NotifyFilter = NotifyFilters.FileName };
        watcher.Created += (_, change) =>
        {
            if (change.Name == controlName) observedControl.TrySetResult();
            else if (change.Name?.StartsWith("rimlink.db", StringComparison.Ordinal) == true) created.Enqueue(change.Name);
        };
        watcher.Error += (_, error) => observedControl.TrySetException(error.GetException());
        watcher.EnableRaisingEvents = true;
        var result = await OutpostAdminActivationTests.Service(f).InspectAsync(Actor.Api, default);
        await File.WriteAllBytesAsync(Path.Combine(f.Data.Root, controlName), []);
        await observedControl.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(created);
        Assert.True(result.Kind == OutpostAuthorityObservationKind.Committed, f.Authority.LastInspectionFailure is Microsoft.Data.Sqlite.SqliteException sql
            ? $"SQLite {sql.SqliteErrorCode}/{sql.SqliteExtendedErrorCode}: {sql}" : f.Authority.LastInspectionFailure?.ToString() ?? result.Kind.ToString());
    }

    [Theory]
    [InlineData("inspect")][InlineData("preview")][InlineData("cancelled")][InlineData("cold")][InlineData("uninitialized")]
    public async Task Observation_has_no_file_lifecycle_size_or_noncoordination_write_effects(string action)
    {
        using var f = new AuthorityHarness();
        if (action == "cold") await OutpostAdminActivationTests.Prepare(f);
        else if (action == "uninitialized") await OutpostAdminUninitializedInspectionTests.Prepare(f);
        else await Prepare(f);
        var violations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        string control = "observation-control-" + Guid.NewGuid().ToString("N");
        var lifecycleSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sizeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var names = new FileSystemWatcher(f.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
        using var sizes = new FileSystemWatcher(f.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.Size };
        using var writes = new FileSystemWatcher(f.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite };
        void Changed(FileSystemEventArgs change, string kind, TaskCompletionSource barrier)
        {
            if (Path.GetFileName(change.FullPath) == control) { barrier.TrySetResult(); return; }
            if (kind == "write" && change.FullPath == f.Data.DatabasePath + "-shm") return; // Bytes checked separately, readmarks only.
            violations.Enqueue(kind + ":" + Path.GetRelativePath(f.Root, change.FullPath));
        }
        names.Created += (_, e) => Changed(e, "create", lifecycleSeen);
        names.Deleted += (_, e) => Changed(e, "delete", lifecycleSeen);
        names.Renamed += (_, e) => Changed(e, "rename", lifecycleSeen);
        sizes.Changed += (_, e) => Changed(e, "size", sizeSeen);
        writes.Changed += (_, e) => Changed(e, "write", writeSeen);
        foreach (var watch in new[] { names, sizes, writes }) watch.Error += (_, e) =>
        { lifecycleSeen.TrySetException(e.GetException()); sizeSeen.TrySetException(e.GetException()); writeSeen.TrySetException(e.GetException()); };
        string before = Files(f);
        names.EnableRaisingEvents = sizes.EnableRaisingEvents = writes.EnableRaisingEvents = true;
        var service = OutpostAdminActivationTests.Service(f);
        if (action == "cancelled")
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InspectAsync(Actor.Api, cancellation.Token));
        }
        else if (action == "preview")
            Assert.NotNull((await service.PreviewActivateDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "read-only preview", default)).Command);
        else
            Assert.Equal(action == "cold" ? OutpostAuthorityObservationKind.Unavailable :
                action == "uninitialized" ? OutpostAuthorityObservationKind.Uninitialized : OutpostAuthorityObservationKind.Committed,
                (await service.InspectAsync(Actor.Api, default)).Kind);
        Assert.Equal(before, Files(f));
        // Positive per-watcher event barriers cover observation, child disposal and failure cleanup; no sleep/polling.
        await File.WriteAllBytesAsync(Path.Combine(f.Root, control), [1]);
        await Task.WhenAll(lifecycleSeen.Task, sizeSeen.Task, writeSeen.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(violations);
        if (action != "cold") { using var pin = f.MainObservation.TryPin(); Assert.NotNull(pin); }
    }

    [Fact]
    public async Task Foreign_prepared_keeper_cannot_redirect_the_observation_to_a_different_main_location()
    {
        using var f = new AuthorityHarness(); await OutpostAdminActivationTests.Prepare(f);
        using var other = new AuthorityHarness(); await other.Db.Database.CloseConnectionAsync();
        // Same exact valid head/schema/history, at a different physical location. Namespace equality is not path authority.
        File.Copy(f.Data.DatabasePath, other.Data.DatabasePath, overwrite: true);
        await OutpostMainObservationTests.Prepare(other, other.MainObservation);
        var feed = new Rimlink.Server.Application.Feed.AdminFeed(Microsoft.Extensions.Options.Options.Create(new Rimlink.Server.Hosting.AdminOptions()),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Rimlink.Server.Application.Feed.AdminFeed>.Instance);
        var service = new OutpostAdminService(f.Db, f.Authority, new Rimlink.Server.Application.Live.LiveStateLock(),
            new AdminAudit(f.Db, feed, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminAudit>.Instance), other.MainObservation,
            new OutpostManifestService(f.Db, f.Authority, new OutpostClock(TimeProvider.System), f.Files));
        var before = Files(f); var otherBefore = OutpostMainObservationTests.Snapshot(other);
        Assert.Equal(OutpostAuthorityObservationKind.Unavailable, (await service.InspectAsync(Actor.Api, default)).Kind);
        Assert.Equal(before, Files(f)); Assert.Equal(otherBefore, OutpostMainObservationTests.Snapshot(other));
    }

    [Fact]
    public async Task Cold_keeper_is_unavailable_before_owner_discovery_without_creating_sidecars()
    {
        using var f = new AuthorityHarness(); await OutpostAdminActivationTests.Prepare(f);
        var before = Files(f);
        var result = await OutpostAdminActivationTests.Service(f).InspectAsync(Actor.Api, default);
        Assert.Equal(OutpostAuthorityObservationKind.Unavailable, result.Kind);
        Assert.Equal("admin-inspection-main-not-prepared", f.Authority.LastInspectionFailure?.Message);
        Assert.Equal(before, Files(f));
        Assert.False(File.Exists(f.Data.DatabasePath + "-wal"));
        Assert.False(File.Exists(f.Data.DatabasePath + "-shm"));
    }

    [Fact]
    public async Task Missing_main_database_is_unavailable_without_creating_a_replacement_database()
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        await f.Db.Database.CloseConnectionAsync();
        // Lifecycle retirement releases the owned attachment before a database is removed or replaced.
        await f.MainObservation.DrainAsync(default);
        string original = f.Data.DatabasePath;
        File.Move(original, original + ".retained-inspection-proof");
        string before = Files(f); var startup = f.Feature.Startup;
        var observed = await OutpostAdminActivationTests.Service(f).InspectAsync(Actor.Api, default);
        Assert.Equal(OutpostAuthorityObservationKind.Unavailable, observed.Kind);
        Assert.False(File.Exists(original));
        Assert.Equal(before, Files(f)); Assert.Equal(startup, f.Feature.Startup);
    }

    [Fact]
    public async Task Preview_allocates_no_history_and_the_exact_advisory_command_can_be_explicitly_confirmed()
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        var before = Files(f); var service = OutpostAdminActivationTests.Service(f);
        var requestId = Guid.NewGuid().ToString("D");
        var preview = await service.PreviewActivateDomainFormatAsync(Actor.Api, requestId, "explicit activation", default);
        Assert.Empty(preview.Blockers); Assert.NotNull(preview.Command); Assert.False(preview.Command.Confirmed);
        Assert.Equal(requestId, preview.Command.Intent.RequestId); Assert.Equal(Actor.Api, preview.Command.Intent.Actor);
        Assert.Equal(OutpostAdminIntentCodec.Digest(preview.Command.Intent), preview.Command.IntentDigest);
        Assert.Equal(before, Files(f)); Assert.Empty(await f.Db.OutpostAdminCommands.ToListAsync());
        var result = await service.ActivateDomainFormatAsync(Actor.Api, preview.Command with { Confirmed = true }, default);
        Assert.False(result.Replay); Assert.Single(await f.Db.OutpostAdminCommands.ToListAsync());
        var repeated = await service.PreviewActivateDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "no second activation", default);
        Assert.Null(repeated.Command); Assert.NotEmpty(repeated.Blockers);
    }

    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("bad\nreason")]
    public async Task Preview_invalid_reason_is_a_typed_refusal_with_no_effects(string reason)
    {
        using var f = new AuthorityHarness(); await Prepare(f); var before = Files(f);
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostAdminActivationTests.Service(f)
            .PreviewActivateDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), reason, default));
        Assert.Equal(before, Files(f));
    }

    [Fact]
    public async Task Non_admin_cannot_inspect_or_preview_even_with_an_administrator_in_body_history()
    {
        using var f = new AuthorityHarness(); await Prepare(f); var before = Files(f);
        var actor = Actor.Api with { Role = Role.Player };
        var service = OutpostAdminActivationTests.Service(f);
        var inspect = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => service.InspectAsync(actor, default));
        var preview = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => service.PreviewActivateDomainFormatAsync(actor, Guid.NewGuid().ToString("D"), "not admin", default));
        Assert.Equal(global::Grpc.Core.StatusCode.PermissionDenied, inspect.StatusCode);
        Assert.Equal(global::Grpc.Core.StatusCode.PermissionDenied, preview.StatusCode);
        Assert.Equal(before, Files(f));
    }

    [Fact]
    public async Task Runtime_composition_prepares_outside_requests_and_retires_the_resource_on_host_shutdown()
    {
        using var f = await LifecycleFixture.Create();
        var observer = f.Host.Services.GetRequiredService<OutpostMainObservation>();
        using (var scope = f.Host.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<OutpostAdminService>();
            Assert.Equal(OutpostAuthorityObservationKind.Committed, (await service.InspectAsync(Actor.Api, default)).Kind);
        }
        await f.Host.DisposeAsync();
        Assert.Null(observer.TryPin());
    }

    [Fact]
    public async Task NoActiveWorld_is_a_verified_committed_admin_observation_not_a_synthetic_uninitialized_head()
    {
        using var f = await LifecycleFixture.Create(); await OutpostLifecycleTests.CloseAndReceive(f);
        await f.RetireLegacyOffline();
        var authority = f.H.F.H.H;
        authority.Db.ChangeTracker.Clear();
        await OutpostMainObservationTests.Prepare(authority, authority.MainObservation);
        var domain = (await OutpostDomainDigest.ComputeAsync(authority.Db, OutpostDomainFormat.V1, default)).ToByteArray();
        var before = await f.Cut(); var startup = authority.Feature.Startup;
        var service = OutpostAdminActivationTests.Service(authority);
        var observed = await service.InspectAsync(Actor.Api, default);
        Assert.True(observed.Kind == OutpostAuthorityObservationKind.Committed, authority.Authority.LastInspectionFailure?.ToString() ?? observed.Kind.ToString());
        Assert.NotNull(observed.Committed); Assert.Null(observed.Uninitialized);
        Assert.Equal(before, await f.Cut()); Assert.Equal(startup, authority.Feature.Startup);
        var preview = await service.PreviewActivateDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "activate retained no-active-world history", default);
        Assert.Empty(preview.Blockers); Assert.NotNull(preview.Command);
        await service.ActivateDomainFormatAsync(Actor.Api, preview.Command with { Confirmed = true }, default);
        Assert.Empty(await authority.Db.World.AsNoTracking().ToListAsync());
        Assert.Equal(domain, (await OutpostDomainDigest.ComputeAsync(authority.Db, OutpostDomainFormat.V1, default)).ToByteArray());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.NoActiveWorld), await authority.Authority.OpenAsync(default));
        Assert.False(authority.Feature.IsAvailable);
    }

    private static async Task<OutpostAdminCommand> Prepare(AuthorityHarness f)
    {
        var command = await OutpostAdminActivationTests.Prepare(f);
        await OutpostMainObservationTests.Prepare(f, f.MainObservation);
        return command;
    }

    internal static string Files(AuthorityHarness f) => string.Join("\n", Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path =>
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream(); file.CopyTo(bytes); var content = bytes.ToArray();
            // Only actual WAL readmark slots are coordination; retain length and every header/index byte.
            if (path == f.Data.DatabasePath + "-shm" && content.Length >= 120) Array.Clear(content, 100, 20);
            return path + ":" + content.Length + ":" + Convert.ToHexString(SHA256.HashData(content));
        }));
    private sealed class Stop : Exception;
}
