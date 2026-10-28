using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;

namespace Rimlink.Client.Tests.Support;

public sealed class PasswordCredentials(string login, string password) : ICredentialProvider
{
    public Task<LoginRequest> BuildLogin(CancellationToken cancellationToken) =>
        Task.FromResult(new LoginRequest { Password = new PasswordCredential { Login = login, Password = password } });
}

public sealed class NoPrompt : IPinPrompt
{
    public Task<bool> Confirm(ServerEndpoint endpoint, string fingerprint, string? previous) => Task.FromResult(true);
}

public static class ClientFixtures
{
    public static RimlinkConnection Connect(ServerFixture fixture, string login, string password = "pw") =>
        new(new ServerEndpoint("test", 1), new PasswordCredentials(login, password), new NoPrompt(), new ConsoleLog(),
            new ConnectionTransport(fixture.Server.BaseAddress, fixture.Server.CreateHandler()));

    /// <summary>Registers the account, connects and returns a live connection with its event loop running.</summary>
    public static async Task<RimlinkConnection> Connected(ServerFixture fixture, string login, string? playerName = null)
    {
        await fixture.RegisterAndLogin(login, playerName: playerName ?? login);
        var connection = Connect(fixture, login);
        await connection.ConnectAsync(CancellationToken.None);
        return connection;
    }

    public static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(50);
        }
    }
}
