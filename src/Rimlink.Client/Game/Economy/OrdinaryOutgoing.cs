using Google.Protobuf;
using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;
using Registry = Rimlink.Client.Game.Outposts.PawnCustodyRegistry;
using Exact = Rimlink.Client.Game.Outposts.OutpostOutgoing;

namespace Rimlink.Client.Game.Economy;

public static partial class Outgoing
{
    private static readonly HashSet<string> ordinaryReturns = [];

    // Untagged ordinary requests keep their existing path. This branch never uses worker eligibility.
    private static void RunOrdinary(List<Taken> taken, IMessage request, string successMessage, Func<IMessage, Task> call)
    {
        var connection = RimlinkSession.Connection!; var login = connection.Login;
        var game = Current.Game; var component = game.GetComponent<RimlinkGameComponent>();
        var outbox = RimlinkSession.Outbox!; OutboxEntry? entry = null; bool committed = false;
        var previousMetadata = component.OutpostRecovery.Metadata.Clone();
        Task<SaveSync.UploadResult> upload;
        try
        {
            if (login?.OutpostVersion != 1 || !OrdinaryContext(game, component, login) || SaveSync.IsSaving)
                throw new InvalidDataException("ordinary custody is not available in this game");
            entry = PrepareOrdinaryRemoval(request);
            Find.TickManager.Pause();
            committed = true; // Any following failure may have changed a real object: never guess an undo.
            foreach (var item in taken)
            {
                if (item.Original is Pawn pawn) Registry.CurrentRegistry!.RecordDeparture(pawn, new(entry.Id, "ordinary-outgoing"));
                item.Commit(); item.Committed = true;
            }
            component.RemovedOperations.Add(entry.Id);
            // No await or frame boundary between departure, exact capture and durable proof binding.
            upload = SaveSync.SaveExactNow(snapshot => outbox.BindOrdinarySnapshot(entry.Id, snapshot.Meta));
        }
        catch (Exception error)
        {
            if (!committed)
            {
                if (entry is not null) outbox.Remove(entry.Id); // Proven unsent, no departure and no snapshot was attempted.
                component.OutpostRecovery = new() { Metadata = previousMetadata };
                Restore(taken);
                Messages.Message("Rimlink.Outposts.RecoveryHold".Translate(), MessageTypeDefOf.RejectInput, false);
            }
            else { SaveSync.BlockWrites(); RimlinkGameComponent.AbortToMenu(); }
            RimlinkLog.Instance.Warn("ordinary departure retained: " + error.Message);
            return;
        }
        var pending = entry!;
        _ = Submit();
        async Task Submit()
        {
            try
            {
                RequireOrdinaryUpload(await upload);
                await MainThread.Get(() => { Check(); if (!TransferOutbox.ReadyFor(pending, login)) throw new InvalidDataException("exact removal has not been accepted"); return true; });
                await connection.ForAuthentication(login!, () => call(TransferOutbox.Decode(pending)));
                outbox.Remove(pending.Id);
                await MainThread.Get(() =>
                {
                    // A successful live proposal can already have started the guest session.
                    if (CurrentGame(game, connection, login))
                    {
                        RimlinkSession.State.Economy.MarkWealthStale();
                        Messages.Message(successMessage, MessageTypeDefOf.PositiveEvent, false);
                    }
                    return true;
                });
            }
            catch (RpcException error) when (TransferOutbox.IsDefinitive(error))
            {
                outbox.Refuse(pending.Id, RpcErrorDetail.Read(error));
                await MainThread.Get(() => { if (StillOrdinaryCurrent()) StartOrdinaryReturn(pending, taken); return true; });
            }
            catch (Exception error)
            {
                var refusal = RpcErrorDetail.Read(error);
                if (refusal.Code is ErrorCode.RequestIntentMismatch or ErrorCode.SaveWriteEpochMismatch or ErrorCode.RecoveryClientRequired)
                    outbox.HoldForRecovery(pending.Id, refusal);
                RimlinkLog.Instance.Warn("ordinary transfer unanswered; original bytes retained: " + error.Message);
                await MainThread.Get(() => { if (StillOrdinaryCurrent()) {
                    if (pending.Ordinary!.RemovalProof is null) { outbox.HoldForRecovery(pending.Id, refusal); SaveSync.BlockWrites(); RimlinkGameComponent.AbortToMenu(); }
                    else Messages.Message("Rimlink.Economy.Unanswered".Translate(RefusalText.Of(refusal)), MessageTypeDefOf.NegativeEvent, false);
                } return true; });
            }
        }
        bool StillOrdinaryCurrent() => CurrentGame(game, connection, login) && OrdinaryContext(game, component, login!);
        void Check() { if (!StillOrdinaryCurrent()) throw new OperationCanceledException("ordinary departure game/session changed"); }
    }

