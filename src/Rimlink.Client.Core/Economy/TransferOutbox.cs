using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Rimlink.Protocol;

namespace Rimlink.Client.Economy;

public enum OutboxKind { Send, Post, Deposit, Deliver, Launch, Land, Report, Buy, AckRemoval, Propose, TradeOpen, TradeSetSide, IncidentOrder, ProcurementCreate, ProcurementSupply, ProcurementCancel, AuctionPost, AuctionBid, AuctionCancel, Community, Influence, OutpostPrepare, OutpostCommit, OutpostCancel, OutpostMutate, OutpostBeginDelivery }
public enum OutboxPhase { Legacy, Prepared, Ready, Returning, RecoveryRequired }

/// <summary>The typed clients a replay needs; a null client leaves entries of its kinds waiting.</summary>
public sealed record OutboxClients(EconomyClient Economy, Raids.RaidClient? Raids, Visits.ShopClient? Shop, Live.LiveClient? Live = null, Social.TradesClient? Trades = null, Incidents.IncidentClient? Incidents = null, Procurement.ProcurementClient? Procurement = null, Social.CommunityClient? Community = null, Influence.InfluenceClient? Influence = null, Outposts.OutpostClient? Outposts = null);

/// <summary>One request whose answer has not been seen yet. The payload is the request itself, so it can be replayed or its contents returned.</summary>
public sealed class OutboxEntry
{
    public string Id { get; set; } = "";
    public OutboxKind Kind { get; set; }
    public byte[] Payload { get; set; } = [];
    /// <summary>Set when the server refused the request for good: the game layer returns the contents, then removes the entry.</summary>
    public string? Refusal { get; set; }
    /// <summary>The refusal's key and arguments, so the game layer shows it in the player's language (null from older entries).</summary>
    public string? RefusalKey { get; set; }
    public List<string>? RefusalArgs { get; set; }
    public DateTime CreatedUtc { get; set; }
    public OutboxPhase Phase { get; set; }
    public long SaveGeneration { get; set; }
    public long SaveWriteEpoch { get; set; }
    public string? SnapshotId { get; set; }
    public OutpostJournal? Outpost { get; set; }
    public OrdinaryJournal? Ordinary { get; set; }
}

public sealed record ReplayReport(int Completed, int Refused, int Pending);

