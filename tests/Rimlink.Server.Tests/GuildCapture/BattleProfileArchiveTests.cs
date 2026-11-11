using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleProfileArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),"guild-profile-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Completed_documents_are_immutable_raw_hash_bound_and_open_no_handle_after_return()
    {
        var store = new BattleProfileArchive(root);
        var proof = store.PutHeld(BattleDocumentKind.Config,"config"u8);
        Assert.Equal(proof,store.PutHeld(BattleDocumentKind.Config,"config"u8));
        Assert.Equal("config"u8.ToArray(),new BattleProfileArchive(root).ReadHeld(proof));
        string path = Path.Combine(root,proof.RelativePath.Replace('/',Path.DirectorySeparatorChar));
        using (File.Open(path,FileMode.Open,FileAccess.Read,FileShare.None)) { }
        File.WriteAllText(path,"damage");
        Assert.Throws<InvalidDataException>(() => store.ReadHeld(proof));
        Assert.Throws<InvalidDataException>(() => store.PutHeld(BattleDocumentKind.Config,"config"u8));
    }
    [Fact]
    public void Caller_paths_kinds_sizes_and_hashes_cannot_rebind_an_archive_document()
    {
        var store = new BattleProfileArchive(root); var proof = store.PutHeld(BattleDocumentKind.Config,"config"u8);
        Assert.Throws<InvalidDataException>(() => store.ReadHeld(proof with { RelativePath = "../outside" }));
        Assert.Throws<InvalidDataException>(() => store.ReadHeld(proof with { Kind = BattleDocumentKind.Template }));
        Assert.Throws<InvalidDataException>(() => store.ReadHeld(proof with { SizeBytes = proof.SizeBytes + 1 }));
        Assert.Throws<InvalidDataException>(() => store.PutHeld(BattleDocumentKind.Unspecified,"config"u8));
        Assert.Throws<InvalidDataException>(() => store.PutHeld(BattleDocumentKind.Profile,new byte[16385]));
    }
    [Fact]
    public void A_partial_write_is_evidence_to_preserve_and_never_approval_material()
    {
        var store = new BattleProfileArchive(root); var proof = store.PutHeld(BattleDocumentKind.Config,"config"u8);
        string path = Path.Combine(root,proof.RelativePath.Replace('/',Path.DirectorySeparatorChar));
        File.Move(path,path + ".part");
        Assert.Throws<InvalidDataException>(() => store.ReadHeld(proof));
        Assert.Throws<InvalidDataException>(() => store.PutHeld(BattleDocumentKind.Config,"config"u8));
        Assert.True(File.Exists(path + ".part")); Assert.False(File.Exists(path));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root,true); }
}
