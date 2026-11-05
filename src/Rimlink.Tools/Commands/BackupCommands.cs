using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Rimlink.Server.Backup;
using Rimlink.Server.Hosting;

namespace Rimlink.Tools.Commands;

public static class BackupCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, Converters = { new AnchorBytesConverter() }
    };

    public static Command Backup(TextWriter? outputWriter = null)
    {
        var data = Required("--data", "Existing server data directory (protected CLI capture requires a stopped server)");
        var output = new Option<string>("--out") { Description = "Archive to write (default: <data>/backups/rimlink-<utc>.tar.zst)" };
        var anchor = new Option<string>("--anchor") { Description = "External authority directory (required for protected history)" };
        var storage = new Option<string>("--storage-contract") { Description = "Explicit qualified storage contract, e.g. sqlite-local-v1" };
        var linux = new Option<string>("--qualified-linux-filesystem") { Description = "Explicitly qualified Linux filesystem (ext4, xfs or btrfs)" };
        var command = new Command("backup", "Capture one coherent cut; use admin backup online when protected history exists") { data, output, anchor, storage, linux };
        command.SetAction(async (parse, ct) => await Run(outputWriter, async writer =>
        {
            string dataDir = parse.GetValue(data)!;
            string file = parse.GetValue(output) ?? Path.Combine(dataDir, "backups", $"rimlink-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.zst");
            if (parse.GetValue(anchor) is not { } authorityDirectory)
                BackupArchive.Create(dataDir, file); // S-held legacy guard refuses protected history even when the feature is off.
            else await StoppedBackup.CreateAsync(dataDir, file, new OutpostOptions { AnchorDirectory = authorityDirectory,
                StorageContract = parse.GetValue(storage) ?? "", QualifiedLinuxFileSystem = parse.GetValue(linux) ?? "" }, ct);
            await writer.WriteLineAsync($"backup written: {file} ({new FileInfo(file).Length / 1024} KB)");
            return 0;
        }));
        return command;
    }

    public static Command Restore(TextWriter? outputWriter = null)
    {
        // Legacy syntax remains guarded; parent options are not required on the explicit protected subcommands.
        var data = new Option<string>("--data") { Description = "Server data directory (server must be stopped)" };
        var from = new Option<string>("--from") { Description = "Legacy archive to restore" };
        var command = new Command("restore", "Legacy restore, or explicit inspect/execute/resume/backout of protected history") { data, from };
        command.SetAction(parse => Run(outputWriter, async writer =>
        {
            string root = parse.GetValue(data) ?? throw new ArgumentException("--data is required");
            string archive = parse.GetValue(from) ?? throw new ArgumentException("--from is required");
            BackupArchive.Restore(archive, root);
            await writer.WriteLineAsync("restored");
            return 0;
        }));
        command.Add(Inspect(outputWriter));
        command.Add(Execute(outputWriter));
        command.Add(Resume(outputWriter));
        command.Add(Backout(outputWriter));
        command.Add(InspectBackout(outputWriter));
        command.Add(PreviewDigest(outputWriter, backout: false));
        command.Add(PreviewDigest(outputWriter, backout: true));
        return command;
    }

    private static Command Inspect(TextWriter? output)
    {
        var data = Required("--data", "Destination data directory (not created by inspection)");
        var anchor = Required("--anchor", "Destination external authority directory (not created by inspection)");
        var from = Required("--from", "Archive to inspect without changing the destination");
        var previous = new Option<string>("--previous-anchor") { Description = "Actual previous authority location to observe independently for LostAnchorRecovery; not a claim that it is unavailable" };
        var command = new Command("inspect", "Verify the full archive and observe its cut separately from the actual external authority") { data, anchor, from, previous };
        command.SetAction(async (parse, ct) => await Run(output, async writer =>
        {
            PreviousAnchorReference? reference = parse.GetValue(previous) is { } location
                ? new(PreviousAnchorReferenceKind.KnownLocation, location, null) : null;
            var inspected = await new OutpostRestoreProcedure().InspectAsync(parse.GetValue(from)!, parse.GetValue(data)!, parse.GetValue(anchor)!, ct, reference);
            await WriteJson(writer, inspected);
            return 0;
        }));
        return command;
    }

    private static Command Execute(TextWriter? output)
    {
        var request = Required("--request", "Bounded JSON RestoreRequest from an explicitly selected inspection/mode/new boundary");
        var confirm = Required("--confirm", "Separate exact SHA-256 selection digest in hex; embedded confirmation is not used");
        var command = new Command("execute", "Execute only the separately confirmed restore selection with the server stopped") { request, confirm };
        command.SetAction(async (parse, ct) => await Run(output, async writer =>
        {
            var selected = await ReadJson<RestoreRequest>(parse.GetValue(request)!, ct);
            var result = await new OutpostRestoreProcedure().ExecuteAsync(selected with { ConfirmationDigest = Digest(parse.GetValue(confirm)!) }, ct);
            await WriteJson(writer, result);
            return Exit(result);
        }));
        return command;
    }

    private static Command Resume(TextWriter? output)
    {
        var data = Required("--data", "Existing destination data directory");
        var anchor = Required("--anchor", "Existing external authority directory");
        var id = Required("--restore-id", "Exact interrupted restore ID (completion only; never backout)");
        var command = new Command("resume", "Complete the exact retained restore without creating a new selection or boundary") { data, anchor, id };
        command.SetAction(async (parse, ct) => await Run(output, async writer =>
        {
            var result = await new OutpostRestoreProcedure().ResumeAsync(parse.GetValue(data)!, parse.GetValue(anchor)!, parse.GetValue(id)!, ct);
            await WriteJson(writer, result);
            return Exit(result);
        }));
        return command;
    }

    private static Command Backout(TextWriter? output)
    {
        var request = Required("--request", "Bounded JSON RestoreBackoutRequest with exact inspected intent/phase/revision");
        var confirm = Required("--confirm", "Separate exact SHA-256 backout action digest in hex");
        var command = new Command("backout", "Explicit confirmed backout to the verified previous set, only before external publication") { request, confirm };
        command.SetAction(async (parse, ct) => await Run(output, async writer =>
        {
            var selected = await ReadJson<RestoreBackoutRequest>(parse.GetValue(request)!, ct);
            var result = await new OutpostRestoreProcedure().BackoutAsync(selected with { ConfirmationDigest = Digest(parse.GetValue(confirm)!) }, ct);
            await WriteJson(writer, result);
            return Exit(result);
        }));
        return command;
    }

    private static Command InspectBackout(TextWriter? output)
    {
        var data = Required("--data", "Existing destination data directory");
        var anchor = Required("--anchor", "Existing external authority directory");
        var id = Required("--restore-id", "Exact interrupted restore ID");
        var command = new Command("backout-inspect", "Print the exact retained action selection without confirming or executing it") { data, anchor, id };
        command.SetAction(async parse => await Run(output, async writer =>
        {
            await WriteJson(writer, new OutpostRestoreProcedure().InspectBackout(parse.GetValue(data)!, parse.GetValue(anchor)!, parse.GetValue(id)!));
            return 0;
        }));
        return command;
    }

    private static Command PreviewDigest(TextWriter? output, bool backout)
    {
        var request = Required("--request", "Bounded JSON selection; computing its digest does not confirm or execute it");
        var command = new Command(backout ? "backout-digest" : "digest", "Print the exact typed action digest without writing or executing anything") { request };
        command.SetAction(async (parse, ct) => await Run(output, async writer =>
        {
            byte[] digest = backout
                ? OutpostRestoreJournalCodec.BackoutDigest(await ReadJson<RestoreBackoutRequest>(parse.GetValue(request)!, ct))
                : OutpostRestoreJournalCodec.SelectionDigest(await ReadJson<RestoreRequest>(parse.GetValue(request)!, ct));
            await writer.WriteLineAsync(Convert.ToHexString(digest));
            return 0;
        }));
        return command;
    }

    // Anchor heads contain protobuf ByteString, a read-only collection that STJ cannot populate by default.
    // Keep the existing byte-array inspection representation; this codec never alters stored journal evidence.
    private sealed class AnchorBytesConverter : JsonConverter<ByteString>
    {
        public override ByteString Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("anchor bytes must be an array");
            var bytes = new List<byte>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (bytes.Count >= 4096 || reader.TokenType != JsonTokenType.Number || !reader.TryGetByte(out byte value))
                    throw new JsonException("anchor byte-array limit or value");
                bytes.Add(value);
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("incomplete anchor bytes");
            return ByteString.CopyFrom(bytes.ToArray());
        }
        public override void Write(Utf8JsonWriter writer, ByteString value, JsonSerializerOptions options)
        {
            if (value.Length > 4096) throw new JsonException("anchor byte-array limit");
            writer.WriteStartArray();
            foreach (byte item in value) writer.WriteNumberValue(item);
            writer.WriteEndArray();
        }
    }

    private static Option<string> Required(string name, string description) => new(name) { Description = description, Required = true };
    private static int Exit(RestoreResult result) => result.Outcome is RestoreOutcome.Completed or RestoreOutcome.BackedOut ? 0 : 1;
    private static Task WriteJson<T>(TextWriter writer, T value) => writer.WriteLineAsync(JsonSerializer.Serialize(value, Json));
    private static byte[] Digest(string hex) => hex.Length == 64 ? Convert.FromHexString(hex) : throw new ArgumentException("confirmation must be exactly 64 hex characters");
    private static async Task<T> ReadJson<T>(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (stream.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("restore-request-size-limit");
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct) ?? throw new InvalidDataException("restore-request-missing");
    }
    private static async Task<int> Run(TextWriter? output, Func<TextWriter, Task<int>> action)
    {
        var writer = output ?? Console.Out;
        try { return await action(writer); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { error = error.Message }, Json));
            return 1;
        }
    }
}
