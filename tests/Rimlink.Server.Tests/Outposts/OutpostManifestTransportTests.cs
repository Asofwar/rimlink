using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using OutpostAuthority = Rimlink.Server.Application.Outposts.OutpostAuthority;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostManifestTransportTests
{
    [Fact]
    public async Task Rest_manifest_requires_admin_scope_and_digest_bound_explicit_confirmation()
    {
        using var f = await ManifestServer.Create();
        using var read = f.Server.Rest("manifest-read-token-123456789");
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/admin/outposts/manifest/binding")).StatusCode);
        using var admin = f.Server.Rest();
        var binding = await admin.GetAsync("/api/admin/outposts/manifest/binding");
        Assert.Equal(HttpStatusCode.OK, binding.StatusCode);
        Assert.Equal((await f.Document()).Binding, JsonParser.Default.Parse<OutpostManifestBinding>(await binding.Content.ReadAsStringAsync()));
        var doc = await f.Document(); var bytes = doc.ToByteArray();
        var chunk = new OutpostManifestChunk { UploadId = Guid.NewGuid().ToString("D"), TotalBytes = bytes.Length,
            Sha256 = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(bytes)), Data = ByteString.CopyFrom(bytes) };
        var staged = await admin.PostAsync("/api/admin/outposts/manifest/stage", Json(chunk));
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var preview = JsonParser.Default.Parse<ManifestPreview>(await staged.Content.ReadAsStringAsync());
        Assert.True(preview.Complete);
        using (var stagedScope = f.Server.Services.CreateScope())
            Assert.False(await stagedScope.ServiceProvider.GetRequiredService<RimlinkDbContext>().OutpostManifests.AsNoTracking().Select(m => m.Confirmed).SingleAsync());
        var wrong = await admin.PostAsync("/api/admin/outposts/manifest/confirm", Json(new OutpostManifestConfirmation { ManifestId = preview.ManifestId, ExpectedDigest = new string('0', 64) }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var page = await admin.PostAsync("/api/admin/outposts/manifest/read", Json(new OutpostManifestPageRequest { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest }));
        Assert.Equal(bytes, JsonParser.Default.Parse<OutpostManifestPage>(await page.Content.ReadAsStringAsync()).Data.ToByteArray());
        var draft = await admin.PostAsJsonAsync("/api/admin/outposts/preview", new { action = "confirm-manifest", requestId = Guid.NewGuid().ToString("D"),
            reason = "Verify the exact REST manifest", manifestId = preview.ManifestId, manifestDigest = preview.Digest });
        draft.EnsureSuccessStatusCode(); var command = await draft.Content.ReadFromJsonAsync<JsonElement>();
        var confirmation = new OutpostManifestConfirmation { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest,
            CanonicalCommand = ByteString.CopyFrom(command.GetProperty("command").GetBytesFromBase64()),
            IntentSha256 = ByteString.CopyFrom(Convert.FromHexString(command.GetProperty("intentDigest").GetString()!)), Confirmed = true };
        var confirmed = await admin.PostAsync("/api/admin/outposts/manifest/confirm", Json(confirmation));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(preview.ManifestId, JsonParser.Default.Parse<ManifestRevision>(await confirmed.Content.ReadAsStringAsync()).ManifestId);
        using var confirmedScope = f.Server.Services.CreateScope();
        Assert.True(await confirmedScope.ServiceProvider.GetRequiredService<RimlinkDbContext>().OutpostManifests.AsNoTracking().Select(m => m.Confirmed && m.Active).SingleAsync());
    }

    [Fact]
    public async Task Rest_rejects_oversized_body_before_protobuf_model_allocation()
    {
        using var f = await ManifestServer.Create(); using var admin = f.Server.Rest();
        var response = await admin.PostAsync("/api/admin/outposts/manifest/stage", new StringContent(new string(' ', 400 * 1024), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var scope = f.Server.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().OutpostManifests.ToListAsync());
    }

    [Fact]
    public async Task Grpc_manifest_requires_admin_and_never_implicitly_confirms()
    {
        using var f = await ManifestServer.Create();
        var login = await f.Server.RegisterAndLogin("manifest-owner");
        var headers = Bearer(login.SessionToken); using var channel = f.Server.CreateChannel();
        var world = new Rimlink.Protocol.V1.World.WorldClient(channel);
        var binding = await world.GetOutpostManifestBindingAsync(new Empty(), headers);
        byte[] bytes = (await f.Document()).ToByteArray();
        var chunk = new OutpostManifestChunk { UploadId = Guid.NewGuid().ToString("D"), TotalBytes = bytes.Length,
            Sha256 = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(bytes)), Data = ByteString.CopyFrom(bytes) };
        var preview = await world.StageOutpostManifestAsync(chunk, headers);
        Assert.True(preview.Complete);
        using (var stagedScope = f.Server.Services.CreateScope())
            Assert.False(await stagedScope.ServiceProvider.GetRequiredService<RimlinkDbContext>().OutpostManifests.AsNoTracking().Select(m => m.Confirmed).SingleAsync());
        var page = await world.ReadOutpostManifestAsync(new OutpostManifestPageRequest { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest }, headers);
        Assert.Equal(bytes, page.Data.ToByteArray());
        await Assert.ThrowsAsync<RpcException>(() => world.ConfirmOutpostManifestAsync(new OutpostManifestConfirmation { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest }, headers).ResponseAsync);
        var command = await world.PreviewOutpostManifestConfirmationAsync(new() { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest,
            ClientRequestId = Guid.NewGuid().ToString("D"), Reason = "Verify the exact game-admin manifest" }, headers);
        Assert.Empty(command.Blockers);
        var confirmation = await world.ConfirmOutpostManifestAsync(new OutpostManifestConfirmation { ManifestId = preview.ManifestId, ExpectedDigest = preview.Digest,
            CanonicalCommand = command.CanonicalCommand, IntentSha256 = command.IntentSha256, Confirmed = true }, headers);
        Assert.Equal(preview.ManifestId, confirmation.ManifestId);
        using (var confirmedScope = f.Server.Services.CreateScope())
            Assert.True(await confirmedScope.ServiceProvider.GetRequiredService<RimlinkDbContext>().OutpostManifests.AsNoTracking().Select(m => m.Confirmed && m.Active).SingleAsync());
        // The fixture deliberately advertises required mods; authenticate a matching ordinary client.
        await f.Server.RegisterAndLogin("manifest-user");
        var userRequest = PasswordLogin("manifest-user", "pw");
        userRequest.RecoveryVersion = SaveRecoveryData.CurrentVersion;
        userRequest.Mods.Add(new ModRef { PackageId = ModContent.RimlinkPackageId });
        userRequest.Mods.Add(new ModRef { PackageId = "example.mod" });
        var user = await new Rimlink.Protocol.V1.Auth.AuthClient(channel).LoginAsync(userRequest);
        Assert.NotEmpty(user.SessionToken);
        var forbidden = await Assert.ThrowsAsync<RpcException>(() => world.GetOutpostManifestBindingAsync(new Empty(), Bearer(user.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, forbidden.StatusCode);
    }

    [Theory]
    [InlineData("entries")][InlineData("configs")][InlineData("defs")]
    public async Task Modpack_change_is_fenced_and_staged_manifest_requires_revalidation(string part)
    {
        using var f = await ManifestServer.Create();
        using var scope = f.Server.Services.CreateScope(); var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<OutpostManifestService>();
        var preview = await service.StageAsync(Actor.Api, await f.Document(), default);
        using var mutationScope = f.Server.Services.CreateScope(); var mp = mutationScope.ServiceProvider;
        var modpack = mp.GetRequiredService<ModpackService>();
        var held = await sp.GetRequiredService<SaveFiles>().PerStoreLock.HoldAsync(default);
        Task changing;
        try
        {
            changing = part switch
            {
                "entries" => modpack.Set([new() { PackageId = ModContent.RimlinkPackageId, Version = "changed" }], Role.Admin, "fixture"),
                "configs" => modpack.SetConfigs([new() { PackageId = "example.mod", Handle = "ExampleSettings", Content = ByteString.CopyFromUtf8("changed") }], Role.Admin, "fixture"),
                _ => modpack.SetDefs(["Steel", "WoodLog", "TemperateForest", "NewDef"], Role.Admin, "fixture", mp.GetRequiredService<ModpackDefs>())
            };
            Assert.NotSame(changing, await Task.WhenAny(changing, Task.Delay(300)));
        }
        finally { held.Dispose(); }
        await changing;
        await Assert.ThrowsAsync<RpcException>(() => service.ConfirmAsync(Actor.Api, preview.ManifestId, preview.Digest, default));
        var row = await sp.GetRequiredService<RimlinkDbContext>().OutpostManifests.AsNoTracking().SingleAsync();
        Assert.False(row.Confirmed); Assert.False(row.Active);
    }

    private static StringContent Json(IMessage message) => new(JsonFormatter.Default.Format(message), Encoding.UTF8, "application/json");
}

internal sealed class ManifestServer : IDisposable
{
    private readonly AuthorityHarness anchor = new();
    public ServerFixture Server { get; }
    private ManifestServer() => Server = HygieneFixtures.HygieneServer()
        .WithSetting("Outposts:AnchorDirectory", Path.Combine(anchor.Root, "external"))
        .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
        .WithSetting("Auth:AdminLogins:0", "manifest-owner")
        .WithToken(0, "reader", "manifest-read-token-123456789", "read");
    public static async Task<ManifestServer> Create()
    {
        var f = new ManifestServer(); using var scope = f.Server.Services.CreateScope(); var sp = scope.ServiceProvider;
        sp.GetRequiredService<OutpostAnchorStore>().Provision();
        var db = sp.GetRequiredService<RimlinkDbContext>();
        db.ModpackDefs.AddRange(new[] { "TemperateForest", "Steel", "WoodLog" }.Select(n => new ModpackDef { DefName = n }));
        db.ModpackEntries.AddRange(new Rimlink.Server.Storage.Entities.ModpackEntry { PackageId = ModContent.RimlinkPackageId, Position = 0 },
            new Rimlink.Server.Storage.Entities.ModpackEntry { PackageId = "example.mod", Position = 1 });
        await db.SaveChangesAsync();
        string world = await db.World.Select(w => w.InfluenceInstanceId).SingleAsync();
        Assert.Equal(OutpostStartupState.Ready, (await sp.GetRequiredService<OutpostAuthority>().InitializeAsync(new(world, "fixture", true), default)).State);
        await sp.GetRequiredService<OutpostAuthority>().PrepareStartupObservationAsync(sp.GetRequiredService<OutpostMainObservation>(), default);
        var admin = sp.GetRequiredService<OutpostAdminService>();
        var activation = await admin.PreviewActivateDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "Activate the manifest unit fixture", default);
        await admin.ActivateDomainFormatAsync(Actor.Api, activation.Command! with { Confirmed = true }, default);
        return f;
    }
    public async Task<OutpostManifestDocument> Document()
    {
        using var scope = Server.Services.CreateScope();
        var d = new OutpostManifestDocument { SchemaVersion = 1,
            Binding = await scope.ServiceProvider.GetRequiredService<OutpostManifestService>().BindingAsync(Actor.Api, default),
            GameVersion = "1.7", GameDefsSha256 = ByteString.CopyFrom(new byte[32]), MainSurfaceId = "planet", TileCount = 1, ValuationVersion = "test-v1" };
        d.Tiles.Add(new OutpostManifestTile { Tile = 0, SurfaceId = "planet", BiomeDef = "TemperateForest", Land = true, CaravanPassable = true, NaturalTrees = true });
        d.Resources.Add(new OutpostResourceValuation { DefName = "Steel", UnitValueX100 = 190 });
        d.Resources.Add(new OutpostResourceValuation { DefName = "WoodLog", UnitValueX100 = 120 });
        return d;
    }
    public void Dispose() { Server.Dispose(); anchor.Dispose(); }
}
