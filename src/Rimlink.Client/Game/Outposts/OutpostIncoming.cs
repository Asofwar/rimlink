using Google.Protobuf;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Outposts;

/// <summary>Fetch is not authorization. One durable continuous-branch fence precedes every protected creation.</summary>
public static class OutpostIncoming
{
    private static readonly HashSet<long> running = [], held = [];
    private static readonly Dictionary<long, OutpostManifestDocument> manifests = [];
    private sealed record Permit(Parcel Parcel, OutpostDeliveryRequest Request, OutpostDeliveryReply Reply, Verse.Game Game, Map Home);
    private static Permit? permit;
    private static long lifetime;
    public static string? LastFailure { get; private set; }
    public static bool Protected(Parcel p) => p.OutpostPayload is not null || p.Kind == ParcelKind.ParcelOutpost || p.Pawns?.Pawns.Any(b => b.Registry is not null) == true;
    public static void Reset() { lifetime++; running.Clear(); held.Clear(); manifests.Clear(); permit = null; LastFailure = null; }

    public static DeliveryDecision Validate(Parcel parcel)
    {
        DeliveryDecision No() => new(false, "Rimlink.Outposts.DeliveryIncompatible", []);
        var metadata = Current.Game?.GetComponent<RimlinkGameComponent>().OutpostRecovery.Metadata;
        if (!SafeHome(Find.CurrentMap) || metadata?.SchemaVersion != 1 || parcel.CalculateSize() > Limits.MaxMessageBytes
            || parcel.AwaitingDecision || OutpostPayloadCompatibility.Refusal(parcel, RimlinkSession.Login?.OutpostVersion ?? 0) is not null
            || parcel.OutpostPayload is not { ChildCount: > 0 } payload || !OutpostDeliveryEvidence.MatchesScope(parcel, metadata.Scope)
            || !OutpostParcelContents.Match(payload, parcel.Items, parcel.Pawns, parcel.Silver)
            || Incoming.MissingDefs(parcel.Items, parcel.Pawns) is not null) return No();
        var original = payload.Clone(); original.PayloadDigest = ByteString.Empty;
        if (!System.Security.Cryptography.SHA256.HashData(original.ToByteArray()).AsSpan().SequenceEqual(payload.PayloadDigest.Span)) return No();
        if (payload.ManifestId.Length == 0) return payload.Resources.Count == 0 ? new(true, null, []) : No();
        if (!manifests.TryGetValue(parcel.Id, out var manifest)) return No();
        try { OutpostManifestCapture.RequireLocalMatch(manifest); }
        catch (InvalidDataException) { return No(); }
        var catalog = new ResourceCatalogSnapshot(payload.ManifestId, payload.ManifestDigest.ToByteArray(), manifest.GameDefsSha256.ToByteArray(),
            new[] { "WoodLog", "Steel" }.Select(name => DefDatabase<ThingDef>.GetNamedSilentFail(name)).Where(d => d is not null)
                .Select(d => new ResourceDefinitionSnapshot(d.defName, d.stackLimit, !d.MadeFromStuff,
                    d.comps?.Any(c => typeof(CompQuality).IsAssignableFrom(c.compClass)) == true)).ToArray(),
            manifest.Resources.Select(v => new ResourceValuationSnapshot(v.DefName, v.UnitValueX100)).ToArray());
        var stacks = new List<ResourceStack>();
        foreach (var resource in payload.Resources)
        {
            var checkedResource = ResourceDeliveryChecks.Validate(resource, catalog);
            if (!checkedResource.Allowed) return No();
            stacks.AddRange(checkedResource.Stacks);
        }
        return new(true, null, stacks);
    }

