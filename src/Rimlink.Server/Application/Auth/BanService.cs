using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Auth;

public sealed record BanRequest(long? AccountId = null, long? SteamId = null, string? Ip = null, string Reason = "", TimeSpan? Duration = null, long IssuedBy = 0);

public sealed class BanService(RimlinkDbContext db, SessionRegistry sessions, ILogger<BanService> log)
{
    public async Task<Ban?> FindActive(long? accountId, long? steamId, long? ownerSteamId, string ip)
    {
        var now = DateTime.UtcNow;
        return await db.Bans
            .Where(b => b.PardonedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now))
            .Where(b => (accountId != null && b.AccountId == accountId)
                     || (steamId != null && b.SteamId == steamId)
                     || (ownerSteamId != null && b.SteamId == ownerSteamId)
                     || b.Ip == ip)
            .OrderBy(b => b.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<Ban> Ban(BanRequest request)
    {
        if (request.AccountId is null && request.SteamId is null && string.IsNullOrWhiteSpace(request.Ip))
            throw new ArgumentException("a ban needs an account, a SteamID or an IP", nameof(request));

        var ban = new Ban
        {
            AccountId = request.AccountId,
            SteamId = request.SteamId,
            Ip = string.IsNullOrWhiteSpace(request.Ip) ? null : request.Ip,
            Reason = request.Reason,
            ExpiresAt = request.Duration is null ? null : DateTime.UtcNow + request.Duration,
            IssuedByAccountId = request.IssuedBy,
            CreatedAt = DateTime.UtcNow,
        };
        db.Bans.Add(ban);
        await db.SaveChangesAsync();
        log.LogInformation("Ban #{Id}: account={AccountId} steam={SteamId} ip={Ip} reason={Reason}", ban.Id, ban.AccountId, ban.SteamId, ban.Ip, ban.Reason);

        foreach (var session in sessions.All)
        {
            bool hit = (ban.AccountId != null && session.AccountId == ban.AccountId) || (ban.Ip != null && session.Ip == ban.Ip);
            if (!hit && ban.SteamId != null)
            {
                var account = await db.Accounts.FindAsync(session.AccountId);
                hit = account?.SteamId == ban.SteamId || account?.OwnerSteamId == ban.SteamId;
            }
            if (hit) sessions.End(session.Token);
        }

        return ban;
    }

    public async Task<bool> Pardon(long banId)
    {
        var ban = await db.Bans.FindAsync(banId);
        if (ban is null || ban.PardonedAt != null) return false;
        ban.PardonedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public Task<List<Ban>> ListActive()
    {
        var now = DateTime.UtcNow;
        return db.Bans.Where(b => b.PardonedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now)).OrderBy(b => b.Id).ToListAsync();
    }
}
