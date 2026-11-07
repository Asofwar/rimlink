using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Tools.Commands;

public static partial class AdminCommand
{
    private const int OutpostRequestLimit = 2 * 1024 * 1024;
    private static async Task<string> RunOutposts(RestClient rest, string[] args)
    {
        if (args is ["inspect", .. var filters])
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < filters.Length; i += 2)
            {
                if (i + 1 == filters.Length) throw OutpostUsage();
                string key = filters[i] switch { "--player" => "playerId", "--operation" => "operationId", "--limit" => "limit", "--cursor" => "cursor", _ => throw OutpostUsage() };
                if (!values.TryAdd(key, filters[i + 1])) throw OutpostUsage();
            }
            string query = string.Join('&', values.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
            return await rest.Get("outposts" + (query.Length == 0 ? "" : "?" + query));
        }
        if (args is ["manifest", "inspect", var id]) return await rest.Get("outposts/manifests/" + Uri.EscapeDataString(id));
        if (args is ["manifest", "upload", var file]) return await UploadOutpostManifest(rest, file);
        bool manifest = args is ["manifest", "confirm", "--request", _];
        int offset = manifest ? 1 : 0;
        if (args.Length != offset + 3 || args[offset + 1] != "--request") throw OutpostUsage();
        string action = args[offset];
        string route = manifest ? "manifests/confirm" : action switch
        { "preview" or "initialize" or "activate-domain-format" or "resolve" => action, _ => throw OutpostUsage() };
        // The file is already retained before a mutating request. No fresh ID/preview/automatic retry is issued here.
        byte[] bytes = await ReadOutpostFile(args[offset + 2], OutpostRequestLimit);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32, AllowDuplicateProperties = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw OutpostUsage();
        if (action == "preview") return await rest.Post("outposts/preview", root);
        string[] allowed = ["command", "intentDigest", "confirmed", "originalInput", "intent", "blockers"];
        if (root.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))
            || !root.TryGetProperty("confirmed", out var confirmation) || confirmation.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The retained command file must explicitly set confirmed=true; a stale preview needs a separate human confirmation.");
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        { ["command"] = root.GetProperty("command"), ["intentDigest"] = root.GetProperty("intentDigest"), ["confirmed"] = confirmation };
        if (root.TryGetProperty("originalInput", out var sidecar)) body.Add("originalInput", sidecar);
        return await rest.Post("outposts/" + route, body);
    }

    private static async Task<string> UploadOutpostManifest(RestClient rest, string path)
    {
        const int maximum = 24 * 1024 * 1024, chunkSize = 256 * 1024;
        byte[] bytes = await ReadOutpostFile(path, maximum);
        using (var input = CodedInputStream.CreateWithLimits(new MemoryStream(bytes, false), maximum, 32))
            _ = OutpostManifestDocument.Parser.ParseFrom(input);
        string upload = Guid.NewGuid().ToString("D"); var hash = ByteString.CopyFrom(SHA256.HashData(bytes)); string? last = null;
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var chunk = new OutpostManifestChunk { UploadId = upload, Sha256 = hash, TotalBytes = bytes.Length, Offset = offset,
                Data = ByteString.CopyFrom(bytes, offset, Math.Min(chunkSize, bytes.Length - offset)) };
            using var json = JsonDocument.Parse(JsonFormatter.Default.Format(chunk));
            last = await rest.Post("outposts/manifests/chunks", json.RootElement);
            var reply = JsonParser.Default.Parse<ManifestPreview>(last);
            if (reply.ReceivedBytes != offset + chunk.Data.Length || reply.Complete != (reply.ReceivedBytes == bytes.Length)
                || reply.Complete && reply.Digest != Convert.ToHexStringLower(hash.Span)) throw new InvalidDataException("Manifest upload acknowledgement mismatch; no confirmation was sent.");
        }
        return last!;
    }

    private static async Task<byte[]> ReadOutpostFile(string path, int limit)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 || stream.Length > limit) throw new InvalidDataException("Outpost request file exceeds its bound.");
        using var target = new MemoryStream(); await stream.CopyToAsync(target);
        if (target.Length > limit) throw new InvalidDataException("Outpost request file changed beyond its bound.");
        return target.ToArray();
    }
    private static InvalidOperationException OutpostUsage() => new("outposts inspect [--player ID] [--operation ID] [--limit N] [--cursor VALUE] | preview|initialize|activate-domain-format|resolve --request FILE | manifest inspect ID|upload FILE|confirm --request FILE");
}
