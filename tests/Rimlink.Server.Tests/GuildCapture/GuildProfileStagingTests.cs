using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildProfileStagingTests
{
    [Fact]
    public async Task Staging_is_idempotent_content_only_and_never_approves_a_profile_or_advances_the_common_head()
    {
        using var f=await GuildFixture.Create();string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        using var first=new MemoryStream("owned test config"u8.ToArray());
        var staged=await f.Service.StageProfileDocumentAsync(Actor.Api,BattleDocumentKind.Config,first,default);
        using var repeated=new MemoryStream("owned test config"u8.ToArray());
        Assert.Equal(staged,await f.Service.StageProfileDocumentAsync(Actor.Api,BattleDocumentKind.Config,repeated,default));
        using var wrongKind=new MemoryStream("not a profile document"u8.ToArray());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>f.Service.StageProfileDocumentAsync(Actor.Api,BattleDocumentKind.InitialSnapshot,wrongKind,default));
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Empty(await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildCaptureOperation>().ToArrayAsync());
    }
}
