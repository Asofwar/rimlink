using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Game.GuildCapture;

internal static class BattleArtifactCapture
{
    internal static BattleFinalCapture Capture(BattleScenario scenario,BattlePreparedManifestV1 start,BattleRuleState state,BattlePawn[] pawns,
        TimeSpan wall,TimeSpan transportLost,long journalBytes,long viewSequence,string requestId)
    {
        var snapshot=scenario.SaveFinal();string journalPath=scenario.ArtifactPath("simulation");
        BattleDocumentDescriptor journal;
        using(var input=new FileStream(journalPath,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            if(input.Length!=journalBytes || input.Length is <1 or >67108864)throw new InvalidDataException("battle-simulation-journal-incomplete");
            journal=new(){Kind=BattleDocumentKind.SimulationJournal,SizeBytes=input.Length,Sha256=ByteString.CopyFrom(SHA256.HashData(input))};
        }
        var final=BattleFinalCapture.Create(start,state,pawns,wall,transportLost,snapshot,journal,viewSequence,requestId);
        WriteOnce(scenario.ArtifactPath("view"),final.View.ToByteArray());
        // The envelope is the last durable write. A cold process may resend these exact bytes, never simulate again.
        WriteOnce(scenario.ArtifactPath("envelope"),final.Envelope.ToByteArray());
        return final;
    }
    private static void WriteOnce(string path,byte[] bytes)
    {
        if(File.Exists(path)){if(!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))throw new InvalidDataException("battle-artifact-conflict");return;}
        if(File.Exists(path+".part"))throw new InvalidDataException("battle-artifact-incomplete");
        using(var output=new FileStream(path+".part",FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}
        File.Move(path+".part",path,false);
    }
}
