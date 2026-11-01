extern alias tools;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Tests.Support;
using tools::Rimlink.Tools.Commands;
using CliAdmin = tools::Rimlink.Tools.Commands.AdminCommand;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Auctions;

public class AuctionCliTests
{
    [Fact]
    public async Task CliAndChatCommandsListAndCancelThroughSharedAdminService()
    {
        using var f = EconomyServer().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);
        var seller = await f.RegisterAndLogin("seller");
        var lot = await new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel()).PostAsync(new AuctionPostRequest
        { ClientRequestId = "cli-auction", Items = Items(("Steel", 1)), StartPrice = 1, DurationSeconds = 60 }, Bearer(seller.SessionToken));
        var http = f.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, "/api/admin/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminRestExtensions.ApiToken);
        using var rest = new RestClient(http);
        Assert.Equal(lot.Id, JsonDocument.Parse(await CliAdmin.Run(rest, "auctions", [])).RootElement[0].GetProperty("id").GetInt64());
        using var scope = f.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AdminCommandRunner>();
        Assert.Equal(lot.Id, JsonDocument.Parse(await runner.Execute(Actor.Api, new Rimlink.Protocol.V1.AdminCommand { Name = "auctions" })).RootElement[0].GetProperty("Id").GetInt64());
        await CliAdmin.Run(rest, "auction", ["cancel", lot.Id.ToString(), "by", "cli"]);
        Assert.Equal("[]", await CliAdmin.Run(rest, "auctions", []));
        Assert.Equal("no such open auction", await runner.Execute(Actor.Api, new Rimlink.Protocol.V1.AdminCommand { Name = "auction", Args = { "cancel", lot.Id.ToString() } }));
    }
}
