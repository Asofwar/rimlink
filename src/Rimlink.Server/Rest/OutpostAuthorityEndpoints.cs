using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Microsoft.AspNetCore.Http.Features;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;

namespace Rimlink.Server.Rest;

public static partial class OutpostAdminEndpoints
{
    private const int MaxCommandJsonBytes = 2 * 1024 * 1024; // A bounded 1 MiB ephemeral ordinary sidecar, base64 and the 64 KiB command.
    private sealed record PreviewBody(string Action, string RequestId, string Reason, string? ManifestId = null, string? ManifestDigest = null,
        OutpostAdminResolutionSelection? Resolution = null, OutpostAdminRecoveryTarget? Recovery = null, string? OriginalInput = null);
    private sealed record CommandBody(string Command, string IntentDigest, bool Confirmed, string? OriginalInput = null);
    private static readonly JsonSerializerOptions CommandJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
    };
    private static readonly JsonSerializerOptions ResponseJson = ResponseOptions();
    static OutpostAdminEndpoints()
    { CommandJson.Converters.Add(new HashBytes()); CommandJson.Converters.Add(new HashByteString()); }

    private static void MapAuthorityAdmin(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/outposts").RequireAdmin();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            await http.RequestServices.GetRequiredService<RateLimitInterceptor>().Acquire("rest:" + http.ReceiptIdentity(),
                "/rimlink.v1.OutpostAdmin/" + (HttpMethods.IsGet(http.Request.Method) ? "Inspect" : "Command"), http.RequestAborted);
            try { return await next(context); }
            catch (BadHttpRequestException error) { return Results.Json(new { code = "InvalidArgument", reason = "outpost-admin-body-invalid" }, statusCode: error.StatusCode); }
            catch (JsonException) { return InvalidBody(); }
            catch (FormatException) { return InvalidBody(); }
            catch (InvalidDataException) { return InvalidBody(); }
            catch (OutpostEvidenceUnavailableException error)
            {
                // In particular, a lost/unfinished external completion is NOT a durable negative business receipt.
                return Results.Json(new { code = "RecoveryRequired", reason = error.Message, noReplayAuthorization = true, durableRejection = false },
                    statusCode: StatusCodes.Status409Conflict);
            }
        });
        group.MapGet("", async (HttpContext context, OutpostAdminService service, long? playerId, string? operationId, int? limit, string? cursor, CancellationToken ct) =>
            Results.Json(await service.InspectAsync(context.Actor(), new(playerId, operationId, limit ?? 50, cursor), ct), ResponseJson)).RequireScope(ApiScope.Read);
        group.MapGet("/authority", async (HttpContext context, OutpostAdminService service, CancellationToken ct) =>
            Results.Json(await service.InspectAsync(context.Actor(), ct), ResponseJson)).RequireScope(ApiScope.Admin);
        group.MapPost("/preview", async (HttpContext context, OutpostAdminService service, CancellationToken ct) =>
        {
            var request = await ReadCommandJson<PreviewBody>(context, ct);
            OutpostAdminPreview preview;
            if (request.Action is "initialize" or "activate-domain-format")
            {
                if (request.ManifestId is not null || request.ManifestDigest is not null || request.Resolution is not null || request.Recovery is not null || request.OriginalInput is not null) throw new JsonException();
                preview = request.Action == "initialize"
                    ? await service.PreviewInitializeAsync(context.Actor(), request.RequestId, request.Reason, ct)
                    : await service.PreviewActivateDomainFormatAsync(context.Actor(), request.RequestId, request.Reason, ct);
            }
            else if (request.Action == "confirm-manifest" && request.ManifestId is not null && request.ManifestDigest is not null
                && request.Resolution is null && request.Recovery is null && request.OriginalInput is null)
                preview = await service.PreviewConfirmManifestAsync(context.Actor(), request.ManifestId, ReadHash(request.ManifestDigest), request.RequestId, request.Reason, ct);
            else if (request.Action == "resolve" && request.ManifestId is null && request.ManifestDigest is null)
            {
                if (request.Resolution is not null && request.Recovery is null && request.OriginalInput is null)
                    preview = await service.PreviewResolveAsync(context.Actor(), request.Resolution, request.RequestId, request.Reason, ct);
                else if (request.Recovery is not null && request.Resolution is null)
                    preview = await service.PreviewResolveAsync(context.Actor(), request.Recovery, ReadOriginalInput(request.OriginalInput), request.RequestId, request.Reason, ct);
                else throw new JsonException();
            }
            else throw new JsonException();
            return Results.Json(new
            {
                command = preview.Command is null ? null : Convert.ToBase64String(OutpostAdminIntentCodec.Encode(preview.Command.Intent)),
                intentDigest = preview.Command is null ? null : Convert.ToHexStringLower(preview.Command.IntentDigest),
                confirmed = false, intent = preview.Command?.Intent, preview.Blockers,
            }, ResponseJson);
        }).RequireScope(ApiScope.Admin);
        group.MapPost("/initialize", (HttpContext context, OutpostAdminService service, CancellationToken ct) =>
            CommitCommand(context, service, OutpostAdminActionKind.Initialize, null, ct)).RequireScope(ApiScope.Admin);
        group.MapPost("/activate-domain-format", (HttpContext context, OutpostAdminService service, CancellationToken ct) =>
            CommitCommand(context, service, OutpostAdminActionKind.ActivateDomainFormat, null, ct)).RequireScope(ApiScope.Admin);
        group.MapPost("/manifests/{id}/confirm", (HttpContext context, string id, OutpostAdminService service, CancellationToken ct) =>
            CommitCommand(context, service, OutpostAdminActionKind.ConfirmManifest, id, ct)).RequireScope(ApiScope.Admin);
        group.MapPost("/manifests/confirm", (HttpContext context, OutpostAdminService service, CancellationToken ct) =>
            CommitCommand(context, service, OutpostAdminActionKind.ConfirmManifest, null, ct)).RequireScope(ApiScope.Admin);
        group.MapPost("/resolve", (HttpContext context, OutpostAdminService service, CancellationToken ct) =>
            CommitCommand(context, service, OutpostAdminActionKind.Resolve, null, ct)).RequireScope(ApiScope.Admin);
        group.MapGet("/manifests/{id}", async (HttpContext context, string id, OutpostAdminService service, CancellationToken ct) =>
            Results.Json(await service.InspectManifestAsync(context.Actor(), id, ct), ResponseJson)).RequireScope(ApiScope.Read);
        group.MapPost("/manifests/chunks", async (HttpContext context, OutpostManifestUploads uploads, OutpostManifestService service, CancellationToken ct) =>
            Json(await uploads.StageAsync(context.Actor(), "rest:" + context.ReceiptIdentity(), await Read<Protocol.V1.OutpostManifestChunk>(context, ct), service, ct))).RequireScope(ApiScope.Admin);
    }

    private static async Task<IResult> CommitCommand(HttpContext context, OutpostAdminService service, OutpostAdminActionKind action, string? manifestId, CancellationToken ct)
    {
        var request = await ReadCommandJson<CommandBody>(context, ct);
        if (request.Command.Length > (65536 + 2) / 3 * 4) throw new JsonException();
        var intent = OutpostAdminIntentCodec.Decode(Convert.FromBase64String(request.Command));
        if (request.OriginalInput is not null && action != OutpostAdminActionKind.Resolve) throw new JsonException();
        if (intent.Action != action || (manifestId is not null && intent.Manifest?.ManifestId != manifestId))
            throw new OutpostEvidenceUnavailableException("admin-intent-or-observation-mismatch");
        var command = new OutpostAdminCommand(intent, ReadHash(request.IntentDigest), request.Confirmed);
        var result = action switch
        {
            OutpostAdminActionKind.Initialize => await service.InitializeAsync(context.Actor(), command, ct),
            OutpostAdminActionKind.ActivateDomainFormat => await service.ActivateDomainFormatAsync(context.Actor(), command, ct),
            OutpostAdminActionKind.ConfirmManifest => await service.ConfirmManifestAsync(context.Actor(), command, ct),
            OutpostAdminActionKind.Resolve when intent.Version == 1 && request.OriginalInput is null => await service.ResolveAsync(context.Actor(), command, ct),
            OutpostAdminActionKind.Resolve when intent.Version == 2 => await service.ResolveAsync(context.Actor(), command, ReadOriginalInput(request.OriginalInput), ct),
            _ => throw new JsonException(),
        };
        return Results.Json(new { result.Outcome, head = new { result.Head.Id, digest = Convert.ToHexStringLower(result.Head.Digest.Span) }, result.Replay }, ResponseJson);
    }

    private static OriginalOrdinaryInput? ReadOriginalInput(string? encoded)
    {
        if (encoded is null) return null;
        if (encoded.Length > (Protocol.Limits.MaxMessageBytes + 2) / 3 * 4) throw new JsonException();
        try { return OutpostAdminIntentCodec.DecodeOrdinaryInput(Convert.FromBase64String(encoded)); }
        catch (FormatException) { throw new JsonException(); }
    }

    private static async Task<T> ReadCommandJson<T>(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType()) throw new BadHttpRequestException("outpost-admin-body-invalid", StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > MaxCommandJsonBytes) throw CommandTooLarge();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = MaxCommandJsonBytes;
        using var body = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + count > MaxCommandJsonBytes) throw CommandTooLarge();
            body.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), CommandJson) ?? throw new JsonException();
    }
    private static byte[] ReadHash(string value)
    {
        if (value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new JsonException();
        return Convert.FromHexString(value);
    }
    private static IResult InvalidBody() => Results.Json(new { code = "InvalidArgument", reason = "outpost-admin-body-invalid" }, statusCode: StatusCodes.Status400BadRequest);
    private static BadHttpRequestException CommandTooLarge() => new("outpost-admin-body-too-large", StatusCodes.Status413PayloadTooLarge);
    private static JsonSerializerOptions ResponseOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32 };
        options.Converters.Add(new HashBytes()); options.Converters.Add(new HashByteString()); return options;
    }
    private sealed class HashBytes : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ReadHash(reader.GetString() ?? throw new JsonException());
        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => writer.WriteStringValue(Convert.ToHexStringLower(value));
    }
    private sealed class HashByteString : JsonConverter<ByteString>
    {
        public override ByteString Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ByteString.CopyFrom(ReadHash(reader.GetString() ?? throw new JsonException()));
        public override void Write(Utf8JsonWriter writer, ByteString value, JsonSerializerOptions options) => writer.WriteStringValue(Convert.ToHexStringLower(value.Span));
    }
}
