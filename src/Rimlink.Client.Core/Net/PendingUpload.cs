using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rimlink.Client.Net;

public sealed record SaveSnapshot(string Id, string Path, SaveMeta Meta);

/// <summary>Pending intent precedes the game write; acknowledgements clear only their immutable snapshot.</summary>
public sealed class PendingUpload
{
    private sealed record Journal(string? Current = null, string? Preparing = null, bool HadPending = false, long Generation = 0, long Epoch = 0);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string directory, flag, journal;
    private readonly object gate;
    public PendingUpload(string saveDir)
    {
        directory = Path.GetFullPath(saveDir); flag = Path.Combine(directory, "pending.flag"); journal = Path.Combine(directory, "pending-snapshot.json");
        gate = Gates.GetOrAdd(directory, _ => new object());
    }
    public bool IsSet { get { lock (gate) { var state = Read(); return File.Exists(flag) || state.Current is not null || state.Preparing is not null; } } }
    public void Set() { lock (gate) Write(flag, System.Text.Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O"))); }
    public void Clear() { lock (gate) { if (File.Exists(flag)) File.Delete(flag); if (File.Exists(journal)) File.Delete(journal); } }

    public void BeginSnapshot(string id, long generation, long epoch)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("invalid snapshot id", nameof(id));
        lock (gate)
        {
            var old = Read();
            if (old.Preparing is not null) throw new IOException("another save is still being prepared");
            Store(old with { Preparing = id, HadPending = File.Exists(flag) || old.Current is not null, Generation = generation, Epoch = epoch });
        }
    }

    public SaveSnapshot CaptureSnapshot(string primary)
    {
        lock (gate)
        {
            var state = Read();
            if (state.Preparing is not { } id) throw new IOException("no snapshot write was prepared");
            var snapshot = Copy(primary, id);
            if (snapshot.Meta.Recovery?.SnapshotId != id || snapshot.Meta.SaveGeneration != state.Generation || snapshot.Meta.Recovery.WriteEpoch != state.Epoch)
                throw new IOException("the game did not write the prepared snapshot");
            Store(state with { Current = id, Preparing = null, HadPending = false });
            Write(flag, System.Text.Encoding.ASCII.GetBytes(id));
            return snapshot;
        }
    }

    public SaveSnapshot? RecoverSnapshot(string primary)
    {
        lock (gate)
        {
            var state = Read();
            if (state.Preparing is { } id)
            {
                if (File.Exists(primary) && SaveMetaReader.Read(primary).Recovery?.SnapshotId == id) return CaptureSnapshot(primary);
                Store(state with { Preparing = null });
                if (!state.HadPending && state.Current is null && File.Exists(flag)) File.Delete(flag);
                state = Read();
            }
            if (state.Current is { } current)
            {
                string path = SnapshotPath(current);
                if (!File.Exists(path))
                {
                    if (!File.Exists(primary) || SaveMetaReader.Read(primary).Recovery?.SnapshotId != current)
                        throw new IOException("the pending snapshot is missing; recovery files were preserved");
                    Copy(primary, current);
                }
                var meta = SaveMetaReader.Read(path);
                if (meta.Recovery is { } recovery && recovery.SnapshotId != current) throw new IOException("pending snapshot identity changed");
                return new(current, path, meta);
            }
            if (!File.Exists(flag)) return null;
            if (!File.Exists(primary)) throw new IOException("the legacy pending save is missing");
            string legacyId = SaveMetaReader.Read(primary).Recovery?.SnapshotId ?? Guid.NewGuid().ToString("N");
            var legacy = Copy(primary, legacyId);
            Store(new Journal(Current: legacyId, Generation: legacy.Meta.SaveGeneration, Epoch: legacy.Meta.Recovery?.WriteEpoch ?? 0));
            return legacy;
        }
    }

    public void CompleteSnapshot(SaveSnapshot snapshot)
    {
        lock (gate)
        {
            var state = Read();
            if (state.Current is null && state.Preparing is null || state.Generation != snapshot.Meta.SaveGeneration || state.Epoch != (snapshot.Meta.Recovery?.WriteEpoch ?? 0)) return;
            if (state.Current == snapshot.Id)
            {
                state = state with { Current = null, HadPending = false }; Store(state);
                if (state.Preparing is null && File.Exists(flag)) File.Delete(flag);
            }
            if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path);
        }
    }

    /// <summary>After a superseding queued upload is accepted, retire copies not referenced by active or preserved recovery.</summary>
    public void PruneSnapshots(IEnumerable<string?> keep)
    {
        lock (gate)
        {
            var protectedIds = keep.Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
            var state = Read(); protectedIds.Add(state.Current); protectedIds.Add(state.Preparing);
            foreach (string preserved in Directory.Exists(directory) ? Directory.GetFiles(directory, "pending-snapshot.json.*") : [])
            {
                try
                {
                    var prior = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(preserved), Json);
                    if (prior is null) return;
                    protectedIds.Add(prior.Current); protectedIds.Add(prior.Preparing);
                }
                catch (JsonException) { return; } // an unresolved recovery file is never a reason to erase its snapshots
            }
            foreach (string path in Directory.Exists(directory) ? Directory.GetFiles(directory, "upload-*.rws") : [])
            {
                string id = Path.GetFileNameWithoutExtension(path)["upload-".Length..];
                if (Guid.TryParseExact(id, "N", out _) && !protectedIds.Contains(id)) File.Delete(path);
            }
        }
    }

    public string KeepLocalSave(string savePath, string reason)
    {
        lock (gate)
        {
            string suffix = $".{reason}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            string kept = Path.ChangeExtension(savePath, suffix + ".rws");
            File.Move(savePath, kept);
            if (File.Exists(journal)) File.Move(journal, journal + suffix);
            if (File.Exists(flag)) File.Move(flag, flag + suffix);
            return kept;
        }
    }

    private string SnapshotPath(string id) => Path.Combine(directory, "upload-" + id + ".rws");
    private SaveSnapshot Copy(string primary, string id)
    {
        string path = SnapshotPath(id);
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path); using var source = File.OpenRead(primary);
            if (!SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(source))) throw new IOException("snapshot identity was reused");
        }
        else
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var input = File.OpenRead(primary)) using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(true); }
                File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return new(id, path, SaveMetaReader.Read(path));
    }

    private Journal Read()
    {
        if (!File.Exists(journal)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journal), Json) ?? throw new JsonException("empty snapshot journal");
            if (state.Current is { } current && !Guid.TryParseExact(current, "N", out _) || state.Preparing is { } preparing && !Guid.TryParseExact(preparing, "N", out _))
                throw new JsonException("invalid snapshot identity");
            return state;
        }
        catch (JsonException ex) { throw new IOException("the pending snapshot journal is unreadable; it was preserved", ex); }
    }
    private void Store(Journal state)
    {
        if (state.Current is null && state.Preparing is null) { if (File.Exists(journal)) File.Delete(journal); }
        else Write(journal, JsonSerializer.SerializeToUtf8Bytes(state, Json));
    }
    private void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(directory); string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
