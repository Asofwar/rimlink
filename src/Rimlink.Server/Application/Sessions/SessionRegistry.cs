using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Sessions;

/// <summary>In-memory authority on who is logged in. One active session per account.</summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, Session> byToken = new();
    private readonly object startLock = new();

    public IReadOnlyCollection<Session> All => byToken.Values.ToArray();

    public Session Start(Account account, Player player, string ip, out Session? replaced)
    {
        lock (startLock)
        {
            replaced = ByAccount(account.Id);
            if (replaced is not null) byToken.TryRemove(replaced.Token, out _);

            var session = new Session
            {
                Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
                AccountId = account.Id,
                PlayerId = player.Id,
                Role = account.Role,
                Ip = ip,
                PlayerName = player.Name,
            };
            byToken[session.Token] = session;
            return session;
        }
    }

    public bool TryGet(string token, out Session session) => byToken.TryGetValue(token, out session!);

    public Session? ByAccount(long accountId) => byToken.Values.FirstOrDefault(s => s.AccountId == accountId);

    public Session? ByPlayer(long playerId) => byToken.Values.FirstOrDefault(s => s.PlayerId == playerId);

    /// <summary>Removes the session and cancels its event stream; later calls with the token are unauthenticated.</summary>
    public void End(string token)
    {
        if (byToken.TryRemove(token, out var session)) session.Cts.Cancel();
    }
}
