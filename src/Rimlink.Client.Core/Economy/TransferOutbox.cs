using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

public enum OutboxKind { Send, Post, Deposit, Deliver }

/// <summary>One request whose answer has not been seen yet. The payload is the request itself, so it can be replayed or its contents returned.</summary>
public sealed class OutboxEntry
{
    public string Id { get; set; } = "";
    public OutboxKind Kind { get; set; }
    public byte[] Payload { get; set; } = [];
    /// <summary>Set when the server refused the request for good: the game layer returns the contents, then removes the entry.</summary>
    public string? Refusal { get; set; }
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

    public void MarkRefused(string id, string reason)
    {
        lock (gate)
        {
            if (entries.FirstOrDefault(e => e.Id == id) is not { } entry) return;
            entry.Refusal = reason;
            Save();
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
        _ => throw new InvalidDataException("unknown outbox entry kind " + entry.Kind),
    };

    /// <summary>
    /// True when the server answered and meant it: a Rimlink error detail, or a status the server returns on purpose.
    /// Everything else (unavailable, deadline, cancelled, internal, unauthenticated after a session loss) says nothing
    /// about whether the request was applied.
    /// </summary>
    public static bool IsDefinitive(RpcException ex)
    {
        // A frozen economy or a rate limit says "not now", not "no".
        if (EconomyClient.CodeOf(ex) is { } code) return code != ErrorCode.EconomyFrozen;
        return ex.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.NotFound
            or StatusCode.PermissionDenied or StatusCode.AlreadyExists or StatusCode.OutOfRange or StatusCode.Unimplemented;
    }

    /// <summary>
    /// Sends every unanswered entry again (two attempts, a pause between them). Replies and deliver refusals remove the
    /// entry; a refused send, post or deposit stays with its reason for the game layer to return the contents.
    /// </summary>
    public async Task<ReplayReport> Replay(EconomyClient economy, CancellationToken ct)
    {
        int completed = 0, refused = 0, pending = 0;
        foreach (var entry in Entries)
        {
            if (entry.Refusal is not null) { refused++; continue; }
            var outcome = await Attempt(economy, entry, ct);
            if (outcome is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                outcome = await Attempt(economy, entry, ct);
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
    private async Task<bool?> Attempt(EconomyClient economy, OutboxEntry entry, CancellationToken ct)
    {
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
            }
            log.Info($"outbox: {entry.Kind} {entry.Id} answered on replay");
            return true;
        }
        catch (RpcException ex) when (IsDefinitive(ex))
        {
            string reason = EconomyClient.MessageOf(ex);
            if (entry.Kind == OutboxKind.Deliver)
            {
                // The pod already dropped; a parcel the server no longer offers needs nothing from us.
                log.Warn($"outbox: deliver {entry.Id} refused ({reason}); dropped");
                Remove(entry.Id);
            }
            else
            {
                log.Warn($"outbox: {entry.Kind} {entry.Id} refused ({reason}); its contents go back to the colony");
                MarkRefused(entry.Id, reason);
            }
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
