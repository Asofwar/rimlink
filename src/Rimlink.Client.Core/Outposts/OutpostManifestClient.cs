using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>Administrator staging is separate from confirmation; player reads expose confirmed current geography only.</summary>
public sealed class OutpostManifestClient(RimlinkConnection connection)
{
    public const int MaxDocumentBytes = 24 * 1024 * 1024, ChunkBytes = 256 * 1024;
    private LoginReply Authentication => connection.Login ?? throw new InvalidOperationException("not authenticated");
    private Task<T> Call<T>(LoginReply login, Func<Task<T>> send) => connection.ForAuthentication(login, send);
    private static DateTime Until => DateTime.UtcNow + EconomyClient.Deadline;
    public Task<OutpostManifestBinding> Binding(CancellationToken ct)
    {
        var login = Authentication;
        return Call(login, () => connection.World.GetOutpostManifestBindingAsync(new(), deadline: Until, cancellationToken: ct).ResponseAsync);
    }
    public async Task<ManifestPreview> Stage(OutpostManifestDocument document, CancellationToken ct)
    {
        if (document.CalculateSize() is < 1 or > MaxDocumentBytes) throw new InvalidDataException("manifest export size");
        byte[] bytes = document.ToByteArray(); var digest = ByteString.CopyFrom(SHA256.HashData(bytes));
        string upload = Guid.NewGuid().ToString("D"); var login = Authentication; ManifestPreview? result = null;
        for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
        {
            ct.ThrowIfCancellationRequested(); int length = Math.Min(ChunkBytes, bytes.Length - offset);
            result = await Call(login, () => connection.World.StageOutpostManifestAsync(new() { UploadId = upload, Sha256 = digest,
                TotalBytes = bytes.Length, Offset = offset, Data = ByteString.CopyFrom(bytes, offset, length) }, deadline: Until, cancellationToken: ct).ResponseAsync);
            if (result.ReceivedBytes != offset + length || result.Complete != (offset + length == bytes.Length))
                throw new InvalidDataException("manifest stage acknowledgement mismatch");
        }
        if (result is null || result.Digest != Convert.ToHexStringLower(digest.Span)) throw new InvalidDataException("manifest stage digest mismatch");
        return result;
    }
    public Task<ManifestRevision> Confirm(ManifestPreview preview, CancellationToken ct)
    {
        if (!preview.Complete || !Guid.TryParseExact(preview.ManifestId, "D", out _) || preview.Digest.Length != 64)
            throw new InvalidDataException("manifest confirmation identity");
        var request = new OutpostManifestConfirmation { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest };
        var login = Authentication;
        return Call(login, () => connection.World.ConfirmOutpostManifestAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync);
    }
    public async Task<OutpostManifestDocument> Read(OutpostStateReply state, CancellationToken ct)
    {
        state = state.Clone(); var login = Authentication;
        if (state.ManifestDigest.Length != 32 || !new OutpostClient(connection).Matches(state.Scope, login)) throw new InvalidDataException("manifest preview scope");
        string expected = Convert.ToHexStringLower(state.ManifestDigest.Span); using var bytes = new MemoryStream(); int total = 0;
        do
        {
            var request = new OutpostManifestReadRequest { Scope = state.Scope.Clone(), ManifestId = state.ManifestId, ExpectedDigest = expected, Offset = (int)bytes.Length };
            var page = await Call(login, () => connection.OutpostsApi.ManifestAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync);
            if (page.TotalBytes is < 1 or > MaxDocumentBytes || total != 0 && total != page.TotalBytes || page.Digest != expected
                || page.Data.Length is < 1 or > ChunkBytes || bytes.Length + page.Data.Length > page.TotalBytes)
                throw new InvalidDataException("manifest page mismatch");
            total = page.TotalBytes; page.Data.WriteTo(bytes);
        } while (bytes.Length < total);
        byte[] raw = bytes.ToArray();
        if (!SHA256.HashData(raw).AsSpan().SequenceEqual(state.ManifestDigest.Span)) throw new InvalidDataException("manifest bytes digest mismatch");
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(raw, false), MaxDocumentBytes, 32);
        var document = OutpostManifestDocument.Parser.ParseFrom(input);
        if (document.SchemaVersion != 1 || document.Binding?.WorldInstanceId != state.Scope.WorldInstanceId || document.MainSurfaceId != "planet"
            || document.TileCount is < 1 or > 2_000_000 || document.TileCount != document.Tiles.Count) throw new InvalidDataException("manifest domain mismatch");
        return document;
    }
    public static void RequireLocalMatch(OutpostManifestDocument document, WorldParams local, string worldInstanceId,
        string gameVersion, IReadOnlyList<string> activeDlc, ByteString gameDefs)
    {
        if (document.Binding is null || document.Binding.WorldInstanceId != worldInstanceId
            || !document.Binding.WorldParamsSha256.Span.SequenceEqual(SHA256.HashData(local.ToByteArray()))
            || document.GameVersion != gameVersion || document.GameDefsSha256 != gameDefs
            || !document.ActiveDlc.SequenceEqual(activeDlc.Order(StringComparer.Ordinal))) throw new InvalidDataException("local world differs from confirmed outpost manifest");
    }
}
