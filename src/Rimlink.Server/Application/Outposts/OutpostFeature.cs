using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Outposts;

public sealed record AuthorityInitialization(string WorldId, string Actor, bool Confirmed);

public sealed record TransitionIntent(string Id, string WorldId, long PlayerId, long SaveGeneration,
    string RestoreBoundary, string Kind, string Actor, AuthorityHead ExpectedHead, Google.Protobuf.ByteString Intent,
    string? PawnCustodyId = null, string? OutpostId = null);

public enum OutpostStartupState { Ready, RequiresInitialization, Maintenance, NoActiveWorld, FormatActivationRequired }
public sealed record OutpostStartupResult(OutpostStartupState State, string Reason = "");

public sealed class OutpostEvidenceUnavailableException(string reason) : InvalidOperationException(reason);

public sealed class OutpostFeature
{
    private readonly IOptions<OutpostOptions> options;
    private readonly IOutpostBuildQualification qualification;
    private readonly Func<OutpostIntegration[]> integrations;
    internal bool RuntimeComposition { get; }
    internal bool? HasHistoryObservation { get; set; }
    internal OutpostDomainFormat? VerifiedDomainFormat { get; private set; }
    internal bool CurrentSchemaVerified { get; private set; }

    // Storage primitives/offline procedures have no network host and cannot advertise capability.
    internal OutpostFeature() : this(Options.Create(new OutpostOptions()), new OutpostProductQualification(), SafetyIntegrations, false) { }
    internal OutpostFeature(IOptions<OutpostOptions> options, IOutpostBuildQualification qualification,
        Func<OutpostIntegration[]>? integrations = null, bool runtimeComposition = true)
    {
        this.options = options; this.qualification = qualification; this.integrations = integrations ?? SafetyIntegrations;
        RuntimeComposition = runtimeComposition;
    }
    public OutpostReadinessDecision Readiness => OutpostReadiness.Evaluate(new(options.Value.Enabled, integrations(),
        qualification.Observe(), Startup, VerifiedDomainFormat, CurrentSchemaVerified, HasHistoryObservation));
    public bool IsAvailable => Readiness.AdvertiseCapability;
    internal void ObserveDomain(OutpostDomainFormat format, bool currentSchema)
    { VerifiedDomainFormat = format; CurrentSchemaVerified = currentSchema; HasHistoryObservation = true; }
    internal void RequireCustodyReadiness()
    {
        if (RuntimeComposition && !Readiness.CanResolveCustody)
            throw new OutpostEvidenceUnavailableException("outpost-readiness-unavailable");
    }
    internal void RequireSuccessorReadiness()
    {
        if (RuntimeComposition && !Readiness.CanBindSuccessorWorld)
            throw new OutpostEvidenceUnavailableException("outpost-successor-readiness-unavailable");
    }
    // Explicit supported contracts, not DI/schema discovery. Build prerequisites qualify these exact source bytes.
    // New required IDs/revisions remain Unknown until deliberately implemented and inventoried here.
    internal static OutpostIntegration[] SafetyIntegrations() => [
        new("authority-storage", 1, OutpostIntegrationState.Implemented), // Authority/backend/domain/raw-evidence tests.
        new("exact-metadata-registry-receipts", 1, OutpostIntegrationState.Implemented), // Snapshot/registry/accepted-receipt tests.
        new("admission-cancellation", 1, OutpostIntegrationState.Implemented), // Assignment/cancel-fence/admin cancellation.
        new("delivery-wealth", 1, OutpostIntegrationState.Implemented), // Typed parcel, physical acceptance and wealth.
        new("ordinary-sources-returns-fences", 1, OutpostIntegrationState.Implemented), // Source/derived route/negative fence tests.
        new("live-raid-coop-ownership", 1, OutpostIntegrationState.Implemented), // Custody live/raid/trade/generation tests.
        new("archive-reset-rollback", 1, OutpostIntegrationState.Implemented), // Lifecycle/world-bind/rollback evidence tests.
        new("coherent-backup-restore-provenance", 1, OutpostIntegrationState.Implemented), // Exact cut/nested restore/crash/backout tests.
        new("game-core-callbacks", 1, OutpostIntegrationState.Implemented), // Real RPC core tests plus required mod/bridge builds and G1–G7.
        new("audited-admin-parity", 1, OutpostIntegrationState.Implemented), // Canonical service + REST/CLI/core/guards; real panel/game G7 remains required.
    ];
    // Diagnostic observations only; not a readiness or power-loss guarantee.
    public OutpostSqliteReport? LastAnchorBackend { get; internal set; }
    public OutpostSqliteReport? LastDomainBackend { get; internal set; }
    public OutpostStartupResult Startup { get; internal set; } = new(OutpostStartupState.RequiresInitialization);

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutpostOptions>(configuration.GetSection(OutpostOptions.Section));
        services.AddSingleton<IOutpostBuildQualification, OutpostProductQualification>();
        services.AddSingleton(sp => new OutpostFeature(sp.GetRequiredService<IOptions<OutpostOptions>>(), sp.GetRequiredService<IOutpostBuildQualification>()));
        services.AddSingleton<OutpostAnchorStore>();
        services.AddSingleton<OutpostMainObservation>();
        services.AddScoped<OutpostAdminService>();
        services.AddScoped<OutpostAuthority>();
        services.AddScoped<OutpostSnapshotGuard>();
        services.AddSingleton<OutpostClock>();
        services.AddScoped<OutpostManifestService>();
        services.AddSingleton<OutpostManifestUploads>();
        services.AddScoped<OutpostTileClaims>();
        services.AddScoped<OutpostAssignmentService>();
        services.AddScoped<OutpostService>();
        services.AddScoped<OutpostParcels>();
        services.AddScoped<PawnCustodyBridge>();
        services.AddScoped<OrdinaryReturnService>();
        services.AddScoped<OutpostLifecycle>();
        services.AddScoped<Backup.OutpostBackupCapture>();
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
        feature.HasHistoryObservation = history;
        if (history && feature.Startup.State is not (OutpostStartupState.Ready or OutpostStartupState.NoActiveWorld))
            throw new InvalidOperationException("outpost-startup-blocked: " + feature.Startup.Reason);
        // Only this existing startup writer phase prepares the reader, after authority/lifecycle gates.
        // A GET never calls this method and never prepares a replacement attachment.
        if (!string.IsNullOrWhiteSpace(provider.GetRequiredService<IOptions<OutpostOptions>>().Value.AnchorDirectory))
            await authority.PrepareStartupObservationAsync(provider.GetRequiredService<OutpostMainObservation>(), ct);
    }
}
