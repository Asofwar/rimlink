using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Outposts;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

public sealed partial class OutpostAcceptanceTools
{
    private static AcceptanceGameController? current;
    [Tool("rimlink/outpost_acceptance_bind", Description = "Bind dev-only pauses to a live private acceptance host and this exact owned game/session; never enables product")]
    public async Task<object> Bind(IRimBridgeContext ctx,
        [ToolParameter(Description = "Exact own run.control.json under the isolated stand evidence directory", Required = true)] string bindingFile)
    {
        try
        {
            var controller = await AcceptanceGameController.BindAsync(ctx, bindingFile);
            return await Bridge.OnMain<object>(ctx, () =>
            {
                controller.CheckCurrent(); OutpostAcceptance.Reset(); current = controller; OutpostAcceptance.Observer = controller;
                return new { success = true, controller.Binding.StandId, controller.Binding.RunId, processId = Environment.ProcessId };
            });
        }
        catch (Exception error) { return Bridge.Fail(error.Message); }
    }

    [Tool("rimlink/outpost_acceptance_pause", Description = "Arm/observe/release/disarm one exact dev-only async boundary of this owned session; no object/state setters")]
    public async Task<object> Pause(IRimBridgeContext ctx,
        [ToolParameter(Description = "arm, observe, release or disarm", Required = true)] string action,
        [ToolParameter(Description = "Exact approved boundary for arm")] string? boundary = null,
        [ToolParameter(Description = "staff or delivery")] string kind = "staff",
        [ToolParameter(Description = "Actual caravan id or parcel id for arm")] string? objectId = null,
        [ToolParameter(Description = "Exact reached operation id for release")] string? operation = null,
        [ToolParameter(Description = "Interrupt the operation through its existing recovery path")] bool drop = false)
    {
        try
        {
            var controller = current ?? throw new InvalidDataException("acceptance game is not bound");
            if (action == "observe" && kind == "delivery" && objectId is not null && operation is not null)
                return await Bridge.OnMain<object>(ctx, () => controller.CancelledDelivery(objectId, operation));
            await controller.VerifyAsync();
            return await Bridge.OnMain<object>(ctx, () => controller.Command(action, boundary, kind, objectId, operation, drop));
        }
        catch (Exception error) { current?.Invalidate(); return Bridge.Fail(error.Message); }
    }
}

