using Rimlink.Server.Application.Outposts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildOutpostQueryTests
{
    [Fact]
    public async Task Public_ownership_is_visible_but_stock_is_limited_to_the_current_alliance_and_reads_do_not_commit()
    {
        using var f=await GuildFixture.Create(true);var placed=await f.Place(0);
        var terms=await f.Service.PreviewConsentAsync(1,50,f.Scope,placed.OutpostId,4,default);
        await f.Service.AcceptConsentAsync(1,50,f.Scope,Guid.NewGuid().ToString("D"),placed.OutpostId,4,terms.Digest,default);
        string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;f.Time.Advance(TimeSpan.FromHours(2));
        var own=Assert.Single((await f.Service.ListOutpostsAsync(1,f.Scope,default)).Outposts);
        var foreign=Assert.Single((await f.Service.ListOutpostsAsync(2,f.Scope,default)).Outposts);
        Assert.True(own.StockVisible);Assert.Equal(20,own.Stock);Assert.Equal(50,foreign.AllianceId);Assert.False(foreign.StockVisible);Assert.Equal(0,foreign.Stock);
        Assert.Empty((await f.Service.ListOutpostsAsync(2,f.Scope,default)).Obligations);
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal(0,(await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>f.Service.ListOutpostsAsync(1,f.Scope with{Boundary=Guid.NewGuid().ToString("D")},default));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
}
