using System.Security.Cryptography;
using System.Collections.Concurrent;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

// Caller owns S → B. These bounded transport records never replace the common runtime assignment.
internal sealed class BattleExecutorHandshakes(TimeProvider time,string? serverBoot=null)
{
    private sealed record Pending(byte[] Binding,string CredentialGeneration,ByteString Nonce,long At,Task<GuildOwnedProcess?>? LocalProof)
    {internal BattleExecutorLeaseMessage? Lease;}
    private readonly ConcurrentDictionary<string,Pending> pending=[];
    private readonly string boot=serverBoot??Guid.NewGuid().ToString("D");
    internal BattleExecutorHelloChallenge BeginHeld(BattleExecutorHelloBindingV1 binding,string credentialGeneration, GuildOwnedProcessWitness? local = null)
    {
        binding=BattleExecutorHelloContracts.Read(binding.ToByteArray());RequireGeneration(credentialGeneration);
        foreach(var key in pending.Where(p=>!Fresh(p.Value)).Select(p=>p.Key).ToArray())pending.TryRemove(key, out _);
        if(pending.Count>=32)throw Invalid();string id=Guid.NewGuid().ToString("D");var nonce=ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        var proof = local?.ObserveHello(id, nonce.ToByteArray());
        if (!pending.TryAdd(id, new(binding.ToByteArray(),credentialGeneration,nonce,time.GetTimestamp(),proof))) throw Invalid();
        return new(){ChallengeId=id,Nonce=nonce,ServerBootId=boot,BindingDigest=ByteString.CopyFrom(BattleExecutorHelloContracts.Digest(binding)),ValidForMs=30000,
            LocalProcessRequired=proof is not null};
    }
    internal async Task<GuildOwnedProcess?> OwnedProcessAsync(BattleExecutorHelloComplete complete, string credentialGeneration, CancellationToken ct)
    {
        var entry = RequireHeld(complete, credentialGeneration);
        return entry.LocalProof is { } proof ? await proof.WaitAsync(ct) ?? throw Invalid() : null;
    }
    internal BattleExecutorLeaseMessage CompleteHeld(BattleExecutorHelloComplete complete,string credentialGeneration,Func<BattleExecutorLeaseMessage> publish)
    {
        var challenge=RequireHeld(complete,credentialGeneration);
        if(challenge.Lease is not null)return challenge.Lease.Clone();
        var lease=publish();challenge.Lease=lease.Clone();return lease.Clone();
    }
    internal BattleExecutorLeaseMessage? ReplayHeld(BattleExecutorHelloComplete complete,string credentialGeneration)=>RequireHeld(complete,credentialGeneration).Lease?.Clone();
    private Pending RequireHeld(BattleExecutorHelloComplete complete,string credentialGeneration)
    {
        if(complete is null || complete.CalculateSize()>4096 || complete.Binding is null)throw Invalid();
        byte[] binding=BattleExecutorHelloContracts.Read(complete.Binding.ToByteArray()).ToByteArray();RequireGeneration(credentialGeneration);
        if(complete.ServerBootId!=boot || !pending.TryGetValue(complete.ChallengeId,out var challenge) || !Fresh(challenge)
            || credentialGeneration!=challenge.CredentialGeneration || !binding.AsSpan().SequenceEqual(challenge.Binding) || complete.Nonce.Length!=32
            || !CryptographicOperations.FixedTimeEquals(complete.Nonce.Span,challenge.Nonce.Span))throw Invalid();
        return challenge;
    }
    private bool Fresh(Pending record)=>time.GetElapsedTime(record.At,time.GetTimestamp()) is var elapsed && elapsed>=TimeSpan.Zero && elapsed<=TimeSpan.FromSeconds(30);
    private static void RequireGeneration(string generation)
    {if(string.IsNullOrWhiteSpace(generation) || generation.Length>128 || generation.Any(char.IsControl))throw Invalid();}
    private static InvalidDataException Invalid()=>new("battle-executor-handshake-refused");
}
