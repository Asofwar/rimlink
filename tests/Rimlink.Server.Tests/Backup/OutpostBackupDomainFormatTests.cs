using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostBackupDomainFormatTests
{
    [Fact]
    public async Task New_backup_binds_verified_v1_format_and_actual_profile_without_activating_the_cut()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        var initial = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "Initialize");
        byte[] original = OutpostRestoreEvidence.Encode(initial);
        var files = OutpostBackupTests.Read(await OutpostBackupTests.Create(f));
        using var document = JsonDocument.Parse(files[OutpostBackupManifest.FileName]); var manifest = document.RootElement;
        Assert.Equal(2, manifest.GetProperty("version").GetInt32());
        var domain = manifest.GetProperty("domain"); Assert.Equal(1, domain.GetProperty("version").GetInt32());
        Assert.Equal((int)OutpostDomainFormat.V1, domain.GetProperty("format").GetInt32());
        var schema = await OutpostDomainSchema.ValidateAsync(h.Db, OutpostSchemaContext.CurrentRuntime, default);
        Assert.Equal(schema.ProfileId, domain.GetProperty("schema").GetProperty("profileId").GetString());
        Assert.Equal(schema.Digest.ToByteArray(), domain.GetProperty("schema").GetProperty("digest").GetBytesFromBase64());
        Assert.Empty(await h.Db.OutpostAdminCommands.ToListAsync());
        Assert.Equal(original, OutpostRestoreEvidence.Encode(await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == initial.Id)));
    }

    [Fact]
    public async Task Backup_uses_only_the_chain_selected_v2_digest_not_the_old_v1_overload()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        var command = await OutpostAdminActivationTests.CommandForCurrent(h);
        await OutpostAdminActivationTests.Service(h).ActivateDomainFormatAsync(command.Intent.Actor, command, default);
        var files = OutpostBackupTests.Read(await OutpostBackupTests.Create(f));
        using var document = JsonDocument.Parse(files[OutpostBackupManifest.FileName]); var manifest = document.RootElement;
        Assert.Equal((await OutpostDomainDigest.ComputeAsync(h.Db, OutpostDomainFormat.V2, default)).ToByteArray(), manifest.GetProperty("domainDigest").GetBytesFromBase64());
        Assert.Equal(2, manifest.GetProperty("version").GetInt32());
        Assert.Equal((int)OutpostDomainFormat.V2, manifest.GetProperty("domain").GetProperty("format").GetInt32());
    }
}
