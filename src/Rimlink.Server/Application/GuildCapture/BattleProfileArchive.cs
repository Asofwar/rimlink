using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildBattleDocument(BattleDocumentKind Kind, string Sha256, long SizeBytes, string RelativePath)
{
    internal BattleDocumentDescriptor Descriptor => new() { Kind = Kind, Sha256 = ByteString.CopyFrom(Convert.FromHexString(Sha256)), SizeBytes = SizeBytes };
}

// Caller owns the shared storage fence. A staged file grants no authority until referenced by a common commit.
internal sealed class BattleProfileArchive(string archiveRoot)
{
    private readonly string root = Path.GetFullPath(archiveRoot);
    internal GuildBattleDocument PutHeld(BattleDocumentKind kind, ReadOnlySpan<byte> bytes)
    {
        var descriptor = new BattleDocumentDescriptor { Kind = kind, SizeBytes = bytes.Length, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)) };
        BattleManifestContracts.RequireDescriptor(descriptor,kind);
        var document = new GuildBattleDocument(kind,Convert.ToHexStringLower(descriptor.Sha256.Span),bytes.Length,Relative(descriptor));
        string path = Resolve(document);
        if (File.Exists(path))
        {
            if (!ReadHeld(document).AsSpan().SequenceEqual(bytes)) throw Invalid();
            return document;
        }
        if (File.Exists(path + ".part")) throw Invalid();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path + ".part",FileMode.CreateNew,FileAccess.Write,FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(path + ".part",path,overwrite:false);
        _ = ReadHeld(document); return document;
    }
    internal byte[] ReadHeld(GuildBattleDocument document)
    {
        string path = Resolve(document);
        if (!File.Exists(path)) throw Invalid();
        using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);
        if (file.Length != document.SizeBytes) throw Invalid();
        byte[] bytes = new byte[checked((int)document.SizeBytes)]; file.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != document.Sha256) throw Invalid();
        return bytes;
    }
    internal GuildBattleDocument Reference(BattleDocumentDescriptor descriptor)
    {
        BattleManifestContracts.RequireDescriptor(descriptor,descriptor.Kind);
        return new(descriptor.Kind,Convert.ToHexStringLower(descriptor.Sha256.Span),descriptor.SizeBytes,Relative(descriptor));
    }
    private string Resolve(GuildBattleDocument document)
    {
        if (document is null || document.Sha256 is not { Length:64 } || document.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
        var descriptor = document.Descriptor;
        BattleManifestContracts.RequireDescriptor(descriptor,document.Kind);
        if (document.RelativePath != Relative(descriptor)) throw Invalid();
        string path = Path.Combine(root,document.RelativePath.Replace('/',Path.DirectorySeparatorChar));
        foreach (string candidate in new[] { path,path + ".part" })
            for (string? current = candidate; current is not null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        return path;
    }
    private static string Relative(BattleDocumentDescriptor descriptor) => "guild-profiles/" + ((int)descriptor.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "/" + Convert.ToHexStringLower(descriptor.Sha256.Span) + ".artifact";
    private static InvalidDataException Invalid() => new("guild-profile-archive-invalid");
}