    public static IReadOnlyList<Thing> Materialize(Parcel parcel, Map home)
    {
        var allowed = permit;
        if (allowed is null || Current.Game != allowed.Game || home != allowed.Home || !parcel.Equals(allowed.Parcel)
            || OutpostDeliveryEvidence.Decide(parcel, allowed.Request, allowed.Reply,
                Current.Game.GetComponent<RimlinkGameComponent>().OutpostRecovery.Metadata,
                PawnCustodyRegistry.CurrentRegistry!.GameBranchId, PawnCustodyRegistry.CurrentRegistry.GameBranchId,
                false, SafeHome(home), true) != MaterializationDecision.Materialize)
            throw new InvalidDataException("protected materialization has no continuous authorized caller");
        var decision = Validate(parcel);
        if (!decision.Allowed) throw new InvalidDataException("protected batch is incompatible");
        var things = new List<Thing>(); var workers = new List<(Pawn Pawn, PawnRegistryEnvelope Envelope)>();
        foreach (var stack in decision.Stacks)
        {
            var thing = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(stack.DefName)); thing.stackCount = stack.Count; things.Add(thing);
        }
        foreach (var blob in parcel.Items.Items) things.Add(ThingBlobs.DeserializeItem(blob, newIds: true));
        foreach (var blob in parcel.Pawns.Pawns)
        {
            var pawn = ThingBlobs.DeserializePawn(blob, newIds: true); things.Add(pawn);
            if (blob.Registry is { } source)
            {
                var envelope = source.Clone(); envelope.Scope = allowed.Request.Scope.Clone(); envelope.NonAuthoritativeMirror = false; envelope.Tombstone = false;
                workers.Add((pawn, envelope));
            }
        }
        // Restore the returning group before binding its final, possibly changed, actual load ids.
        RelationLedger.Rejoin(things.OfType<Pawn>().ToArray());
        DropPodUtility.DropThingsNear(Incoming.DropCenter(home), home, things, openDelay: 0,
            canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: true, forbid: false);
        foreach (var worker in workers) PawnCustodyRegistry.CurrentRegistry!.Bind(worker.Pawn, worker.Envelope);
        var registry = PawnCustodyRegistry.CurrentRegistry!.ValidateOwnGame();
        if (registry.RecoveryRequired) throw new InvalidDataException("delivered custody registry is ambiguous");
        return things;
    }

    public static void Receive(Parcel value, Map home)
    {
        if (running.Contains(value.Id) || held.Contains(value.Id) || !SafeHome(home) || RimlinkSession.Outbox is not { } outbox
            || RimlinkSession.Connection is not { } connection || connection.Login is not { OutpostVersion: 1 } login) return;
        var game = Current.Game; var parcel = value.Clone(); var component = game.GetComponent<RimlinkGameComponent>();
        var scope = new OutpostScopeMessage { WorldInstanceId = RimlinkSession.World?.WorldInstanceId ?? "", OwnerPlayerId = login.Player.PlayerId,
            SaveGeneration = login.SaveGeneration, SaveWriteEpoch = login.SaveWriteEpoch, RestoreBoundary = login.OutpostRestoreBoundary };
        try
        {
            component.OutpostRecovery = new() { Metadata = OutpostDeliveryEvidence.PrepareRecipient(component.OutpostRecovery.Metadata, scope,
                component.SaveGeneration, component.SaveWriteEpoch, component.OutpostLoadFailure is not null, PawnCustodyRegistry.CurrentRegistry!.HasRecords) };
        }
        catch (InvalidDataException error)
        {
            LastFailure = error.Message; held.Add(parcel.Id); RimlinkSession.RecoveryNeeded = true;
            Messages.Message("Rimlink.Outposts.DeliveryHold".Translate(), MessageTypeDefOf.RejectInput, false); return;
        }
        long generation = lifetime;
        string branch = PawnCustodyRegistry.CurrentRegistry!.GameBranchId; running.Add(parcel.Id); bool spawnStarted = false;
        var client = new OutpostClient(connection); client.BindScope(scope); var token = RimlinkSession.RecoveryToken;
        _ = Task.Run(async () =>
        {
            try
            {
                OutpostManifestDocument? document = null;
                if (parcel.OutpostPayload is { ManifestId.Length: > 0 } payload)
                    document = await new OutpostManifestClient(connection).Read(new OutpostStateReply { Scope = scope.Clone(),
                        ManifestId = payload.ManifestId, ManifestDigest = payload.ManifestDigest }, token, parcel.Id);
                var entry = await MainThread.Get(() =>
                {
                    Check(); if (document is not null) manifests[parcel.Id] = document;
                    if (!Validate(parcel).Allowed) throw new InvalidDataException("protected parcel is incompatible with this home game");
                    Find.TickManager.Pause();
                    return outbox.Entries.SingleOrDefault(e => e.Kind == OutboxKind.OutpostBeginDelivery
                        && ((OutpostDeliveryRequest)TransferOutbox.Decode(e)).ParcelId == parcel.Id);
                });
                OutpostDeliveryRequest request;
                if (entry is null)
                {
                    request = new() { Scope = scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"), ParcelId = parcel.Id,
                        PayloadDigest = parcel.OutpostPayload.PayloadDigest, RestoreAuthorizationId = parcel.OutpostRestoreAuthorization?.AuthorizationId ?? "" };
                    RequireUploaded(await SaveSync.SaveExactAsync(snapshot =>
                    {
                        Check(); request.CanonicalSnapshot = OutpostOutgoing.Proof(snapshot); entry = outbox.Prepare(request);
                        outbox.ArmOutpostDelivery(entry.Id, branch);
                    }));
                }
                else request = (OutpostDeliveryRequest)TransferOutbox.Decode(entry);
                if (entry?.Outpost is not { } journal) throw new InvalidDataException("protected delivery journal is missing");
                // A pre-upload outage leaves the same immutable canonical snapshot queued. Wait for its
                // acceptance rather than inventing another request or asking Begin about unaccepted bytes.
                if (!await MainThread.Get(() => { Check(); return TransferOutbox.ReadyFor(entry, login); })) return;
                await MainThread.Get(() => { Check(); outbox.MarkReplyUnknown(entry.Id); return true; });
                var reply = await client.BeginDelivery(request, token);
                await MainThread.Get(() => { Check(); outbox.RecordOutpostReply(entry.Id, reply); return true; });
                var disposition = await MainThread.Get(() =>
                {
                    Check(); var metadata = component.OutpostRecovery.Metadata;
                    var choice = OutpostDeliveryEvidence.Decide(parcel, request, reply, metadata,
                        journal.DeliveryBranchId ?? "", branch, journal.SpawnStarted, SafeHome(home), true);
                    if (choice == MaterializationDecision.RecoveryRequired) throw new InvalidDataException("delivery requires manual recovery; no object was created");
                    if (choice == MaterializationDecision.Materialize)
                    {
                        outbox.MarkOutpostSpawnStarted(entry.Id, branch); spawnStarted = true;
                        permit = new(parcel, request, reply, game, home);
                        try { Materialize(parcel, home); } finally { permit = null; }
                        OutpostOutgoing.AddReceipt(component, OutpostDeliveryEvidence.Receipt(parcel, request, reply));
                        RimlinkSession.RecoveryNeeded = true;
                    }
                    return choice;
                });
                // Even an ACK-only local receipt must first have its complete exact XML accepted by the server.
                RequireUploaded(await SaveSync.SaveExactAsync());
                var ack = await client.BeginDelivery(request, token);
                if (!ack.AckOnly) throw new InvalidDataException("the exact materialization receipt was not accepted");
                await MainThread.Get(() => { Check(); outbox.Remove(entry.Id); RimlinkSession.State.Economy.MarkDelivered(parcel.Id);
                    Messages.Message("Rimlink.Outposts.DeliveryComplete".Translate(), MessageTypeDefOf.PositiveEvent, false); return true; });
            }
            catch (Exception error)
            {
                await MainThread.Get(() =>
                {
                    if (generation != lifetime || Current.Game != game || RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, login)) return false;
                    LastFailure = error.Message; RimlinkSession.RecoveryNeeded = true;
                    if (OutpostDeliveryEvidence.MayRetryBeforeSpawn(error, spawnStarted))
                    {
                        RimlinkLog.Instance.Warn("protected delivery interrupted before spawn; retaining original intent for retry: " + error.Message);
                        return true;
                    }
                    held.Add(parcel.Id);
                    RimlinkLog.Instance.Warn("protected parcel held: " + error.Message);
                    Messages.Message("Rimlink.Outposts.DeliveryHold".Translate(), MessageTypeDefOf.RejectInput, false);
                    if (spawnStarted) { SaveSync.BlockWrites(); RimlinkGameComponent.AbortToMenu(); }
                    return true;
                });
            }
            finally { await MainThread.Get(() => { if (generation == lifetime) { running.Remove(parcel.Id); manifests.Remove(parcel.Id); } return true; }); }
        });
        void Check()
        {
            if (generation != lifetime || Current.Game != game || RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, login) || !SafeHome(home))
                throw new OperationCanceledException("delivery game/session changed");
        }
    }
    private static bool SafeHome(Map? home) => RimlinkSession.Active && Current.ProgramState == ProgramState.Playing && home is { IsPlayerHome: true }
        && Find.CurrentMap == home && Find.Maps.Contains(home) && PawnCustodyRegistry.IsOwnGame && !Live.MirrorMode.Active && !SaveSync.WritesBlocked;
    private static void RequireUploaded(SaveSync.UploadResult result)
    { if (!result.Uploaded || result.Snapshot is null) throw result.Error ?? new IOException("protected delivery snapshot was not accepted"); }
}
