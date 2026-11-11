using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleTransportAdmissionTests
{
    [Fact]
    public async Task Hot_admission_does_not_acquire_A_and_leaves_no_owned_database_handle_after_S()
    {
        using var f = await GuildFixture.Create();
        f.H.MainObservation.Dispose(); // Its retained WAL keeper is unrelated to transport handle ownership.
        string path = f.H.Data.DatabasePath, moved = path + ".transport-rename";
        File.Move(path, moved); File.Move(moved, path); // Establish the unlocked baseline before opening the transport reader.
        var admission = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, new BattleStateLock());
        using (await f.H.Anchor.AcquireAsync(default))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var held = await admission.HoldAsync(deadline.Token);
            Assert.Equal(f.Scope.WorldId, held.Cut.WorldId);
            Assert.Equal(f.Scope.Boundary, held.Cut.Boundary);
        }
        File.Move(path, moved); File.Move(moved, path);
    }

    [Theory]
    [InlineData("unpublished")][InlineData("head")][InlineData("boundary")]
    public async Task A_missing_or_stale_published_cut_refuses_before_transport_work(string fault)
    {
        using var f = await GuildFixture.Create();
        if (fault == "unpublished") f.H.Feature.GuildCut = null;
        if (fault == "head") await f.H.Db.Database.ExecuteSqlRawAsync("UPDATE outpost_authority SET HeadId = 'changed'");
        if (fault == "boundary") await f.H.Db.Database.ExecuteSqlRawAsync("UPDATE outpost_authority SET RestoreBoundary = 'changed'");
        var admission = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, new BattleStateLock());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => admission.HoldAsync(default));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var storage = await f.H.Files.PerStoreLock.HoldAsync(deadline.Token); // A refusal released all owned locks.
    }

    [Fact]
    public async Task Transport_reader_cannot_mutate_financial_rows()
    {
        using var f = await GuildFixture.Create();
        var admission = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, new BattleStateLock());
        await using var held = await admission.HoldAsync(default);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => held.Db.Database.ExecuteSqlRawAsync("UPDATE outpost_authority SET HeadId = 'changed'"));
    }
}
