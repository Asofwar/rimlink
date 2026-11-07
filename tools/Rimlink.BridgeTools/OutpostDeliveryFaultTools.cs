using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using HarmonyLib;
using RimBridgeServer.Sdk;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Outposts;
using Rimlink.Protocol.V1;
using Verse;
using RimWorld;

namespace Rimlink.BridgeTools;

public sealed partial class OutpostAcceptanceTools
{
    [Tool("rimlink/outpost_acceptance_fault", Description = "Dev-only one-shot partial-placement or declared-definition availability failure for one real owned protected parcel; no state setters")]
    public async Task<object> Fault(IRimBridgeContext ctx,
        [ToolParameter(Description = "arm, observe or disarm", Required = true)] string action,
        [ToolParameter(Description = "delivery-placement-fail-after-prefix or delivery-definition-unavailable")] string? fault = null,
        [ToolParameter(Description = "Actual pending protected parcel id")] long parcelId = 0,
        [ToolParameter(Description = "One actually declared thing:<defName> or kind:<defName>")] string? definition = null,
        [ToolParameter(Description = "Positive number of actually placed objects before the partial failure")] int prefixObjects = 1)
    {
        try
        {
            var controller = current ?? throw new InvalidDataException("acceptance game is not bound");
            if (action == "observe") return await Bridge.OnMain<object>(ctx, controller.ObserveDeliveryFault);
            await controller.VerifyAsync();
            if (action == "arm") return await controller.ArmDeliveryFaultAsync(ctx, fault, parcelId, definition, prefixObjects);
            if (action != "disarm") throw new InvalidDataException("acceptance delivery fault action invalid");
            return await Bridge.OnMain<object>(ctx, () => { controller.ClearDeliveryFault(); return controller.ObserveDeliveryFault(); });
        }
        catch (Exception error) { current?.Invalidate(); return Bridge.Fail(error.Message); }
    }
}

internal sealed partial class AcceptanceGameController
{
    private const string PlacementFault = "delivery-placement-fail-after-prefix";
    private const string DefinitionFault = "delivery-definition-unavailable";
    private sealed class DeliveryFault(string id, string kind, long parcel, string payload, string? definition, int prefix, string file,
        string? gameAssemblySha256)
    {
        internal readonly string Id = id, Kind = kind, Payload = payload, File = file;
        internal readonly long Parcel = parcel;
        internal readonly string? Definition = definition;
        internal readonly string? GameAssemblySha256 = gameAssemblySha256;
        internal readonly int Prefix = prefix;
        internal string State = "Armed";
        internal string? EvidenceSha256, OutcomeSha256;
    }
    private DeliveryFault? deliveryFault;
    private OutpostAcceptanceContext? verifiedDelivery;
    private long verifiedAt;

    internal object ObserveDeliveryFault() => new { success = true, binding.StandId, binding.RunId, processId = Environment.ProcessId,
        fault = deliveryFault is not { } f ? null : new { f.Id, f.Kind, parcelId = f.Parcel, payloadSha256 = f.Payload, f.Definition,
            prefixObjects = f.Prefix, f.GameAssemblySha256, f.State, evidenceFile = f.File, f.EvidenceSha256,
            outcomeFile = f.File + ".outcome.json", f.OutcomeSha256 } };

    internal async Task<object> ArmDeliveryFaultAsync(IRimBridgeContext ctx, string? kind, long parcelId, string? definition, int prefix)
    {
        if (kind is not (PlacementFault or DefinitionFault) || parcelId <= 0 || prefix is < 1 or > 10000
            || kind == PlacementFault && definition is not null || kind == DefinitionFault && (definition is not { Length: > 0 and <= 256 }
                || definition.Any(char.IsControl) || prefix != 1)) throw new InvalidDataException("acceptance delivery fault selection invalid");
        var parcel = await new Rimlink.Client.Economy.EconomyClient(owner.Connection).Get(parcelId, lifetime.Token);
        await VerifyAsync();
        string? gameAssemblySha256 = null;
        if (kind == PlacementFault)
        {
            string assembly = typeof(DropPodUtility).Assembly.Location;
            if (!Path.GetFullPath(assembly).Equals(Path.Combine(Path.GetDirectoryName(gamePath)!, "Assembly-CSharp.dll"), PathComparison))
                throw new InvalidDataException("acceptance placement hook requires the owned game assembly");
            gameAssemblySha256 = Hash(await File.ReadAllBytesAsync(assembly, lifetime.Token));
            // Both inspected builds use the same one-object drop-pod placement path.
            if (gameAssemblySha256 is not ("fe9b615cf5918955ab750f9d627fb0082fd23c556bdfdee410f88857c67a081b" // 1.7.4998
                or "d55aa8e47fa3c5463ca85b0eccd51f195b4bbcd04f56da3af21c2f6688a81254")) // 1.7.5000
                throw new InvalidDataException("acceptance placement hook requires an inspected RimWorld assembly");
        }
        return await Bridge.OnMain<object>(ctx, () =>
        {
            CheckCurrent();
            if (deliveryFault?.State == "Armed" || DeliveryPlacement.Active?.Controller == this)
                throw new InvalidDataException("acceptance delivery fault already armed");
            if (parcel.Id != parcelId || parcel.OutpostPayload is not { PayloadDigest.Length: 32 } payload
                || !Rimlink.Client.Outposts.OutpostDeliveryEvidence.MatchesScope(parcel, owner.Scope))
                throw new InvalidDataException("actual own protected parcel required");
            bool declared = definition is not null && (parcel.Items.Items.Any(i => definition == "thing:" + i.DefName
                || i.StuffDef.Length > 0 && definition == "thing:" + i.StuffDef)
                || parcel.Pawns.Pawns.Any(p => p.KindDef.Length > 0 && definition == "kind:" + p.KindDef));
            if (kind == DefinitionFault && !declared) throw new InvalidDataException("definition is not declared by the actual selected parcel");
            if (kind == PlacementFault) DeliveryPlacement.EnsurePatched();
            string id = Guid.NewGuid().ToString("D");
            deliveryFault = new(id, kind!, parcelId, Convert.ToHexStringLower(payload.PayloadDigest.Span), definition, prefix,
                Path.Combine(Path.GetDirectoryName(bindingPath)!, "delivery-fault-" + id + ".json"), gameAssemblySha256);
            verifiedDelivery = null;
            return ObserveDeliveryFault();
        });
    }

