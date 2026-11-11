using System.Text.Json;
using HarmonyLib;
using RimBridgeServer.Sdk;
using Rimlink.Client.Game;
using Rimlink.Client.Game.GuildCapture;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

// Only this companion's owned loopback acceptance processes can arm the fault.
public sealed class GuildHomeFaultTools
{
    private sealed record Fault(string Stand, string Battle, string Root, long Player);
    private static Fault? beforeLoad;
    private static bool patched;

    [Tool("rimlink/guild_home_fault", Description = "Dev-only: terminate the owned acceptance client before the mirror load or after it is bound")]
    public Task<object> FaultPoint(IRimBridgeContext ctx, [ToolParameter(Required = true)] string standId,
        [ToolParameter(Required = true)] string battleId, [ToolParameter(Required = true)] string point) => Bridge.OnMain<object>(ctx, () =>
    {
        var fault = RequireOwned(standId, battleId);
        if (point == "after-load")
        {
            if (!BattleGameContext.IsBattleGame || Current.ProgramState != ProgramState.Playing) throw Refused();
            Fire(fault, point);
        }
        if (point != "before-load" || beforeLoad is not null || BattleGameContext.Active || Current.ProgramState != ProgramState.Playing)
            throw Refused();
        if (!patched)
        {
            new Harmony("rimlink.bridgetools.guild-home-fault").Patch(
                AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), [typeof(string)]),
                prefix: new HarmonyMethod(typeof(GuildHomeFaultTools), nameof(BeforeLoad)) { priority = Priority.First });
            patched = true;
        }
        beforeLoad = fault;
        return new { success = true, armed = point };
    });

    public static void BeforeLoad(string saveFileName)
    {
        if (beforeLoad is not { } fault || !saveFileName.StartsWith("GuildView_" + fault.Battle + "_", StringComparison.Ordinal)) return;
        if (RequireOwned(fault.Stand, fault.Battle) != fault) throw Refused();
        beforeLoad = null;
        Fire(fault, "before-load");
    }

    private static Fault RequireOwned(string standId, string battleId)
    {
        if (!Guid.TryParseExact(standId, "D", out var stand) || stand == Guid.Empty || !Guid.TryParseExact(battleId, "D", out var battle)
            || battle == Guid.Empty || !RimlinkSession.Active || RimlinkSession.Connection?.Login?.GuildRuntime is not
                { Mode: GuildRuntimeMode.IsolatedAcceptanceV1 } selected || selected.StandId != standId
            || RimlinkSession.Endpoint is not { Host: "127.0.0.1" } endpoint || RimlinkSession.MyPlayerId <= 0) throw Refused();
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GenFilePaths.SaveDataFolderPath));
        string root = Path.GetDirectoryName(data) ?? throw Refused();
        if (Path.GetFileName(data) is not ("saves-attacker" or "saves-defender")
            || !string.Equals(Path.GetDirectoryName(typeof(Pawn).Assembly.Location), Path.Combine(root, "game"), StringComparison.OrdinalIgnoreCase))
            throw Refused();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bootstrap.json")));
        var target = doc.RootElement.GetProperty("stand");
        if (doc.RootElement.GetProperty("mode").GetString() != "GuildCaptureIsolatedAcceptanceV1"
            || target.GetProperty("standId").GetString() != standId || target.GetProperty("port").GetInt32() != endpoint.Port
            || !string.Equals(target.GetProperty("root").GetString(), root, StringComparison.OrdinalIgnoreCase)) throw Refused();
        return new(standId, battleId, root, RimlinkSession.MyPlayerId);
    }

    private static void Fire(Fault fault, string point)
    {
        if (BattleGameContext.Role != BattleMapRole.Viewer || BattleGameContext.Scope?.BattleId != fault.Battle
            || !BattlePreparedViewer.HasRetainedMarker(RimlinkSession.Endpoint!, fault.Player)) throw Refused();
        string path = Path.Combine(fault.Root, "evidence", $"home-fault-{point}-{fault.Battle}-{fault.Player}.json");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(file, new { fault.Stand, fault.Battle, fault.Player, point, marker = true });
            file.Flush(true);
        }
        // Exit outside RimWorld’s save/quit path without invoking Windows crash reporting.
        Environment.Exit(97);
    }

    private static InvalidDataException Refused() => new("guild-home-fault-requires-owned-acceptance-viewer");
}
