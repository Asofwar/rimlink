using RimWorld;
using Rimlink.Client.Game.Live;
using Rimlink.Client.Game.Outposts;
using Rimlink.Client.Game.Visits;
using Rimlink.Protocol.V1;
using Verse;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

[CollectionDefinition("Pawn custody save context", DisableParallelization = true)]
public sealed class PawnCustodySaveContextCollection { }

[Collection("Pawn custody save context")]
public sealed class PawnCustodySaveContextTests : IDisposable
{
    private readonly PawnCustodyRegistry registry = new(new Verse.Game());
    private readonly Pawn pawn = new() { thingIDNumber = 12 };
    private readonly PawnRegistryEnvelope envelope;

    public PawnCustodySaveContextTests()
    {
        Reset();
        PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.Add(pawn);
        envelope = registry.Bootstrap(pawn, new OutpostScopeMessage
        {
            WorldInstanceId = "world-1", OwnerPlayerId = 7, SaveGeneration = 3,
            SaveWriteEpoch = 4, RestoreBoundary = "boundary-1"
        });
    }

    private static void HomeLivePending()
    {
        LiveGuest.Session = new LiveSession { Id = 42 };
        LiveGuest.Loading = true;
    }

    [Fact]
    public void Home_live_presave_exports_the_existing_uuid_while_guest_session_is_pending()
    {
        HomeLivePending();
        var validation = registry.ValidateOwnGame();
        Assert.False(validation.RecoveryRequired);
        Assert.Equal(envelope, Assert.Single(validation.Registry));
    }

    [Fact]
    public void Home_live_presave_rejects_ambiguous_registry_before_scribing()
    {
        var duplicate = new Pawn { thingIDNumber = 12 };
        PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.Add(duplicate);
        registry.Bind(duplicate, envelope);
        HomeLivePending();
        Scribe.mode = LoadSaveMode.Saving;
        Assert.Throws<InvalidDataException>(() => registry.ExposeData());
        Assert.Equal(0, Scribe_Collections.Writes);
    }

    [Fact]
    public void Pending_guest_session_keeps_authority_ingress_blocked()
    {
        HomeLivePending();
        Assert.False(PawnCustodyRegistry.IsOwnGame);
        Assert.Throws<InvalidDataException>(() => registry.Bootstrap(new Pawn { thingIDNumber = 13 }, envelope.Scope));
        Assert.Throws<InvalidDataException>(() => registry.Bind(new Pawn(), envelope));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Actual_foreign_copy_never_exports_home_authority(bool inVisit, bool mirror)
    {
        VisitSession.InVisit = inVisit;
        MirrorMode.Active = mirror;
        LiveGuest.Session = new LiveSession { Id = 42 };
        var validation = registry.ValidateOwnGame();
        Assert.False(validation.RecoveryRequired);
        Assert.Empty(validation.Registry);
    }

    private static void Reset()
    {
        LiveGuest.Session = null;
        LiveGuest.Loading = false;
        VisitSession.InVisit = false;
        MirrorMode.Active = false;
        Scribe.mode = LoadSaveMode.Inactive;
        Scribe_Collections.Writes = 0;
        PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.Clear();
    }

    public void Dispose() => Reset();
}
