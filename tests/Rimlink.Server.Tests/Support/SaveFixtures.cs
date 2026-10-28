using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using ZstdSharp;

namespace Rimlink.Server.Tests.Support;

public static class SaveFixtures
{
    /// <summary>A minimal RimWorld-shaped save (.rws XML), zstd-compressed.</summary>
    /// <param name="faction">The player faction's name; null for a faction the player has not named yet (no &lt;name&gt; element).</param>
    /// <param name="scenarioFactionDef">When set, the scenario block names this player faction def (as modded starts do).</param>
    public static byte[] MakeSave(string seed, string? faction, long tick = 1000, int paddingBytes = 0, string factionDef = "PlayerColony", string? scenarioFactionDef = null)
    {
        string xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <savegame>
              <meta><gameVersion>1.7.4990 rev1069</gameVersion></meta>
              <game>
                <scenario><name>Crashlanded</name>{(scenarioFactionDef is null ? "" : $"<playerFaction><def>PlayerFaction</def><factionDef>{scenarioFactionDef}</factionDef></playerFaction>")}</scenario>
                <tickManager><ticksGame>{tick}</ticksGame></tickManager>
                <world>
                  <info><seedString>{seed}</seedString><name>Planet</name></info>
                  <factionManager>
                    <allFactions>
                      <li><def>Pirate</def><name>Raiders</name></li>
                      <li><def>{factionDef}</def><loadID>16</loadID>{(faction is null ? "" : $"<name>{faction}</name>")}</li>
                    </allFactions>
                  </factionManager>
                </world>
                <padding>{Padding(paddingBytes)}</padding>
              </game>
            </savegame>
            """;
        return Compress(Encoding.UTF8.GetBytes(xml));
    }

    /// <summary>Incompressible filler, so compressed size tracks the requested size and uploads span several chunks.</summary>
    private static string Padding(int bytes)
    {
        if (bytes == 0) return "";
        var random = new Random(bytes);
        byte[] raw = new byte[bytes * 3 / 4];
        random.NextBytes(raw);
        return Convert.ToBase64String(raw);
    }

    public static byte[] Compress(byte[] raw)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(raw).ToArray();
    }

    public static byte[] Decompress(byte[] zst)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(zst).ToArray();
    }

    public static SaveHeader HeaderFor(byte[] compressed, string seed, string faction, long tick = 1000) => new()
    {
        GameTick = tick,
        WorldSeed = seed,
        FactionName = faction,
        TotalSize = compressed.Length,
        Sha256 = ByteString.CopyFrom(SHA256.HashData(compressed)),
    };

    public static async Task<SaveInfo> Upload(Protocol.V1.Saves.SavesClient saves, string token, byte[] compressed, SaveHeader header, int? stopAfterChunks = null, CancellationToken cancellationToken = default)
    {
        using var call = saves.Upload(GrpcTestExtensions.Bearer(token), cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new SaveChunk { Header = header }, cancellationToken);
        int sent = 0;
        foreach (var chunk in compressed.Chunk(Limits.SaveChunkBytes))
        {
            if (stopAfterChunks is { } n && sent >= n) break;
            await call.RequestStream.WriteAsync(new SaveChunk { Data = ByteString.CopyFrom(chunk) }, cancellationToken);
            sent++;
        }
        await call.RequestStream.CompleteAsync();
        return await call;
    }

    public static async Task<(SaveHeader header, byte[] data)> Download(Protocol.V1.Saves.SavesClient saves, string token)
    {
        using var call = saves.Download(new Empty(), GrpcTestExtensions.Bearer(token));
        SaveHeader? header = null;
        using var buffer = new MemoryStream();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            var chunk = call.ResponseStream.Current;
            if (chunk.PartCase == SaveChunk.PartOneofCase.Header) header = chunk.Header;
            else chunk.Data.WriteTo(buffer);
        }
        return (header!, buffer.ToArray());
    }
}
