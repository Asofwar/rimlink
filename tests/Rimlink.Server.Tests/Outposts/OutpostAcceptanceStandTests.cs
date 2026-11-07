using System.Runtime.InteropServices;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceStandTests
{
    [Fact]
    public async Task Early_startup_refusals_keep_evidence_without_publishing_a_clock_tip()
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        string evidence = Path.Combine(f.Stand.Root, "evidence"); Directory.CreateDirectory(evidence);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var result = await ProvisionCommand(f.Stand, "run");
            Assert.Equal(2, result.ExitCode);
            Assert.True(result.Output.Contains("acceptance-configuration-outside-owned-stand", StringComparison.Ordinal),
                $"Startup attempt {attempt + 1}: {result.Output}");
        }
        Assert.Empty(Directory.EnumerateFiles(evidence, "run-*.clock.jsonl"));
        Assert.Empty(Directory.EnumerateFiles(evidence, "run-*.control.json"));
        Assert.Equal(2, Directory.EnumerateFiles(evidence, "launch-*.request.json").Count());
        Assert.Equal(2, Directory.EnumerateFiles(evidence, "launch-*.refusal.json").Count());
    }

    [Fact]
    public async Task Offline_adapter_refuses_an_unqualified_owned_stand_before_any_restore_effect()
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        string evidence = Path.Combine(f.Stand.Root, "evidence"); Directory.CreateDirectory(evidence);
        string descriptor = Path.Combine(f.Stand.Root, "stand.json"), invocation = Path.Combine(evidence, "invocation.json");
        File.WriteAllText(descriptor, System.Text.Json.JsonSerializer.Serialize(f.Stand, AcceptanceStandFiles.Json));
        File.WriteAllText(invocation, System.Text.Json.JsonSerializer.Serialize(new AcceptanceOfflineInvocation(1, "resume", RestoreId: Guid.NewGuid().ToString("D")), AcceptanceStandFiles.Json));
        string before = StateBytes(f.Stand);
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { typeof(AcceptanceComposition).Assembly.Location, "offline", descriptor, invocation }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, process.ExitCode);
        Assert.Contains("acceptance-offline-current-owned-qualification-required", await stdout + await stderr);
        Assert.Equal(before, StateBytes(f.Stand));
        Assert.Empty(Directory.EnumerateFiles(evidence, "offline-*"));
    }
    [Fact]
    public async Task Explicit_offline_provision_creates_only_a_real_null_anchor_and_never_replaces_it()
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        var first = await ProvisionCommand(f.Stand);
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.False(File.Exists(Path.Combine(f.Stand.DataDirectory, "rimlink.db")));
        var store = new OutpostAnchorStore(Rimlink.Server.Storage.DataDirectory.Existing(f.Stand.DataDirectory),
            Microsoft.Extensions.Options.Options.Create(new Rimlink.Server.Hosting.OutpostOptions
            { AnchorDirectory = f.Stand.AnchorDirectory, StorageContract = "sqlite-local-v1", QualifiedLinuxFileSystem = f.Stand.QualifiedLinuxFileSystem }));
        using (var held = await store.AcquireAsync(default)) Assert.Null(store.ReadEnvelope(held));
        string before = StateBytes(f.Stand);
        var repeated = await ProvisionCommand(f.Stand);
        Assert.NotEqual(0, repeated.ExitCode);
        Assert.Equal(before, StateBytes(f.Stand));
    }

    [Theory]
    [InlineData("data-history")][InlineData("partial-anchor")][InlineData("saved-game")][InlineData("busy-server")]
    public async Task Offline_provision_refuses_history_partial_state_or_a_running_server_without_effects(string damage)
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        FileStream? busy = null;
        try
        {
            if (damage == "data-history") File.WriteAllText(Path.Combine(f.Stand.DataDirectory, "rimlink.db"), "retained history");
            if (damage == "partial-anchor") File.WriteAllBytes(Path.Combine(f.Stand.AnchorDirectory, "authority.db"), []);
            if (damage == "saved-game") { Directory.CreateDirectory(Path.Combine(f.Stand.HostSaveData, "Saves")); File.WriteAllText(Path.Combine(f.Stand.HostSaveData, "Saves", "home.rws"), "retained save"); }
            if (damage == "busy-server") busy = new FileStream(Path.Combine(f.Stand.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string before = StateBytes(f.Stand);
            var result = await ProvisionCommand(f.Stand);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(before, StateBytes(f.Stand));
        }
        finally { busy?.Dispose(); }
    }

    private static string StateBytes(AcceptanceStand stand) => string.Join("\n", new[] { stand.DataDirectory, stand.AnchorDirectory, stand.HostSaveData, stand.GuestSaveData }
        .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)).Where(p => Path.GetFileName(p) != "server.lock")
        .Order(StringComparer.Ordinal).Select(p => p + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))));

    private static async Task<(int ExitCode, string Output)> ProvisionCommand(AcceptanceStand stand, string command = "provision")
    {
        string descriptor = Path.Combine(stand.Root, "stand.json");
        File.WriteAllText(descriptor, System.Text.Json.JsonSerializer.Serialize(stand, AcceptanceStandFiles.Json));
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(AcceptanceComposition).Assembly.Location);
        start.ArgumentList.Add(command); start.ArgumentList.Add(descriptor);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await stdout + await stderr);
    }

    [Fact]
    public void Dev_host_carries_the_same_real_panel_asset_bytes_as_the_embedded_safety_inventory()
    {
        const string source = "src/Rimlink.Server/wwwroot/admin/views/outposts.js";
        using var manifest = typeof(OutpostFeature).Assembly.GetManifestResourceStream("Rimlink.Outposts.SafetySources")!;
        using var reader = new StreamReader(manifest);
        string hash = Assert.Single(reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), line => line.StartsWith(source + "|", StringComparison.Ordinal)).Split('|')[1];
        string asset = Path.Combine(Path.GetDirectoryName(typeof(AcceptanceComposition).Assembly.Location)!, "wwwroot", "admin", "views", "outposts.js");
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(asset))));
    }
    [Fact]
    public void Preparation_only_owns_empty_private_state_and_does_not_provision_authority()
    {
        using var f = new StandFixture();
        AcceptanceStandFiles.PrepareOwner(f.Stand);
        AcceptanceStandFiles.VerifyOwner(f.Stand);
        Assert.Empty(Directory.EnumerateFiles(f.Stand.DataDirectory, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Stand.AnchorDirectory));
        Assert.Throws<IOException>(() => AcceptanceStandFiles.PrepareOwner(f.Stand));
    }

    [Theory]
    [InlineData("owner")][InlineData("paths")][InlineData("game-id")][InlineData("foreign-root")][InlineData("preexisting-state")]
    public void Preparation_refuses_foreign_or_overlapping_state_without_modifying_it(string defect)
    {
        using var f = new StandFixture(); var stand = f.Stand;
        string? sentinel = null;
        switch (defect)
        {
            case "owner": stand = stand with { StandId = Guid.NewGuid().ToString("D") }; break;
            case "paths": stand = stand with { HostSaveData = stand.GuestSaveData }; break;
            case "game-id": stand = stand with { HostGameId = "rimworld-host" }; break;
            case "foreign-root": sentinel = Path.Combine(stand.Root, "foreign.txt"); break;
            case "preexisting-state": sentinel = Path.Combine(stand.DataDirectory, "rimlink.db"); break;
        }
        if (sentinel is not null) File.WriteAllText(sentinel, "чужой WIP", System.Text.Encoding.UTF8);
        Assert.Throws<InvalidDataException>(() => AcceptanceStandFiles.PrepareOwner(stand));
        Assert.False(File.Exists(AcceptanceStandFiles.OwnerPath(stand)));
        Assert.False(File.Exists(AcceptanceStandFiles.LockPath(stand)));
        if (sentinel is not null) Assert.Equal("чужой WIP", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Existing_owner_cannot_be_rebound_and_a_second_host_cannot_take_the_lock()
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        Assert.Throws<InvalidDataException>(() => AcceptanceStandFiles.VerifyOwner(f.Stand with { Port = f.Stand.Port + 1 }));
        using (var first = AcceptanceComposition.Open(f.Stand))
        {
            _ = first.Observe();
            Assert.Throws<IOException>(() => AcceptanceComposition.Open(f.Stand));
        }
        using var next = AcceptanceComposition.Open(f.Stand);
    }

    [Fact]
    public void Missing_prerequisites_and_changed_configuration_are_pending_observations_not_a_bypass()
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        using var composition = AcceptanceComposition.Open(f.Stand);
        var absent = composition.Observe();
        Assert.Equal(OutpostRuntimeMode.IsolatedAcceptance, absent.Mode);
        Assert.Equal(OutpostQualificationState.Pending, absent.State);
        Assert.False(absent.AutomatedPrerequisitesVerified);
        Assert.NotNull(composition.LastRefusal);
        File.WriteAllText(f.Stand.ConfigurationFile, "{\"changed\":true}");
        Assert.False(composition.Observe().IsolationVerified);
        Assert.Equal("acceptance-configuration-changed", composition.LastRefusal);
    }

    [Fact]
    public void Hard_links_in_owned_state_are_rejected_without_touching_either_name()
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        string original = Path.Combine(f.Stand.DataDirectory, "original"), alias = Path.Combine(f.Stand.AnchorDirectory, "alias");
        File.WriteAllText(original, "evidence");
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLinkW(alias, original, IntPtr.Zero));
        else Assert.Equal(0, link(original, alias));
        Assert.Throws<InvalidDataException>(() => AcceptanceStandFiles.VerifyOwner(f.Stand));
        Assert.Equal("evidence", File.ReadAllText(original)); Assert.Equal("evidence", File.ReadAllText(alias));
    }

    [Fact]
    public void Strict_documents_reject_duplicate_or_unknown_fields()
    {
        using var f = new StandFixture(); string path = Path.Combine(f.Stand.Root, "prerequisites.json");
        foreach (string json in new[] { "{\"version\":1,\"version\":1}", "{\"version\":1,\"force\":true}" })
        {
            File.WriteAllText(path, json);
            Assert.Throws<InvalidDataException>(() => AcceptanceStandFiles.Read<AcceptancePrerequisites>(path));
        }
    }

    [Fact]
    public void Preparation_and_restart_refuse_hard_links_inside_the_game_copy_too()
    {
        using var f = new StandFixture(); AcceptanceStandFiles.PrepareOwner(f.Stand);
        string original = Path.Combine(f.Stand.GameRoot, "original"), alias = Path.Combine(f.Stand.GameRoot, "alias");
        File.WriteAllText(original, "game data");
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLinkW(alias, original, IntPtr.Zero));
        else Assert.Equal(0, link(original, alias));
        Assert.Throws<InvalidDataException>(() => AcceptanceStandFiles.VerifyLaunchOwner(f.Stand));
        Assert.Throws<InvalidDataException>(() => AcceptanceComposition.Open(f.Stand));
        File.Delete(AcceptanceStandFiles.OwnerPath(f.Stand)); File.Delete(AcceptanceStandFiles.LockPath(f.Stand));
        Assert.Throws<InvalidDataException>(() => AcceptanceStandFiles.PrepareOwner(f.Stand));
        Assert.False(File.Exists(AcceptanceStandFiles.OwnerPath(f.Stand)));
    }

    private sealed class StandFixture : IDisposable
    {
        internal AcceptanceStand Stand { get; }
        internal StandFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "rimlink-acceptance-test-" + Guid.NewGuid().ToString("N"));
            string At(string path) => Path.Combine(root, path);
            foreach (string path in new[] { "data/backups", "data/archive", "anchor", "game", "saves-host", "saves-guest" }) Directory.CreateDirectory(At(path));
            string id = Guid.NewGuid().ToString("D"), prefix = "rimlink-outposts-" + Guid.Parse(id).ToString("N");
            Stand = new(1, id, root, At("data"), At("anchor"), At("data/backups"), At("data/archive"), At("game"), prefix + "-host", prefix + "-guest",
                At("saves-host"), At("saves-guest"), At("game/Mods/Rimlink/Assemblies/Rimlink.Client.dll"), At("game/BridgeTools/Rimlink/Rimlink.BridgeTools.dll"),
                At("config.json"), At("prerequisites.json"), 19647, OperatingSystem.IsWindows() ? "" : "ext4");
            File.WriteAllText(Stand.ConfigurationFile, "{}");
        }
        public void Dispose() => Directory.Delete(Stand.Root, recursive: true);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
    [DllImport("libc", SetLastError = true)] private static extern int link(string existing, string name);
}
