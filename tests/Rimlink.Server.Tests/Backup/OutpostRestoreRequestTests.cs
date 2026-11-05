using System.Security.Cryptography;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreRequestTests
{
    [Theory]
    [InlineData("confirmation")]
    [InlineData("archive")]
    [InlineData("witness")]
    [InlineData("installation")]
    [InlineData("boundary")]
    [InlineData("anchor-missing")]
    [InlineData("live-server")]
    public async Task Execute_refuses_changed_or_unconfirmed_selection_without_replacing_bytes(string change)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var data = f.H.F.H.H.Data;
        string anchor = change == "anchor-missing" ? Path.Combine(f.H.F.H.H.Root, "missing-anchor")
            : Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)!;
        var procedure = new OutpostRestoreProcedure();
        var inspected = await procedure.InspectAsync(archive, data.Root, anchor, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1",
            Rimlink.Server.Tests.Support.LocalTestStorage.LinuxQualification(data.Root));
        if (change == "installation") request = request with { TargetInstallationId = Guid.NewGuid().ToString("D") };
        if (change == "boundary") request = request with { TargetBoundary = inspected.Manifest.Authority.RestoreBoundary };
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        if (change == "confirmation") request = request with { ConfirmationDigest = [] };
        if (change == "archive")
        {
            using var appended = new FileStream(archive, FileMode.Append, FileAccess.Write); appended.WriteByte(1);
        }
        if (change == "witness")
        {
            f.H.F.Time.Advance(TimeSpan.FromHours(1));
            await f.H.Service.StateAsync(1, new Rimlink.Protocol.V1.OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
        }
        if (change != "live-server") f.Host.Dispose(); // Only OUR fixture server, never a workstation process.
        string before = await f.Cut();
        byte[] oldDatabase = DatabaseDigest(data.DatabasePath);
        byte[] oldAnchor = File.ReadAllBytes(f.H.F.H.H.Anchor.DatabasePath);
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal(RestoreOutcome.Refused, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(request.RestoreId, result.RestoreId);
        Assert.Equal(before, await f.Cut());
        Assert.Equal(oldDatabase, DatabaseDigest(data.DatabasePath));
        Assert.Equal(oldAnchor, File.ReadAllBytes(f.H.F.H.H.Anchor.DatabasePath));
        Assert.False(Directory.Exists(Path.Combine(anchor, "restores")));
    }

    private static byte[] DatabaseDigest(string path)
    {
        // Observation of OUR running SQLite host must permit its existing read/write handle.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(file);
    }
}
