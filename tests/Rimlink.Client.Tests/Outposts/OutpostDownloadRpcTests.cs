using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostDownloadRpcTests
{
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
