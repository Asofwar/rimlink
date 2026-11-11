using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>Administrator staging is separate from confirmation; player reads expose confirmed current geography only.</summary>
public sealed class OutpostManifestClient(RimlinkConnection connection)
{
    public const int MaxDocumentBytes = 24 * 1024 * 1024, ChunkBytes = 256 * 1024;
    public sealed record DefinitionPack(int Position, string Package, string Content, string[] Defs);
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
    public Task<OutpostManifestAdminPreview> PreviewConfirmation(ManifestPreview preview, string requestId, string reason, CancellationToken ct)
    {
        var request = new OutpostManifestAdminPreviewRequest { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest,
            ClientRequestId = requestId, Reason = reason };
        var login = Authentication;
        return Call(login, () => connection.World.PreviewOutpostManifestConfirmationAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync);
    }
    public Task<ManifestRevision> Confirm(ManifestPreview preview, OutpostManifestAdminPreview command, CancellationToken ct)
    {
        if (!preview.Complete || !Guid.TryParseExact(preview.ManifestId, "D", out _) || preview.Digest.Length != 64)
            throw new InvalidDataException("manifest confirmation identity");
        if (command.ManifestId != preview.ManifestId || command.Digest != preview.Digest || command.Revision != preview.Revision
            || command.Blockers.Count != 0 || command.CanonicalCommand.Length is < 1 or > 65536 || command.IntentSha256.Length != 32
            || !SHA256.HashData(command.CanonicalCommand.Span).AsSpan().SequenceEqual(command.IntentSha256.Span))
            throw new InvalidDataException("canonical manifest command is unavailable or stale; request a new preview explicitly");
        var request = new OutpostManifestConfirmation { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest,
            CanonicalCommand = command.CanonicalCommand, IntentSha256 = command.IntentSha256, Confirmed = true };
        var login = Authentication;
        return Call(login, () => connection.World.ConfirmOutpostManifestAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync);
    }
    public async Task<OutpostManifestDocument> Read(OutpostStateReply state, CancellationToken ct, long parcelId = 0)
    {
        state = state.Clone(); var login = Authentication;
        if (state.ManifestDigest.Length != 32 || !new OutpostClient(connection).Matches(state.Scope, login)) throw new InvalidDataException("manifest preview scope");
        return await ReadDocument(state.ManifestDigest, state.Scope.WorldInstanceId, async offset =>
        {
            var request = new OutpostManifestReadRequest { Scope = state.Scope.Clone(), ManifestId = state.ManifestId,
                ExpectedDigest = Convert.ToHexStringLower(state.ManifestDigest.Span), Offset = offset, ParcelId = parcelId };
            return await Call(login, () => connection.OutpostsApi.ManifestAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync);
        }, ct);
    }
    internal static async Task<OutpostManifestDocument> ReadDocument(ByteString digest, string worldId,
        Func<int, Task<OutpostManifestPage>> read, CancellationToken ct)
    {
        if (digest.Length != 32) throw new InvalidDataException("manifest digest");
        string expected = Convert.ToHexStringLower(digest.Span); using var bytes = new MemoryStream(); int total = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            OutpostManifestPage page;
            try { page = await read((int)bytes.Length); }
            catch (RpcException error) when (error.StatusCode == StatusCode.DeadlineExceeded && !ct.IsCancellationRequested)
            { page = await read((int)bytes.Length); }
            if (page.TotalBytes is < 1 or > MaxDocumentBytes || total != 0 && total != page.TotalBytes || page.Digest != expected
                || page.Data.Length is < 1 or > ChunkBytes || bytes.Length + page.Data.Length > page.TotalBytes)
                throw new InvalidDataException("manifest page mismatch");
            total = page.TotalBytes; page.Data.WriteTo(bytes);
        } while (bytes.Length < total);
        byte[] raw = bytes.ToArray();
        if (!SHA256.HashData(raw).AsSpan().SequenceEqual(digest.Span)) throw new InvalidDataException("manifest bytes digest mismatch");
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(raw, false), MaxDocumentBytes, 32);
        var document = OutpostManifestDocument.Parser.ParseFrom(input);
        if (document.SchemaVersion != 1 || document.Binding?.WorldInstanceId != worldId || document.MainSurfaceId != "planet"
            || document.TileCount is < 1 or > 2_000_000 || document.TileCount != document.Tiles.Count) throw new InvalidDataException("manifest domain mismatch");
        return document;
    }
    public static WorldParams BindWorldParameters(WorldParams actual, WorldParams? recorded, WorldParams? server)
    {
        if (recorded is not null) RequireSamePlanet(actual, recorded);
        if (server is not null) RequireSamePlanet(actual, server);
        return server ?? recorded ?? actual;
    }

    private static void RequireSamePlanet(WorldParams actual, WorldParams expected)
    {
        var planet = actual.Clone();
        // Each colony can choose its scenario without changing the shared planet inputs.
        planet.ScenarioDef = expected.ScenarioDef;
        planet.StorytellerDef = expected.StorytellerDef;
        planet.DifficultyDef = expected.DifficultyDef;
        planet.DifficultyValues = expected.DifficultyValues;
        if (!planet.Equals(expected)) throw new InvalidDataException("loaded planet differs from its generation fingerprint");
    }

    public static ByteString DefinitionFingerprint(IReadOnlyList<DefinitionPack> packs, IEnumerable<ModSettingsHash> modSettings, bool legacy)
    {
        // Preserve the v1 serialization and ordering for existing confirmed documents.
        var settings = modSettings.Where(s => legacy || s.PackageId != ClientInfo.ModPackageId || s.Handle != "RimlinkMod")
            .OrderBy(s => s.PackageId, StringComparer.Ordinal).ThenBy(s => s.Handle, StringComparer.Ordinal)
            .Select(s => new { s.PackageId, s.Handle, s.Hash }).ToArray();
        return ByteString.CopyFrom(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Format = legacy ? "outpost-game-defs-v1" : "outpost-game-defs-v2",
            Packs = packs,
            Settings = settings,
        })));
    }

    public static void RequireLocalMatch(OutpostManifestDocument document, WorldParams local, string worldInstanceId,
        string gameVersion, IReadOnlyList<string> activeDlc, ByteString gameDefs)
    {
        var expectedDefs = document.GameplayDefsSha256.IsEmpty ? document.GameDefsSha256 : document.GameplayDefsSha256;
        if (document.Binding is null || document.Binding.WorldInstanceId != worldInstanceId
            || !document.Binding.WorldParamsSha256.Span.SequenceEqual(SHA256.HashData(local.ToByteArray()))
            || document.GameVersion != gameVersion || expectedDefs.Length != 32 || expectedDefs != gameDefs
            || !document.ActiveDlc.SequenceEqual(activeDlc.Order(StringComparer.Ordinal))) throw new InvalidDataException("local world differs from confirmed outpost manifest");
    }
}
