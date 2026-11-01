using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Net;

/// <summary>Panel REST rollback through the real server and client login/download; does not load a game map.</summary>
public class CompactedRollbackLoginTests
{
    [Fact]
    public async Task PanelRollbackToACompactedVersionIsDownloadedAfterRelogin()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true")
            .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);
        string local = Path.Combine(fixture.DataDir, "client", "colony.rws");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        byte[] older = SaveFixtures.Decompress(SaveFixtures.MakeSave(ServerFixture.WorldSeed, "Nova", 100, paddingBytes: 1_000_000));
        byte[] newer = SaveFixtures.Decompress(SaveFixtures.MakeSave(ServerFixture.WorldSeed, "Nova", 200, paddingBytes: 1_000_000));
        long playerId;
        await using (var connection = await ClientFixtures.Connected(fixture, "nova", "Nova"))
        {
            playerId = connection.Login!.Player.PlayerId;
            var transfer = new SaveTransfer(connection);
            await File.WriteAllBytesAsync(local, older);
            await transfer.UploadAsync(local, new SaveMeta(ServerFixture.WorldSeed, "Nova", 100), CancellationToken.None);
            await File.WriteAllBytesAsync(local, newer);
            await transfer.UploadAsync(local, new SaveMeta(ServerFixture.WorldSeed, "Nova", 200), CancellationToken.None);
        }
        await SaveFixtures.Compact(fixture, playerId);
        var files = fixture.Services.GetRequiredService<SaveFiles>();
        Assert.IsType<SaveForm.Delta>(files.Locate(playerId, 1));
        using var admin = fixture.Rest();

        using var response = await admin.PostAsync($"/api/admin/players/{playerId}/saves/1/rollback", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var reconnected = ClientFixtures.Connect(fixture, "nova");
        var login = await reconnected.ConnectAsync(CancellationToken.None);
        Assert.True(login.HasSave);
        Assert.Equal(LoginAction.DownloadThenLoad, LoginPlan.Decide(pendingFlag: false, localSaveExists: true, login.HasSave, login.HasSettlement));
        Assert.Equal(newer, await File.ReadAllBytesAsync(local));
        string? downloaded = await new SaveTransfer(reconnected).DownloadAsync(local, CancellationToken.None);
        Assert.Equal(local, downloaded);
        Assert.Equal(older, await File.ReadAllBytesAsync(local));
        Assert.True(File.Exists(Path.Combine(fixture.DataDir, "saves", playerId.ToString(), "1.rws.zst")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(local)!, "*.tmp"));
    }
}
