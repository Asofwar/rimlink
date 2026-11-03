using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Protocol.V1;
using Rimlink.Client.Net;

namespace Rimlink.Client.Mods.Delivery;

public sealed record InstalledDeliveryMod(string PackageId, string ActivationId, string Root, string Version, ModContentInfo Content)
{
    public ModRef Reference(int position) => new() { PackageId = PackageId, Version = Version, FilesHash = Content.FilesHash, Position = position, HasDefs = Content.HasDefs, HasPatches = Content.HasPatches, HasAssemblies = Content.HasAssemblies };
}
public sealed record OwnedDeliveryMod(string Folder, string PackageId, string TreeHash);
public sealed record PlannedDeliveryMod(DeliveryMod Mod, string Folder, string? ReuseRoot, string ActivationId);
public sealed record ModpackInstallPlan(string OperationId, PackageManifest Manifest, PlannedDeliveryMod[] Mods, OwnedDeliveryMod[] Retire,
    InstalledDeliveryMod[] Future, Dictionary<string, string> Fingerprints, string? ConfigHash, string[] ActiveIds, byte[] ConfigBytes);
public sealed record InstallMove(string Kind, string Folder, string StageFolder, string Hash, bool Done = false);
public sealed record InstallWrite(string Name, string? OriginalHash, string NewHash, bool Done = false);
public sealed record ModpackInstallJournal(string OperationId, string ModsRoot, string ConfigPath, string StateRoot, string State, PackageManifest Manifest,
    InstallMove[] Moves, InstallWrite[] Writes, string? IntentHash = null, string SettingsKey = "", string SettingsHash = "", string LaunchId = "", string TargetServerId = "");

/// <summary>Each volume has its own staging and backups. The common durable journal decides what may be rolled back.</summary>
public sealed class ModpackInstaller : IDisposable
{
    private readonly string modsRoot, configPath, stateRoot, work;
    private FileStream? operationLock;
    private string JournalPath => Path.Combine(stateRoot, "install-journal.json");
    private string OwnershipPath => Path.Combine(work, "owned.json");
    private string PendingPath => Path.Combine(work, "pending.json");
    public string? State => ReadJournal()?.State;
    internal Action<string>? AfterStep { get; set; }
    private static readonly string CurrentLaunch = Guid.NewGuid().ToString("N");
    internal string LaunchId { get; set; } = CurrentLaunch;

