using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Hosting;

// Only the friend dev executable/test assembly can reach the isolated host entry point.
internal interface IOutpostAcceptanceComposition
{
    IOutpostBuildQualification Qualification { get; }
    TimeProvider? OutpostTimeProvider => null;
    void ConfigureServices(IServiceCollection services) { }
    void ConfigureConfiguration(ConfigurationManager configuration);
    void ValidateConfiguration(ServerOptions server, TlsOptions tls, IConfiguration configuration);
}
