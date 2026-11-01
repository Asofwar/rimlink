using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rimlink.Client.Net;

namespace Rimlink.Client.Diagnostics;

public enum DiagnosticLiveRole { None, Host, Guest, Visitor }
public sealed record DiagnosticSnapshot(ConnectionState Connection = ConnectionState.Disconnected, UploadSnapshot? Upload = null,
    DiagnosticLiveRole LiveRole = DiagnosticLiveRole.None, int LiveSnapshotVersion = 0, int PendingRequests = 0, bool PendingSave = false,
    Protocol.V1.LiveState? LiveState = null, int ReconnectingSeconds = 0, int LiveSpeed = 0, int PausedBy = 0);
public sealed record DiagnosticMod(string PackageId, string Version);

/// <summary>Only typed allowlisted data crosses this boundary. No exception text, settings, saves or raw logs.</summary>
public sealed class DiagnosticBundle
{
    public const int MaxMods = 128;
    public const int MaxZipBytes = 64 * 1024;
    public const int MaxEntryBytes = 48 * 1024;
    public const int MaxPreviewBytes = 64 * 1024;
    public string Preview { get; }
    public byte[] ZipBytes { get; }

    private DiagnosticBundle(string preview, byte[] zip) => (Preview, ZipBytes) = (preview, zip);

    public static DiagnosticBundle Create(DiagnosticSnapshot snapshot, IEnumerable<DiagnosticMod> mods, DiagnosticJournal journal)
    {
        var bounded = mods.Take(MaxMods + 1).ToArray();
        var upload = snapshot.Upload ?? new UploadSnapshot(null, 0, UploadFailure.None);
        string json = JsonSerializer.Serialize(new
        {
            format = 1,
            clientVersion = ClientInfo.Version,
            protocolVersion = ClientInfo.ProtocolVersion,
            gameVersion = "1.7",
            connection = SafeEnum(snapshot.Connection),
            reconnectingSeconds = Math.Clamp(snapshot.ReconnectingSeconds, 0, 31_536_000),
            upload = new { lastSuccessUtc = upload.LastSuccessUtc, version = Math.Max(0, upload.Version), failure = SafeEnum(upload.Failure) },
            liveRole = SafeEnum(snapshot.LiveRole),
            liveState = snapshot.LiveState is { } state ? SafeEnum(state) : "None",
            liveSnapshotVersion = Math.Max(0, snapshot.LiveSnapshotVersion),
            liveSpeed = Math.Clamp(snapshot.LiveSpeed, 0, 3),
            pausedBy = Math.Clamp(snapshot.PausedBy, 0, 3),
            pendingRequests = Math.Clamp(snapshot.PendingRequests, 0, 1_000_000),
            pendingSave = snapshot.PendingSave,
            mods = bounded.Take(MaxMods).Select((m, i) => new { position = i, package = SafePackage(m.PackageId), version = m.PackageId == ClientInfo.ModPackageId ? ClientInfo.Version : "omitted" }),
            modsTruncated = bounded.Length > MaxMods,
            privacy = "Unrecognized mod identifiers are anonymous. Free-form log details are omitted.",
        }, new JsonSerializerOptions { WriteIndented = true });
        string logs = string.Join("\n", journal.Snapshot().Select(e => $"{e.Utc:O} {SafeEnum(e.Severity)} details omitted"));
        string preview = "diagnostics.json\n" + json + "\n\nevents.log\n" + logs;
        if (Encoding.UTF8.GetByteCount(json) > MaxEntryBytes || Encoding.UTF8.GetByteCount(logs) > MaxEntryBytes ||
            Encoding.UTF8.GetByteCount(preview) > MaxPreviewBytes) throw new InvalidOperationException("Diagnostic limit exceeded.");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "diagnostics.json", json);
            Write(zip, "events.log", logs);
        }
        if (memory.Length > MaxZipBytes) throw new InvalidOperationException("Diagnostic limit exceeded.");
        return new(preview, memory.ToArray());
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string SafeEnum<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";

    // Mod metadata is untrusted text too: a private mod can use a login, path or token as its package id/version.
    private static string SafePackage(string id) => id switch
    {
        "ludeon.rimworld" or "ludeon.rimworld.royalty" or "ludeon.rimworld.ideology" or "ludeon.rimworld.biotech" or
        "ludeon.rimworld.anomaly" or "ludeon.rimworld.odyssey" or "brrainz.harmony" or "asofwar.rimlink" => id,
        _ => "anonymous-mod",
    };

}
