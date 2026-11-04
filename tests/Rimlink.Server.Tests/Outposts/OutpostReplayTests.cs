using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Authority = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Double_click_and_competing_workers_have_one_winner(bool differentIntent)
    {
        using var f = await AssignmentHarness.Create();
        using var left = new Caller(f); using var right = new Caller(f);
        var other = f.Request.Clone();
        if (differentIntent)
        {
            other.ClientRequestId = Guid.NewGuid().ToString("N"); other.Caravan.ClientRequestId = other.ClientRequestId;
            other.IntentDigest = OutpostRequestIdentity.Digest(other);
        }
        var held = await f.H.H.Authority.AcquireAsync([1], default);
        var first = Attempt(left.Service, f.Request); var second = Attempt(right.Service, other);
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); await held.DisposeAsync();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(differentIntent ? 1 : 2, results.Count(x => x is not null));
        if (!differentIntent) Assert.Equal(results[0], results[1]);
        Assert.Single(await f.Db.OutpostOperations.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.OutpostAssignments.AsNoTracking().Where(a => a.Active).ToListAsync());
        Assert.Equal(200, await f.Wallet.Available(1));
        Assert.Equal(OutpostStartupState.Ready, (await f.H.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(0u, ErrorCode.OutpostClientRequired)]
    [InlineData(1u, ErrorCode.OutpostUnavailable)]
    public async Task Admission_RPC_stays_closed_until_client_and_complete_graph_are_ready(uint clientVersion, ErrorCode code)
    {
        using var server = HygieneFixtures.HygieneServer();
        var auth = new Rimlink.Protocol.V1.Auth.AuthClient(server.CreateChannel());
        var login = await auth.RegisterAsync(new RegisterRequest { Login = "outpost-rpc", Password = "pw", PlayerName = "RPC", RecoveryVersion = 1, OutpostVersion = clientVersion });
        Assert.NotEmpty(login.SessionToken); Assert.Equal(0u, login.OutpostVersion);
        var client = new Rimlink.Protocol.V1.Outposts.OutpostsClient(server.CreateChannel());
        var error = await Assert.ThrowsAsync<RpcException>(() => client.PrepareAsync(new OutpostPrepareRequest(), GrpcTestExtensions.Bearer(login.SessionToken)).ResponseAsync);
        Assert.Equal(code, error.ErrorDetail()?.Code);
        Assert.NotEqual(StatusCode.Unimplemented, error.StatusCode);
        var stateError = await Assert.ThrowsAsync<RpcException>(() => client.StateAsync(new OutpostStateQuery(), GrpcTestExtensions.Bearer(login.SessionToken)).ResponseAsync);
        var mutationError = await Assert.ThrowsAsync<RpcException>(() => client.MutateAsync(new OutpostMutationRequest(), GrpcTestExtensions.Bearer(login.SessionToken)).ResponseAsync);
        var deliveryError = await Assert.ThrowsAsync<RpcException>(() => client.BeginDeliveryAsync(new OutpostDeliveryRequest(), GrpcTestExtensions.Bearer(login.SessionToken)).ResponseAsync);
        Assert.Equal(code, stateError.ErrorDetail()?.Code); Assert.Equal(code, mutationError.ErrorDetail()?.Code); Assert.Equal(code, deliveryError.ErrorDetail()?.Code);
        var session = server.Services.GetRequiredService<SessionRegistry>().All.Single(s => s.PlayerId == login.Player.PlayerId);
        Assert.Equal(clientVersion, session.OutpostVersion);
    }

    [Fact]
    public async Task Old_client_cannot_login_to_a_colony_with_retained_custody_history()
    {
        using var server = HygieneFixtures.HygieneServer(); var login = await server.RegisterAndLogin("custody-login");
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            db.PawnCustody.Add(new Rimlink.Server.Storage.Entities.PawnCustodyRecord { Id = Guid.NewGuid().ToString("D"),
                WorldId = "retained", PlayerId = login.Player.PlayerId, LocationKind = "Home", LocationId = "Thing_Human1" });
            await db.SaveChangesAsync();
        }
        var auth = new Rimlink.Protocol.V1.Auth.AuthClient(server.CreateChannel());
        var request = GrpcTestExtensions.PasswordLogin("custody-login", "pw"); request.RecoveryVersion = 1;
        var error = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(request).ResponseAsync);
        Assert.Equal(ErrorCode.OutpostClientRequired, error.ErrorDetail()?.Code);
    }
    [Fact]
    public void Admission_mutations_have_explicit_bounded_rate_policy()
    {
        foreach (string method in new[] { "Prepare", "Commit", "Cancel", "Mutate", "BeginDelivery" })
            Assert.Equal((5, TimeSpan.FromSeconds(5)), RateLimitPolicy.For("/rimlink.v1.Outposts/" + method));
    }

    private static async Task<OutpostOperationReply?> Attempt(OutpostAssignmentService service, OutpostPrepareRequest request)
    { try { return await service.PrepareAsync(1, request, default); } catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { return null; } }

    private sealed class Caller : IDisposable
    {
        private readonly RimlinkDbContext db;
        public OutpostAssignmentService Service { get; }
        public Caller(AssignmentHarness f)
        {
            db = new(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options);
            var h = f.H.H; var authority = new Authority(db, h.Anchor, h.Files, h.Owners, h.Feature);
            var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance); var options = Options.Create(new EconomyOptions());
            Service = new(db, authority, new(db, h.Files), new(db, authority, new(f.Time), h.Files), new(db, authority),
                new(db, events), new(options, f.Freeze, new(db, events)), new(db, options), new(f.Time));
        }
        public void Dispose() => db.Dispose();
    }
}
