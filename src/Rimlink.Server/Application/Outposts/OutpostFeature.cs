using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Outposts;

public sealed record AuthorityInitialization(string WorldId, string Actor, bool Confirmed);

public sealed record TransitionIntent(string Id, string WorldId, long PlayerId, long SaveGeneration,
    string RestoreBoundary, string Kind, string Actor, AuthorityHead ExpectedHead, Google.Protobuf.ByteString Intent,
    string? PawnCustodyId = null, string? OutpostId = null);

public enum OutpostStartupState { Ready, RequiresInitialization, Maintenance }
public sealed record OutpostStartupResult(OutpostStartupState State, string Reason = "");

public sealed class OutpostEvidenceUnavailableException(string reason) : InvalidOperationException(reason);

public sealed class OutpostFeature
{
    // Only the complete custody/lifecycle safety graph may advertise the user capability.
    public bool IsAvailable { get; internal set; }
    // Diagnostic observations only; not a readiness or power-loss guarantee.
    public OutpostSqliteReport? LastAnchorBackend { get; internal set; }
    public OutpostSqliteReport? LastDomainBackend { get; internal set; }
    public OutpostStartupResult Startup { get; internal set; } = new(OutpostStartupState.RequiresInitialization);

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutpostOptions>(configuration.GetSection(OutpostOptions.Section));
        services.AddSingleton<OutpostFeature>();
        services.AddSingleton<OutpostAnchorStore>();
        services.AddScoped<OutpostAuthority>();
        services.AddScoped<OutpostSnapshotGuard>();
        services.AddSingleton<OutpostClock>();
        services.AddScoped<OutpostManifestService>();
        services.AddSingleton<OutpostManifestUploads>();
        services.AddScoped<OutpostTileClaims>();
    }

    // Invoked before any hosted service, compactor, expiry sweep or request can run.
    public static async Task CheckStartupAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var authority = provider.GetRequiredService<OutpostAuthority>();
        var feature = provider.GetRequiredService<OutpostFeature>();
        bool history;
        if (string.IsNullOrWhiteSpace(provider.GetRequiredService<IOptions<OutpostOptions>>().Value.AnchorDirectory))
        {
            using var storage = await provider.GetRequiredService<SaveFiles>().PerStoreLock.HoldAsync(ct);
            history = await authority.HasHistoryAsync(ct);
            feature.Startup = history
                ? new(OutpostStartupState.Maintenance, "anchor-missing-with-history")
                : new(OutpostStartupState.RequiresInitialization, "explicit-initialization-required");
        }
        else
        {
            try { await authority.OpenAsync(ct); }
            catch (Exception error) when (error is InvalidDataException or IOException or Microsoft.Data.Sqlite.SqliteException)
            { feature.Startup = new(OutpostStartupState.Maintenance, "anchor-storage-unavailable"); }
            history = await authority.HasHistoryAsync(ct);
        }
        // Disabling the user feature never disables protection of retained custody history.
        if (history && feature.Startup.State != OutpostStartupState.Ready)
            throw new InvalidOperationException("outpost-startup-blocked: " + feature.Startup.Reason);
    }
}