    private static void StartOrdinaryReturn(OutboxEntry entry, List<Taken>? immediate = null)
    {
        if (entry.Ordinary is not { } journal || entry.Phase == OutboxPhase.RecoveryRequired || ordinaryReturns.Contains(entry.Id)
            || RimlinkSession.Connection is not { } connection || connection.Login is not { OutpostVersion: 1 } login) return;
        var game = Current.Game; var component = game.GetComponent<RimlinkGameComponent>();
        if (!OrdinaryContext(game, component, login) || SaveSync.IsSaving) return;
        var outbox = RimlinkSession.Outbox!; var scope = OrdinaryScope(login); var branch = Registry.CurrentRegistry!.GameBranchId;
        var client = new OutpostClient(connection); client.BindScope(scope); var token = RimlinkSession.RecoveryToken;
        bool spawnStarted = false, ambiguousStarted = false;
        ordinaryReturns.Add(entry.Id);
        _ = Run();
        async Task Run()
        {
            try
            {
                OrdinaryCancelRequest request;
                if (journal.ReturnRequest is { } frozen) request = OrdinaryCancelRequest.Parser.ParseFrom(frozen);
                else
                {
                    // A current accepted XML is separate from the immutable original removal proof.
                    var canonical = RequireOrdinaryUpload(await SaveSync.SaveExactAsync());
                    request = await MainThread.Get(() => { Check(); return outbox.PrepareOrdinaryReturn(entry.Id, Exact.Proof(canonical), branch); });
                }
                var reply = await client.OrdinaryOperation(new() { Operation = request.Clone() }, token);
                if (reply.Status == OrdinaryOperationStatus.Applied)
                {
                    await MainThread.Get(() => { Check(); outbox.Remove(entry.Id); return true; });
                    return;
                }
                if (reply.Status == OrdinaryOperationStatus.Unknown)
                    reply = await client.BeginOrdinaryReturn(request, token); // Installs a permanent fence, not an absence-based spawn.
                await MainThread.Get(() => { Check(); outbox.RecordOrdinaryReturnReply(entry.Id, request, reply); return true; });
                var receiptUpload = await MainThread.Get(() =>
                {
                    Check();
                    var decision = OrdinaryReturnEvidence.Decide(request, reply, component.OutpostRecovery.Metadata,
                        journal.ReturnBranchId ?? "", branch, journal.SpawnStarted, true, true);
                    if (decision == MaterializationDecision.RecoveryRequired)
                    {
                        ambiguousStarted = reply.Status == OrdinaryOperationStatus.ReturnStarted;
                        throw new InvalidDataException("ordinary return has no continuous materialization proof");
                    }
                    if (decision == MaterializationDecision.Materialize)
                    {
                        ValidateOrdinaryPayload(reply.ReturnProof.Payload);
                        outbox.MarkOrdinaryReturnSpawnStarted(entry.Id, branch); spawnStarted = true;
                        if (immediate is not null) Restore(immediate, authorizedOrdinaryReturn: true);
                        else MaterializeOrdinaryHome(reply.ReturnProof.Payload);
                        component.ReturnedOperations.Add(entry.Id);
                        Exact.AddReceipt(component, OrdinaryReturnEvidence.Receipt(request, reply.ReturnProof));
                    }
                    return SaveSync.SaveExactNow();
                });
                RequireOrdinaryUpload(await receiptUpload);
                var accepted = await client.OrdinaryOperation(new() { Operation = request.Clone() }, token);
                if (accepted.Status != OrdinaryOperationStatus.ReturnAccepted) throw new InvalidDataException("ordinary return receipt was not accepted");
                await MainThread.Get(() => { Check(); Messages.Message("Rimlink.Economy.Returned".Translate(RefusalText.Of(entry)), MessageTypeDefOf.NegativeEvent, false);
                    if (TransferOutbox.Decode(entry) is RaidReport report) Raids.RaidResolver.ReportAfterOrdinaryReturn(report);
                    return true; });
                // ReconcileSnapshot removed the entry ONLY after the exact Returned XML was accepted.
            }
            catch (Exception error)
            {
                await MainThread.Get(() =>
                {
                    if (!CurrentGame(game, connection, login)) return false;
                    RimlinkSession.RecoveryNeeded = true;
                    RimlinkLog.Instance.Warn("ordinary return evidence retained: " + error.Message);
                    if (error is not OperationCanceledException && error is not RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded })
                    {
                        outbox.HoldForRecovery(entry.Id, RpcErrorDetail.Read(error));
                        Messages.Message("Rimlink.Outposts.RecoveryHold".Translate(), MessageTypeDefOf.RejectInput, false);
                    }
                    if (spawnStarted || ambiguousStarted) { SaveSync.BlockWrites(); RimlinkGameComponent.AbortToMenu(); }
                    return true;
                });
            }
            finally { await MainThread.Get(() => { ordinaryReturns.Remove(entry.Id); return true; }); }
        }
        void Check()
        {
            if (!CurrentGame(game, connection, login) || !OrdinaryContext(game, component, login))
                throw new OperationCanceledException("ordinary return game/session changed");
        }
    }

    /// <summary>Main-thread producer shared by economy departures and the separate own-captives report route.</summary>
    internal static OutboxEntry PrepareOrdinaryRemoval(IMessage request)
    {
        var game = Current.Game; var component = game.GetComponent<RimlinkGameComponent>(); var login = RimlinkSession.Login;
        if (login?.OutpostVersion != 1 || !OrdinaryContext(game, component, login) || SaveSync.IsSaving)
            throw new InvalidDataException("ordinary removal cannot start now");
        var scope = OrdinaryScope(login); var outbox = RimlinkSession.Outbox!;
        if (!component.OutpostRecovery.Present || !component.OutpostRecovery.Metadata.Scope.Equals(scope))
            throw new InvalidDataException("ordinary custody metadata has another scope");
        var entry = outbox.PrepareOrdinary(request, scope);
        try
        {
            var capture = OrdinaryOutgoingCapture.Parser.ParseFrom(entry.Ordinary!.Capture);
            var future = component.OutpostRecovery.Metadata.Clone(); future.OrdinaryOutgoing.Add(capture.Clone());
            foreach (var item in capture.Pawns)
            {
                var envelope = item.Pawn.Registry; var row = future.Registry.Single(r => r.PawnCustodyId == envelope.PawnCustodyId);
                if (!row.Equals(envelope)) throw new InvalidDataException("ordinary pawn changed before departure");
                row.Tombstone = true;
            }
            new OutpostRecoveryData { Metadata = future }.Validate();
            component.OutpostRecovery.Metadata.OrdinaryOutgoing.Add(capture);
            return entry;
        }
        catch { outbox.Remove(entry.Id); throw; } // Nothing departed or wrote a snapshot yet.
    }

    private static void ValidateOrdinaryPayload(OrdinaryReturnPayload payload)
    {
        if (payload.Items.Items.Any(i => DefDatabase<ThingDef>.GetNamedSilentFail(i.DefName) is null
                || i.StuffDef.Length > 0 && DefDatabase<ThingDef>.GetNamedSilentFail(i.StuffDef) is null)
            || payload.Pawns.Pawns.Any(p => DefDatabase<PawnKindDef>.GetNamedSilentFail(p.KindDef) is null))
            throw new InvalidDataException("ordinary return contains missing defs");
    }
    private static void MaterializeOrdinaryHome(OrdinaryReturnPayload payload)
    {
        var home = Find.AnyPlayerHomeMap ?? throw new InvalidDataException("ordinary return has no own home");
        var things = payload.Items.Items.Select(i => ThingBlobs.DeserializeItem(i)).ToList();
        var pawns = payload.Pawns.Pawns.Select(p => (Pawn: ThingBlobs.DeserializePawn(p), Blob: p)).ToArray();
        things.AddRange(pawns.Select(p => p.Pawn)); RelationLedger.RejoinAll(things);
        DropPodUtility.DropThingsNear(Incoming.DropCenter(home), home, things, openDelay: 110,
            canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: true, forbid: false);
        foreach (var pair in pawns.Where(p => p.Blob.Registry is { NonAuthoritativeMirror: false })) Registry.CurrentRegistry!.Bind(pair.Pawn, pair.Blob.Registry);
        if (Registry.CurrentRegistry!.ValidateOwnGame().RecoveryRequired) throw new InvalidDataException("ordinary home return has ambiguous custody");
    }
    private static bool CurrentGame(Verse.Game game, RimlinkConnection connection, LoginReply? login) => Current.Game == game
        && RimlinkSession.Connection == connection && ReferenceEquals(connection.Login, login);
    private static bool OrdinaryContext(Verse.Game game, RimlinkGameComponent component, LoginReply login) => Current.Game == game
        && Current.ProgramState == ProgramState.Playing && Registry.IsOwnGame && !Live.MirrorMode.Active && !SaveSync.WritesBlocked
        && component.SaveGeneration == login.SaveGeneration && component.SaveWriteEpoch == login.SaveWriteEpoch;
    private static OutpostScopeMessage OrdinaryScope(LoginReply login) => new() { WorldInstanceId = RimlinkSession.World?.WorldInstanceId ?? "",
        OwnerPlayerId = login.Player.PlayerId, SaveGeneration = login.SaveGeneration, SaveWriteEpoch = login.SaveWriteEpoch, RestoreBoundary = login.OutpostRestoreBoundary };
    private static SaveSnapshot RequireOrdinaryUpload(SaveSync.UploadResult result)
    { if (!result.Uploaded || result.Snapshot is null) throw result.Error ?? new IOException("ordinary exact snapshot was not accepted"); return result.Snapshot; }
}
