using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostManifestClientTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Deadline_retries_a_manifest_page_once(int deadlines)
    {
        using var f = await OutpostClientFixture.Create();
        int attempts = 0;
        using var traffic = new OutpostTraffic(f.Server.Server.CreateHandler(), path =>
        {
            if (path != "/rimlink.v1.Outposts/Manifest" || ++attempts > deadlines) return null;
            var refused = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Version = new Version(2, 0),
                Content = new ByteArrayContent([])
            };
            refused.Content.Headers.ContentType = new("application/grpc");
            refused.Headers.Add("grpc-status", "4");
            return refused;
        });
        await using var session = await OutpostTraffic.Connect(f, traffic);
        var state = await new OutpostClient(session.Connection).State(new() { Scope = f.Scope }, default);
        var manifests = new OutpostManifestClient(session.Connection);
        if (deadlines == 1)
            Assert.Equal("world-1", (await manifests.Read(state, default)).Binding.WorldInstanceId);
        else
            Assert.Equal(StatusCode.DeadlineExceeded, (await Assert.ThrowsAsync<RpcException>(() => manifests.Read(state, default))).StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact] public async Task Ordinary_owner_reads_only_current_confirmed_manifest_over_real_rpc()
    {
        using var f = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(f.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(f, traffic); var c = new OutpostManifestClient(session.Connection);
        Assert.True(session.Connection.Login!.OutpostIsolatedAcceptance); // Synthetic unit composition, not a real acceptance run.
        var state = await new OutpostClient(session.Connection).State(new() { Scope = f.Scope }, default);
        var doc = await c.Read(state, default); Assert.Equal("world-1", doc.Binding.WorldInstanceId); Assert.Equal(doc.TileCount, doc.Tiles.Count);
        Assert.Contains("/rimlink.v1.Outposts/Manifest", traffic.Paths);
        await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => c.Binding(default));
        state.ManifestDigest = ByteString.CopyFrom(new byte[32]); await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => c.Read(state, default));
    }
    [Fact] public async Task Admin_stage_does_not_activate_before_separate_exact_confirmation()
    {
        using var f = await OutpostClientFixture.Create(admin: true); using var traffic = new OutpostTraffic(f.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(f, traffic); var c = new OutpostManifestClient(session.Connection);
        var api = new OutpostClient(session.Connection); var old = await api.State(new() { Scope = f.Scope }, default);
        var doc = await c.Read(old, default); Assert.Equal(doc.Binding, await c.Binding(default)); doc.ValuationVersion = "client-export-v2";
        var preview = await c.Stage(doc, default); Assert.True(preview.Complete);
        Assert.Equal(old.ManifestId, (await api.State(new() { Scope = f.Scope }, default)).ManifestId);
        Assert.DoesNotContain("/rimlink.v1.World/ConfirmOutpostManifest", traffic.Paths);
        var command = await c.PreviewConfirmation(preview, Guid.NewGuid().ToString("D"), "Check exact client export", default);
        Assert.Empty(command.Blockers);
        var confirmed = await c.Confirm(preview, command, default); Assert.Equal(preview.ManifestId, confirmed.ManifestId);
        var replay = await c.Confirm(preview, command, default); Assert.True(replay.Replay); Assert.Equal(confirmed.CommandReceiptId, replay.CommandReceiptId);
        Assert.Equal(preview.ManifestId, (await api.State(new() { Scope = f.Scope }, default)).ManifestId);
    }
    [Theory] [InlineData("params")] [InlineData("world")] [InlineData("defs")] [InlineData("dlc")] [InlineData("version")]
    public void Same_seed_alone_never_proves_complete_manifest_identity(string fault)
    {
        var local = new WorldParams { Seed = "same", Rainfall = 2, Temperature = 3, FactionDefs = { "A", "B" }, DifficultyValues = ByteString.CopyFromUtf8("custom") };
        var defs = ByteString.CopyFrom(new byte[32]); var doc = new OutpostManifestDocument { GameVersion = "1.7.4995", GameDefsSha256 = defs,
            Binding = new() { WorldInstanceId = "world-1", WorldParamsSha256 = ByteString.CopyFrom(SHA256.HashData(local.ToByteArray())) }, ActiveDlc = { "royalty" } };
        OutpostManifestClient.RequireLocalMatch(doc, local, "world-1", "1.7.4995", ["royalty"], defs);
        if (fault == "params") local.Temperature++;
        Assert.Throws<InvalidDataException>(() => OutpostManifestClient.RequireLocalMatch(doc, local, fault == "world" ? "world-2" : "world-1",
            fault == "version" ? "1.7.other" : "1.7.4995", fault == "dlc" ? ["biotech"] : ["royalty"], fault == "defs" ? ByteString.CopyFromUtf8("changed") : defs));
    }
    [Fact] public void Chunk_policy_allows_one_bounded_complete_export_not_default_thirty_pages()
    {
        foreach (var method in new[] { "/rimlink.v1.World/StageOutpostManifest", "/rimlink.v1.World/ReadOutpostManifest", "/rimlink.v1.Outposts/Manifest" })
            Assert.InRange(Rimlink.Server.Grpc.RateLimitPolicy.For(method).Permits, 96, 256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Different_colony_setup_uses_the_same_world_binding_only_for_the_same_planet(bool changedPlanet)
    {
        var server = new WorldParams { Seed = "shared", PlanetCoverage = 1, Rainfall = 2, ScenarioDef = "Lost Tribe", FactionDefs = { "Tribe", "Union" } };
        var recorded = server.Clone();
        recorded.ScenarioDef = "Crashlanded";
        if (changedPlanet) recorded.Rainfall++;
        var actual = recorded.Clone();

        if (changedPlanet)
            Assert.Throws<InvalidDataException>(() => OutpostManifestClient.BindWorldParameters(actual, recorded, server));
        else
            Assert.Equal(server.ToByteArray(), OutpostManifestClient.BindWorldParameters(actual, recorded, server).ToByteArray());
        Assert.Equal("Crashlanded", recorded.ScenarioDef);
    }

    [Fact]
    public void Server_parameters_do_not_replace_evidence_of_a_changed_loaded_planet()
    {
        var server = new WorldParams { Seed = "shared", Temperature = 2 };
        var recorded = server.Clone();
        recorded.Temperature++;
        Assert.Throws<InvalidDataException>(() => OutpostManifestClient.BindWorldParameters(server, recorded, server));
    }
}