/// <summary>
/// A file-backed journal of economy requests. An entry is written before the call and removed after a definitive answer
/// (a reply, or a refusal the server meant). A call that got no answer (timeout, lost connection, crash) is replayed at
/// the next login with the same client_request_id, which the server treats as the same request. Nothing is guessed:
/// an unanswered transfer is neither restored nor forgotten until the server says what became of it.
/// </summary>
public sealed partial class TransferOutbox
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string path;
    private readonly ILog log;
    private readonly List<OutboxEntry> entries;
    private readonly object gate = new();
    private readonly SemaphoreSlim replayGate = new(1, 1);
    private readonly long generation, epoch;

    public TransferOutbox(string path, ILog log, long saveGeneration = 0, long saveWriteEpoch = 0)
    {
        this.path = path;
        this.log = log;
        generation = saveGeneration; epoch = saveWriteEpoch;
        entries = Load();
    }

    public IReadOnlyList<OutboxEntry> Entries
    {
        get { lock (gate) return entries.ToList(); }
    }
    public bool HasUnresolved { get { lock (gate) return entries.Any(e => e.Phase == OutboxPhase.RecoveryRequired
        || e.Phase == OutboxPhase.Legacy && NeedsSnapshot(e) || IsOutpost(e.Kind) && (e.Phase == OutboxPhase.Prepared
            || e.Outpost?.ReplyUnknown == true || e.Kind == OutboxKind.OutpostBeginDelivery && e.Outpost?.DeliveryReply is not null
                && e.Outpost.AcceptedReceipt is null)); } }
    public string? QuarantineOutsideScope(long saveGeneration, long saveWriteEpoch)
    {
        lock (gate)
        {
            var old = entries.Where(e => e.SaveGeneration != saveGeneration || e.SaveWriteEpoch != saveWriteEpoch).ToArray();
            if (old.Length == 0) return null;
            string kept = path + $".before-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json";
            WriteDocument(kept, JsonSerializer.SerializeToUtf8Bytes(old, Json));
            foreach (var entry in old) entries.Remove(entry);
            Save();
            return kept;
        }
    }

    public OutboxEntry Add(IMessage request) => Create(request, OutboxPhase.Ready);
    public OutboxEntry Prepare(IMessage request) => Create(request, OutboxPhase.Prepared);
    private OutboxEntry Create(IMessage request, OutboxPhase phase, OutpostScopeMessage? ordinaryScope = null) => request switch
    {
        TransferRequest => Add(OutboxKind.Send, request, phase, ordinaryScope), PostRequest => Add(OutboxKind.Post, request, phase, ordinaryScope),
        DepositRequest => Add(OutboxKind.Deposit, request, phase), DeliverRequest => Add(OutboxKind.Deliver, request, phase),
        LaunchRequest => Add(OutboxKind.Launch, request, phase, ordinaryScope), RaidTick => Add(OutboxKind.Land, request, phase),
        RaidReport => Add(OutboxKind.Report, request, phase, ordinaryScope), BuyRequest => Add(OutboxKind.Buy, request, phase),
        RemovalAck => Add(OutboxKind.AckRemoval, request, phase), LiveProposal => Add(OutboxKind.Propose, request, phase, ordinaryScope),
        ProcurementCreate => Add(OutboxKind.ProcurementCreate, request, phase), ProcurementSupply => Add(OutboxKind.ProcurementSupply, request, phase),
        ProcurementCancel => Add(OutboxKind.ProcurementCancel, request, phase), TradeOpenRequest => Add(OutboxKind.TradeOpen, request, phase, ordinaryScope),
        TradeSideRequest => Add(OutboxKind.TradeSetSide, request, phase, ordinaryScope), OrderIncidentRequest => Add(OutboxKind.IncidentOrder, request, phase),
        AuctionPostRequest => Add(OutboxKind.AuctionPost, request, phase), AuctionBidRequest => Add(OutboxKind.AuctionBid, request, phase),
        AuctionCancelRequest => Add(OutboxKind.AuctionCancel, request, phase), CommunityRequest => Add(OutboxKind.Community, request, phase),
        InfluenceMutation => Add(OutboxKind.Influence, request, phase),
        OutpostPrepareRequest => Add(OutboxKind.OutpostPrepare, request, OutboxPhase.Prepared),
        OutpostProofRequest => Add(OutboxKind.OutpostCommit, request, OutboxPhase.Prepared),
        OutpostCancelRequest => Add(OutboxKind.OutpostCancel, request, OutboxPhase.Prepared),
        OutpostMutationRequest => Add(OutboxKind.OutpostMutate, request, OutboxPhase.Prepared),
        OutpostDeliveryRequest => Add(OutboxKind.OutpostBeginDelivery, request, OutboxPhase.Prepared),
        _ => throw new ArgumentException("not an outbox request: " + request.GetType().Name),
    };

    private OutboxEntry Add(OutboxKind kind, IMessage request, OutboxPhase phase, OutpostScopeMessage? ordinaryScope = null)
    {
        bool ordinary = HasOrdinaryPawns(request);
        if (ordinary && ordinaryScope is null) throw new InvalidDataException("ordinary custody requires a scoped journal");
        var entry = new OutboxEntry { Id = Guid.NewGuid().ToString("N"), Kind = kind, Payload = request.ToByteArray(), CreatedUtc = DateTime.UtcNow,
            Phase = phase, SaveGeneration = generation, SaveWriteEpoch = epoch, Outpost = IsOutpost(kind) ? NewOutpostJournal(request) : null };
        if (ordinary) entry.Ordinary = new() { Capture = OrdinaryCapture.Create(request, entry.Id, ordinaryScope!).ToByteArray() };
        lock (gate)
        {
            entries.Add(entry);
            try { Save(); } catch { entries.Remove(entry); throw; }
        }
        return entry;
    }

    public void ReconcileSelectedSnapshot(SaveRecoveryData snapshot, long gameTick, SaveHeader? verifiedDownload)
    {
        long selectedGeneration = verifiedDownload?.SaveGeneration ?? snapshot.Generation;
        long selectedEpoch = verifiedDownload?.SaveWriteEpoch ?? snapshot.WriteEpoch;
        bool accepted = snapshot.Generation == selectedGeneration && snapshot.WriteEpoch == selectedEpoch;
        // A verified rollback selects a new write epoch while the snapshot keeps its original custody proof.
        ReconcileSnapshot(snapshot, accepted, gameTick, selected: true, selectedGeneration, selectedEpoch);
    }

    public void ReconcileSnapshot(SaveRecoveryData snapshot, bool accepted, long? gameTick = null, bool selected = false)
        => ReconcileSnapshot(snapshot, accepted, gameTick, selected, snapshot.Generation, snapshot.WriteEpoch);

    private void ReconcileSnapshot(SaveRecoveryData snapshot, bool accepted, long? gameTick, bool selected,
        long selectedGeneration, long selectedEpoch)
    {
        snapshot.Validate();
        var removed = snapshot.Removed.ToHashSet(StringComparer.Ordinal);
        var returned = snapshot.Returned.ToHashSet(StringComparer.Ordinal);
        var parcels = snapshot.ParcelIds.ToHashSet();
        lock (gate)
        {
            foreach (var entry in entries.ToArray())
            {
                if (IsOutpost(entry.Kind) || entry.Phase == OutboxPhase.RecoveryRequired) continue;
                if (IsOrdinary(entry))
                {
                    if (entry.Ordinary is null && selected) entry.Phase = OutboxPhase.RecoveryRequired;
                    continue; // Legacy Removed/Returned/tick never authorizes a protected ordinary operation.
                }
                if (entry.Phase == OutboxPhase.Legacy)
                {
                    if (NeedsSnapshot(entry)) { if (selected) entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                    entry.Phase = OutboxPhase.Ready;
                }
                if (entry.SaveGeneration != selectedGeneration || entry.SaveWriteEpoch != selectedEpoch)
                { if (selected) entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                if (returned.Contains(entry.Id))
                {
                    if (accepted) entries.Remove(entry); else entry.Phase = OutboxPhase.Returning;
                    continue;
                }
                bool present = entry.Kind == OutboxKind.Deliver ? parcels.Contains(((DeliverRequest)Decode(entry)).ParcelId) : removed.Contains(entry.Id);
                if (entry.Phase == OutboxPhase.Prepared)
                {
                    if (!present) { if (selected) entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                    if (!accepted) continue;
                    entry.Phase = OutboxPhase.Ready;
                    var request = Decode(entry);
                    if (gameTick is { } tick) request.Descriptor.FindFieldByName("game_tick")?.Accessor.SetValue(request, tick);
                    entry.Payload = request.ToByteArray();
                    entry.SnapshotId = snapshot.SnapshotId;
                }
                if (entry.Phase == OutboxPhase.Ready && NeedsSnapshot(entry) && snapshot.Version > 0)
                {
                    if (!present) { if (selected) entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                    if (accepted) entry.SnapshotId = snapshot.SnapshotId;
                    if (accepted && entry.Kind == OutboxKind.Deliver)
                    {
                        var deliver = (DeliverRequest)Decode(entry);
                        deliver.SnapshotId = snapshot.SnapshotId; deliver.SaveGeneration = snapshot.Generation; deliver.SaveWriteEpoch = snapshot.WriteEpoch;
                        entry.Payload = deliver.ToByteArray(); entry.SnapshotId = snapshot.SnapshotId;
                    }
                }
            }
            Save();
        }
    }

    private static bool NeedsSnapshot(OutboxEntry entry) => entry.SnapshotId is not null || !NothingToReturn(entry)
        || entry.Kind is OutboxKind.Deliver or OutboxKind.AckRemoval or OutboxKind.Land
        || entry.Kind == OutboxKind.Report && ((RaidReport)Decode(entry)).Outcomes.Any(o => o.Blob is not null);

    public void Remove(string id)
    {
        lock (gate)
        {
            if (entries.RemoveAll(e => e.Id == id) > 0) Save();
        }
    }

    /// <summary>
    /// The server refused the request for good: an entry that holds things of ours stays with its reason for the game layer
    /// to return them; one that holds nothing is dropped, so no empty pod comes back. True when the entry was kept.
    /// </summary>
    public bool Refuse(string id, string reason) => Refuse(id, new Refusal(null, reason, null, []));

    public bool Refuse(string id, Refusal refusal)
    {
        lock (gate)
        {
            if (entries.FirstOrDefault(e => e.Id == id) is not { } entry) return false;
            if (IsOutpost(entry.Kind)) { HoldForRecovery(entry.Id, refusal); return true; }
            if (IsOrdinary(entry))
            {
                // Record a business refusal without granting a return or downgrading an evidence hold.
                var previous = (entry.Refusal, entry.RefusalKey, entry.RefusalArgs);
                entry.Refusal = refusal.Message; entry.RefusalKey = refusal.Key;
                entry.RefusalArgs = refusal.Key is null ? null : [.. refusal.Args];
                try { Save(); } catch { (entry.Refusal, entry.RefusalKey, entry.RefusalArgs) = previous; throw; }
                return true;
            }
            if (!NothingToReturn(entry))
            {
                var previous = (entry.Phase, entry.Refusal, entry.RefusalKey, entry.RefusalArgs);
                entry.Phase = OutboxPhase.Returning;
                entry.Refusal = refusal.Message;
                entry.RefusalKey = refusal.Key;
                entry.RefusalArgs = refusal.Key is null ? null : [.. refusal.Args];
                try { Save(); }
                catch { (entry.Phase, entry.Refusal, entry.RefusalKey, entry.RefusalArgs) = previous; throw; }
                return true;
            }
            entries.Remove(entry);
            Save();
            return false;
        }
    }

    public bool Contains(OutboxKind kind, Func<IMessage, bool> match)
    {
        lock (gate) return entries.Any(e => e.Kind == kind && match(Decode(e)));
    }
    public void HoldForRecovery(string id, Refusal refusal)
    {
        lock (gate)
        {
            if (entries.FirstOrDefault(e => e.Id == id) is not { } entry) return;
            entry.Phase = OutboxPhase.RecoveryRequired; entry.Refusal = refusal.Message; entry.RefusalKey = refusal.Key;
            entry.RefusalArgs = refusal.Key is null ? null : [.. refusal.Args]; Save();
        }
    }

    public static IMessage Decode(OutboxEntry entry) => entry.Kind switch
    {
        OutboxKind.OutpostPrepare => OutpostPrepareRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.OutpostCommit => OutpostProofRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.OutpostCancel => OutpostCancelRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.OutpostMutate => OutpostMutationRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.OutpostBeginDelivery => OutpostDeliveryRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.Community => CommunityRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.Influence => InfluenceMutation.Parser.ParseFrom(entry.Payload),
        OutboxKind.Send => TransferRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.Post => PostRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.Deposit => DepositRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.Deliver => DeliverRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.Launch => LaunchRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.Land => RaidTick.Parser.ParseFrom(entry.Payload),
        OutboxKind.Report => RaidReport.Parser.ParseFrom(entry.Payload),
        OutboxKind.Buy => BuyRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.AckRemoval => RemovalAck.Parser.ParseFrom(entry.Payload),
        OutboxKind.Propose => LiveProposal.Parser.ParseFrom(entry.Payload),
        OutboxKind.ProcurementCreate => ProcurementCreate.Parser.ParseFrom(entry.Payload),
        OutboxKind.ProcurementSupply => ProcurementSupply.Parser.ParseFrom(entry.Payload),
        OutboxKind.ProcurementCancel => ProcurementCancel.Parser.ParseFrom(entry.Payload),
        OutboxKind.TradeOpen => TradeOpenRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.TradeSetSide => TradeSideRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.IncidentOrder => OrderIncidentRequest.Parser.ParseFrom(entry.Payload),

        OutboxKind.AuctionPost => AuctionPostRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.AuctionBid => AuctionBidRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.AuctionCancel => AuctionCancelRequest.Parser.ParseFrom(entry.Payload),
        _ => throw new InvalidDataException("unknown outbox entry kind " + entry.Kind),
    };

    /// <summary>
    /// True when the server answered and meant it: a Rimlink error detail, or a status the server returns on purpose.
    /// Everything else (unavailable, deadline, cancelled, internal, unauthenticated after a session loss) says nothing
    /// about whether the request was applied.
    /// </summary>
    public static bool IsDefinitive(RpcException ex)
    {
        // A frozen economy or a rate limit says "not now", not "no". PLAYER_FROZEN is a "no": the server checks it after the
        // replay lookup, so a request applied before the freeze still returns its result and a refused one was never applied.
        if (ex.StatusCode == StatusCode.ResourceExhausted) return false;
        if (EconomyClient.CodeOf(ex) is { } code) return code is not (ErrorCode.EconomyFrozen or ErrorCode.InfluenceUnavailable or ErrorCode.RecoveryRequired
            or ErrorCode.RequestIntentMismatch or ErrorCode.SaveWriteEpochMismatch or ErrorCode.RecoveryClientRequired or ErrorCode.OutpostUnavailable);
        return ex.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.NotFound
            or StatusCode.PermissionDenied or StatusCode.AlreadyExists or StatusCode.OutOfRange or StatusCode.Unimplemented;
    }

    /// <summary>
    /// Sends every unanswered entry again (two attempts, a pause between them). Replies remove the entry; a refused entry
    /// that holds things of ours stays with its reason for the game layer to return them, any other refused entry is removed.
    /// </summary>
    public Task<ReplayReport> Replay(EconomyClient economy, CancellationToken ct) => Replay(new OutboxClients(economy, null, null), ct);

    public async Task<ReplayReport> Replay(OutboxClients clients, CancellationToken ct)
    {
        await replayGate.WaitAsync(ct);
        try { return await ReplayCore(clients, ct); }
        finally { replayGate.Release(); }
    }

    private async Task<ReplayReport> ReplayCore(OutboxClients clients, CancellationToken ct)
    {
        var authentication = clients.Economy.Connection.Login;
        int completed = 0, refused = 0, pending = 0;
        foreach (var entry in Entries)
        {
            if (IsOutpost(entry.Kind) && QuarantineForLogin(entry, authentication, clients.Outposts)) { pending++; continue; }
            if (IsOutpost(entry.Kind) && entry.Outpost?.DeliveryReply is not null && entry.Outpost.AcceptedReceipt is null)
            { pending++; continue; }
            if (entry.Phase == OutboxPhase.Legacy && NeedsSnapshot(entry)) { pending++; continue; }
            if (entry.Phase is OutboxPhase.Prepared or OutboxPhase.RecoveryRequired) { pending++; continue; }
            bool? outcome;
            if (entry.Ordinary is not null) outcome = await AttemptOrdinary(clients, entry, authentication, ct);
            else
            {
                if (entry.Refusal is not null) { refused++; continue; }
                if (!ReadyFor(entry, authentication)) { pending++; continue; }
                if (!CanReplay(clients, entry.Kind)) { pending++; continue; }
                outcome = await Attempt(clients, entry, authentication!, ct);
            }
            if (outcome is null && !IsOutpost(entry.Kind) && entry.Ordinary is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                outcome = await Attempt(clients, entry, authentication!, ct);
            }
            switch (outcome)
            {
                case true:
                    Remove(entry.Id);
                    completed++;
                    break;
                case false:
                    refused++;
                    break;
                case null:
                    pending++;
                    break;
            }
        }
        if (completed + refused + pending > 0) log.Info($"outbox replay: {completed} completed, {refused} refused, {pending} still unanswered");
        return new ReplayReport(completed, refused, pending);
    }

    /// <summary>True: answered. False: refused for good (entry marked, or removed for a deliver). Null: still unknown.</summary>
    public static bool ReadyFor(OutboxEntry entry, LoginReply? login) => login is not null
        && entry.SaveGeneration == login.SaveGeneration && entry.SaveWriteEpoch == login.SaveWriteEpoch
        && (IsOutpost(entry.Kind) ? OutpostReadyFor(entry, login) : IsOrdinary(entry) ? OrdinaryReadyFor(entry, login)
            : entry.Phase == OutboxPhase.Ready || entry.Phase == OutboxPhase.Legacy && !NeedsSnapshot(entry));

    private static bool CanReplay(OutboxClients clients, OutboxKind kind) => kind switch
    {
        OutboxKind.OutpostPrepare or OutboxKind.OutpostCommit or OutboxKind.OutpostCancel or OutboxKind.OutpostMutate
            or OutboxKind.OutpostBeginDelivery => clients.Outposts is not null,
        OutboxKind.Launch or OutboxKind.Land or OutboxKind.Report => clients.Raids is not null,
        OutboxKind.Buy or OutboxKind.AckRemoval => clients.Shop is not null,
        OutboxKind.Propose => clients.Live is not null,
        OutboxKind.ProcurementCreate or OutboxKind.ProcurementSupply or OutboxKind.ProcurementCancel => clients.Procurement is not null,
        OutboxKind.TradeOpen or OutboxKind.TradeSetSide => clients.Trades is not null,
        OutboxKind.IncidentOrder => clients.Incidents is not null,

        OutboxKind.Community => clients.Community is not null,
        OutboxKind.Influence => clients.Influence is not null,
        _ => true,
    };

    /// <summary>
    /// Entries whose refusal leaves nothing to return: the entry is simply dropped. Deliver, land, buy and removal acks
    /// never hold anything of ours (the pod already dropped, the raid is on the map, the shop copy is discarded); neither
    /// do wallet silver, raids by copies and a report without loot or captives. A refused report keeps its loot and its
    /// captives (the defender's own colonists, already gone from the colony) for the game layer to put back; the raiders go
    /// home at the server's expiry. Keep in step with the game layer's Outgoing.Contents.
    /// </summary>
    private static bool NothingToReturn(OutboxEntry entry)
    {
        if (entry.Kind is OutboxKind.Deliver or OutboxKind.Land or OutboxKind.Buy or OutboxKind.AckRemoval or OutboxKind.IncidentOrder or OutboxKind.Community or OutboxKind.Influence) return true;
        return Decode(entry) switch
        {
            TransferRequest send => (send.Items?.Items.Count ?? 0) == 0 && (send.Pawns?.Pawns.Count ?? 0) == 0,
            PostRequest post => (post.Items?.Items.Count ?? 0) == 0 && post.Pawn is null,
            DepositRequest deposit => deposit.Silver <= 0,
            LaunchRequest launch => launch.PawnMode != PawnMode.PawnReal || (launch.Pawns?.Pawns.Count ?? 0) == 0,
            LiveProposal proposal => (proposal.Pawns?.Pawns.Count ?? 0) == 0,
            RaidReport report => (report.Loot?.Items.Count ?? 0) == 0 && (report.Captives?.Pawns.Count ?? 0) == 0,
            ProcurementCreate or ProcurementCancel => true,
            ProcurementSupply supply => (supply.Items?.Items.Count ?? 0) == 0,
            TradeOpenRequest open => (open.Items?.Items.Count ?? 0) == 0 && (open.Pawns?.Pawns.Count ?? 0) == 0,
            TradeSideRequest side => (side.Items?.Items.Count ?? 0) == 0 && (side.Pawns?.Pawns.Count ?? 0) == 0,
            AuctionPostRequest post => (post.Items?.Items.Count ?? 0) == 0,
            AuctionBidRequest or AuctionCancelRequest => true,
            _ => false,
        };
    }

    private async Task<bool?> Attempt(OutboxClients clients, OutboxEntry entry, LoginReply authentication, CancellationToken ct)
    {
        var economy = clients.Economy;
        if (!ReferenceEquals(economy.Connection.Login, authentication) || !ReadyFor(entry, authentication)) return null;
        try
        {
            if (IsOutpost(entry.Kind)) return await economy.Connection.ForAuthentication(authentication,
                () => AttemptOutpost(clients.Outposts!, entry, ct));
            await economy.Connection.ForAuthentication(authentication, async () =>
            {
                switch (entry.Kind)
                {
                    case OutboxKind.Send: await economy.Send(TransferRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.Post: await economy.Post(PostRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.Deposit: await economy.Deposit(DepositRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.Deliver:
                        var deliver = DeliverRequest.Parser.ParseFrom(entry.Payload);
                        await economy.Deliver(deliver, ct);
                        break;
                    case OutboxKind.Launch: await clients.Raids!.Launch(LaunchRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.Land:
                        var land = RaidTick.Parser.ParseFrom(entry.Payload);
                        await clients.Raids!.Land(land.RaidId, land.GameTick, ct);
                        break;
                    case OutboxKind.Report: await clients.Raids!.Report(RaidReport.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.Buy: await clients.Shop!.Buy(BuyRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.AckRemoval: await clients.Shop!.AckRemoval(RemovalAck.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.Propose: await clients.Live!.Propose(LiveProposal.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.ProcurementCreate: await clients.Procurement!.Create(ProcurementCreate.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.ProcurementSupply: await clients.Procurement!.Supply(ProcurementSupply.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.ProcurementCancel: await clients.Procurement!.Cancel(ProcurementCancel.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.TradeOpen: await clients.Trades!.Open(TradeOpenRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.TradeSetSide: await clients.Trades!.SetSide(TradeSideRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.IncidentOrder: await clients.Incidents!.Order(OrderIncidentRequest.Parser.ParseFrom(entry.Payload), ct); break;

                    case OutboxKind.AuctionPost: await economy.PostAuction(AuctionPostRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.AuctionBid: await economy.BidAuction(AuctionBidRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.AuctionCancel: await economy.CancelAuction(AuctionCancelRequest.Parser.ParseFrom(entry.Payload), ct); break;

                    case OutboxKind.Community: await clients.Community!.Mutate(CommunityRequest.Parser.ParseFrom(entry.Payload), ct); break;
                    case OutboxKind.Influence: await clients.Influence!.Mutate(InfluenceMutation.Parser.ParseFrom(entry.Payload), ct); break;
                }
            });
            log.Info($"outbox: {entry.Kind} {entry.Id} answered on replay");
            return true;
        }
        catch (RpcException ex) when (EconomyClient.CodeOf(ex) is ErrorCode.RequestIntentMismatch or ErrorCode.SaveWriteEpochMismatch or ErrorCode.RecoveryClientRequired)
        {
            HoldForRecovery(entry.Id, RpcErrorDetail.Read(ex));
            return null;
        }
        catch (RpcException ex) when (ReferenceEquals(economy.Connection.Login, authentication) && IsDefinitive(ex))
        {
            var refusal = RpcErrorDetail.Read(ex);
            string reason = refusal.Message;
            log.Warn(Refuse(entry.Id, refusal)
                ? $"outbox: {entry.Kind} {entry.Id} refused ({reason}); its contents go back to the colony"
                : $"outbox: {entry.Kind} {entry.Id} refused ({reason}); nothing to return, dropped");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsOutpost(entry.Kind)) MarkReplyUnknown(entry.Id);
            log.Warn($"outbox: {entry.Kind} {entry.Id} still unanswered: {ex.Message}");
            return null;
        }
    }

    private List<OutboxEntry> Load()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<List<OutboxEntry>>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty outbox document");
        }
        catch (Exception ex)
        {
            log.Error($"outbox file {path} is unreadable and was preserved: {ex.Message}");
            throw new IOException("the outbox journal is unreadable; it was preserved", ex);
        }
        return [];
    }

    private void Save()
    {
        WriteDocument(path, JsonSerializer.SerializeToUtf8Bytes(entries, Json));
    }
    private static void WriteDocument(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(content); file.Flush(true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
