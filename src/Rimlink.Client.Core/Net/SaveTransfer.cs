using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using ZstdSharp;

namespace Rimlink.Client.Net;

/// <param name="DevMode">Dev mode was on in this game since the previous upload (integrity spec §3).</param>
public sealed record SaveMeta(string WorldSeed, string FactionName, long GameTick, long WealthTotal = 0, long WealthItems = 0, bool DevMode = false,
    long SaveGeneration = 0, SaveRecoveryData? Recovery = null);
public sealed record DownloadedSave(string Path, SaveHeader Header, byte[] RawSha256)
{
    public bool MatchesFile(string path)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        return SHA256.HashData(file).AsSpan().SequenceEqual(RawSha256);
    }
}
/// <summary>Moves .rws files to and from the server: zstd in 1 MB chunks, hashed, atomically written on download.</summary>
public sealed class SaveTransfer(RimlinkConnection connection)
{
    /// <summary>Compresses the file into memory (saves are tens of MB at most), then streams it; the local file is never touched.</summary>
    public async Task<SaveInfo> UploadAsync(string rwsPath, SaveMeta meta, CancellationToken cancellationToken)
    {
        try
        {
            var info = await UploadCore(rwsPath, meta, cancellationToken);
            connection.UploadStatus.Succeeded(info.Version);
            return info;
        }
        catch (Exception ex)
        {
            connection.UploadStatus.Failed(ex);
            throw;
        }
    }

    private async Task<SaveInfo> UploadCore(string rwsPath, SaveMeta meta, CancellationToken cancellationToken)
    {
        byte[] compressed;
        using (var compressor = new Compressor(3))
        {
            compressed = compressor.Wrap(await File.ReadAllBytesAsync(rwsPath, cancellationToken)).ToArray();
        }
        if (compressed.Length > Limits.MaxSaveBytes)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"compressed save is {compressed.Length / 1024 / 1024} MB, the limit is {Limits.MaxSaveBytes / 1024 / 1024} MB"));

        using var call = connection.Saves.Upload(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new SaveChunk
        {
            Header = new SaveHeader
            {
                GameTick = meta.GameTick,
                WorldSeed = meta.WorldSeed,
                FactionName = meta.FactionName,
                WealthTotal = meta.WealthTotal,
                WealthItems = meta.WealthItems,
                DevMode = meta.DevMode,
                SaveGeneration = meta.SaveGeneration,
                SnapshotId = meta.Recovery?.SnapshotId ?? "", SaveWriteEpoch = meta.Recovery?.WriteEpoch ?? 0,
                RecoveryVersion = meta.Recovery?.Version ?? 0,
                TotalSize = compressed.Length,
                Sha256 = ByteString.CopyFrom(SHA256.HashData(compressed)),
            },
        }, cancellationToken);
        for (int offset = 0; offset < compressed.Length; offset += Limits.SaveChunkBytes)
        {
            int length = Math.Min(Limits.SaveChunkBytes, compressed.Length - offset);
            await call.RequestStream.WriteAsync(new SaveChunk { Data = ByteString.CopyFrom(compressed, offset, length) }, cancellationToken);
        }
        await call.RequestStream.CompleteAsync();
        return await call;
    }

    /// <summary>Downloads the current server save to <paramref name="targetPath"/> (temp file + rename). Null when the server has none.</summary>
    public Task<string?> DownloadAsync(string targetPath, CancellationToken cancellationToken) =>
        Receive(connection.Saves.Download(new Empty(), cancellationToken: cancellationToken), targetPath, cancellationToken);

    /// <summary>Only this verified own-colony download can grant the write epoch of a rolled-back file.</summary>
    public async Task<DownloadedSave?> DownloadSnapshotAsync(string targetPath, CancellationToken cancellationToken)
    {
        DownloadedSave? result = null;
        await Receive(connection.Saves.Download(new Empty(), cancellationToken: cancellationToken), targetPath, cancellationToken,
            (header, hash) => result = new(targetPath, header, hash));
        return result;
    }

    /// <summary>Another player's current save, allowed while visiting them. Null when they have none.</summary>
    public Task<string?> DownloadOfAsync(long playerId, string targetPath, CancellationToken cancellationToken) =>
        Receive(connection.Saves.DownloadOf(new PlayerRef { PlayerId = playerId }, cancellationToken: cancellationToken), targetPath, cancellationToken);

    private static async Task<string?> Receive(AsyncServerStreamingCall<SaveChunk> call, string targetPath, CancellationToken cancellationToken,
        Action<SaveHeader, byte[]>? verified = null)
    {
        string targetDir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(targetDir);
        string final = targetPath;
        string temp = Path.Combine(targetDir, $"download-{Guid.NewGuid():N}.tmp");
        try
        {
            using var _ = call;
            using var compressed = new MemoryStream();
            SaveHeader? header = null;
            try
            {
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    var chunk = call.ResponseStream.Current;
                    if (chunk.PartCase == SaveChunk.PartOneofCase.Header)
                    {
                        if (header is not null || compressed.Length != 0) throw new InvalidDataException("duplicate save header");
                        header = chunk.Header.Clone();
                    }
                    else if (chunk.PartCase == SaveChunk.PartOneofCase.Data)
                    {
                        if (header is null || compressed.Length + chunk.Data.Length > Limits.MaxSaveBytes) throw new InvalidDataException("invalid save stream");
                        chunk.Data.WriteTo(compressed);
                    }
                    else throw new InvalidDataException("invalid save chunk");
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return null;
            }

            if (header is null || header.TotalSize != compressed.Length || header.Sha256.Length != 32
                || !SHA256.HashData(compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length))).AsSpan().SequenceEqual(header.Sha256.Span))
                throw new InvalidDataException("downloaded save hash or size does not match its header");
            compressed.Position = 0;
            using (var file = File.Create(temp))
            using (var decompressor = new DecompressionStream(compressed))
            {
                byte[] buffer = new byte[64 * 1024];
                long expanded = 0;
                int read;
                while ((read = await decompressor.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    expanded += read;
                    if (expanded > Limits.MaxSaveBytes) throw new InvalidDataException("downloaded save exceeds the decompressed size limit");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            SaveRecoveryData recovery;
            byte[] rawHash;
            using (var file = File.OpenRead(temp)) recovery = SaveRecoveryData.ReadXml(file, out rawHash);
            if (recovery.Version != header.RecoveryVersion || recovery.SnapshotId != header.SnapshotId)
                throw new InvalidDataException("downloaded save identity does not match its header");
            File.Move(temp, final, overwrite: true);
            verified?.Invoke(header, rawHash);
            return final;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
