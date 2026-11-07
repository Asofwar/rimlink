using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed class AcceptanceComposition : IOutpostAcceptanceComposition, IOutpostBuildQualification, IDisposable
{
    private readonly AcceptanceStand stand;
    private readonly string configurationHash;
    private readonly FileStream ownership;
    private bool disposed;
    internal string? LastRefusal { get; private set; }
    internal AcceptanceRuntime? Runtime { get; set; }
    public TimeProvider? OutpostTimeProvider => Runtime?.Clock;
    public void ConfigureServices(IServiceCollection services)
    {
        if (Runtime is not { } runtime) return;
        services.AddSingleton(runtime);
        services.AddSingleton<AcceptanceReplyInterceptor>();
        services.Configure<global::Grpc.AspNetCore.Server.GrpcServiceOptions>(o => o.Interceptors.Add<AcceptanceReplyInterceptor>());
    }
    private AcceptanceComposition(AcceptanceStand stand, FileStream ownership)
    { this.stand = stand; this.ownership = ownership; configurationHash = AcceptanceStandFiles.HashFile(stand.ConfigurationFile); }

    internal static AcceptanceComposition Open(AcceptanceStand stand)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new InvalidDataException("acceptance-platform-unsupported");
        AcceptanceStandFiles.VerifyLaunchOwner(stand);
        // Same cross-process file-sharing fence as SaveStorageLock; Unix byte-range locks alone are process-owned.
        var ownership = new FileStream(AcceptanceStandFiles.LockPath(stand), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            return new(stand, ownership);
        }
        catch { ownership.Dispose(); throw; }
    }
    public IOutpostBuildQualification Qualification => this;
    public OutpostBuildQualification Observe()
    {
        bool isolated = false, automated = false; LastRefusal = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Runtime?.Failed == true) throw new InvalidDataException("acceptance-clock-journal-failed");
            AcceptanceStandFiles.VerifyOwner(stand);
            if (AcceptanceStandFiles.HashFile(stand.ConfigurationFile) != configurationHash)
                throw new InvalidDataException("acceptance-configuration-changed");
            AcceptancePaths.File(Path.Combine(stand.GameRoot, OperatingSystem.IsWindows() ? "RimWorld.exe" : "RimWorldLinux"));
            isolated = true;
            automated = AcceptanceStandFiles.VerifyPrerequisites(stand);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException or ObjectDisposedException)
        { LastRefusal = error.Message; }
        return new(OutpostRuntimeMode.IsolatedAcceptance, OutpostQualificationState.Pending, OutpostProductQualification.SourceIdentity, automated, isolated);
    }
    public void ConfigureConfiguration(ConfigurationManager configuration)
    {
        // This explicitly selected stand file is the only configuration input; no inherited production auth/environment.
        configuration.Sources.Clear();
        configuration.AddJsonFile(stand.ConfigurationFile, optional: false, reloadOnChange: false);
    }
    public void ValidateConfiguration(ServerOptions server, TlsOptions tls, IConfiguration configuration)
    {
        AcceptanceStandFiles.VerifyLaunchOwner(stand);
        if (!Path.GetFullPath(server.DataDir).Equals(stand.DataDirectory, AcceptancePaths.Comparison) || server.Port != stand.Port
            || configuration["Outposts:AnchorDirectory"] != stand.AnchorDirectory || configuration["Outposts:StorageContract"] != "sqlite-local-v1"
            || (configuration["Outposts:QualifiedLinuxFileSystem"] ?? "") != stand.QualifiedLinuxFileSystem
            || configuration.GetSection("Kestrel").GetChildren().Any() || configuration["urls"] is not null
            || configuration.GetSection("Serilog:WriteTo").GetChildren().Any() || configuration.GetSection("Serilog:Using").GetChildren().Any())
            throw new InvalidDataException("acceptance-configuration-outside-owned-stand");
        foreach (string? path in new[] { tls.CertificatePath, tls.KeyPath, tls.SelfSignedPath })
            if (!string.IsNullOrEmpty(path) && !AcceptancePaths.Within(Path.GetFullPath(path), stand.DataDirectory))
                throw new InvalidDataException("acceptance-tls-path-outside-owned-stand");
        if (OutpostFeature.SafetyIntegrations().Any(i => i.State != OutpostIntegrationState.Implemented || i.Revision != OutpostReadiness.ContractRevision))
            throw new InvalidDataException("acceptance-safety-integration-unavailable");
        // Kestrel binds the same loopback address and fails if another owner wins the gap after this probe.
        var probe = new TcpListener(IPAddress.Loopback, stand.Port);
        try { probe.Start(); } finally { probe.Stop(); }
        if (Observe() is not { AutomatedPrerequisitesVerified: true, IsolationVerified: true })
            throw new InvalidDataException("acceptance-prerequisites-or-isolation-unverified");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ownership.Dispose();
    }
}
