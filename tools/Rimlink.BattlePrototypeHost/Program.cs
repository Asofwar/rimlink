using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.BattlePrototype;

try
{
    if (args.Length is not (3 or 5) || args[1] != "--stand" || args[0] is not ("prepare" or "serve" or "inspect" or "fault" or "rotate-boundary")
        || args.Length == 5 && (args[0] != "fault" || args[3] != "--at"))
        throw new InvalidDataException("Usage: Rimlink.BattlePrototypeHost prepare|serve|inspect|fault|rotate-boundary --stand <local-owned-root>");
    string stand = BattleLedger.OwnedRoot(args[2]);
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(stand, "stand.json")));
    string game = manifest.RootElement.GetProperty("gameRoot").GetString() ?? throw new InvalidDataException("missing game root");
    if (Path.GetFullPath(game) != Path.Combine(stand, "game") || !manifest.RootElement.GetProperty("noProductionConnection").GetBoolean())
        throw new InvalidDataException("not an isolated owned game stand");
    string active = Path.Combine(stand, "active-battle.json");
    if (args[0] == "prepare")
    {
        using var env = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(stand, "evidence", "env-verified.json")));
        if (env.RootElement.GetProperty("gate").GetString() != "ENV-PASS") throw new InvalidDataException("ENV gate has not passed");
        if (File.Exists(active))
        {
            var prior = Read<BattlePreparation>(active);
            using var old = new BattleLedger(Path.Combine(stand, "battles", prior.Identity.BattleId.ToString("N")), prior.Identity);
            if (old.State.Phase is BattlePhase.Prepared or BattlePhase.Started or BattlePhase.RecoveryRequired)
                throw new InvalidDataException("previous fixture is not safely terminal; preserve it before another prepare");
        }
        string gameHash = Hash(Path.Combine(game, "Assembly-CSharp.dll"));
        string templateHash = Hash(Path.Combine(stand, "templates", "vanilla.rws"));
        if (gameHash != env.RootElement.GetProperty("gameAssemblySha256").GetString()
            || templateHash != env.RootElement.GetProperty("template").GetProperty("sha256").GetString())
            throw new InvalidDataException("verified game/template changed");
        var roster = BattleFixture.Roster();
        var identity = new BattleIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            BattleContract.RulesDigest(BattleFixture.Arena, BattleFixture.Zone), templateHash, BattleContract.Digest(roster), 1);
        var preparation = new BattlePreparation(identity, BattleFixture.Arena, BattleFixture.Zone, roster);
        string match = BattleLedger.OwnedRoot(Path.Combine(stand, "battles", identity.BattleId.ToString("N")));
        using var ledger = new BattleLedger(match, identity);
        foreach (var role in Enum.GetValues<BattleRole>())
        {
            string profile = RoleName(role);
            string savedata = Path.Combine(stand, "savedata", profile);
            if (!Directory.Exists(savedata)) throw new InvalidDataException("missing role profile");
            var binding = new BattleBinding(role, $"rimlink-battle-{identity.BattleId:N}-{profile}",
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), identity, stand, game, savedata,
                gameHash, Hash(Path.Combine(game, "Mods", "Rimlink", "Assemblies", "Rimlink.Client.dll")),
                Hash(Path.Combine(game, "BridgeTools", "Rimlink", "Rimlink.BridgeTools.dll")));
            WriteNew(Path.Combine(match, profile + ".json"), binding);
        }
        WriteNew(Path.Combine(match, "prepared.json"), preparation);
        AtomicWrite(active, preparation);
        Console.WriteLine(JsonSerializer.Serialize(new { phase = "Prepared", identity.BattleId, fixture = match }));
        return 0;
    }
    var prepared = Read<BattlePreparation>(active);
    string root = BattleLedger.OwnedRoot(Path.Combine(stand, "battles", prepared.Identity.BattleId.ToString("N")));
    using var current = new BattleLedger(root, prepared.Identity);
    switch (args[0])
    {
        case "serve":
            var bindings = Enum.GetValues<BattleRole>().Select(role => Read<BattleBinding>(Path.Combine(root, RoleName(role) + ".json"))).ToArray();
            using (var cancellation = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "Listening", prepared.Identity.BattleId }));
                await new BattleTransport(current, bindings).Run(cancellation.Token);
            }
            break;
        case "fault":
            if (args.Length == 3) current.RevokeExecutor();
            else WriteNew(BattleFixture.FaultPath(root, args[4]), new { point = args[4] });
            break;
        case "rotate-boundary": current.RotateBoundary(Guid.NewGuid()); break;
    }
    Console.WriteLine(JsonSerializer.Serialize(current.State, BattleContract.Json));
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException)
{
    Console.Error.WriteLine("Prototype refused: " + ex.Message); return 1;
}

static string RoleName(BattleRole role) => role switch { BattleRole.Arbiter => "arbiter", BattleRole.SideA => "side-a", BattleRole.SideB => "side-b", _ => throw new InvalidDataException("unknown role") };
static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), BattleContract.Json) ?? throw new InvalidDataException("empty local record");
static string Hash(string path)
{
    BattleFixture.CheckLocalPath(path);
    using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
static void WriteNew<T>(string path, T value)
{
    BattleFixture.CheckLocalPath(path);
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    JsonSerializer.Serialize(stream, value, BattleContract.Json); stream.Flush(flushToDisk: true);
}
static void AtomicWrite<T>(string path, T value)
{
    string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    WriteNew(temp, value); File.Move(temp, path, overwrite: true);
}
