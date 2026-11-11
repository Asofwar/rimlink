using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority
{
    // Internal dev bootstrap only; there is no REST/CLI/config path that can adopt an existing domain.
    internal async Task<GuildAcceptanceProvenance> BindGuildAcceptanceAsync(CancellationToken ct)
    {
        await using var lease=await AcquireAsync([],ct);
        if((await CheckHeldAsync(lease,ct)).State!=OutpostStartupState.Ready || feature.VerifiedDomainFormat!=OutpostDomainFormat.V3 || !feature.RuntimeComposition)throw Invalid();
        var grant=feature.GuildGrant??throw Invalid();var row=await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var identity=new GuildAcceptanceProvenance(1,"GuildCaptureIsolatedAcceptanceV1",grant.StandId,grant.BootstrapDigest,grant.SourceIdentity,row.WorldId,row.RestoreBoundary);
        GuildAuthorityEvidence.ValidateAcceptanceIdentity(identity);
        if(grant.Scope!=GuildAcceptanceScope.Home)throw Invalid();
        GuildRuntimeAdmission.Require(feature.Readiness,new(row.WorldId,row.RestoreBoundary,row.HeadId,ByteString.CopyFrom(row.HeadDigest),identity),grant,GuildRuntimeUse.Setup);
        var existing=await GuildAuthorityEvidence.AcceptanceIdentityAsync(db,ct);
        if(existing is not null){if(existing!=identity)throw Invalid();return existing;}
        if(await db.Set<GuildCaptureOperation>().AnyAsync(ct))throw Invalid();
        string id=Guid.NewGuid().ToString("D");
        await using var transaction=await BeginCoreAsync(lease,new(id,row.WorldId,0,0,row.RestoreBoundary,GuildAuthorityEvidence.AcceptanceBindingKind,"system:guild-acceptance-bootstrap",
            new(row.HeadId,ByteString.CopyFrom(row.HeadDigest)),ByteString.Empty),null,ct,guildBootstrap:identity);
        var operation=new GuildCaptureOperation{Id=Guid.NewGuid().ToString("D"),ActorId=0,RequestId=grant.StandId,Kind=GuildAuthorityEvidence.AcceptanceBindingKind,
            IntentDigest=GuildAuthorityEvidence.IntentDigest(identity),Receipt=JsonSerializer.SerializeToUtf8Bytes(new GuildAcceptanceBindingReceipt(identity,id)),TransitionId=id};
        db.Add(operation);await transaction.CompleteAsync(GuildAuthorityEvidence.Binding(operation),ct);return identity;
        static OutpostEvidenceUnavailableException Invalid()=>new("guild-acceptance-bootstrap-refused");
    }
}