    public async Task VerifyDeliveryAsync(OutpostAcceptanceContext context, CancellationToken ct)
    {
        if (deliveryFault?.State != "Armed") return;
        ct.ThrowIfCancellationRequested(); await VerifyAsync();
        await Rimlink.Client.UI.MainThread.Get(() =>
        {
            CheckCurrent(); OutpostAcceptance.RequireCurrent(context);
            verifiedDelivery = context; verifiedAt = Stopwatch.GetTimestamp(); return true;
        });
    }

    private bool MatchesDelivery(OutpostAcceptanceContext context, Parcel parcel)
    {
        if (deliveryFault is not { } f || context.Kind != "delivery" || context.ObjectId != f.Parcel.ToString(CultureInfo.InvariantCulture)
            || parcel.Id != f.Parcel || parcel.OutpostPayload is not { } payload || Convert.ToHexStringLower(payload.PayloadDigest.Span) != f.Payload)
            return false;
        CheckCurrent(); OutpostAcceptance.RequireCurrent(context);
        if (!ReferenceEquals(verifiedDelivery, context) || Stopwatch.GetElapsedTime(verifiedAt) > TimeSpan.FromSeconds(5)
            || Hash(context.Scope.ToByteArray()) != scopeHash || !ReferenceEquals(context.Login, owner.Login))
            throw new InvalidDataException("acceptance delivery requires fresh exact local attestation");
        return true;
    }

    public bool DefinitionAvailable(OutpostAcceptanceContext context, Parcel parcel, string definition, bool actual)
    {
        if (!actual || deliveryFault is not { State: "Armed", Kind: DefinitionFault } f || f.Definition != definition
            || !MatchesDelivery(context, parcel)) return actual;
        f.State = "Triggered";
        f.EvidenceSha256 = WriteFaultEvidence(f.File, new { kind = DefinitionFault, proof = Proof(context, parcel), definition,
            actualAvailable = true, injectedAvailable = false, injected = true, spawnedByThisAttempt = 0 });
        return false;
    }

    public IDisposable? OpenPlacement(OutpostAcceptanceContext context, Parcel parcel, Map map, IReadOnlyList<Thing> things,
        IReadOnlyList<(Pawn Pawn, PawnRegistryEnvelope Envelope)> workers)
    {
        if (deliveryFault is not { State: "Armed", Kind: PlacementFault } f || !MatchesDelivery(context, parcel)) return null;
        if (f.Prefix >= things.Count || things.Count > 10000 || things.Distinct(ReferenceEqualityComparer.Instance).Count() != things.Count)
        { f.State = "NotReached: no proper nonempty prefix"; return null; }
        if (DeliveryPlacement.Active is not null) throw new InvalidDataException("acceptance protected placement is already active");
        var placement = new DeliveryPlacement(this, f, context, parcel, map, things.ToArray(), workers.ToArray());
        DeliveryPlacement.Active = placement; return placement;
    }

    public void DeliveryFailed(OutpostAcceptanceContext context, Parcel parcel, bool spawnStarted, Exception error)
    {
        if (deliveryFault is not { State: "Triggered" } f || f.Parcel != parcel.Id) return;
        OutpostAcceptance.RequireCurrent(context);
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        f.OutcomeSha256 = WriteFaultEvidence(f.File + ".outcome.json", new { proof = Proof(context, parcel), spawnStarted,
            writesBlocked = SaveSync.WritesBlocked, recoveryNeeded = RimlinkSession.RecoveryNeeded, error = error.Message,
            localMaterializationReceipts = component.OutpostRecovery.Metadata.Receipts.Where(r => r.ParcelIds.Contains(parcel.Id)
                && r.DeliveryAttemptId.Length > 0).Select(r => new { r.TransitionId, r.DeliveryAttemptId, r.ClientRequestId }).ToArray(),
            outbox = RimlinkSession.Outbox?.Entries.Where(e => e.Kind == Rimlink.Client.Economy.OutboxKind.OutpostBeginDelivery)
                .Select(e => new { e.Id, spawnStarted = e.Outpost?.SpawnStarted }).ToArray() });
    }

