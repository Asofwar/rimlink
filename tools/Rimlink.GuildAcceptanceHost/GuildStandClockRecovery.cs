using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Backup;

namespace Rimlink.GuildAcceptanceHost;

internal sealed partial class GuildAcceptanceComposition
{
    internal async Task SynchronizeClockAsync(IServiceProvider services,CancellationToken ct)
    {
        using var scoped=services.CreateScope();var sp=scoped.ServiceProvider;
        var authority=sp.GetRequiredService<OutpostAuthority>();var feature=sp.GetRequiredService<OutpostFeature>();
        var db=sp.GetRequiredService<RimlinkDbContext>();
        await using var held=await authority.AcquireAsync([],ct);
        using var battle=await sp.GetRequiredService<BattleStateLock>().HoldAsync(ct);
        var startup=await authority.CheckHeldAsync(held,ct);
        if(startup.State is not(OutpostStartupState.Ready or OutpostStartupState.RequiresInitialization or OutpostStartupState.NoActiveWorld))throw GuildAcceptanceClockRules.Invalid();
        if (restore is { } selected)
        {
            var transition = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == selected.Permit.RestoreId && t.Kind == "BulkRestore", ct);
            if (startup.State != OutpostStartupState.Ready || feature.GuildCut?.Acceptance != selected.Permit.Target
                || transition is null || OutpostRestoreEvidence.Plan(transition).Journal.Selection.Isolated != selected.Permit)
                throw GuildAcceptanceRestorePermit.Invalid();
        }
        var prefix=await GuildAuthorityEvidence.ClockJournalAsync(db,ct);
        if (prefix.Length != 0 && (feature.GuildCut?.Acceptance?.BootstrapDigest != BootstrapDigest
            || prefix[0].Request.BootstrapDigest != (restore?.Permit.Source.BootstrapDigest ?? BootstrapDigest)))
            throw GuildAcceptanceClockRules.Invalid();
        string directory=GuildStandFiles.PathIn(Bootstrap.Stand,"evidence/clock-runs");Directory.CreateDirectory(directory);
        GuildStandFiles.Create(Path.Combine(directory,clock.RunId+".json"),new{Version=1,Mode=GuildStandFiles.Mode,Bootstrap.Stand.StandId,BootstrapDigest,
            clock.RunId,clock.StartedRealUtc,clock.StartedRealTimestamp,clock.RealFrequency,DomainFrequency=clock.TimestampFrequency,
            RecoveredRevision=prefix.Length,RecoveredAdvanceTicks=prefix.LastOrDefault()?.TotalAdvanceTicks??0,Cut=feature.GuildCut});
        clock.Synchronize(prefix);
    }
}
