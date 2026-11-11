using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rimlink.BattlePrototype;

public sealed class BattleTransport
{
    private readonly BattleLedger ledger;
    private readonly IReadOnlyList<BattleBinding> bindings;
    private readonly TimeProvider clock;
    private readonly Dictionary<BattleRole, Queue<long>> rates = new();
    private readonly long[] lastSeen = new long[4];

    public BattleTransport(BattleLedger ledger, IReadOnlyList<BattleBinding> bindings, TimeProvider? clock = null)
    {
        if (bindings.Count != 3 || bindings.Select(b => b.Role).Distinct().Count() != 3
            || bindings.Any(b => !Enum.IsDefined(b.Role) || b.Identity != ledger.State.Identity
                || !BattleContract.IsHash(b.Capability) || string.IsNullOrEmpty(b.PipeName) || b.PipeName.Length > 120
                || b.PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            || bindings.Select(b => b.PipeName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3
            || bindings.Select(b => b.Capability).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("three distinct local role bindings are required");
        this.ledger = ledger; this.bindings = bindings; this.clock = clock ?? TimeProvider.System;
        foreach (var binding in bindings) { rates.Add(binding.Role, new()); lastSeen[(int)binding.Role] = this.clock.GetTimestamp(); }
    }

    public Task Run(CancellationToken ct) => Task.WhenAll(bindings.Select(binding => Serve(binding, ct)));

    private async Task Serve(BattleBinding binding, CancellationToken ct)
    {
        // Keep the Unix listener alive while the next client queues behind this connection.
        using var pipe = new NamedPipeServerStream(binding.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        while (!ct.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                connected = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                BattleWireReply reply;
                try
                {
                    var request = await BattleFrames.Read<BattleWireRequest>(pipe, BattleFrames.RequestLimit, deadline.Token);
                    reply = request is null ? new(false, "InvalidRequest") : Dispatch(binding, request);
                }
                catch (JsonException) { reply = new(false, "InvalidRequest"); }
                catch (BattleRefusalException ex) { reply = new(false, ex.Code); }
                catch (UnauthorizedAccessException) { reply = new(false, "Forbidden"); }
                catch (InvalidDataException) { reply = new(false, "InvalidRequest"); }
                await BattleFrames.Write(pipe, reply, BattleFrames.ReplyLimit, deadline.Token);
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) return; }
            catch (IOException) { /* A disconnected local peer retries the same sequence. */ }
            finally { if (connected) pipe.Disconnect(); }
        }
    }

    private BattleWireReply Dispatch(BattleBinding binding, BattleWireRequest request)
    {
        BattleContract.Validate(request.Identity);
        if (!BattleContract.IsHash(request.Capability) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(request.Capability), Convert.FromHexString(binding.Capability)))
            throw new UnauthorizedAccessException();
        if (request.Identity != binding.Identity) throw new InvalidDataException("binding identity differs");
        if (request.Operation is "start" or "ack" or "view" or "final" && binding.Role != BattleRole.Arbiter)
            throw new UnauthorizedAccessException();
        int payloads = (request.Start is null ? 0 : 1) + (request.Command is null ? 0 : 1)
            + (request.View is null ? 0 : 1) + (request.Result is null ? 0 : 1) + (request.Ack is null ? 0 : 1);
        switch (request.Operation)
        {
            case "poll" when payloads == 0: break;
            case "start" when payloads == 1 && request.Start is { } start:
                if (start.View is null || start.View.State is null || start.Snapshot is null) throw new InvalidDataException("incomplete start");
                CrashAt("before-start-commit"); ledger.Start(binding.Role, start); CrashAt("after-start-commit"); break;
            case "command" when payloads == 1 && request.Command is { } command:
                ledger.Enqueue(binding.Role, request.Identity, command, () => AllowNew(binding.Role)); break;
            case "ack" when payloads == 1 && request.Ack is { } ack:
                ledger.Acknowledge(binding.Role, request.Identity, ack); break;
            case "view" when payloads == 1 && request.View is { } view:
                ledger.Publish(binding.Role, view); CrashAt("after-view-commit"); break;
            case "final" when payloads == 1 && request.Result is { } result:
                if (result.Decision is null || result.Snapshot is null || result.Journal is null) throw new InvalidDataException("incomplete result");
                ledger.Apply(binding.Role, result); CrashAt("after-result-commit"); break;
            default: throw new InvalidDataException("unknown operation or unexpected payload");
        }
        Interlocked.Exchange(ref lastSeen[(int)binding.Role], clock.GetTimestamp());
        bool Online(BattleRole role) => clock.GetElapsedTime(Interlocked.Read(ref lastSeen[(int)role])) < TimeSpan.FromSeconds(30);
        return ledger.Poll(binding.Role, request.Identity) with { AttackerOnline = Online(BattleRole.SideA), DefenderOnline = Online(BattleRole.SideB) };
    }

    private bool AllowNew(BattleRole role)
    {
        var queue = rates[role]; long now = clock.GetTimestamp();
        while (queue.Count != 0 && clock.GetElapsedTime(queue.Peek(), now) >= TimeSpan.FromSeconds(1)) queue.Dequeue();
        if (queue.Count >= 20) return false;
        queue.Enqueue(now); return true;
    }

    private void CrashAt(string point)
    {
        BattleFixture.CrashIfArmed(ledger.Root, point);
    }

    public static Task<BattleWireReply> Request(BattleBinding binding, BattleWireRequest request, CancellationToken ct = default)
        => BattleFrames.Request(binding, request, ct);
}