    private object Proof(OutpostAcceptanceContext context, Parcel parcel) => new { version = 1, binding.StandId, binding.RunId,
        binding.SourceIdentity, processId = Environment.ProcessId, realUtc = DateTimeOffset.UtcNow,
        realTimestamp = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency, sessionSha256 = sessionHash, scopeSha256 = scopeHash,
        operationId = context.OperationId, intentSha256 = context.IntentSha256, parcelId = parcel.Id,
        payloadSha256 = Convert.ToHexStringLower(parcel.OutpostPayload.PayloadDigest.Span) };

    private static string WriteFaultEvidence(string path, object value)
    {
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("acceptance fault evidence parent is aliased");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("acceptance fault evidence too large");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        file.Write(bytes); file.Flush(flushToDisk: true); file.Position = 0;
        byte[] actual = new byte[bytes.Length]; file.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(bytes) || file.Length != bytes.Length) throw new IOException("acceptance fault evidence readback failed");
        return Hash(bytes);
    }

    internal void ClearDeliveryFault()
    {
        if (deliveryFault is { State: "Armed" } f) f.State = "Cancelled";
        if (DeliveryPlacement.Active is { } active && ReferenceEquals(active.Controller, this)) active.Dispose();
        verifiedDelivery = null;
    }

    private sealed class DeliveryPlacement(AcceptanceGameController controller, DeliveryFault fault, OutpostAcceptanceContext context,
        Parcel parcel, Map map, Thing[] things, (Pawn Pawn, PawnRegistryEnvelope Envelope)[] workers) : IDisposable
    {
        internal static DeliveryPlacement? Active;
        internal AcceptanceGameController Controller => controller;
        private static bool patched;
        internal static void EnsurePatched()
        {
            if (patched) return;
            var method = AccessTools.Method(typeof(DropPodUtility), nameof(DropPodUtility.MakeDropPodAt),
                [typeof(IntVec3), typeof(Map), typeof(ActiveTransporterInfo), typeof(Faction)])
                ?? throw new InvalidDataException("inspected placement method signature is unavailable");
            new Harmony("rimlink.bridgetools.outposts.acceptance-placement").Patch(method,
                postfix: new HarmonyMethod(typeof(DeliveryPlacement), nameof(Placed)));
            patched = true;
        }
        private static void Placed(Map map, ActiveTransporterInfo info) => Active?.Observe(map, info);
        private void Observe(Map actualMap, ActiveTransporterInfo info)
        {
            if (fault.State != "Armed" || !ReferenceEquals(actualMap, map) || !controller.MatchesDelivery(context, parcel)
                || !info.innerContainer.Any(t => things.Any(own => ReferenceEquals(own, t)))) return;
            var placed = things.Select((thing, ordinal) => new { thing, ordinal,
                root = thing.Spawned ? thing : thing.ParentHolder is null ? null : ThingOwnerUtility.SpawnedParentOrMe(thing.ParentHolder) })
                .Where(v => v.root?.Spawned == true && ReferenceEquals(v.root.Map, map) && ReferenceEquals(v.thing.MapHeld, map)).ToArray();
            if (placed.Length != fault.Prefix || placed.Length == 0 || placed.Length >= things.Length
                || !placed.Select(v => v.ordinal).SequenceEqual(Enumerable.Range(0, placed.Length))) return;
            fault.State = "Triggered";
            var rows = things.Select((thing, ordinal) => new { ordinal, thingId = thing.GetUniqueLoadID(), definition = thing.def.defName,
                quantity = thing.stackCount, placed = placed.Any(p => ReferenceEquals(p.thing, thing)),
                container = placed.FirstOrDefault(p => ReferenceEquals(p.thing, thing))?.root?.GetUniqueLoadID(),
                pawnCustodyId = workers.FirstOrDefault(w => ReferenceEquals(w.Pawn, thing)).Envelope?.PawnCustodyId }).ToArray();
            fault.EvidenceSha256 = WriteFaultEvidence(fault.File, new { kind = PlacementFault, proof = controller.Proof(context, parcel),
                gameAssemblySha256 = fault.GameAssemblySha256,
                mapId = map.uniqueID, expectedObjects = things.Length, placedObjects = placed.Length, remainingObjects = things.Length - placed.Length,
                expectedQuantity = things.Sum(t => (long)t.stackCount), placedQuantity = placed.Sum(p => (long)p.thing.stackCount), objects = rows });
            throw new IOException("isolated acceptance: protected delivery failed after a proved physical prefix");
        }
        public void Dispose()
        {
            if (ReferenceEquals(Active, this)) Active = null;
            if (fault.State == "Armed") fault.State = "NotReached: placement call exited";
        }
    }
}
