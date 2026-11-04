using Google.Protobuf;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;
using PawnExit = Rimlink.Client.Game.Economy.PawnExit;
using Worker = Rimlink.Protocol.V1.OutpostWorkerCapture;

namespace Rimlink.Client.Game.Outposts;

/// <summary>Exact accepted pre/post snapshots, never a tick guess or a local refund of an unanswered custody operation.</summary>
public static class OutpostOutgoing
{
    private static readonly CaravanAdmissionGate Runs = new();
    public static string StatusKey { get; private set; } = "";
    public static string? LastFailure { get; private set; }
    public static OutpostOperationReply? LastOperation { get; private set; }
    public static bool Busy { get; private set; }
    public static IReadOnlyList<string> PendingAdmissionIds => RimlinkSession.Outbox?.Entries
        .Where(e => e.Kind is OutboxKind.OutpostPrepare or OutboxKind.OutpostCommit or OutboxKind.OutpostCancel)
        .Select(e => TransferOutbox.Decode(e) switch { OutpostPrepareRequest r => r.ClientRequestId, OutpostProofRequest r => r.ClientRequestId, OutpostCancelRequest r => r.ClientRequestId, _ => "" })
        .Where(id => id.Length > 0).Distinct().ToArray() ?? [];
    public static void Begin(Caravan caravan, IReadOnlyList<Pawn> workers, OutpostProfile profile, string? outpostId)
    {
        if (Busy || !Runs.TryBegin(caravan.GetUniqueLoadID(), out string id)) return;
        Busy = true; LastFailure = null; LastOperation = null; StatusKey = "Rimlink.Outposts.Preflight";
        Find.TickManager.Pause();
        var game = Current.Game; var connection = RimlinkSession.Connection; var login = connection?.Login;
        if (connection is null || login?.OutpostVersion != 1 || !PawnCustodyRegistry.IsOwnGame || RimlinkSession.Outbox?.HasUnresolved != false)
        { Finish(caravan.GetUniqueLoadID(), new InvalidDataException("current outpost custody support and a resolved journal are required")); return; }
        var selected = workers.ToArray(); var scope = new OutpostScopeMessage { WorldInstanceId = RimlinkSession.World?.WorldInstanceId ?? "",
            OwnerPlayerId = login.Player.PlayerId, SaveGeneration = login.SaveGeneration, SaveWriteEpoch = login.SaveWriteEpoch, RestoreBoundary = login.OutpostRestoreBoundary };
        var client = new OutpostClient(connection); client.BindScope(scope);
        _ = Run();
        async Task Run()
        {
            bool removed = false;
            var outbox = RimlinkSession.Outbox!; OutboxEntry? preparedEntry = null, commitEntry = null;
            try
            {
                var state = await client.State(new() { Scope = scope.Clone() }, RimlinkSession.RecoveryToken);
                var manifest = await new OutpostManifestClient(connection).Read(state, RimlinkSession.RecoveryToken);
                var initial = await MainThread.Get(() =>
                {
                    EnsureCurrent(); OutpostManifestCapture.RequireLocalMatch(manifest);
                    var component = game.GetComponent<RimlinkGameComponent>(); var registry = game.GetComponent<PawnCustodyRegistry>();
                    if (component.OutpostRecovery.Present && !component.OutpostRecovery.Metadata.Scope.Equals(scope))
                        throw new InvalidDataException("loaded outpost metadata belongs to another boundary");
                    if (!component.OutpostRecovery.Present) component.OutpostRecovery = new() { Metadata = new() { SchemaVersion = 1, Scope = scope.Clone() } };
                    foreach (var pawn in selected) registry.Bootstrap(pawn, scope);
                    var facts = Capture(caravan, selected, profile, manifest, id);
                    Demand(CaravanAdmission.Check(facts));
                    var metadata = component.OutpostRecovery.Metadata; metadata.WorkerCaptures.Clear(); metadata.Caravans.Clear();
                    metadata.WorkerCaptures.Add(facts.Workers.Select(w => w.Clone())); metadata.Caravans.Add(facts.Caravan.Clone());
                    StatusKey = "Rimlink.Outposts.PreSnapshot";
                    return facts;
                });
                var target = state.Outposts.SingleOrDefault(o => o.OutpostId == outpostId);
                var request = new OutpostPrepareRequest { Scope = scope.Clone(), ClientRequestId = id, Kind = outpostId is null ? OutpostPrepareKind.Found : OutpostPrepareKind.Assign,
                    OutpostId = outpostId ?? Guid.NewGuid().ToString("D"), ExpectedRevision = target?.Revision ?? 0, Profile = profile,
                    ManifestId = state.ManifestId, ManifestDigest = state.ManifestDigest, SurfaceId = initial.Caravan.SurfaceId, Tile = initial.Caravan.Tile,
                    Caravan = initial.Caravan.Clone() };
                request.Workers.Add(initial.Workers.Select(w => w.Clone()));
                var pre = await SaveSync.SaveExactAsync(snapshot =>
                {
                    EnsureCurrent(); request.PreRemoval = Proof(snapshot); request.IntentDigest = CaravanAdmission.IntentDigest(request);
                    if (request.CalculateSize() > Limits.EconomyMaxParcelBytes) throw new InvalidDataException("selected worker batch is too large before removal");
                    preparedEntry = outbox.Prepare(request);
                });
                RequireUploaded(pre); EnsureCurrentAsyncBoundary();
                await MainThread.Get(() => { EnsureCurrent(); StatusKey = "Rimlink.Outposts.Preparing"; return true; });
                outbox.MarkReplyUnknown(preparedEntry!.Id);
                var prepared = await client.Prepare(request, RimlinkSession.RecoveryToken);
                if (prepared.Status != OutpostOperationStatus.Prepared) throw new InvalidDataException("prepare did not authorize a new removal");
                var decision = await MainThread.Get(() =>
                {
                    EnsureCurrent(); var current = Capture(caravan, selected, profile, manifest, id, initial.Workers);
                    var check = CaravanAdmission.Revalidate(initial, current);
                    if (!check.Allowed) return check;
                    var receipt = OutpostAdmissionReceipts.Create(request, prepared);
                    // From this point an interruption is recovery, NEVER a guessed local return or no-removal cancellation.
                    removed = true; var component = game.GetComponent<RimlinkGameComponent>();
                    foreach (var worker in request.Workers)
                    {
                        var pawn = selected.Single(p => game.GetComponent<PawnCustodyRegistry>().Capture(p)?.PawnCustodyId == worker.PawnCustodyId);
                        PawnExit.Leave(pawn, new(id, "outpost", worker.AssignmentId, ClearReference: true));
                    }
                    if (check.RemoveEmptyCaravan && caravan.PawnsListForReading.Count == 0) caravan.Destroy();
                    AddReceipt(component, receipt); component.RemovedOperations.Add(id); StatusKey = "Rimlink.Outposts.PostSnapshot";
                    return check;
                });
                if (!decision.Allowed) { await AbortNoRemoval(request, prepared); return; }
                OutpostProofRequest? commit = null;
                var post = await SaveSync.SaveExactAsync(snapshot =>
                {
                    EnsureCurrent(); commit = new() { Scope = scope.Clone(), ClientRequestId = id, IntentDigest = request.IntentDigest,
                        PostRemoval = Proof(snapshot), ExpectedRevision = prepared.AuthoritativeRevision };
                    commitEntry = outbox.Prepare(commit); outbox.Remove(preparedEntry.Id);
                });
                RequireUploaded(post); EnsureCurrentAsyncBoundary(); outbox.MarkReplyUnknown(commitEntry!.Id);
                var result = await client.Commit(commit!, RimlinkSession.RecoveryToken);
                if (result.Status != OutpostOperationStatus.Committed) throw new InvalidDataException("committed custody needs recovery");
                outbox.Remove(commitEntry.Id);
                await MainThread.Get(() => { EnsureCurrent(); LastOperation = result.Clone(); StatusKey = "Rimlink.Outposts.Complete"; RimlinkSession.RecoveryNeeded = true; return true; });
            }
            catch (Exception error)
            {
                RimlinkLog.Instance.Warn("outpost staffing retained for recovery (removed=" + removed + "): " + error.Message);
                await MainThread.Get(() => { if (Current.Game == game) Finish(caravan.GetUniqueLoadID(), error); return true; });
                return;
            }
            await MainThread.Get(() => { Finish(caravan.GetUniqueLoadID(), null); return true; });

            async Task AbortNoRemoval(OutpostPrepareRequest request, OutpostOperationReply prepared)
            {
                var cancellation = new OutpostCancelRequest { Scope = scope.Clone(), ClientRequestId = id, IntentDigest = request.IntentDigest,
                    Kind = OutpostCancelKind.AbortNoRemoval, CancellationProof = request.PreRemoval.Clone(), ExpectedRevision = prepared.AuthoritativeRevision,
                    ContinuousBranchId = initialBranch() };
                var fenceEntry = outbox.Prepare(cancellation); outbox.MarkReplyUnknown(fenceEntry.Id);
                var fence = await client.Cancel(cancellation, RimlinkSession.RecoveryToken);
                await MainThread.Get(() => { EnsureCurrent(); AddReceipt(game.GetComponent<RimlinkGameComponent>(), OutpostAdmissionReceipts.Create(request, fence, true)); return true; });
                OutboxEntry? finalEntry = null;
                var proof = await SaveSync.SaveExactAsync(snapshot =>
                {
                    EnsureCurrent(); cancellation.CancellationProof = Proof(snapshot); finalEntry = outbox.Prepare(cancellation);
                    outbox.Remove(fenceEntry.Id); outbox.Remove(preparedEntry!.Id);
                });
                RequireUploaded(proof); EnsureCurrentAsyncBoundary(); outbox.MarkReplyUnknown(finalEntry!.Id);
                var result = await client.Cancel(cancellation, RimlinkSession.RecoveryToken);
                if (result.Status != OutpostOperationStatus.Cancelled) throw new InvalidDataException("no-removal fence needs recovery");
                outbox.Remove(finalEntry.Id);
                await MainThread.Get(() => { EnsureCurrent(); LastOperation = result.Clone(); StatusKey = "Rimlink.Outposts.Cancelled"; Finish(caravan.GetUniqueLoadID(), null); return true; });
                string initialBranch() => request.Caravan.GameBranchId;
            }
            void EnsureCurrentAsyncBoundary()
            { if (RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, login)) throw new InvalidDataException("outpost operation changed authentication"); }
            void EnsureCurrent()
            { EnsureCurrentAsyncBoundary(); if (Current.Game != game || !PawnCustodyRegistry.IsOwnGame) throw new InvalidDataException("outpost operation changed the own game"); }
        }
    }
    /// <summary>Query the original operation before cancellation; cold or partial removals remain in recovery, never a local respawn.</summary>
    public static void Cancel(string clientRequestId)
    {
        if (Busy || !PawnCustodyRegistry.IsOwnGame || RimlinkSession.Outbox is not { } outbox || RimlinkSession.Connection is not { } connection) return;
        var entries = outbox.Entries.Where(e => e.Kind is OutboxKind.OutpostPrepare or OutboxKind.OutpostCommit or OutboxKind.OutpostCancel)
            .Where(e => TransferOutbox.Decode(e) switch { OutpostPrepareRequest r => r.ClientRequestId == clientRequestId, OutpostProofRequest r => r.ClientRequestId == clientRequestId, OutpostCancelRequest r => r.ClientRequestId == clientRequestId, _ => false }).ToArray();
        if (entries.Length == 0) { Finish("cancel", new InvalidDataException("no original admission journal")); return; }
        Busy = true; LastFailure = null; var game = Current.Game; var login = connection.Login;
        Find.TickManager.Pause(); _ = RunCancel();
        async Task RunCancel()
        {
            try
            {
                var source = TransferOutbox.Decode(entries[0]);
                var (scope, intent) = source switch { OutpostPrepareRequest r => (r.Scope.Clone(), r.IntentDigest),
                    OutpostProofRequest r => (r.Scope.Clone(), r.IntentDigest), OutpostCancelRequest r => (r.Scope.Clone(), r.IntentDigest),
                    _ => throw new InvalidDataException("not an admission journal") };
                var client = new OutpostClient(connection); client.BindScope(scope);
                var operation = await client.Operation(new() { Scope = scope, ClientRequestId = clientRequestId, IntentDigest = intent }, RimlinkSession.RecoveryToken);
                await MainThread.Get(() => { Check(); return true; });
                if (operation.Status is OutpostOperationStatus.Committed or OutpostOperationStatus.Cancelled)
                { await MainThread.Get(() => { Check(); foreach (var entry in entries) outbox.Remove(entry.Id); LastOperation = operation.Clone(); return true; }); return; }
                var request = await MainThread.Get(() =>
                {
                    Check(); var metadata = game.GetComponent<RimlinkGameComponent>().OutpostRecovery.Metadata;
                    var original = entries.FirstOrDefault(e => e.Kind == OutboxKind.OutpostPrepare);
                    if (original is not null) return ((OutpostPrepareRequest)TransferOutbox.Decode(original)).Clone();
                    var receipt = metadata.Receipts.Single(r => r.ClientRequestId == clientRequestId && r.IntentDigest.Equals(intent) && r.CancellationKind == OutpostCancelKind.Unspecified);
                    var reconstructed = new OutpostPrepareRequest { Scope = scope.Clone(), ClientRequestId = clientRequestId, IntentDigest = intent,
                        PreRemoval = receipt.Snapshot.Clone(), Caravan = metadata.Caravans.Single(c => c.ClientRequestId == clientRequestId).Clone() };
                    reconstructed.Workers.Add(metadata.WorkerCaptures.Where(w => receipt.PawnCustodyIds.Contains(w.PawnCustodyId)).Select(w => w.Clone())); return reconstructed;
                });
                var kind = await MainThread.Get(() =>
                {
                    Check(); var component = game.GetComponent<RimlinkGameComponent>(); var registry = game.GetComponent<PawnCustodyRegistry>();
                    var validation = registry.ValidateOwnGame(); if (validation.RecoveryRequired) throw new InvalidDataException("ambiguous local custody cannot be cancelled");
                    component.OutpostRecovery.Metadata.Registry.Clear(); component.OutpostRecovery.Metadata.Registry.Add(validation.Registry.Select(e => e.Clone()));
                    return OutpostAdmissionCancellation.Select(request, component.OutpostRecovery.Metadata, registry.GameBranchId, component.RemovedOperations.Contains(clientRequestId));
                });
                var cancel = new OutpostCancelRequest { Scope = scope.Clone(), ClientRequestId = clientRequestId, IntentDigest = intent,
                    Kind = kind, CancellationProof = request.PreRemoval.Clone(), ExpectedRevision = operation.AuthoritativeRevision,
                    ContinuousBranchId = kind == OutpostCancelKind.AbortNoRemoval ? request.Caravan.GameBranchId : "" };
                OutboxEntry? pending = null;
                if (kind == OutpostCancelKind.AbortNoRemoval)
                {
                    pending = outbox.Prepare(cancel); outbox.MarkReplyUnknown(pending.Id);
                    var fence = await client.Cancel(cancel, RimlinkSession.RecoveryToken);
                    await MainThread.Get(() => { Check(); var component = game.GetComponent<RimlinkGameComponent>();
                        if (!component.OutpostRecovery.Metadata.Receipts.Any(r => r.TransitionId == fence.TransitionId)) AddReceipt(component, OutpostAdmissionReceipts.Create(request, fence, true)); return true; });
                }
                var accepted = await SaveSync.SaveExactAsync(snapshot => { Check(); cancel.CancellationProof = Proof(snapshot);
                    var next = outbox.Prepare(cancel); if (pending is not null) outbox.Remove(pending.Id); pending = next; });
                RequireUploaded(accepted); outbox.MarkReplyUnknown(pending!.Id);
                var result = await client.Cancel(cancel, RimlinkSession.RecoveryToken);
                if (result.Status != OutpostOperationStatus.Cancelled) throw new InvalidDataException("cancellation remains in recovery");
                await MainThread.Get(() => { Check(); foreach (var entry in entries) outbox.Remove(entry.Id); outbox.Remove(pending.Id);
                    LastOperation = result.Clone(); StatusKey = "Rimlink.Outposts.Cancelled"; RimlinkSession.RecoveryNeeded = true; return true; });
            }
            catch (Exception error) { await MainThread.Get(() => { if (Current.Game == game) Finish("cancel", error); return true; }); }
            finally { await MainThread.Get(() => { if (Current.Game == game) Busy = false; return true; }); }
        }
        void Check() { if (Current.Game != game || RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, login)) throw new OperationCanceledException(); }
    }
    private static void Finish(string key, Exception? error)
    {
        Runs.Finish(key); Busy = false;
        if (error is null) return;
        LastFailure = error.Message; StatusKey = "Rimlink.Outposts.RecoveryHold";
        Messages.Message(StatusKey.Translate(), MessageTypeDefOf.RejectInput, false);
    }
    private static void Demand(AdmissionDecision decision) { if (!decision.Allowed) throw new InvalidDataException("caravan admission: " + decision.Reason); }
    private static void RequireUploaded(SaveSync.UploadResult result)
    { if (!result.Uploaded || result.Snapshot is null) throw result.Error ?? new IOException("exact outpost snapshot was not accepted"); }
    public static OutpostSnapshotProof Proof(SaveSnapshot snapshot) => new() { Scope = snapshot.Meta.Recovery!.Outposts.Metadata.Scope.Clone(),
        SnapshotId = snapshot.Id, RawSha256 = ByteString.CopyFrom(snapshot.Meta.RawSha256 ?? throw new InvalidDataException("snapshot has no raw digest")) };
    public static void AddReceipt(RimlinkGameComponent component, OutpostTransitionReceipt receipt)
    {
        var metadata = component.OutpostRecovery.Metadata;
        if (metadata.Receipts.Any(r => r.TransitionId == receipt.TransitionId)) throw new InvalidDataException("duplicate local receipt transition");
        metadata.Receipts.Add(receipt); metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = receipt.TransitionId,
            TransitionDigest = receipt.TransitionDigest, PredecessorId = receipt.PredecessorId });
    }
    private static CaravanAdmissionFacts Capture(Caravan caravan, IReadOnlyList<Pawn> selected, OutpostProfile profile, OutpostManifestDocument manifest,
        string requestId, IReadOnlyList<Worker>? original = null)
    {
        var roster = caravan.PawnsListForReading.ToArray(); var remaining = roster.Where(p => !selected.Any(s => ReferenceEquals(s, p))).ToArray();
        var attestation = new OutpostCaravanAttestation { CaravanId = caravan.GetUniqueLoadID(), SurfaceId = caravan.Tile.Layer == Find.WorldGrid.Surface && caravan.Tile.Layer.IsRootSurface ? "planet" : "other",
            Tile = caravan.Tile.tileId, ClientRequestId = requestId, GameBranchId = PawnCustodyRegistry.CurrentRegistry!.GameBranchId,
            RemainingMobile = remaining.Any(p => p.IsColonist && !p.Downed && !p.Dead && !p.InMentalState), SelectedCargoCaptured = true,
            ManifestDigest = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(manifest.ToByteArray())) };
        attestation.RosterReferenceTokens.Add(roster.Select(p => p.GetUniqueLoadID()).Order(StringComparer.Ordinal));
        attestation.RemainingReferenceTokens.Add(remaining.Select(p => p.GetUniqueLoadID()).Order(StringComparer.Ordinal));
        var workers = selected.Select(p =>
        {
            if (!roster.Any(r => ReferenceEquals(r, p))) throw new InvalidDataException("selected worker is no longer in this caravan");
            var captured = OutpostWorkerCapture.Capture(p, profile); if (!captured.Eligible) throw new InvalidDataException(captured.Refusal);
            var w = captured.Worker!; w.AssignmentId = original?.Single(o => o.PawnCustodyId == w.PawnCustodyId).AssignmentId ?? Guid.NewGuid().ToString("D"); return w;
        }).ToArray();
        attestation.SelectedCustodyIds.Add(workers.Select(w => w.PawnCustodyId).Order(StringComparer.Ordinal));
        bool tile = attestation.Tile >= 0 && attestation.Tile < manifest.TileCount && manifest.Tiles[attestation.Tile] is { Land: true, CaravanPassable: true } t
            && (profile != OutpostProfile.Wood || t.NaturalTrees);
        return new(attestation, workers, profile, caravan.IsPlayerControlled && caravan.Faction == Faction.OfPlayer,
            caravan.Spawned && !caravan.pather.Moving, TileAvailable: tile);
    }
}
