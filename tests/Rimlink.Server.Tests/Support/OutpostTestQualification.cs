using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Outposts;

namespace Rimlink.Server.Tests.Support;

// Synthetic unit composition only; it is neither a product seal nor actual G1–G7 stand evidence.
internal sealed class OutpostTestQualification : IOutpostBuildQualification
{
    internal bool Available { get; set; } = true;
    public OutpostBuildQualification Observe() => new(OutpostRuntimeMode.IsolatedAcceptance, OutpostQualificationState.Pending,
        OutpostProductQualification.SourceIdentity, Available, true);
    internal static OutpostIntegration[] Integrations() => OutpostReadiness.RequiredIntegrations.Select(id =>
        new OutpostIntegration(id, OutpostReadiness.ContractRevision, OutpostIntegrationState.Implemented)).ToArray();
}

internal static class OutpostUnitComposition
{
    internal static ServerFixture WithOutpostUnitQualification(this ServerFixture server, OutpostTestQualification? qualification = null) =>
        server.WithSetting("Outposts:Enabled", "true").WithServices(services =>
        {
            services.AddSingleton(qualification ?? new OutpostTestQualification());
            services.AddSingleton(sp => new OutpostFeature(sp.GetRequiredService<IOptions<OutpostOptions>>(),
                sp.GetRequiredService<OutpostTestQualification>(), OutpostTestQualification.Integrations));
        });

    internal static async Task Activate(AuthorityHarness root)
    {
        await root.Authority.OpenAsync(default);
        if (root.Feature.VerifiedDomainFormat == OutpostDomainFormat.V1)
            await OutpostAdminActivationTests.Service(root).ActivateDomainFormatAsync(Rimlink.Server.Application.Admin.Actor.Api,
                await OutpostAdminActivationTests.CommandForCurrent(root), default);
    }

    internal static async Task Activate(IServiceProvider provider)
    {
        var authority = provider.GetRequiredService<OutpostAuthority>();
        await authority.OpenAsync(default);
        await authority.PrepareStartupObservationAsync(provider.GetRequiredService<OutpostMainObservation>(), default);
        if (provider.GetRequiredService<OutpostFeature>().VerifiedDomainFormat != OutpostDomainFormat.V1) return;
        var admin = provider.GetRequiredService<OutpostAdminService>();
        var preview = await admin.PreviewActivateDomainFormatAsync(Rimlink.Server.Application.Admin.Actor.Api,
            Guid.NewGuid().ToString("D"), "Explicit unit-fixture v2 activation", default);
        await admin.ActivateDomainFormatAsync(Rimlink.Server.Application.Admin.Actor.Api, preview.Command! with { Confirmed = true }, default);
    }
}
