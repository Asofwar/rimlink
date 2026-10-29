using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.World;

/// <summary>Finds abandoned colonies (every enabled condition of spec §2.3) and sends them to the archive.</summary>
public sealed class ColonyHygiene(RimlinkDbContext db, SessionRegistry sessions, ColonyArchive archive, IOptions<HygieneOptions> options, ILogger<ColonyHygiene> log)
{
    public static readonly Actor Sweeper = Actor.Scheduled("system:hygiene");

    /// <returns>How many colonies were archived.</returns>
    public async Task<int> SweepDue(DateTime now)
    {
        var o = options.Value;
        if (o.ArchiveAfterDays == 0) return 0;
        var cutoff = now - TimeSpan.FromDays(o.ArchiveAfterDays);

        var colonies = await db.WorldObjects.AsNoTracking()
            .Where(w => w.Type == WorldObjectTypes.Settlement)
            .Select(w => new
            {
                PlayerId = w.OwnerPlayerId,
                w.Owner.WealthTotal,
                w.Owner.Account.LastSeenAt,
                w.Owner.Account.LastSessionEndAt,
                w.Owner.Account.CreatedAt,
                Tick = db.SaveVersions.Where(v => v.PlayerId == w.OwnerPlayerId && v.IsCurrent).Select(v => (long?)v.GameTick).FirstOrDefault(),
            })
            .ToListAsync();

        int archived = 0;
        foreach (var c in colonies)
        {
            if (sessions.ByPlayer(c.PlayerId) is not null) continue;
            var active = Activity.Latest(c.LastSeenAt, c.LastSessionEndAt) ?? c.CreatedAt;
            if (active >= cutoff) continue;
            bool hasSave = c.Tick is not null;
            if (o.ArchiveMaxWealth > 0)
            {
                if (hasSave && c.WealthTotal == 0) continue; // a save that never reported its wealth: unknown, not poor
                if ((hasSave ? c.WealthTotal : 0) >= o.ArchiveMaxWealth) continue;
            }
            if (o.ArchiveMaxColonyDays > 0 && (c.Tick ?? 0) / GameTime.TicksPerDay >= o.ArchiveMaxColonyDays) continue;

            try
            {
                if (await archive.Archive(c.PlayerId, "auto", Sweeper) is ArchiveOutcome.Archived) archived++;
            }
            catch (Exception ex)
            {
                HygieneMetrics.Archived.WithLabels("failed").Inc();
                log.LogWarning(ex, "Could not archive the colony of player {PlayerId}", c.PlayerId);
            }
        }
        if (archived > 0) log.LogInformation("Sweep archived {Count} abandoned colonies", archived);
        return archived;
    }
}

/// <summary>Runs <see cref="ColonyHygiene.SweepDue"/> a minute after start, then every <c>Hygiene:SweepHours</c>, one pass at a time.</summary>
public sealed class ColonyHygieneService(IServiceScopeFactory scopes, IOptions<HygieneOptions> options, TimeProvider time, ILogger<ColonyHygieneService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (options.Value.ArchiveAfterDays > 0)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<ColonyHygiene>().SweepDue(time.GetUtcNow().UtcDateTime);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogError(ex, "Colony sweep failed");
                    }
                }
                await Task.Delay(TimeSpan.FromHours(options.Value.SweepHours), time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
