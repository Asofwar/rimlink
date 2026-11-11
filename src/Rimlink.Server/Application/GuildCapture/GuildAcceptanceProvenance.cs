using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

public sealed record GuildAcceptanceProvenance(int Version,string Mode,string StandId,string BootstrapDigest,string SourceIdentity,string WorldId,string Boundary);
internal sealed record GuildAcceptanceBindingReceipt(GuildAcceptanceProvenance Identity,string TransitionId);

internal static partial class GuildAuthorityEvidence
{
    internal const string AcceptanceBindingKind="GuildAcceptanceBound";
    internal static void ValidateAcceptanceIdentity(GuildAcceptanceProvenance value)
    {
        if(value is null || value.Version!=1 || value.Mode!="GuildCaptureIsolatedAcceptanceV1" || !Uuid(value.StandId) || !Uuid(value.Boundary)
            || !Hex(value.BootstrapDigest) || !Hex(value.SourceIdentity) || string.IsNullOrWhiteSpace(value.WorldId) || value.WorldId.Length>256 || value.WorldId.Any(char.IsControl))throw InvalidAcceptance();
        static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
        static bool Hex(string value)=>value is{Length:64} && value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    }
    internal static async Task<GuildAcceptanceProvenance?> AcceptanceIdentityAsync(RimlinkDbContext db,CancellationToken ct)
    {
        var records=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind==AcceptanceBindingKind).Select(o=>o.Receipt).Take(2).ToArrayAsync(ct);
        if(records.Length==0)return null;if(records.Length!=1)throw InvalidAcceptance();
        try{var value=JsonSerializer.Deserialize<GuildAcceptanceBindingReceipt>(records[0],Json)??throw InvalidAcceptance();ValidateAcceptanceIdentity(value.Identity);return value.Identity;}
        catch(JsonException){throw InvalidAcceptance();}
    }
    private static void ValidateAcceptanceBindings(GuildCaptureOperation[] operations,IReadOnlyDictionary<string,OutpostTransition> transitions)
    {
        var records=operations.Where(o=>o.Kind==AcceptanceBindingKind).ToArray();if(records.Length>1)throw InvalidAcceptance();
        foreach(var operation in records)
        {
            GuildAcceptanceBindingReceipt receipt;
            try{receipt=JsonSerializer.Deserialize<GuildAcceptanceBindingReceipt>(operation.Receipt,Json)??throw InvalidAcceptance();}catch(JsonException){throw InvalidAcceptance();}
            ValidateAcceptanceIdentity(receipt.Identity);
            if(operation.ActorId!=0 || operation.RequestId!=receipt.Identity.StandId || operation.TransitionId!=receipt.TransitionId
                || !IntentDigest(receipt.Identity).AsSpan().SequenceEqual(operation.IntentDigest) || !transitions.TryGetValue(receipt.TransitionId,out var transition)
                || transition.WorldId!=receipt.Identity.WorldId || transition.RestoreBoundary!=receipt.Identity.Boundary)throw InvalidAcceptance();
        }
    }
    private static OutpostEvidenceUnavailableException InvalidAcceptance()=>new("guild-acceptance-provenance-invalid");
}
