using System.Security.Cryptography;
using Google.Protobuf;
using RimWorld.Planet;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Outposts;

/// <summary>Owner-only detached markers and guarded commands. No cached preview authorizes a pawn import.</summary>
public static class OutpostSync
{
    private static OutpostStateReply? applied;
    private static Verse.Game? game;
    private static DateTime nextRead;
    private static long lifetime;
    public static bool Busy { get; private set; }
    public static string? LastFailure { get; private set; }
    public static void Reset()
    {
        lifetime++; Busy = false; applied = null; game = null; nextRead = default; LastFailure = null;
        if (Find.WorldObjects is not null) foreach (var marker in Find.WorldObjects.AllWorldObjects.OfType<OutpostWorldObject>().ToArray()) Find.WorldObjects.Remove(marker);
        OutpostIncoming.Reset();
    }
    public static void Apply(OutpostStateReply state)
    {
        if (!PawnCustodyRegistry.IsOwnGame || RimlinkSession.State.Outposts.Scope is not { } scope || !scope.Equals(state.Scope)) return;
        if (game == Current.Game && applied?.Equals(state) == true) return;
        if (Find.WorldObjects is null) return;
        foreach (var old in Find.WorldObjects.AllWorldObjects.OfType<OutpostWorldObject>().ToArray()) Find.WorldObjects.Remove(old);
        foreach (var row in state.Outposts.Where(o => o.Lifecycle is "Active" or "SafetyHold" or "Archived"))
        {
            if (row.SurfaceId != "planet" || row.Tile < 0 || row.Tile >= Find.WorldGrid.TilesCount) continue;
            var marker = (OutpostWorldObject)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("Rimlink_Outpost"));
            marker.OwnerPlayerId = scope.OwnerPlayerId; marker.Summary = row.Clone(); marker.Tile = row.Tile;
            Find.WorldObjects.Add(marker);
        }
        game = Current.Game; applied = state.Clone();
    }
    public static void Tick()
    {
        if (!RimlinkSession.Active || RimlinkSession.Login?.OutpostVersion != 1 || !PawnCustodyRegistry.IsOwnGame)
        { if (applied is not null) Reset(); return; }
        if (RimlinkSession.State.Outposts.Snapshot is { } value) Apply(value);
        if (DateTime.UtcNow >= nextRead && !Busy) Refresh();
    }
    public static void Refresh()
    {
        if (Busy || RimlinkSession.State.Outposts.Scope is not { } scope || RimlinkSession.Connection is not { } connection
            || connection.Login is not { OutpostVersion: 1 } login || !PawnCustodyRegistry.IsOwnGame) return;
        Busy = true; nextRead = DateTime.UtcNow.AddSeconds(30); var currentGame = Current.Game; long run = lifetime;
        var client = new OutpostClient(connection); client.BindScope(scope); var token = RimlinkSession.RecoveryToken;
        _ = Task.Run(async () =>
        {
            try
            {
                var value = await client.State(new() { Scope = scope }, token);
                await MainThread.Get(() => { if (IsCurrent()) { RimlinkSession.State.Outposts.Load(value); Apply(value); LastFailure = null; } return true; });
            }
            catch (Exception error) { await MainThread.Get(() => { if (IsCurrent()) LastFailure = error.Message; return true; }); }
            finally { await MainThread.Get(() => { if (run == lifetime) Busy = false; return true; }); }
        });
        bool IsCurrent() => run == lifetime && currentGame == Current.Game && RimlinkSession.Connection == connection && ReferenceEquals(connection.Login, login) && PawnCustodyRegistry.IsOwnGame;
    }
    public static void Mutate(OutpostSummary selected, OutpostMutationKind kind, IReadOnlyList<string>? workerIds = null,
        OutpostProfile? profile = null, bool confirmFractionDiscard = false)
    {
        if (Busy || OutpostOutgoing.Busy || !PawnCustodyRegistry.IsOwnGame || SaveSync.WritesBlocked || RimlinkSession.Outbox is not { } outbox
            || RimlinkSession.Connection is not { } connection || connection.Login is not { OutpostVersion: 1 } login
            || RimlinkSession.State.Outposts.Scope is not { } scope || Current.Game.GetComponent<RimlinkGameComponent>().OutpostRecovery.Metadata.Scope?.Equals(scope) != true) return;
        var row = selected.Clone(); var currentGame = Current.Game; long run = lifetime; Busy = true; LastFailure = null;
        var request = new OutpostMutationRequest { Scope = scope, ClientRequestId = Guid.NewGuid().ToString("N"), Kind = kind,
            OutpostId = row.OutpostId, ExpectedRevision = row.Revision, Profile = profile ?? row.Profile, SurfaceId = row.SurfaceId, Tile = row.Tile,
            ManifestId = row.ManifestId, ManifestDigest = row.ManifestDigest, ConfirmFractionDiscard = confirmFractionDiscard };
        if (kind == OutpostMutationKind.Recall)
            foreach (var id in workerIds ?? []) { var worker = row.Workers.Single(w => w.PawnCustodyId == id); request.PawnCustodyIds.Add(id); request.AssignmentIds.Add(worker.AssignmentId); }
        var client = new OutpostClient(connection); client.BindScope(scope); var token = RimlinkSession.RecoveryToken; OutboxEntry? entry = null;
        Find.TickManager.Pause();
        _ = Task.Run(async () =>
        {
            try
            {
                var saved = await SaveSync.SaveExactAsync(snapshot =>
                {
                    Check(); request.CanonicalSnapshot = OutpostOutgoing.Proof(snapshot); request.IntentDigest = ByteString.Empty;
                    request.IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())); entry = outbox.Prepare(request);
                });
                if (!saved.Uploaded) throw saved.Error ?? new IOException("mutation snapshot was not accepted");
                Check(); outbox.MarkReplyUnknown(entry!.Id); var reply = await client.Mutate(request, token);
                await MainThread.Get(() =>
                {
                    Check(); if (!reply.Scope.Equals(scope) || reply.ClientRequestId != request.ClientRequestId || !reply.IntentDigest.Equals(request.IntentDigest)
                        || reply.Status != OutpostOperationStatus.Committed) throw new InvalidDataException("mutation has no canonical completion");
                    outbox.Remove(entry.Id); RimlinkSession.RecoveryNeeded = true;
                    Messages.Message("Rimlink.Outposts.CommandComplete".Translate(), RimWorld.MessageTypeDefOf.PositiveEvent, false); return true;
                });
            }
            catch (Exception error)
            {
                await MainThread.Get(() => { if (IsCurrent()) { LastFailure = error.Message; RimlinkSession.RecoveryNeeded = true;
                    Messages.Message("Rimlink.Outposts.CommandHold".Translate(), RimWorld.MessageTypeDefOf.RejectInput, false); } return true; });
            }
            finally { await MainThread.Get(() => { if (run == lifetime) { Busy = false; nextRead = default; } return true; }); }
        });
        bool IsCurrent() => run == lifetime && Current.Game == currentGame && RimlinkSession.Connection == connection && ReferenceEquals(connection.Login, login) && PawnCustodyRegistry.IsOwnGame;
        void Check() { if (!IsCurrent()) throw new OperationCanceledException("outpost command game/session changed"); }
    }
}
