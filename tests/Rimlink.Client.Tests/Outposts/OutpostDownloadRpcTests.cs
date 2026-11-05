using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostDownloadRpcTests
{
    [Fact]
    public async Task Real_restore_download_supplies_one_exact_bridge_without_relabelling_original_XML()
    {
        using var fixture = await OutpostClientFixture.Create(); string oldBoundary = fixture.Scope.RestoreBoundary;
        await fixture.RestoreForDownload();
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        string directory = Path.Combine(Path.GetTempPath(), "rimlink-restore-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "current.rws");
            var downloaded = (await new SaveTransfer(connection).DownloadSnapshotAsync(file, default))!;
            Assert.Equal(fixture.RawSave, File.ReadAllBytes(file)); Assert.True(downloaded.MatchesFile(file));
            Assert.NotNull(downloaded.Header.OutpostRestoreBridge); Assert.Null(downloaded.Header.OutpostRollbackBridge);
            Assert.Null(downloaded.Header.OutpostRollbackFromViewBridge); Assert.Equal((uint)1, downloaded.Header.OutpostVersion);
            Assert.NotEqual(oldBoundary, downloaded.Header.OutpostRestoreBoundary); Assert.Equal(0, downloaded.Header.SaveWriteEpoch);
            using var raw = File.OpenRead(file); var recovery = SaveRecoveryData.ReadXml(raw, out byte[] hash);
            Assert.Equal(oldBoundary, recovery.Outposts.Metadata.Scope.RestoreBoundary);
            Assert.True(OutpostDownloadChecks.Validate(downloaded.Header, recovery, hash, connection.Login).Allowed);
            var loaded = OutpostLoadChecks.Prepare(downloaded.Header, recovery, hash, connection.Login);
            Assert.True(loaded.Allowed); Assert.True(loaded.RequiresFreshCapture);
            Assert.Equal(downloaded.Header.OutpostRestoreBoundary, loaded.Metadata!.Scope.RestoreBoundary);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Real_restore_then_rollback_download_uses_one_from_view_root_and_keeps_actual_B0_bytes()
    {
        using var fixture = await OutpostClientFixture.Create(); string oldBoundary = fixture.Scope.RestoreBoundary;
        await fixture.RestoreForDownload(); await fixture.RollbackCurrentForDownload();
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        string directory = Path.Combine(Path.GetTempPath(), "rimlink-nested-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "current.rws");
            var downloaded = (await new SaveTransfer(connection).DownloadSnapshotAsync(file, default))!;
            Assert.Equal(fixture.RawSave, File.ReadAllBytes(file)); Assert.True(downloaded.MatchesFile(file));
            Assert.NotNull(downloaded.Header.OutpostRollbackFromViewBridge); Assert.Null(downloaded.Header.OutpostRollbackBridge);
            Assert.Null(downloaded.Header.OutpostRestoreBridge); Assert.Equal(1, downloaded.Header.SaveWriteEpoch);
            Assert.NotEqual(oldBoundary, downloaded.Header.OutpostRestoreBoundary);
            using var raw = File.OpenRead(file); var recovery = SaveRecoveryData.ReadXml(raw, out byte[] hash);
            Assert.Equal(oldBoundary, recovery.Outposts.Metadata.Scope.RestoreBoundary); Assert.Equal(0, recovery.WriteEpoch);
            Assert.True(OutpostDownloadChecks.Validate(downloaded.Header, recovery, hash, connection.Login).Allowed);
            var loaded = OutpostLoadChecks.Prepare(downloaded.Header, recovery, hash, connection.Login);
            Assert.True(loaded.Allowed); Assert.True(loaded.RequiresFreshCapture); Assert.NotNull(loaded.Metadata!.RollbackFromViewBridge);
            Assert.Null(loaded.Metadata.RestoreBridge); Assert.Null(loaded.Metadata.RollbackBridge);
            Assert.Equal(downloaded.Header.OutpostRestoreBoundary, loaded.Metadata.Scope.RestoreBoundary); Assert.Equal(1, loaded.Metadata.Scope.SaveWriteEpoch);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Real_server_download_keeps_immutable_bytes_and_supplies_only_the_committed_epoch_grant(bool rollback)
    {
        using var fixture = await OutpostClientFixture.Create(); if (rollback) await fixture.RollbackForDownload();
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        string directory = Path.Combine(Path.GetTempPath(), "rimlink-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "current.rws"); File.WriteAllText(file, "previous local snapshot");
            var downloaded = (await new SaveTransfer(connection).DownloadSnapshotAsync(file, default))!;
            Assert.Equal(fixture.RawSave, File.ReadAllBytes(file)); Assert.True(downloaded.MatchesFile(file));
            Assert.Equal(rollback ? 1 : 0, downloaded.Header.SaveWriteEpoch);
            using var raw = File.OpenRead(file); var recovery = SaveRecoveryData.ReadXml(raw, out byte[] hash);
            Assert.Equal(0, recovery.WriteEpoch); Assert.Equal(downloaded.RawSha256, hash);
            Assert.True(OutpostDownloadChecks.Validate(downloaded.Header, recovery, hash, connection.Login).Allowed);
            if (rollback) Assert.NotNull(downloaded.Header.OutpostRollbackBridge); else Assert.Null(downloaded.Header.OutpostRollbackBridge);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