internal sealed partial class AcceptanceGameController(AcceptanceControlBinding binding, string bindingPath, OutpostAcceptanceContext owner,
    string gamePath, string saveData, string sessionHash, string scopeHash) : IOutpostAcceptanceObserver
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private const string BeforeMaterialize = "delivery-before-materialize";
    private static readonly string[] Boundaries = ["pre-save", "pre-local-durable", "pre-accepted", "prepared-received", "removed-before-save",
        "post-local-durable", "post-accepted", "committed-before-cleanup", "committed-after-cleanup", BeforeMaterialize,
        "receipt-local-durable", "receipt-accepted-before-ack"];
    private readonly AcceptanceHold hold = new();
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime);
    private string? armedObject;
    private string? lastSnapshot;
    internal AcceptanceControlBinding Binding => binding;
    internal void CheckCurrent() { lifetime.Token.ThrowIfCancellationRequested(); OutpostAcceptance.RequireCurrent(owner); }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static async Task<AcceptanceGameController> BindAsync(IRimBridgeContext ctx, string bindingFile)
    {
        string path = Path.GetFullPath(bindingFile);
        if (!File.Exists(path) || new FileInfo(path).Length is < 1 or > 16 * 1024) throw new InvalidDataException("acceptance binding file unavailable");
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("acceptance binding path is aliased");
        var binding = JsonSerializer.Deserialize<AcceptanceControlBinding>(await File.ReadAllBytesAsync(path), AcceptanceControlJson.Options)
            ?? throw new InvalidDataException("acceptance binding invalid");
        var controller = await Bridge.OnMain<AcceptanceGameController>(ctx, () =>
        {
            if (!PawnCustodyRegistry.IsOwnGame || RimlinkSession.Connection is not { Login: { OutpostVersion: 1, OutpostIsolatedAcceptance: true } login } connection
                || connection.Endpoint.Host != "127.0.0.1") throw new InvalidDataException("actual isolated own-game login required");
            var scope = new OutpostScopeMessage { WorldInstanceId = RimlinkSession.World!.WorldInstanceId, OwnerPlayerId = login.Player.PlayerId,
                SaveGeneration = login.SaveGeneration, SaveWriteEpoch = login.SaveWriteEpoch, RestoreBoundary = login.OutpostRestoreBoundary };
            return new(binding, path, new(Current.Game, connection, login, scope, "binding", "binding", "", RimlinkSession.RecoveryToken),
                Path.GetFullPath(Environment.ProcessPath!), Path.TrimEndingDirectorySeparator(Path.GetFullPath(GenFilePaths.SaveDataFolderPath)),
                Hash(Encoding.UTF8.GetBytes(login.SessionToken)), Hash(scope.ToByteArray()));
        });
        await controller.VerifyAsync(); return controller;
    }

    internal async Task VerifyAsync()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        var reply = await AcceptanceControlPipe.SendAsync(binding, new("observe"), lifetime.Token);
        if (!reply.Success || reply.Data is not { } data || data.GetProperty("standId").GetString() != binding.StandId
            || data.GetProperty("runId").GetString() != binding.RunId || data.GetProperty("sourceIdentity").GetString() != binding.SourceIdentity
            || data.GetProperty("port").GetInt32() != owner.Connection.Endpoint.Port) throw new InvalidDataException("acceptance host attestation mismatch");
        string game = Path.GetFullPath(data.GetProperty("gameRoot").GetString()!);
        string evidence = Path.Combine(Path.GetDirectoryName(game)!, "evidence", "run-" + binding.RunId + ".control.json");
        if (!bindingPath.Equals(evidence, PathComparison)
            || !gamePath.Equals(Path.Combine(game, OperatingSystem.IsWindows() ? "RimWorld.exe" : "RimWorldLinux"), PathComparison)
            || !new[] { "hostSaveData", "guestSaveData" }.Any(k => saveData.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(data.GetProperty(k).GetString()!)), PathComparison))
            || !data.GetProperty("sessions").EnumerateArray().Any(s => s.GetProperty("sessionSha256").GetString() == sessionHash
                && s.GetProperty("playerId").GetInt64() == owner.Login.Player.PlayerId && s.GetProperty("outpostVersion").GetUInt32() == 1
                && s.GetProperty("saveWriteEpoch").GetInt64() == owner.Login.SaveWriteEpoch)) throw new InvalidDataException("acceptance game/session ownership mismatch");
        var binaries = data.GetProperty("binaries").EnumerateArray().ToArray();
        foreach (var assembly in new[] { typeof(OutpostOutgoing).Assembly, typeof(OutpostAcceptanceTools).Assembly, typeof(Rimlink.Client.ClientInfo).Assembly, typeof(OutpostScopeMessage).Assembly })
        {
            var binary = binaries.Single(b => b.GetProperty("assembly").GetString() == assembly.GetName().Name);
            if (!Path.GetFullPath(assembly.Location).Equals(Path.GetFullPath(binary.GetProperty("path").GetString()!), PathComparison)
                || Hash(await File.ReadAllBytesAsync(assembly.Location, lifetime.Token)) != binary.GetProperty("sha256").GetString())
                throw new InvalidDataException("acceptance loaded binary mismatch");
        }
        await Rimlink.Client.UI.MainThread.Get(() => { CheckCurrent(); return true; });
    }

    internal object Command(string action, string? boundary, string kind, string? objectId, string? operation, bool drop)
    {
        CheckCurrent();
        switch (action)
        {
            case "arm":
                if (!Boundaries.Contains(boundary) || kind is not ("staff" or "delivery") || string.IsNullOrWhiteSpace(objectId)
                    || (kind == "delivery") != (boundary == BeforeMaterialize || boundary!.StartsWith("receipt", StringComparison.Ordinal)))
                    throw new InvalidDataException("acceptance pause target invalid");
                hold.Arm(boundary!, new(sessionHash, scopeHash, "", kind)); armedObject = objectId; lastSnapshot = null; break;
            case "release":
                var reached = hold.Observe();
                if (reached is null || reached.Target.OperationId != operation) throw new InvalidDataException("exact reached operation required");
                hold.Release(reached.Target, drop); break;
            case "disarm": hold.Disarm(); break;
            case "observe": break;
            default: throw new InvalidDataException("acceptance pause action invalid");
        }
        return Readback(hold.Observe());
    }

    internal object CancelledDelivery(string objectId, string operation)
    {
        if (Current.ProgramState != ProgramState.Entry || !lifetime.IsCancellationRequested || armedObject != objectId
            || hold.Observe() is not { Boundary: BeforeMaterialize, State: "Cancelled" } observed || observed.Target.OperationId != operation)
            return Bridge.Fail("exact cancelled delivery after menu required");
        return Readback(observed);
    }

    private object Readback(AcceptanceHoldState? observed) =>
        new { success = true, binding.StandId, binding.RunId, processId = Environment.ProcessId, hold = observed, objectId = armedObject, snapshotId = lastSnapshot };

    public async Task ReachAsync(string boundary, OutpostAcceptanceContext context, string? snapshotId, CancellationToken ct)
    {
        if (hold.Observe() is not { State: "Armed" } armed || armed.Boundary != boundary || context.ObjectId != armedObject) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            await VerifyAsync();
            await Rimlink.Client.UI.MainThread.Get(() => { OutpostAcceptance.RequireCurrent(context); return true; });
            var target = new AcceptanceFaultTarget(Hash(Encoding.UTF8.GetBytes(context.Login.SessionToken)), Hash(context.Scope.ToByteArray()), context.OperationId, context.Kind, context.IntentSha256);
            lastSnapshot = snapshotId; var waiting = hold.ReachAsync(boundary, target, stop.Token);
            while (!waiting.IsCompleted)
            {
                await Task.WhenAny(waiting, Task.Delay(100, stop.Token));
                await Rimlink.Client.UI.MainThread.Get(() => { CheckCurrent(); OutpostAcceptance.RequireCurrent(context); return true; });
            }
            if (await waiting == true) throw new IOException("isolated acceptance: operation interrupted at " + boundary);
            await VerifyAsync();
        }
        catch { hold.Disarm(); throw; }
    }
    public void Invalidate() { lifetime.Cancel(); hold.Disarm(); ClearDeliveryFault(); }
}
