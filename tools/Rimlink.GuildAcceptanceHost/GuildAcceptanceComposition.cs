using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Hosting;

namespace Rimlink.GuildAcceptanceHost;

internal sealed partial class GuildAcceptanceComposition:IOutpostAcceptanceComposition,IOutpostBuildQualification,IGuildRuntimeAdmission,IDisposable
{
    private readonly FileStream owner;
    private readonly GuildStandEvidence evidence;
    private readonly GuildStandBusinessClock clock;
    internal GuildStandBootstrap Bootstrap{get;}
    internal string BootstrapDigest{get;}
    internal string? LastRefusal{get;private set;}
    internal bool SetupClockAdvanced=>clock.ClockProjection.TotalAdvanceTicks!=0;
    private bool disposed;
    private GuildStandWorld? world;
    private GuildStandRestore? restore;
    private GuildAcceptanceScope scope=GuildAcceptanceScope.Home;
    private readonly object gate=new();
    private GuildAcceptanceComposition(GuildStandBootstrap bootstrap,FileStream owner)
    {
        Bootstrap=bootstrap;this.owner=owner;BootstrapDigest=GuildStandFiles.Hash(GuildStandFiles.PathIn(bootstrap.Stand,"bootstrap.json"));
        evidence=new(bootstrap);
        try
        {
            ReadRestore();
            var origin = restore?.Permit.Source;
            clock=new(TimeProvider.System,origin?.StandId??bootstrap.Stand.StandId,origin?.BootstrapDigest??BootstrapDigest,bootstrap.SourceIdentity);
        }
        catch { evidence.Dispose(); throw; }
    }
    internal static GuildAcceptanceComposition Open(string root)
    {
        string canonical=AcceptancePaths.Directory(root);if(canonical!=root)throw new InvalidDataException("guild-stand-root-noncanonical");
        var bootstrap=GuildStandFiles.Read<GuildStandBootstrap>(Path.Combine(root,"bootstrap.json"));
        var stand=bootstrap.Stand;
        if(bootstrap.Version!=1 || bootstrap.Mode!=GuildStandFiles.Mode || bootstrap.DomainFormat!=3 || bootstrap.Clock is null || bootstrap.Clock.Version!=1 || bootstrap.Clock.InitialAdvanceTicks!=0
            || bootstrap.Clock.Budget!=GuildClockBudget.V1 || bootstrap.Clock.DomainUtc.Kind!=DateTimeKind.Utc || bootstrap.Clock.DomainUtc!=bootstrap.Clock.RealUtc || stand.Root!=root
            || !Guid.TryParseExact(bootstrap.OwnerId,"D",out _) || bootstrap.SourceIdentity!=OutpostProductQualification.SourceIdentity)
            throw new InvalidDataException("guild-stand-bootstrap-mismatch");
        GuildStandFiles.ValidateLayout(stand,false);
        AcceptancePaths.File(GuildStandFiles.PathIn(stand,"stand.lock"));
        var owner=new FileStream(GuildStandFiles.PathIn(stand,"stand.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        GuildAcceptanceComposition? result=null;
        try
        {
            GuildStandFiles.Checks(stand,stand.Prerequisites,GuildStandFiles.HomeCases,bootstrap.SourceIdentity);GuildStandFiles.Binaries(stand);
            string profilePath=GuildStandFiles.PathIn(stand,"profile.pb");
            byte[] profileBytes=File.ReadAllBytes(profilePath);
            _=BattleManifestContracts.ReadProfile(profileBytes);
            if(Convert.ToHexStringLower(BattleManifestContracts.Digest(BattleDocumentKind.Profile,profileBytes))!=stand.ProfileDigest)throw new InvalidDataException("guild-stand-profile-mismatch");
            result=new(bootstrap,owner);result.ReadWorldAndScope();return result;
        }
        catch{if(result is not null)result.Dispose();else owner.Dispose();throw;}
    }
    public IOutpostBuildQualification Qualification=>this;
    public TimeProvider? OutpostTimeProvider=>clock;
    IGuildAcceptanceClockControl? IGuildRuntimeAdmission.ClockControl=>clock;
    string? IGuildRuntimeAdmission.StandId=>Bootstrap.Stand.StandId;
    public OutpostBuildQualification Observe()
    {
        lock(gate)
        {
            try{ObjectDisposedException.ThrowIf(disposed,this);if(clock.Fault is not null)throw new InvalidDataException(clock.Fault);evidence.RequireUnchanged();
                if (restore is null && File.Exists(GuildStandFiles.PathIn(Bootstrap.Stand, RestoreAdmission))) throw GuildAcceptanceRestorePermit.Invalid();
                ReadWorldAndScope();LastRefusal=null;
                return new(OutpostRuntimeMode.IsolatedAcceptance,OutpostQualificationState.Pending,Bootstrap.SourceIdentity,true,true);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or ObjectDisposedException)
            {LastRefusal=error.Message;return new(OutpostRuntimeMode.IsolatedAcceptance,OutpostQualificationState.Pending,Bootstrap.SourceIdentity,false,false);}
        }
    }
    GuildRuntimeGrant? IGuildRuntimeAdmission.Observe()
    {
        lock(gate)
        {
            if(Observe() is not{AutomatedPrerequisitesVerified:true,IsolationVerified:true} || world is null)return null;
            return new(Bootstrap.Stand.StandId,BootstrapDigest,Bootstrap.SourceIdentity,world.WorldId,world.Boundary,scope,clock.ClockProjection.Ready);
        }
    }
    private void ReadWorldAndScope()
    {
        var stand=Bootstrap.Stand;string path=GuildStandFiles.PathIn(stand,"world.json");
        if(!File.Exists(path)){if(world is not null)throw new InvalidDataException("guild-stand-world-binding-lost");return;}
        var read=GuildStandFiles.Read<GuildStandWorld>(path);
        if(read.Version!=1 || read.StandId!=stand.StandId || read.BootstrapSha256!=BootstrapDigest || string.IsNullOrWhiteSpace(read.WorldId)
            || !Guid.TryParseExact(read.Boundary,"D",out var boundary) || boundary==Guid.Empty || world is not null && world!=read)throw new InvalidDataException("guild-stand-world-binding-mismatch");
        if(world is null)evidence.Pin(path);world=read;
        path=GuildStandFiles.PathIn(stand,"slice.json");
        if (restore is { } selected)
        {
            if (File.Exists(path) || read.WorldId != selected.Permit.Target.WorldId || read.Boundary != selected.Permit.Target.Boundary)
                throw GuildAcceptanceRestorePermit.Invalid();
            scope = GuildAcceptanceScope.Restore;
            return;
        }
        if(!File.Exists(path)){if(scope==GuildAcceptanceScope.Slice)throw new InvalidDataException("guild-stand-slice-admission-lost");return;}
        var admission=GuildStandFiles.Read<GuildStandSlice>(path);
        if(admission.Version!=1 || admission.StandId!=stand.StandId || admission.BootstrapSha256!=BootstrapDigest || admission.WorldSha256!=GuildStandFiles.Hash(GuildStandFiles.PathIn(stand,"world.json")))throw new InvalidDataException("guild-stand-slice-admission-mismatch");
        GuildStandFiles.Checks(stand,admission.Prerequisites,GuildStandFiles.SliceCases,Bootstrap.SourceIdentity);
        if(scope!=GuildAcceptanceScope.Slice){evidence.Pin(path);foreach(var check in admission.Prerequisites)evidence.Pin(GuildStandFiles.PathIn(stand,check.RelativePath),check.Sha256);}
        scope=GuildAcceptanceScope.Slice;
    }
    internal void BindWorld(string worldId,string boundary)
    {
        lock(gate)
        {
            if(Observe() is not{AutomatedPrerequisitesVerified:true,IsolationVerified:true})throw new InvalidDataException("guild-stand-unqualified");
            var binding=new GuildStandWorld(1,Bootstrap.Stand.StandId,BootstrapDigest,worldId,boundary);
            if(world is not null){if(world!=binding)throw new InvalidDataException("guild-stand-world-rebind-refused");return;}
            if(restore is not null)throw GuildAcceptanceRestorePermit.Invalid();
            GuildStandFiles.Create(GuildStandFiles.PathIn(Bootstrap.Stand,"world.json"),binding);ReadWorldAndScope();
        }
    }
    public void ConfigureConfiguration(ConfigurationManager configuration)
    {configuration.Sources.Clear();configuration.AddJsonFile(GuildStandFiles.PathIn(Bootstrap.Stand,"config.json"),false,false);}
    public void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IGuildRuntimeAdmission>();services.AddSingleton<IGuildRuntimeAdmission>(this);
        services.RemoveAll<BattleExecutorRegistry>();services.AddSingleton(sp=>new BattleExecutorRegistry(sp.GetRequiredService<TimeProvider>(),Bootstrap.Stand.ExecutorId,Convert.FromHexString(Bootstrap.Stand.ProfileDigest)));
        services.AddSingleton(this);
        services.AddHostedService(sp=>new GuildStandBindingService(sp.GetRequiredService<IServiceScopeFactory>(),BindWorld));
        services.AddSingleton<GuildStandInterceptor>();services.Configure<global::Grpc.AspNetCore.Server.GrpcServiceOptions>(o=>o.Interceptors.Add<GuildStandInterceptor>());
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter,GuildStandRequestFilter>();
    }
    public void ValidateConfiguration(ServerOptions server,TlsOptions tls,IConfiguration configuration)
    {
        var stand=Bootstrap.Stand;
        if(server.DataDir!=GuildStandFiles.PathIn(stand,"data") || server.Port!=stand.Port || server.MaxPlayers!=2 || string.IsNullOrEmpty(server.ServerPassword)
            || !tls.Required || tls.CertificatePath is not null || tls.KeyPath is not null || tls.SelfSignedPath is not null
            || configuration.GetValue<bool>("GuildCapture:Enabled") || configuration.GetValue<bool>("Outposts:Enabled")
            || configuration["Outposts:AnchorDirectory"]!=GuildStandFiles.PathIn(stand,"anchor") || configuration["Outposts:StorageContract"]!="sqlite-local-v1"
            || configuration.GetSection("Kestrel").Exists() || configuration["urls"] is not null || configuration.GetSection("Serilog").Exists()
            || GuildStandFiles.Hash(GuildStandFiles.PathIn(stand,"config.json"))!=Bootstrap.ConfigurationSha256
            || restore is not null && scope != GuildAcceptanceScope.Restore
            || Observe() is not{AutomatedPrerequisitesVerified:true,IsolationVerified:true})throw new InvalidDataException("guild-stand-configuration-refused");
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;evidence.Dispose();owner.Dispose();}}
}
