using System.Security.Cryptography;
using Google.Protobuf;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game.Outposts;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

public static class GuildIncoming
{
    private static readonly HashSet<long> held = [];
    private static long lifetime;
    public static bool Busy { get; private set; }
    public static string? LastFailure { get; private set; }
    public static bool Protected(Parcel parcel) => parcel.GuildResourcePayload is not null || parcel.Kind == ParcelKind.ParcelGuildResources;
    public static void Reset() { lifetime++; Busy = false; held.Clear(); LastFailure = null; }

    public static void Receive(Parcel value, Map home)
    {
        if (Busy || OutpostIncoming.Busy || OutpostOutgoing.Busy || OutpostSync.Mutating || held.Contains(value.Id) || !SafeHome(home)
            || RimlinkSession.Outbox is not { } outbox || RimlinkSession.Connection is not { } connection
            || connection.Login is not { GuildVersion: 1 } login) return;
        var game = Current.Game; var component = game.GetComponent<RimlinkGameComponent>(); var parcel = value.Clone();
        var scope = new GuildColonyScope { WorldInstanceId = RimlinkSession.World?.WorldInstanceId ?? "", PlayerId = login.Player.PlayerId,
            SaveGeneration = login.SaveGeneration, SaveWriteEpoch = login.SaveWriteEpoch, RestoreBoundary = login.OutpostRestoreBoundary };
        try
        {
            if (!GuildResourceContracts.DeliveryScope(parcel).Equals(scope)) throw new InvalidDataException("guild parcel belongs to another colony");
            component.OutpostRecovery = new() { Metadata = GuildDeliveryEvidence.PrepareRecipient(component.OutpostRecovery.Metadata, scope,
                component.SaveGeneration, component.SaveWriteEpoch, component.OutpostLoadFailure is not null, PawnCustodyRegistry.CurrentRegistry!.HasRecords) };
        }
        catch (InvalidDataException error) { Hold(error); return; }
        string branch = component.GuildDeliveryBranchId; long captured = lifetime; Busy = true; Find.TickManager.Pause();
        var client = new GuildCaptureClient(connection); var token = RimlinkSession.RecoveryToken; bool created = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var document = await client.Manifest(parcel, token);
                var entry = await MainThread.Get(() =>
                {
                    Check(); _ = Stacks(parcel, document);
                    return outbox.Entries.SingleOrDefault(e => e.Kind == OutboxKind.GuildBeginDelivery
                        && ((GuildDeliveryRequest)TransferOutbox.Decode(e)).ParcelId == parcel.Id);
                });
                GuildDeliveryRequest request;
                if (entry is null)
                {
                    request = new() { Scope = scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("D"), ParcelId = parcel.Id,
                        PayloadDigest = ByteString.CopyFrom(SHA256.HashData(parcel.GuildResourcePayload.ToByteArray())) };
                    Uploaded(await SaveSync.SaveExactAsync(snapshot =>
                    {
                        Check(); request.BeforeSnapshot = new() { Scope = scope.Clone(), SnapshotId = snapshot.Id,
                            RawSha256 = ByteString.CopyFrom(snapshot.Meta.RawSha256 ?? throw new InvalidDataException("guild before snapshot has no raw hash")) };
                        entry = outbox.PrepareGuildDelivery(parcel, request, branch);
                    }));
                }
                else request = (GuildDeliveryRequest)TransferOutbox.Decode(entry);
                if (entry?.Guild is not { } journal) throw new InvalidDataException("guild delivery has no durable journal");
                if (!await MainThread.Get(() => { Check(); return TransferOutbox.ReadyFor(entry, login); })) return;
                await MainThread.Get(() => { Check(); outbox.MarkGuildReplyUnknown(entry.Id); return true; });
                var reply = await client.BeginDelivery(request, token);
                await MainThread.Get(() =>
                {
                    Check(); outbox.RecordGuildReply(entry.Id, reply);
                    var decision = GuildDeliveryEvidence.Decide(parcel, request, reply, component.OutpostRecovery.Metadata,
                        journal.BranchId, branch, journal.SpawnStarted, SafeHome(home), true);
                    if (decision == MaterializationDecision.RecoveryRequired) throw new InvalidDataException("guild delivery requires recovery without repeat creation");
                    if (decision == MaterializationDecision.Materialize)
                    {
                        var stacks = Stacks(parcel, document); var receipt = GuildDeliveryEvidence.Receipt(parcel, request, reply);
                        var next = component.OutpostRecovery.Metadata.Clone(); next.GuildMetadata.Receipts.Add(receipt);
                        new OutpostRecoveryData { Metadata = next }.Validate(); // Refuse receipt limits before touching the map.
                        outbox.MarkGuildSpawnStarted(entry.Id, branch); created = true;
                        var things = new List<Thing>();
                        foreach (var stack in stacks)
                        {
                            var thing = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(stack.DefName));
                            thing.stackCount = stack.Count; things.Add(thing);
                        }
                        DropPodUtility.DropThingsNear(Incoming.DropCenter(home), home, things, openDelay: 0,
                            canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: true, forbid: false);
                        component.OutpostRecovery = new() { Metadata = next };
                        RimlinkSession.RecoveryNeeded = true;
                    }
                    return true;
                });
                // A local receipt suppresses creation, but only accepted exact XML can complete the server's attempt.
                Uploaded(await SaveSync.SaveExactAsync());
                var ack = await client.BeginDelivery(request, token);
                if (!ack.AckOnly) throw new InvalidDataException("guild physical receipt has not been accepted");
                await MainThread.Get(() =>
                {
                    Check(); outbox.RecordGuildReply(entry.Id, ack); outbox.Remove(entry.Id);
                    RimlinkSession.State.Economy.MarkDelivered(parcel.Id);
                    Messages.Message("Rimlink.Guild.DeliveryComplete".Translate(), MessageTypeDefOf.PositiveEvent, false); return true;
                });
            }
            catch (Exception error)
            {
                await MainThread.Get(() =>
                {
                    if (captured != lifetime || Current.Game != game || RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, login)) return false;
                    LastFailure = error.Message; RimlinkSession.RecoveryNeeded = true;
                    if (OutpostDeliveryEvidence.MayRetryBeforeSpawn(error, created)) return true;
                    Hold(error);
                    if (created) { SaveSync.BlockWrites(); RimlinkGameComponent.AbortToMenu(); }
                    return true;
                });
            }
            finally { await MainThread.Get(() => { if (captured == lifetime) Busy = false; return true; }); }
        });
        void Check()
        {
            if (captured != lifetime || Current.Game != game || RimlinkSession.Connection != connection
                || !ReferenceEquals(connection.Login, login) || !SafeHome(home)) throw new OperationCanceledException("guild delivery game/session changed");
        }
        void Hold(Exception error)
        {
            LastFailure = error.Message; held.Add(parcel.Id); RimlinkSession.RecoveryNeeded = true;
            RimlinkLog.Instance.Warn("guild parcel held: " + error.Message);
            Messages.Message("Rimlink.Guild.DeliveryHold".Translate(), MessageTypeDefOf.RejectInput, false);
        }
    }

    private static IReadOnlyList<ResourceStack> Stacks(Parcel parcel, OutpostManifestDocument document)
    {
        var payload = parcel.GuildResourcePayload; long value = GuildResourceContracts.Validate(payload);
        OutpostManifestCapture.RequireLocalMatch(document);
        if (!SHA256.HashData(document.ToByteArray()).AsSpan().SequenceEqual(payload.ManifestDigest.Span)) throw new InvalidDataException("guild manifest changed");
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(payload.DefName);
        if (def is null) throw new InvalidDataException("guild resource definition missing");
        var catalog = new ResourceCatalogSnapshot(payload.ManifestId, payload.ManifestDigest.ToByteArray(), document.GameDefsSha256.ToByteArray(),
            [new(def.defName, def.stackLimit, !def.MadeFromStuff, def.comps?.Any(c => typeof(CompQuality).IsAssignableFrom(c.compClass)) == true)],
            document.Resources.Select(r => new ResourceValuationSnapshot(r.DefName, r.UnitValueX100)).ToArray());
        var decision = ResourceDeliveryChecks.Validate(new() { SchemaVersion = 1, DefName = payload.DefName, Count = payload.Count, ImportValueX100 = value }, catalog);
        if (!decision.Allowed) throw new InvalidDataException("guild resource definition or valuation incompatible");
        return decision.Stacks;
    }
    private static bool SafeHome(Map? home) => RimlinkSession.Active && Current.ProgramState == ProgramState.Playing && home is { IsPlayerHome: true }
        && Find.CurrentMap == home && Find.Maps.Contains(home) && PawnCustodyRegistry.IsOwnGame && !Live.MirrorMode.Active && !SaveSync.WritesBlocked;
    private static void Uploaded(SaveSync.UploadResult result)
    { if (!result.Uploaded || result.Snapshot is null) throw result.Error ?? new IOException("guild delivery snapshot was not accepted"); }
}
