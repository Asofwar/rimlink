using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestorePreviousDomainFormatTests
{
    [Fact]
    public async Task V2_previous_set_is_verified_and_can_be_explicitly_restored_without_rewriting_its_format()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await OutpostBackupTests.Create(f); // C is an actual v1 cut.
        var activation = await OutpostAdminActivationTests.CommandForCurrent(h);
        await OutpostAdminActivationTests.Service(h).ActivateDomainFormatAsync(activation.Intent.Actor, activation, default);
        string anchor = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == "AfterOldSetRetained") throw new StopRestore(); } };
        var inspected = await procedure.InspectAsync(archive, h.Data.Root, anchor, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(h.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        f.Host.Dispose();
        RestoreResult? returned = null;
        var interrupted = await Record.ExceptionAsync(async () => { returned = await procedure.ExecuteAsync(request, default); });
        Assert.True(interrupted is StopRestore, $"Expected exact owned stop; returned={returned}; exception={interrupted}");
        string root = Path.Combine(anchor, "restores", request.RestoreId);
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(root,"journal.json")), OutpostBackupManifest.Json)!;
        Assert.True(journal.OldSetUsable, journal.OldSetReason);
        var selected = procedure.InspectBackout(h.Data.Root, anchor, request.RestoreId);
        var result = await procedure.BackoutAsync(selected with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selected) }, default);
        Assert.Equal(RestoreOutcome.BackedOut, result.Outcome);
        Assert.Equal(inspected.Destination.External!.Committed.Id, result.HeadId);
        using var held = await h.Anchor.AcquireAsync(default);
        var envelope = h.Anchor.Read(held); Assert.NotNull(envelope);
        var observed = await OutpostAuthority.ReadStoredChainAsync(h.Db, h.Files, await h.Db.OutpostAuthority.SingleAsync(), envelope.Committed, currentRuntime: true, default);
        Assert.Equal(OutpostDomainFormat.V2, observed.Format);
        Assert.Equal(1, await h.Db.OutpostAdminCommands.CountAsync()); Assert.Equal(1, await h.Db.AdminActions.CountAsync(a => a.Action == "outposts.ActivateDomainFormat"));
    }

    private sealed class StopRestore : Exception;
}
