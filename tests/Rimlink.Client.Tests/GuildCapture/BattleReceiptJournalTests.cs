using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleReceiptJournalTests:IDisposable
{
    private readonly string path=Path.Combine(Path.GetTempPath(),"rimlink-native-receipts-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(File.Exists(path))File.Delete(path);}
    [Fact]
    public void Receipt_and_cut_survive_after_terminal_without_changing_the_simulation_artifact()
    {
        byte[] start=SHA256.HashData("start"u8);var journal=new BattleReceiptJournal(path,start);
        var receipt=new BattleCommandReceipt{Side=BattleFighterSide.Attacker,Sequence=1,CommandDigest=ByteString.CopyFrom(SHA256.HashData("command"u8)),
            AcceptedTick=12,Disposition=BattleCommandDisposition.Executed};
        journal.Append(receipt);journal.Append(receipt);string cut=Guid.NewGuid().ToString("D");journal.AppendCut(cut);journal.AppendCut(cut);
        var records=BattleReceiptJournal.ReadCold(path,start);Assert.Single(records.Receipts);Assert.Equal(receipt,records.Receipts[0]);Assert.Equal(cut,Assert.Single(records.CutIds));
        using var input=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.None);
    }
    [Theory]
    [InlineData("conflict")][InlineData("torn")][InlineData("digest")][InlineData("start")]
    public void Conflicts_and_unknown_bytes_are_preserved_for_recovery(string damage)
    {
        byte[] start=SHA256.HashData("start"u8);var journal=new BattleReceiptJournal(path,start);
        var receipt=new BattleCommandReceipt{Side=BattleFighterSide.Defender,Sequence=1,CommandDigest=ByteString.CopyFrom(SHA256.HashData("command"u8)),
            Disposition=BattleCommandDisposition.NotExecuted,Reason=BattleCommandReason.Terminal};journal.Append(receipt);
        if(damage=="conflict"){receipt.Reason=BattleCommandReason.ControlRevoked;Assert.Throws<InvalidDataException>(()=>journal.Append(receipt));return;}
        byte[] bytes=File.ReadAllBytes(path);if(damage=="torn")bytes=bytes[..^1];if(damage=="digest")bytes[^1]^=1;File.WriteAllBytes(path,bytes);
        if(damage=="start")start=SHA256.HashData("other start"u8);
        Assert.Throws<InvalidDataException>(()=>BattleReceiptJournal.ReadCold(path,start));Assert.Equal(bytes,File.ReadAllBytes(path));
    }
}