    public ModpackInstaller(string modsRoot, string configPath, string stateRoot)
    {
        this.modsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsRoot)); this.configPath = Path.GetFullPath(configPath); this.stateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot));
        work = Path.Combine(Path.GetDirectoryName(this.modsRoot)!, ".rimlink-modpack");
        if (Path.GetPathRoot(this.configPath) != Path.GetPathRoot(this.stateRoot)) throw new DeliveryException("UnsafePath");
        GuardRoots();
    }
    public IDisposable Acquire()
    {
        if (operationLock is not null) throw new DeliveryException("Busy");
        GuardRoots(); Directory.CreateDirectory(work);
        try { operationLock = new FileStream(Path.Combine(work, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DeliveryException("Busy"); }
        return new Lease(this);
    }
    private sealed class Lease(ModpackInstaller owner) : IDisposable
    {
        public void Dispose() { owner.operationLock?.Dispose(); owner.operationLock = null; }
    }
    private IDisposable? LockIfNeeded() => operationLock is null ? Acquire() : null;
    private void GuardRoots() { InstallFiles.NoLinks(modsRoot); InstallFiles.NoLinks(configPath); InstallFiles.NoLinks(stateRoot); InstallFiles.NoLinks(work); }
    private string ModStage(string op) => Path.Combine(work, "operations", op);
    private string ConfigStage(string op) => Path.Combine(stateRoot, "installs", op);
    private string StageFile(string op, string name, string extension) => Path.Combine(name == "Ownership" ? ModStage(op) : ConfigStage(op), "files", name + extension);
    private string FilePath(string name) => name switch { "Ownership" => OwnershipPath, "ActiveSettings" => Path.Combine(stateRoot, "server-settings", "active.json"), "ModsConfig" => configPath, "JoinIntent" => Path.Combine(stateRoot, "join-intent.json"), _ => throw new DeliveryException("RecoveryRequired") };
    private static bool ManagedFolder(string name) => name.StartsWith("rimlink-managed-", StringComparison.Ordinal) && name.Length == 81 && DeliveryValidation.Id(name.Substring(16, 32)) && name[48] == '-' && DeliveryValidation.Id(name[49..]);
    private static string FullRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private static bool Loaded(string root, IReadOnlyCollection<string> loaded) => loaded.Any(p => string.Equals(FullRoot(p), FullRoot(root), StringComparison.OrdinalIgnoreCase));

    public ModpackInstallPlan Plan(DownloadedModpack package, IReadOnlyList<InstalledDeliveryMod> installed, IReadOnlyList<string> activeIds)
    {
        using var gate = LockIfNeeded(); GuardRoots();
        if (File.Exists(PendingPath) || File.Exists(JournalPath)) throw new DeliveryException("RecoveryRequired");
        DeliveryValidation.Validate(package.Manifest, true); DeliveryValidation.Validate(package.Settings, package.Manifest);
        var ownership = ReadOwnership(); string op = Guid.NewGuid().ToString("N");
        var planned = new List<PlannedDeliveryMod>(); var retire = new List<OwnedDeliveryMod>(); var future = new List<InstalledDeliveryMod>(); var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byId = installed.GroupBy(m => DeliveryValidation.Normalize(m.PackageId)).ToDictionary(g => g.Key, g => g.ToArray());
        var requested = package.Manifest.Mods.ToDictionary(m => m.PackageId);
        foreach (var entry in package.Manifest.Entries.OrderBy(e => e.Position))
        {
            bool active = activeIds.Any(id => DeliveryValidation.Normalize(id) == entry.PackageId);
            if (entry.Kind == EntryKind.Allowed && !active) continue;
            byId.TryGetValue(entry.PackageId, out var copies); copies ??= [];
            if (copies.Length > 1) throw new DeliveryException("Duplicate", string.Join("; ", copies.Select(m => m.Root)));
            if (DeliveryValidation.Official(entry.PackageId))
            {
                if (copies.Length != 1 || entry.Version.Length > 0 && copies[0].Version != entry.Version) throw new DeliveryException("Prerequisite", entry.PackageId);
                future.Add(copies[0]); continue;
            }
            if (!requested.TryGetValue(entry.PackageId, out var mod))
            {
                if (copies.Length == 1) { future.Add(copies[0]); fingerprints[copies[0].Root] = TreeHash(copies[0].Root); }
                continue;
            }
            string folder = "rimlink-managed-" + op + "-" + mod.FolderId;
            if (copies.Length == 1)
            {
                var copy = copies[0]; InstallFiles.NoLinks(copy.Root);
                string tree = TreeHash(copy.Root); fingerprints[copy.Root] = tree;
                var content = ModContent.Scan(copy.Root);
                var disk = ModMetadata.Read(copy.Root, mod.FolderId, mod.Position);
                if (disk.Version == mod.Version && content.FilesHash == mod.FilesHash && disk.ContentHash == mod.ContentHash)
                { planned.Add(new(mod, "", copy.Root, copy.ActivationId)); future.Add(copy with { Content = content }); continue; }
                var owned = ownership.SingleOrDefault(o => o.PackageId == mod.PackageId && string.Equals(Path.Combine(modsRoot, o.Folder), FullRoot(copy.Root), StringComparison.OrdinalIgnoreCase));
                if (owned is null || owned.TreeHash != tree) throw new DeliveryException("Conflict", copy.Root);
                retire.Add(owned);
            }
            planned.Add(new(mod, folder, null, mod.PackageId));
            future.Add(new(mod.PackageId, mod.PackageId, Path.Combine(modsRoot, folder), mod.Version, new(mod.FilesHash, true, false, false)));
        }
        // Keep only extras accepted by the existing comparer; rejected extras are disabled, never removed.
        var listed = package.Manifest.Entries.Select(e => e.PackageId).ToHashSet();
        foreach (string id in activeIds)
        {
            string normalized = DeliveryValidation.Normalize(id);
            if (listed.Contains(normalized) || !byId.TryGetValue(normalized, out var copies)) continue;
            if (copies.Length != 1) throw new DeliveryException("Duplicate", normalized);
            var copy = copies[0]; var content = ModContent.Scan(copy.Root);
            if (content.HasAssemblies || content.HasDefs || content.HasPatches) continue;
            future.Add(copy with { Content = content }); fingerprints[copy.Root] = TreeHash(copy.Root);
        }
        var refs = future.Select((m, i) => m.Reference(i)).ToList();
        if (ModpackComparison.Compare(package.Manifest.Entries.Select(e => e.ToProto()).ToList(), refs) is not null) throw new DeliveryException("PreparedMismatch");
        var ids = future.Select(m => m.ActivationId).ToArray();
        byte[]? previous = File.Exists(configPath) ? InstallFiles.Read(configPath, DeliveryLimits.MaxManifestBytes) : null;
        byte[] config = ConfigWithOrder(previous, ids);
        return new(op, package.Manifest, planned.ToArray(), retire.ToArray(), future.ToArray(), fingerprints, InstallFiles.ExistingHash(configPath), ids, config);
    }

    public void Commit(ModpackInstallPlan plan, DownloadedModpack package, string serverKey, string display, JoinIntent? intent, IReadOnlyCollection<string> runningRoots, CancellationToken ct)
    {
        using var gate = LockIfNeeded(); GuardRoots();
        if (!DeliveryValidation.Id(plan.OperationId) || plan.Manifest != package.Manifest || File.Exists(JournalPath) || File.Exists(PendingPath)) throw new DeliveryException("RecoveryRequired");
        DeliveryValidation.Validate(package.Settings, package.Manifest);
        if (!ServerSettingsStore.IsSafe(new(serverKey, "Settings"))) throw new DeliveryException("UnsafePath");
        CheckPlan(plan, runningRoots);
        InstallFiles.Space(work, package.Manifest.ExpandedBytes + plan.Retire.Sum(o => DirectoryBytes(Path.Combine(modsRoot, o.Folder))) + DeliveryLimits.SpareBytes);
        InstallFiles.Space(stateRoot, plan.ConfigBytes.Length * 2L + package.Settings.Configs.Sum(c => (long)c.Content.Length) * 2 + DeliveryLimits.SpareBytes);
        string modStage = ModStage(plan.OperationId), configStage = ConfigStage(plan.OperationId);
        if (Directory.Exists(modStage) || Directory.Exists(configStage)) throw new DeliveryException("RecoveryRequired");
        ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(modStage); Directory.CreateDirectory(configStage);
        ModpackInstallJournal? journal = null;
        try
        {
            string extracted = Path.Combine(modStage, "staging"); SafeModArchive.ExtractValidated(package.ArchivePath, package.Manifest, extracted, ct);
            var moves = new List<InstallMove>();
            foreach (var old in plan.Retire) moves.Add(new("Retire", old.Folder, "", old.TreeHash));
            foreach (var item in plan.Mods.Where(m => m.ReuseRoot is null))
            {
                string staged = Path.Combine(extracted, "mods", item.Mod.FolderId);
                if (ModContent.Scan(staged).FilesHash != item.Mod.FilesHash) throw new DeliveryException("HashMismatch");
                moves.Add(new("Install", item.Folder, item.Mod.FolderId, TreeHash(staged)));
            }
            string settingsKey = serverKey + "_" + plan.OperationId;
            string settingsStage = Path.Combine(configStage, "settings");
            new ServerSettingsStore(settingsStage).Activate(settingsKey, display, package.Settings.Configs.Select(c => new ModConfig { PackageId = c.PackageId, Handle = c.Handle, Hash = c.Hash, Content = ByteString.CopyFrom(c.Content) }));
            var owned = ReadOwnership().Where(o => !plan.Retire.Any(r => r.Folder == o.Folder)).ToList();
            owned.AddRange(moves.Where(m => m.Kind == "Install").Select(m => new OwnedDeliveryMod(m.Folder, plan.Mods.Single(p => p.Folder == m.Folder).Mod.PackageId, m.Hash)));
            var writes = new List<InstallWrite>();
            void Stage(string name, byte[] bytes)
            {
                string path = StageFile(plan.OperationId, name, ".new"); InstallFiles.Write(path, bytes);
                byte[]? original = File.Exists(FilePath(name)) ? InstallFiles.Read(FilePath(name), DeliveryLimits.MaxManifestBytes) : null;
                if (original is not null) InstallFiles.Write(StageFile(plan.OperationId, name, ".bak"), original);
                writes.Add(new(name, original is null ? null : ModContent.Hash(original), ModContent.Hash(bytes)));
            }
            Stage("ActiveSettings", InstallFiles.Read(Path.Combine(settingsStage, "active.json"), DeliveryLimits.MaxManifestBytes));
            Stage("Ownership", DeliveryJson.Write(owned));
            if (intent is not null) Stage("JoinIntent", JsonSerializer.SerializeToUtf8Bytes(intent));
            Stage("ModsConfig", plan.ConfigBytes);
            string settingsHash = Directory.Exists(Path.Combine(settingsStage, settingsKey)) ? TreeHash(Path.Combine(settingsStage, settingsKey)) : "";
            journal = new(plan.OperationId, modsRoot, configPath, stateRoot, "Prepared", package.Manifest, moves.ToArray(), writes.ToArray(), intent is null ? null : writes.Single(w => w.Name == "JoinIntent").NewHash, settingsKey, settingsHash, LaunchId,
                intent is null ? serverKey : new ServerEndpoint(intent.Host, intent.Port).Id);
            Save(journal); InstallFiles.Write(PendingPath, DeliveryJson.Write(new Pending(plan.OperationId, ModContent.Hash(Encoding.UTF8.GetBytes(stateRoot)))));
            CheckPlan(plan, runningRoots); ct.ThrowIfCancellationRequested();
            journal = journal with { State = "Committing" }; Save(journal);
            for (int i = 0; i < journal.Moves.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); var move = journal.Moves[i]; GuardRoots();
                string source = MoveSource(journal.OperationId, move), target = MoveTarget(journal.OperationId, move);
                InstallFiles.NoLinks(source); InstallFiles.NoLinks(target);
                if (TreeHash(source) != move.Hash || Directory.Exists(target)) throw new DeliveryException("ExternalChange", source);
                if (move.Kind == "Retire" && Loaded(source, runningRoots)) throw new DeliveryException("LoadedMod", source);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); Directory.Move(source, target);
                journal.Moves[i] = move with { Done = true }; Save(journal); AfterStep?.Invoke("Move:" + move.Kind);
            }
            // Unique settings directory: no previous server or personal settings file is overwritten.
            if (Directory.Exists(Path.Combine(settingsStage, settingsKey)))
            {
                string destination = Path.Combine(stateRoot, "server-settings", settingsKey); InstallFiles.NoLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.Move(Path.Combine(settingsStage, settingsKey), destination);
            }
            for (int i = 0; i < journal.Writes.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); var write = journal.Writes[i];
                if (write.Name == "ModsConfig") CheckFuture(plan);
                string path = FilePath(write.Name), staged = StageFile(journal.OperationId, write.Name, ".new"), backup = StageFile(journal.OperationId, write.Name, ".current");
                GuardRoots(); InstallFiles.NoLinks(path); InstallFiles.NoLinks(backup);
                if (InstallFiles.ExistingHash(path) != write.OriginalHash || InstallFiles.Hash(staged) != write.NewHash) throw new DeliveryException("ExternalChange", path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (write.OriginalHash is null) File.Move(staged, path);
                else File.Replace(staged, path, backup);
                if (write.OriginalHash is not null && InstallFiles.Hash(backup) != write.OriginalHash) throw new DeliveryException("ExternalChange", backup);
                journal.Writes[i] = write with { Done = true }; Save(journal); AfterStep?.Invoke("File:" + write.Name);
            }
            ct.ThrowIfCancellationRequested();
            journal = journal with { State = "AwaitingRestart" }; Save(journal);
        }
        catch
        {
            if (journal is not null) Rollback(journal, runningRoots);
            else { DeleteOwnedDirectory(modStage); DeleteOwnedDirectory(configStage); }
            throw;
        }
    }
    private void CheckPlan(ModpackInstallPlan plan, IReadOnlyCollection<string> runningRoots)
    {
        GuardRoots();
        if (InstallFiles.ExistingHash(configPath) != plan.ConfigHash) throw new DeliveryException("ExternalChange", configPath);
        foreach (var pair in plan.Fingerprints) if (TreeHash(pair.Key) != pair.Value) throw new DeliveryException("ExternalChange", pair.Key);
        foreach (var old in plan.Retire) if (Loaded(Path.Combine(modsRoot, old.Folder), runningRoots)) throw new DeliveryException("LoadedMod", Path.Combine(modsRoot, old.Folder));
    }
    private static void CheckFuture(ModpackInstallPlan plan)
    {
        foreach (var item in plan.Future)
        {
            InstallFiles.NoLinks(item.Root);
            if (!Directory.Exists(item.Root)) throw new DeliveryException("PreparedMismatch", item.Root);
        }
        foreach (var mod in plan.Mods)
        {
            string path = mod.ReuseRoot ?? plan.Future.Single(m => m.PackageId == mod.Mod.PackageId).Root;
            var disk = ModMetadata.Read(path, mod.Mod.FolderId, mod.Mod.Position);
            if (disk.FilesHash != mod.Mod.FilesHash || disk.ContentHash != mod.Mod.ContentHash || disk.Version != mod.Mod.Version) throw new DeliveryException("PreparedMismatch", path);
        }
    }
    public bool Recover(IReadOnlyCollection<string> runningRoots)
    {
        GuardRoots();
        if (!File.Exists(JournalPath) && !File.Exists(PendingPath)) return false;
        using var gate = LockIfNeeded(); GuardRoots();
        var journal = ReadJournal();
        if (journal is null) { if (File.Exists(PendingPath)) throw new DeliveryException("RecoveryRequired"); return false; }
        if (journal.State == "VerifiedAfterRestart") { Finish(journal); return false; }
        if (journal.State == "AwaitingRestart") return true;
        Rollback(journal, runningRoots); return false;
    }
    public bool VerifyAfterRestart(IReadOnlyList<ModRef> loaded, IReadOnlyList<ModSettingsHash> settings, IReadOnlyDictionary<string, string> loadedRoots, bool loginSucceeded)
    {
        using var gate = LockIfNeeded(); var journal = ReadJournal(); if (journal?.State != "AwaitingRestart" || journal.LaunchId == LaunchId || !loginSucceeded) return false;
        var manifest = journal.Manifest;
        if (loaded.GroupBy(m => DeliveryValidation.Normalize(m.PackageId)).Any(g => g.Count() > 1)) return false;
        foreach (var mod in manifest.Mods)
        {
            var actual = loaded.SingleOrDefault(m => DeliveryValidation.Normalize(m.PackageId) == mod.PackageId);
            if (actual is null || actual.FilesHash != mod.FilesHash || actual.Version != mod.Version) return false;
            if (!loadedRoots.TryGetValue(mod.PackageId, out string? root)) return false;
            var disk = ModMetadata.Read(root, mod.FolderId, mod.Position);
            if (disk.ContentHash != mod.ContentHash || disk.FilesHash != mod.FilesHash || disk.Version != mod.Version) return false;
        }
        var store = new ServerSettingsStore(Path.Combine(stateRoot, "server-settings")); var snapshot = store.ReadActive();
        var activation = journal.Writes.Single(w => w.Name == "ActiveSettings");
        if (snapshot is null || snapshot.Server != journal.SettingsKey || InstallFiles.ExistingHash(FilePath("ActiveSettings")) != activation.NewHash) return false;
        // The complete approved snapshot stays intact even for optional mods this player does not load.
        var shared = snapshot.Mods.Select(k => new SharedSettingHash(k.PackageId, k.Handle, ModContent.SettingsHash(InstallFiles.Read(store.PathFor(snapshot.Server, k), Limits.MaxModConfigBytes)))).ToArray();
        if (DeliveryValidation.Describe(manifest.GameVersion, manifest.Entries, shared).CatalogHash != manifest.CatalogHash) return false;
        var active = loaded.Select(m => DeliveryValidation.Normalize(m.PackageId)).ToHashSet();
        foreach (var config in shared)
        {
            if (!active.Contains(config.PackageId) && manifest.Entries.Any(e => e.PackageId == config.PackageId && e.Kind == EntryKind.Allowed)) continue;
            var actual = settings.Where(s => DeliveryValidation.Normalize(s.PackageId) == config.PackageId && s.Handle == config.Handle).ToArray();
            if (actual.Length != 1 || actual[0].Hash != config.Hash) return false;
        }
        if (ModpackComparison.Compare(manifest.Entries.Select(e => e.ToProto()).ToList(), loaded) is not null) return false;
        Save(journal with { State = "VerifiedAfterRestart" }); Finish(journal); return true;
    }
    public bool IsPendingForServer(string serverId) => ReadJournal() is { State: "AwaitingRestart" } journal && journal.TargetServerId == serverId;
    public void Restore(IReadOnlyCollection<string> runningRoots)
    {
        using var gate = LockIfNeeded(); if (ReadJournal() is { } journal) Rollback(journal, runningRoots);
    }
    private void Rollback(ModpackInstallJournal journal, IReadOnlyCollection<string> runningRoots)
    {
        try
        {
            GuardRoots();
            foreach (var write in journal.Writes)
            {
                string backup = StageFile(journal.OperationId, write.Name, ".bak"), staged = StageFile(journal.OperationId, write.Name, ".new"), replaced = StageFile(journal.OperationId, write.Name, ".current");
                if (File.Exists(backup) && InstallFiles.ExistingHash(backup) != write.OriginalHash || File.Exists(staged) && InstallFiles.Hash(staged) != write.NewHash) throw new DeliveryException("ExternalChange");
                if (File.Exists(replaced) && InstallFiles.Hash(replaced) != write.OriginalHash) throw new DeliveryException("ExternalChange", replaced);
            }
            foreach (var write in journal.Writes.Reverse())
            {
                string path = FilePath(write.Name), backup = StageFile(journal.OperationId, write.Name, ".bak");
                string? current = InstallFiles.ExistingHash(path);
                if (current == write.OriginalHash) continue;
                if (current != write.NewHash) throw new DeliveryException("ExternalChange", path);
                if (write.OriginalHash is null) File.Delete(path);
                else
                {
                    if (InstallFiles.ExistingHash(backup) != write.OriginalHash) throw new DeliveryException("ExternalChange", backup);
                    File.Move(backup, path, true);
                }
            }
            foreach (var move in journal.Moves.Reverse())
            {
                string source = MoveSource(journal.OperationId, move), target = MoveTarget(journal.OperationId, move);
                if (!Directory.Exists(target)) continue;
                if (Directory.Exists(source) || TreeHash(target) != move.Hash || Loaded(target, runningRoots)) throw new DeliveryException("ExternalChange", target);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.Move(target, source);
            }
            string settings = Path.Combine(stateRoot, "server-settings", journal.SettingsKey);
            if (Directory.Exists(settings))
            {
                if (TreeHash(settings) != journal.SettingsHash) throw new DeliveryException("ExternalChange", settings);
                DeleteOwnedDirectory(settings);
            }
            Finish(journal);
        }
        catch
        {
            Save(journal with { State = "RecoveryRequired" }); throw;
        }
    }
    private string MoveSource(string op, InstallMove move) => move.Kind == "Retire" ? Path.Combine(modsRoot, move.Folder) : Path.Combine(ModStage(op), "staging", "mods", move.StageFolder);
    private string MoveTarget(string op, InstallMove move) => move.Kind == "Retire" ? Path.Combine(ModStage(op), "rollback", move.Folder) : Path.Combine(modsRoot, move.Folder);
    private void Save(ModpackInstallJournal journal) => InstallFiles.Write(JournalPath, DeliveryJson.Write(journal));
    private ModpackInstallJournal? ReadJournal()
    {
        if (!File.Exists(JournalPath)) return null;
        var j = DeliveryJson.Read<ModpackInstallJournal>(InstallFiles.Read(JournalPath, DeliveryLimits.MaxManifestBytes * 3), DeliveryLimits.MaxManifestBytes * 3);
        if (!DeliveryValidation.Id(j.OperationId) || j.ModsRoot != modsRoot || j.ConfigPath != configPath || j.StateRoot != stateRoot || j.Moves is null || j.Writes is null
            || j.Moves.Length > DeliveryLimits.MaxMods * 2 || j.Writes.Length > 4 || j.Moves.Any(m => m is null || m.Kind is not ("Retire" or "Install") || !ManagedFolder(m.Folder) || m.Kind == "Install" && !DeliveryValidation.Id(m.StageFolder) || !DeliveryValidation.Sha256(m.Hash))
            || j.Writes.Any(w => w is null || w.Name is not ("Ownership" or "ActiveSettings" or "JoinIntent" or "ModsConfig") || w.OriginalHash is not null && !DeliveryValidation.Sha256(w.OriginalHash) || !DeliveryValidation.Sha256(w.NewHash))
            || j.Writes.DistinctBy(w => w.Name).Count() != j.Writes.Length || !DeliveryValidation.Id(j.LaunchId) || !j.SettingsKey.EndsWith("_" + j.OperationId, StringComparison.Ordinal)
            || !ServerSettingsStore.IsSafe(new(j.SettingsKey, "Settings")) || j.SettingsHash.Length > 0 && !DeliveryValidation.Sha256(j.SettingsHash)
            || j.TargetServerId is null || j.TargetServerId.Length is 0 or > 512 || j.TargetServerId.Any(char.IsControl)) throw new DeliveryException("RecoveryRequired");
        DeliveryValidation.Validate(j.Manifest, true); return j;
    }
    private OwnedDeliveryMod[] ReadOwnership()
    {
        if (!File.Exists(OwnershipPath)) return [];
        var list = DeliveryJson.Read<OwnedDeliveryMod[]>(InstallFiles.Read(OwnershipPath, DeliveryLimits.MaxManifestBytes));
        if (list.Length > DeliveryLimits.MaxMods || list.Any(m => m is null || !ManagedFolder(m.Folder) || !DeliveryValidation.Sha256(m.TreeHash) || m.PackageId != DeliveryValidation.Normalize(m.PackageId))
            || list.DistinctBy(m => m.Folder).Count() != list.Length) throw new DeliveryException("RecoveryRequired");
        return list;
    }
    private void Finish(ModpackInstallJournal journal)
    {
        // Backups disappear only after a complete rollback or a fresh matching login.
        foreach (var write in journal.Writes)
        {
            string backup = StageFile(journal.OperationId, write.Name, ".bak");
            if (File.Exists(backup) && InstallFiles.Hash(backup) != write.OriginalHash) throw new DeliveryException("ExternalChange", backup);
            string replaced = StageFile(journal.OperationId, write.Name, ".current");
            if (File.Exists(replaced) && InstallFiles.Hash(replaced) != write.OriginalHash) throw new DeliveryException("ExternalChange", replaced);
        }
        foreach (var move in journal.Moves.Where(m => m.Kind == "Retire"))
        {
            string backup = MoveTarget(journal.OperationId, move);
            if (Directory.Exists(backup) && TreeHash(backup) != move.Hash) throw new DeliveryException("ExternalChange", backup);
        }
        DeleteOwnedDirectory(ModStage(journal.OperationId)); DeleteOwnedDirectory(ConfigStage(journal.OperationId));
        if (File.Exists(PendingPath))
        {
            var marker = DeliveryJson.Read<Pending>(InstallFiles.Read(PendingPath, DeliveryLimits.SimpleBodyBytes), DeliveryLimits.SimpleBodyBytes);
            if (marker.OperationId != journal.OperationId || marker.ProfileHash != ModContent.Hash(Encoding.UTF8.GetBytes(stateRoot))) throw new DeliveryException("RecoveryRequired");
            File.Delete(PendingPath);
        }
        File.Delete(JournalPath);
    }
    private static void DeleteOwnedDirectory(string path)
    {
        InstallFiles.NoLinks(path);
        if (!Directory.Exists(path)) return;
        _ = ModMetadata.Tree(path).ToArray(); Directory.Delete(path, true);
    }
    private static long DirectoryBytes(string path) => ModMetadata.Files(path).Sum(f => new FileInfo(f).Length);
    internal static string TreeHash(string root)
    {
        InstallFiles.NoLinks(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in ModMetadata.Tree(root).OrderBy(p => Path.GetRelativePath(root, p), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            bool directory = Directory.Exists(path); hash.AppendData(Encoding.UTF8.GetBytes((directory ? "d:" : "f:") + relative + "\0"));
            if (!directory) { using var file = File.OpenRead(path); hash.AppendData(SHA256.HashData(file)); }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static byte[] ConfigWithOrder(byte[]? original, string[] active)
    {
        XDocument doc;
        try
        {
            using var input = new MemoryStream(original ?? Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7</version></ModsConfigData>"));
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = DeliveryLimits.MaxManifestBytes });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { throw new DeliveryException("InvalidConfig"); }
        if (doc.Root?.Name != "ModsConfigData") throw new DeliveryException("InvalidConfig");
        doc.Root.Elements("activeMods").Remove(); doc.Root.Add(new XElement("activeMods", active.Select(id => new XElement("li", id))));
        using var output = new MemoryStream(); using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true })) doc.Save(writer);
        return output.ToArray();
    }
    private sealed record Pending(string OperationId, string ProfileHash);
    public void Dispose() { operationLock?.Dispose(); operationLock = null; }
}
