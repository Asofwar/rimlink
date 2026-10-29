using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

public enum OutboxKind { Send, Post, Deposit, Deliver, Launch, Land, Report, Buy, AckRemoval, Propose, TradeOpen, TradeSetSide }

/// <summary>The typed clients a replay needs; a null client leaves entries of its kinds waiting.</summary>
public sealed record OutboxClients(EconomyClient Economy, Raids.RaidClient? Raids, Visits.ShopClient? Shop, Live.LiveClient? Live = null, Social.TradesClient? Trades = null);

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
}

public sealed record ReplayReport(int Completed, int Refused, int Pending);

/// <summary>
/// A file-backed journal of economy requests. An entry is written before the call and removed after a definitive answer
/// (a reply, or a refusal the server meant). A call that got no answer (timeout, lost connection, crash) is replayed at
/// the next login with the same client_request_id, which the server treats as the same request. Nothing is guessed:
/// an unanswered transfer is neither restored nor forgotten until the server says what became of it.
/// </summary>
public sealed class TransferOutbox
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string path;
    private readonly ILog log;
    private readonly List<OutboxEntry> entries;
    private readonly object gate = new();

    public TransferOutbox(string path, ILog log)
    {
        this.path = path;
        this.log = log;
        entries = Load();
    }

    public IReadOnlyList<OutboxEntry> Entries
    {
        get { lock (gate) return entries.ToList(); }
    }

    public OutboxEntry Add(IMessage request) => request switch
    {
        TransferRequest => Add(OutboxKind.Send, request),
        PostRequest => Add(OutboxKind.Post, request),
        DepositRequest => Add(OutboxKind.Deposit, request),
        DeliverRequest => Add(OutboxKind.Deliver, request),
        LaunchRequest => Add(OutboxKind.Launch, request),
        RaidTick => Add(OutboxKind.Land, request),
        RaidReport => Add(OutboxKind.Report, request),
        BuyRequest => Add(OutboxKind.Buy, request),
        RemovalAck => Add(OutboxKind.AckRemoval, request),
        LiveProposal => Add(OutboxKind.Propose, request),
        TradeOpenRequest => Add(OutboxKind.TradeOpen, request),
        TradeSideRequest => Add(OutboxKind.TradeSetSide, request),
        _ => throw new ArgumentException("not an outbox request: " + request.GetType().Name),
    };

    private OutboxEntry Add(OutboxKind kind, IMessage request)
    {
        var entry = new OutboxEntry { Id = Guid.NewGuid().ToString("N"), Kind = kind, Payload = request.ToByteArray(), CreatedUtc = DateTime.UtcNow };
        lock (gate)
        {
            entries.Add(entry);
            Save();
        }
        return entry;
    }

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
            if (!NothingToReturn(entry))
            {
                entry.Refusal = refusal.Message;
                entry.RefusalKey = refusal.Key;
                entry.RefusalArgs = refusal.Key is null ? null : [.. refusal.Args];
                Save();
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

    public static IMessage Decode(OutboxEntry entry) => entry.Kind switch
    {
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
        OutboxKind.TradeOpen => TradeOpenRequest.Parser.ParseFrom(entry.Payload),
        OutboxKind.TradeSetSide => TradeSideRequest.Parser.ParseFrom(entry.Payload),
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
        if (EconomyClient.CodeOf(ex) is { } code) return code != ErrorCode.EconomyFrozen;
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
        int completed = 0, refused = 0, pending = 0;
        foreach (var entry in Entries)
        {
            if (entry.Refusal is not null) { refused++; continue; }
            if (!CanReplay(clients, entry.Kind)) { pending++; continue; }
            var outcome = await Attempt(clients, entry, ct);
            if (outcome is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                outcome = await Attempt(clients, entry, ct);
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
    private static bool CanReplay(OutboxClients clients, OutboxKind kind) => kind switch
    {
        OutboxKind.Launch or OutboxKind.Land or OutboxKind.Report => clients.Raids is not null,
        OutboxKind.Buy or OutboxKind.AckRemoval => clients.Shop is not null,
        OutboxKind.Propose => clients.Live is not null,
        OutboxKind.TradeOpen or OutboxKind.TradeSetSide => clients.Trades is not null,
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
        if (entry.Kind is OutboxKind.Deliver or OutboxKind.Land or OutboxKind.Buy or OutboxKind.AckRemoval) return true;
        return Decode(entry) switch
        {
            TransferRequest send => (send.Items?.Items.Count ?? 0) == 0 && (send.Pawns?.Pawns.Count ?? 0) == 0,
            PostRequest post => (post.Items?.Items.Count ?? 0) == 0 && post.Pawn is null,
            DepositRequest deposit => deposit.Silver <= 0,
            LaunchRequest launch => launch.PawnMode != PawnMode.PawnReal || (launch.Pawns?.Pawns.Count ?? 0) == 0,
            LiveProposal proposal => (proposal.Pawns?.Pawns.Count ?? 0) == 0,
            RaidReport report => (report.Loot?.Items.Count ?? 0) == 0 && (report.Captives?.Pawns.Count ?? 0) == 0,
            TradeOpenRequest open => (open.Items?.Items.Count ?? 0) == 0 && (open.Pawns?.Pawns.Count ?? 0) == 0,
            TradeSideRequest side => (side.Items?.Items.Count ?? 0) == 0 && (side.Pawns?.Pawns.Count ?? 0) == 0,
            _ => false,
        };
    }

    private async Task<bool?> Attempt(OutboxClients clients, OutboxEntry entry, CancellationToken ct)
    {
        var economy = clients.Economy;
        try
        {
            switch (entry.Kind)
            {
                case OutboxKind.Send: await economy.Send(TransferRequest.Parser.ParseFrom(entry.Payload), ct); break;
                case OutboxKind.Post: await economy.Post(PostRequest.Parser.ParseFrom(entry.Payload), ct); break;
                case OutboxKind.Deposit: await economy.Deposit(DepositRequest.Parser.ParseFrom(entry.Payload), ct); break;
                case OutboxKind.Deliver:
                    var deliver = DeliverRequest.Parser.ParseFrom(entry.Payload);
                    await economy.Deliver(deliver.ParcelId, deliver.GameTick, ct);
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
                case OutboxKind.TradeOpen: await clients.Trades!.Open(TradeOpenRequest.Parser.ParseFrom(entry.Payload), ct); break;
                case OutboxKind.TradeSetSide: await clients.Trades!.SetSide(TradeSideRequest.Parser.ParseFrom(entry.Payload), ct); break;
            }
            log.Info($"outbox: {entry.Kind} {entry.Id} answered on replay");
            return true;
        }
        catch (RpcException ex) when (IsDefinitive(ex))
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
            log.Warn($"outbox: {entry.Kind} {entry.Id} still unanswered: {ex.Message}");
            return null;
        }
    }

    private List<OutboxEntry> Load()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<List<OutboxEntry>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception ex)
        {
            log.Error($"outbox file {path} is unreadable and will be rewritten: {ex.Message}");
        }
        return [];
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
        File.Move(temp, path, overwrite: true);
    }
}
