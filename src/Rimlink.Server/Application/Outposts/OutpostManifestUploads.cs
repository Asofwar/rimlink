using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Grpc;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Ephemeral bounded assembly; only complete digest-checked documents enter durable authority.</summary>
public sealed class OutpostManifestUploads(TimeProvider time)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(string Identity, string Id), Upload> pending = [];
    private readonly Dictionary<(string Identity, string Id), Receipt> completed = [];
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private sealed class Upload(byte[] digest, byte[] bytes, long touched)
    {
        public readonly byte[] Digest = digest;
        public readonly byte[] Bytes = bytes;
        public int Received;
        public long Touched = touched;
    }
    private sealed record Receipt(byte[] Digest, int Total, ManifestPreview Preview, long Touched);

    public async Task<ManifestPreview> StageAsync(Actor actor, string identity, OutpostManifestChunk chunk, OutpostManifestService manifests, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 256 || !Guid.TryParseExact(chunk.UploadId, "D", out _) ||
            chunk.Sha256.Length != 32 || chunk.TotalBytes is < 1 or > OutpostManifestService.MaxDocumentBytes ||
            chunk.Data.Length is < 1 or > OutpostManifestService.MaxChunkBytes || chunk.Offset < 0 ||
            (long)chunk.Offset + chunk.Data.Length > chunk.TotalBytes)
            throw Invalid("manifest-chunk-shape");
        // Take immutable copies before awaiting: callers must not change a validated protobuf underneath assembly.
        chunk = chunk.Clone();
        await gate.WaitAsync(ct);
        try
        {
            long now = time.GetTimestamp();
            foreach (var pair in pending.Where(p => Expired(p.Value.Touched, now)).ToArray()) pending.Remove(pair.Key);
            foreach (var pair in completed.Where(p => Expired(p.Value.Touched, now)).ToArray()) completed.Remove(pair.Key);
            var key = (identity, chunk.UploadId);
            if (completed.TryGetValue(key, out var receipt))
            {
                if (receipt.Total != chunk.TotalBytes || !chunk.Sha256.Span.SequenceEqual(receipt.Digest)) throw Invalid("manifest-chunk-binding");
                // Receipts retain no document buffer. Compare retries against the immutable durable page instead.
                var page = await manifests.ReadAsync(actor, new() { ManifestId = receipt.Preview.ManifestId,
                    ExpectedDigest = receipt.Preview.Digest, Offset = chunk.Offset }, ct);
                if (page.Data.Length < chunk.Data.Length || !page.Data.Span[..chunk.Data.Length].SequenceEqual(chunk.Data.Span))
                    throw Invalid("manifest-chunk-replay");
                return receipt.Preview.Clone();
            }
            if (!pending.TryGetValue(key, out var upload))
            {
                if (chunk.Offset != 0) throw Invalid("manifest-chunk-gap");
                if (pending.Count >= 2 || pending.Keys.Any(k => k.Identity == identity)) throw Busy();
                pending.Add(key, upload = new(chunk.Sha256.ToByteArray(), new byte[chunk.TotalBytes], now));
            }
            if (upload.Bytes.Length != chunk.TotalBytes || !chunk.Sha256.Span.SequenceEqual(upload.Digest)) throw Invalid("manifest-chunk-binding");
            if (chunk.Offset < upload.Received)
            {
                if ((long)chunk.Offset + chunk.Data.Length > upload.Received ||
                    !upload.Bytes.AsSpan(chunk.Offset, chunk.Data.Length).SequenceEqual(chunk.Data.Span)) throw Invalid("manifest-chunk-replay");
            }
            else
            {
                if (chunk.Offset != upload.Received) throw Invalid("manifest-chunk-gap");
                chunk.Data.Span.CopyTo(upload.Bytes.AsSpan(upload.Received));
                upload.Received += chunk.Data.Length;
            }
            upload.Touched = now;
            if (upload.Received < upload.Bytes.Length) return new() { Complete = false, ReceivedBytes = upload.Received };
            if (!SHA256.HashData(upload.Bytes).AsSpan().SequenceEqual(upload.Digest))
            {
                pending.Remove(key);
                throw Invalid("manifest-upload-digest");
            }
            OutpostManifestDocument document;
            try { document = OutpostManifestDocument.Parser.ParseFrom(upload.Bytes); }
            catch (InvalidProtocolBufferException) { pending.Remove(key); throw Invalid("manifest-upload-protobuf"); }
            ManifestPreview preview;
            try { preview = await manifests.StageAsync(actor, document, ct); }
            catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
            {
                pending.Remove(key);
                throw;
            }
            preview.ReceivedBytes = upload.Received;
            pending.Remove(key);
            if (completed.Count >= 32) completed.Remove(completed.MinBy(p => p.Value.Touched).Key);
            completed.Add(key, new(upload.Digest, upload.Received, preview.Clone(), now));
            return preview;
        }
        finally { gate.Release(); }
    }

    private bool Expired(long then, long now) => now >= then && time.GetElapsedTime(then, now) >= Lifetime;
    private static RpcException Invalid(string reason) => RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.OutpostManifestMismatch, RefusalKeys.OutpostsManifest, reason);
    private static RpcException Busy() => RpcErrors.With(StatusCode.ResourceExhausted, ErrorCode.OutpostManifestMismatch, RefusalKeys.OutpostsUploadBusy, "manifest-upload-capacity");
}
