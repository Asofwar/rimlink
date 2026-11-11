using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public sealed record BattleReceiptArchive(IReadOnlyList<BattleCommandReceipt> Receipts,IReadOnlyList<string> CutIds);

// This archive resends observations only. Opening a cold archive never permits a native tick or order.
public sealed class BattleReceiptJournal
{
    private const long MaximumBytes=64L*1024*1024;
    private readonly string path;
    private readonly Dictionary<(BattleFighterSide,long),byte[]> receipts=[];
    private readonly HashSet<Guid> cuts=[];
    private byte[] tail;
    private long length=40;
    private bool poisoned;
    public BattleReceiptJournal(string path,byte[] startDigest):this(path,startDigest,true){}
    private BattleReceiptJournal(string path,byte[] startDigest,bool create)
    {
        if(startDigest is not{Length:32})throw Invalid();this.path=path;tail=startDigest.ToArray();
        if(create){using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write("RLBGRJ01"u8);output.Write(startDigest);output.Flush(true);}
    }
    public void Append(BattleCommandReceipt receipt)
    {
        Healthy();byte[] bytes=BattleCommandContracts.ReadReceipt(receipt.ToByteArray()).ToByteArray();var key=(receipt.Side,receipt.Sequence);
        if(receipts.TryGetValue(key,out var prior)){if(!prior.AsSpan().SequenceEqual(bytes))throw Invalid();return;}
        if(receipts.Count>=48000)throw Invalid();AppendFrame([1,..bytes]);receipts.Add(key,bytes);
    }
    public void AppendCut(string cutId)
    {
        Healthy();if(!Guid.TryParseExact(cutId,"D",out var id) || id==Guid.Empty || id.ToString("D")!=cutId)throw Invalid();
        if(cuts.Contains(id))return;if(cuts.Count>=48000)throw Invalid();AppendFrame([2,..id.ToByteArray()]);cuts.Add(id);
    }
    public static BattleReceiptArchive ReadCold(string path,byte[] startDigest)
    {
        var journal=new BattleReceiptJournal(path,startDigest,false);
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);
            if(input.Length is <40 or >MaximumBytes)throw Invalid();Span<byte> header=stackalloc byte[40];input.ReadExactly(header);
            if(!header[..8].SequenceEqual("RLBGRJ01"u8) || !header[8..].SequenceEqual(startDigest))throw Invalid();
            Span<byte> size=stackalloc byte[4];Span<byte> stored=stackalloc byte[32];
            while(input.Position<input.Length)
            {
                input.ReadExactly(size);int count=BinaryPrimitives.ReadInt32LittleEndian(size);
                if(count is <2 or >513 || input.Length-input.Position<count+32)throw Invalid();
                byte[] bytes=new byte[count];input.ReadExactly(bytes);input.ReadExactly(stored);byte[] digest=Hash(journal.tail,bytes);
                if(!CryptographicOperations.FixedTimeEquals(stored,digest))throw Invalid();journal.tail=digest;
                if(bytes[0]==1)
                {
                    var receipt=BattleCommandContracts.ReadReceipt(bytes.AsSpan(1));
                    if(journal.receipts.Count>=48000 || !journal.receipts.TryAdd((receipt.Side,receipt.Sequence),bytes[1..]))throw Invalid();
                }
                else if(bytes[0]==2 && bytes.Length==17)
                {var id=new Guid(bytes.AsSpan(1));if(id==Guid.Empty || journal.cuts.Count>=48000 || !journal.cuts.Add(id))throw Invalid();}
                else throw Invalid();
            }
            return new(journal.receipts.Values.Select(b=>BattleCommandReceipt.Parser.ParseFrom(b)).ToArray(),journal.cuts.Select(id=>id.ToString("D")).ToArray());
        }
        catch(EndOfStreamException error){throw new InvalidDataException("battle-native-receipt-journal-invalid",error);}
    }
    private void AppendFrame(byte[] bytes)
    {
        Healthy();if(length+bytes.Length+36>MaximumBytes)throw Invalid();
        try
        {
            using var output=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            if(output.Length!=length)throw Invalid();
            if(length>40){output.Position=length-32;Span<byte> prior=stackalloc byte[32];output.ReadExactly(prior);if(!prior.SequenceEqual(tail))throw Invalid();}
            output.Position=length;Span<byte> size=stackalloc byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,bytes.Length);byte[] digest=Hash(tail,bytes);
            output.Write(size);output.Write(bytes);output.Write(digest);output.Flush(true);length=output.Position;tail=digest;
        }
        catch{poisoned=true;throw;}
    }
    private static byte[] Hash(byte[] previous,byte[] body)
    {using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(previous);hash.AppendData(body);return hash.GetHashAndReset();}
    private void Healthy(){if(poisoned)throw Invalid();}
    private static InvalidDataException Invalid()=>new("battle-native-receipt-journal-invalid");
}
