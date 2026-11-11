using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleExecutorAuthTests
{
    [Fact]
    public void Enabled_transport_with_missing_credentials_refuses_startup()
    {
        using var fixture = new ServerFixture().WithSetting("GuildCapture:Enabled", "true");
        var error = Record.Exception(() => fixture.CreateChannel());
        Assert.NotNull(error); Assert.Contains(nameof(OptionsValidationException), error.ToString());
        Assert.Contains("Enabled GuildCapture requires an executor identity and two SHA-256 digests", error.ToString());
    }
    [Fact]
    public async Task Executor_messages_are_bounded_before_allocating_a_global_save_message()
    {
        using var fixture = Configured(); using var channel = fixture.CreateChannel();
        var error = await Assert.ThrowsAsync<RpcException>(() => new BattleExecutor.BattleExecutorClient(channel)
            .RegisterAsync(new() { ExecutorId = new string('x', 300000), ProfileDigest = ByteString.CopyFrom(Profile) }, Headers()).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }
    private const string Token = "test-executor-credential-is-not-a-player-session-token";
    private static readonly byte[] Profile = SHA256.HashData("test-approved-native-profile"u8);
    private static Metadata Headers(string token = Token) => new() { { "authorization", "RimlinkExecutor " + token } };
    private static ServerFixture Configured() => new ServerFixture().WithSetting("Auth:AllowPassword", "true").WithSetting("GuildCapture:Enabled", "true")
        .WithSetting("GuildCapture:ExecutorId", "arbiter-1")
        .WithSetting("GuildCapture:ExecutorTokenSha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token))))
        .WithSetting("GuildCapture:ApprovedProfileDigest", Convert.ToHexStringLower(Profile));
    private static BattleExecutorHello Hello() => new() { ExecutorId = "arbiter-1", ProfileDigest = ByteString.CopyFrom(Profile) };

    [Fact]
    public async Task Dedicated_credential_registers_one_profile_and_never_creates_a_player_session()
    {
        using var fixture = Configured(); using var channel = fixture.CreateChannel(); var api = new BattleExecutor.BattleExecutorClient(channel);
        var reply = await api.RegisterAsync(Hello(), Headers());
        Assert.Equal("arbiter-1", reply.ExecutorId); Assert.True(reply.Fence > 0); Assert.Equal(Profile, reply.ProfileDigest.ToByteArray());
        var heartbeat = await api.HeartbeatAsync(new() { Lease = reply, HasUnfinishedProcess = true }, Headers());
        Assert.Equal(reply, heartbeat);
        var refused = await Assert.ThrowsAsync<RpcException>(() => new Wallet.WalletClient(channel).GetAsync(new(), Headers()).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
    }

    [Theory]
    [InlineData("none")][InlineData("wrong")][InlineData("player")][InlineData("profile")][InlineData("identity")]
    public async Task Player_login_foreign_identity_and_wrong_profile_cannot_become_an_executor(string fault)
    {
        using var fixture = Configured(); using var channel = fixture.CreateChannel(); var api = new BattleExecutor.BattleExecutorClient(channel);
        var headers = Headers(); var hello = Hello();
        if (fault == "none") headers = new();
        if (fault == "wrong") headers = Headers("wrong-dedicated-credential");
        if (fault == "player") { var login = await fixture.RegisterAndLogin("player"); headers = new() { { "authorization", "Bearer " + login.SessionToken } }; }
        if (fault == "profile") hello.ProfileDigest = ByteString.CopyFrom(new byte[32]);
        if (fault == "identity") hello.ExecutorId = "other";
        var error = await Assert.ThrowsAsync<RpcException>(() => api.RegisterAsync(hello, headers).ResponseAsync);
        Assert.Equal(ErrorCode.BattleExecutorRefused, EconomyCode(error));
    }

    [Fact]
    public async Task Default_deployment_keeps_the_executor_transport_disabled()
    {
        using var fixture = new ServerFixture(); using var channel = fixture.CreateChannel();
        var error = await Assert.ThrowsAsync<RpcException>(() => new BattleExecutor.BattleExecutorClient(channel).RegisterAsync(Hello(), Headers()).ResponseAsync);
        Assert.Equal(ErrorCode.GuildUnavailable, EconomyCode(error));
    }

    private static ErrorCode EconomyCode(RpcException error) => ErrorDetail.Parser.ParseFrom(error.Trailers.GetValueBytes(RpcErrors.DetailTrailer)!).Code;
}
