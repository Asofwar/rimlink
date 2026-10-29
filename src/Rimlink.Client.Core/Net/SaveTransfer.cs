using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using ZstdSharp;

namespace Rimlink.Client.Net;

/// <param name="DevMode">Dev mode was on in this game since the previous upload (integrity spec §3).</param>
public sealed record SaveMeta(string WorldSeed, string FactionName, long GameTick, long WealthTotal = 0, long WealthItems = 0, bool DevMode = false);

/// <summary>Moves .rws files to and from the server: zstd in 1 MB chunks, hashed, atomically written on download.</summary>
public sealed class SaveTransfer(RimlinkConnection connection)
{
    /// <summary>Compresses the file into memory (saves are tens of MB at most), then streams it; the local file is never touched.</summary>
    public async Task<SaveInfo> UploadAsync(string rwsPath, SaveMeta meta, CancellationToken cancellationToken)
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

    /// <summary>Another player's current save, allowed while visiting them. Null when they have none.</summary>
    public Task<string?> DownloadOfAsync(long playerId, string targetPath, CancellationToken cancellationToken) =>
        Receive(connection.Saves.DownloadOf(new PlayerRef { PlayerId = playerId }, cancellationToken: cancellationToken), targetPath, cancellationToken);

    private static async Task<string?> Receive(AsyncServerStreamingCall<SaveChunk> call, string targetPath, CancellationToken cancellationToken)
    {
        string targetDir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(targetDir);
        string final = targetPath;
        string temp = Path.Combine(targetDir, $"download-{Guid.NewGuid():N}.tmp");
        try
        {
            using var _ = call;
            using var compressed = new MemoryStream();
            try
            {
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    var chunk = call.ResponseStream.Current;
                    if (chunk.PartCase == SaveChunk.PartOneofCase.Data) chunk.Data.WriteTo(compressed);
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return null;
            }

            compressed.Position = 0;
            using (var file = File.Create(temp))
            using (var decompressor = new DecompressionStream(compressed))
            {
                await decompressor.CopyToAsync(file, cancellationToken);
            }
            File.Move(temp, final, overwrite: true);
            return final;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>A marker file saying "the local save was not uploaded"; checked on the next login.</summary>
public sealed class PendingUpload(string saveDir)
{
    private string Path_ => Path.Combine(saveDir, "pending.flag");

    public bool IsSet => File.Exists(Path_);

    public void Set()
    {
        Directory.CreateDirectory(saveDir);
        File.WriteAllText(Path_, DateTime.UtcNow.ToString("O"));
    }

    public void Clear()
    {
        if (File.Exists(Path_)) File.Delete(Path_);
    }
}
