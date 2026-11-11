using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleReadyOfferTests
{
    private static BattleReadyOfferService Service(GuildFixture f)=>new(f.Executors,new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Profiles,f.Sessions,f.Clock);
    private static BattleReadyOfferRequest Request(BattleStartRequest start)=>new(){ClientRequestId=Guid.NewGuid().ToString("D"),Scope=start.Scope.Clone()};
    private static BattleReadyOfferAckRequest Ack(BattleReadyOfferReply offer)=>new(){Scope=offer.Scope.Clone(),DeliveryId=offer.DeliveryId,Nonce=offer.Nonce};
    [Fact]
    public async Task Exact_offer_delivery_and_automatic_ack_do_not_mean_Ready_or_create_a_financial_transition()
    {
        using var f=await GuildFixture.Create(true);var(start,manifest)=await GuildBattleStartTests.Prepare(f,false);var service=Service(f);var session=f.Sessions.ByPlayer(2)!;var request=Request(start);
        string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        using(await f.H.Anchor.AcquireAsync(default))
        {
            var offer=await service.ReadAsync(session,request,default);Assert.Equal(offer,await service.ReadAsync(session,request,default));Assert.Equal(manifest.ToByteArray(),offer.Manifest.ToByteArray());
            f.Time.Advance(TimeSpan.FromSeconds(1));var ack=await service.AcknowledgeAsync(session,Ack(offer),default);Assert.Equal(ack,await service.AcknowledgeAsync(session,Ack(offer),default));
            Assert.Equal(offer.DeliveryId,ack.DeliveryId);Assert.True(ack.ReceivedAtUtcTicks>0);
        }
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);Assert.Empty(await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildBattleControlGrant>().ToArrayAsync());
    }
    [Theory]
    [InlineData("nonce")][InlineData("player")][InlineData("deadline")][InlineData("boundary")]
    public async Task Wrong_or_late_ack_never_proves_delivery(string damage)
    {
        using var f=await GuildFixture.Create(true);var(start,_)=await GuildBattleStartTests.Prepare(f,false);var service=Service(f);var session=f.Sessions.ByPlayer(2)!;
        var offer=await service.ReadAsync(session,Request(start),default);var ack=Ack(offer);
        switch(damage){case "nonce":ack.Nonce=ByteString.CopyFrom(new byte[32]);break;case "player":session=f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a=>a.Id==1),await f.H.Db.Players.SingleAsync(p=>p.Id==1),"127.0.0.1",out _);session.GuildVersion=1;break;
            case "deadline":f.Time.Advance(TimeSpan.FromMinutes(5));break;case "boundary":ack.Scope.RestoreBoundary=Guid.NewGuid().ToString("D");break;}
        await Assert.ThrowsAnyAsync<Exception>(()=>service.AcknowledgeAsync(session,ack,default));
        Assert.Null(f.Artifacts.DescriptorHeld(start.Scope.BattleId,start.Scope.Fence,"offer-ack-1"));
    }
}
