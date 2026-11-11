using System.CommandLine;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Rimlink.Protocol;

namespace Rimlink.Tools.Commands;

/// <summary>
/// `rimlink-tools arbiter setup|run`: the guild battle arbiter as an unattended background process. The command keeps a copy
/// of the game of its own (no Steam app id, the modpack's mods inside it), captures and publishes the arbiter profile whenever
/// the game or the modpack changes, and runs the arbiter without a window, restarting it when it exits.
/// </summary>
public static class ArbiterCommand
{
    private sealed record Settings(string Game, string Workshop, string Server, string TokenFile, bool Insecure, string Executor);
    private sealed record State(string Fingerprint, string ProfileDigest, string ProfileDirectory);
    private sealed record ModSource(string PackageId, string? Directory);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Dictionary<Process, StreamWriter> Logs = [];

    public static Command Build()
    {
        var home = new Option<string>("--home") { Description = "Folder the arbiter owns: its copy of the game, save data, profiles, secret and logs", Required = true };
        var game = new Option<string>("--game") { Description = "The installed RimWorld folder to mirror (the players' build)" };
        var workshop = new Option<string>("--workshop") { Description = "Steam Workshop content folder of RimWorld (…/workshop/content/294100), if mods come from it" };
        var server = new Option<string>("--server") { Description = "Server base URL, e.g. https://host:25560" };
        var tokenFile = new Option<string>("--token-file") { Description = "File holding Admin:ApiToken, or an env file with RIMLINK_API_TOKEN=…" };
        var insecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate (the arbiter still pins it)" };
        var executor = new Option<string>("--executor") { Description = "GuildCapture:ExecutorId of the server" };

        var setup = new Command("setup", "Prepare or refresh the arbiter: mirror the game and the modpack's mods, capture and publish the profile") { home, game, workshop, server, tokenFile, insecure, executor };
        setup.SetAction(async parse =>
        {
            string root = Path.GetFullPath(parse.GetValue(home)!);
            Directory.CreateDirectory(root);
            var settings = Merge(root, parse.GetValue(game), parse.GetValue(workshop), parse.GetValue(server), parse.GetValue(tokenFile),
                parse.GetValue(insecure), parse.GetValue(executor));
            await Refresh(root, settings, force: false);
            return 0;
        });
        var run = new Command("run", "Keep the arbiter running in the background: refresh it when the game or the modpack changes, restart it when it exits") { home };
        run.SetAction(async (parse, ct) =>
        {
            string root = Path.GetFullPath(parse.GetValue(home)!);
            await Supervise(root, ReadSettings(root) ?? throw new InvalidOperationException("run `arbiter setup` first"), ct);
            return 0;
        });
        return new Command("arbiter", "Guild battle arbiter as a background process (no window, no Steam)") { setup, run };
    }

    private static Settings Merge(string root, string? game, string? workshop, string? server, string? tokenFile, bool insecure, string? executor)
    {
        var saved = ReadSettings(root);
        var settings = new Settings(Full(game) ?? saved?.Game ?? throw Missing("--game"), Full(workshop) ?? saved?.Workshop ?? "",
            server ?? saved?.Server ?? throw Missing("--server"), Full(tokenFile) ?? saved?.TokenFile ?? throw Missing("--token-file"),
            insecure || saved?.Insecure == true, executor ?? saved?.Executor ?? throw Missing("--executor"));
        File.WriteAllText(Path.Combine(root, "arbiter.json"), JsonSerializer.Serialize(settings, Json));
        return settings;
        static string? Full(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        static InvalidOperationException Missing(string option) => new(option + " is required on the first setup");
    }

    private static Settings? ReadSettings(string root)
    {
        string file = Path.Combine(root, "arbiter.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) : null;
    }

    private static string Token(Settings settings)
    {
        foreach (string line in File.ReadAllLines(settings.TokenFile))
            if (line.StartsWith("RIMLINK_API_TOKEN=", StringComparison.Ordinal)) return line["RIMLINK_API_TOKEN=".Length..].Trim();
        return File.ReadAllText(settings.TokenFile).Trim();
    }

    private static RestClient Rest(Settings settings) => new(settings.Server, Token(settings), settings.Insecure, TimeSpan.FromMinutes(5));

    /// <summary>What the arbiter must run, read from the server's modpack and this machine's files.</summary>
    private static async Task<(string Fingerprint, List<ModSource> Mods)> Plan(Settings settings)
    {
        using var rest = Rest(settings);
        using var modpack = JsonDocument.Parse(await rest.Get("modpack"));
        var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string parent in new[] { Path.Combine(settings.Game, "Mods"), settings.Workshop })
            if (parent.Length != 0 && Directory.Exists(parent))
                foreach (string directory in Directory.GetDirectories(parent))
                    if (PackageId(directory) is { } id) (candidates.TryGetValue(id, out var list) ? list : candidates[id] = []).Add(directory);
        if (modpack.RootElement.GetProperty("entries").GetArrayLength() == 0)
            throw new InvalidOperationException("the server has no modpack: import one first (rimlink-tools modpack import), the arbiter runs exactly its mods");
        var mods = new List<ModSource>();
        var print = new StringBuilder(File.ReadAllText(Path.Combine(settings.Game, "Version.txt")).Trim()).Append('\n')
            .Append(FileHash(Path.Combine(settings.Game, "Assembly-CSharp.dll"))).Append('\n');
        foreach (var entry in modpack.RootElement.GetProperty("entries").EnumerateArray().OrderBy(e => e.GetProperty("position").GetInt32()))
        {
            string id = entry.GetProperty("packageId").GetString()!.ToLowerInvariant();
            if (id.StartsWith("ludeon.rimworld", StringComparison.Ordinal)) { mods.Add(new(id, null)); print.Append(id).Append('\n'); continue; }
            string expected = entry.GetProperty("filesHash").GetString() ?? "";
            long workshopId = entry.GetProperty("workshopId").GetInt64();
            var found = candidates.GetValueOrDefault(id) ?? [];
            if (workshopId != 0 && settings.Workshop.Length != 0 && Directory.Exists(Path.Combine(settings.Workshop, workshopId.ToString())))
                found = [.. found, Path.Combine(settings.Workshop, workshopId.ToString())];
            // The server's modpack is the authority: take the local copy whose gameplay content it names.
            string source = found.Distinct().FirstOrDefault(d => expected.Length == 0 || ModContent.Scan(d).FilesHash == expected)
                ?? throw new InvalidOperationException($"mod {id}: no copy on this machine matches the server's modpack ({found.Count} found); update the mod or the modpack");
            mods.Add(new(id, source));
            print.Append(id).Append(' ').Append(ModContent.Scan(source).FilesHash).Append(' ').Append(Version(source)).Append('\n');
            if (id == ModContent.RimlinkPackageId)
                foreach (string dll in Directory.GetFiles(Path.Combine(source, "Assemblies"), "Rimlink.*.dll").Order(StringComparer.Ordinal))
                    print.Append(FileHash(dll)).Append('\n');
        }
        return (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(print.ToString()))), mods);
    }

    private static async Task<State> Refresh(string root, Settings settings, bool force)
    {
        var (fingerprint, mods) = await Plan(settings);
        string stateFile = Path.Combine(root, "state.json");
        var state = File.Exists(stateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile)) : null;
        if (!force && state is not null && state.Fingerprint == fingerprint && File.Exists(Path.Combine(root, "executor-settings.json")))
        { Console.WriteLine("arbiter is current: profile " + state.ProfileDigest[..12]); return state; }

        string game = Path.Combine(root, "game"), saves = Path.Combine(root, "save");
        Console.WriteLine("mirroring the game into " + game);
        Mirror(settings.Game, game, top => top is "Mods" or "steam_appid.txt");
        string modsRoot = Path.Combine(game, "Mods"); Directory.CreateDirectory(modsRoot);
        foreach (var mod in mods.Where(m => m.Directory is not null)) Mirror(mod.Directory!, Path.Combine(modsRoot, mod.PackageId), _ => false);
        foreach (string extra in Directory.GetDirectories(modsRoot).Where(d => !mods.Any(m => m.Directory is not null && m.PackageId == Path.GetFileName(d))))
            Directory.Delete(extra, true);
        Directory.CreateDirectory(Path.Combine(saves, "Config"));
        File.WriteAllText(Path.Combine(saves, "Config", "ModsConfig.xml"), new XDocument(new XElement("ModsConfigData",
            new XElement("version", File.ReadAllText(Path.Combine(settings.Game, "Version.txt")).Trim()),
            new XElement("activeMods", mods.Select(m => new XElement("li", m.PackageId))),
            new XElement("knownExpansions", mods.Where(m => m.Directory is null && m.PackageId != "ludeon.rimworld").Select(m => new XElement("li", m.PackageId))))).ToString());

        string tokenFile = Path.Combine(root, "executor-token.private.txt");
        if (!File.Exists(tokenFile)) File.WriteAllText(tokenFile, RandomNumberGenerator.GetHexString(64, lowercase: true), Encoding.ASCII);
        Console.WriteLine("GuildCapture:ExecutorTokenSha256 = " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(tokenFile))));

        string profile = Path.Combine(root, "profiles", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(profile);
        string previous = state is null ? "" : Path.Combine(state.ProfileDirectory, "template.rws");
        if (File.Exists(previous)) File.Copy(previous, Path.Combine(profile, "template.rws")); // the map is reusable; only builds and mods changed
        Console.WriteLine("capturing the arbiter profile (the game starts without a window)");
        await Capture(root, profile);
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "profile-capture.json")));
        string digest = capture.RootElement.GetProperty("ProfileDigest").GetString()!;

        using var rest = Rest(settings);
        using var approval = JsonDocument.Parse(await AdminCommand.Run(rest, "guild-capture",
            ["profile", "publish", profile, settings.Executor, "Arbiter profile for " + capture.RootElement.GetProperty("GameVersion").GetString()]));
        if (approval.RootElement.GetProperty("profileDigest").GetString() != digest) throw new InvalidOperationException("the server approved another profile");
        using var status = JsonDocument.Parse(await rest.Get("guild-capture"));
        var scope = status.RootElement.GetProperty("scope");
        var executor = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(profile, "executor-settings.json")))!;
        void Set(string key, string value) => executor[key] = JsonSerializer.SerializeToElement(value);
        Set("ExecutorId", settings.Executor); Set("WorldInstanceId", scope.GetProperty("worldId").GetString()!);
        Set("RestoreBoundary", scope.GetProperty("boundary").GetString()!); Set("Endpoint", settings.Server.TrimEnd('/') + "/");
        Set("CertificateSha256", await CertificateSha256(settings.Server)); Set("CredentialPath", tokenFile);
        File.WriteAllText(Path.Combine(root, "executor-settings.json"), JsonSerializer.Serialize(executor, Json));
        state = new(fingerprint, digest, profile);
        File.WriteAllText(stateFile, JsonSerializer.Serialize(state, Json));
        Console.WriteLine("arbiter profile published: " + digest);
        return state;
    }

    private static async Task Capture(string root, string profile)
    {
        string log = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "logs")).FullName, "capture.log");
        using var process = Start(root, log, "-rimlinkExecutorCapture=" + profile);
        try
        {
            var limit = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(profile, "profile-capture.json")))
            {
                if (process.HasExited || limit.Elapsed > TimeSpan.FromMinutes(15) || File.Exists(log) && ReadShared(log).Contains("arbiter profile capture failed", StringComparison.Ordinal)
                    || File.Exists(log) && ReadShared(log).Contains("arbiter template failed", StringComparison.Ordinal))
                    throw new InvalidOperationException("the profile capture did not finish; see " + log);
                await Task.Delay(1000);
            }
            await Task.Delay(1000); // the summary is the last file written
        }
        finally { Stop(process); }
    }

    private static async Task Supervise(string root, Settings settings, CancellationToken ct)
    {
        foreach (var stale in Running(root)) { stale.Kill(entireProcessTree: true); stale.WaitForExit(); }
        Process? arbiter = null;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (arbiter is not null) Stop(arbiter); };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Refresh(root, settings, force: false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Keep serving on the profile we have: a modpack that is ahead of this machine must not take the arbiter down.
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " refresh failed: " + error.Message);
                    if (!File.Exists(Path.Combine(root, "executor-settings.json"))) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
                }
                string log = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "logs")).FullName, "arbiter.log");
                if (File.Exists(log)) File.Move(log, Path.Combine(root, "logs", "arbiter.previous.log"), true);
                arbiter = Start(root, log, "-rimlinkExecutorConfig=" + Path.Combine(root, "executor-settings.json"));
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " arbiter started, pid " + arbiter.Id);
                var check = Stopwatch.StartNew();
                while (!arbiter.HasExited && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { });
                    if (check.Elapsed < TimeSpan.FromMinutes(10)) continue;
                    check.Restart();
                    try
                    {
                        string current = JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(root, "state.json")))!.Fingerprint;
                        if ((await Plan(settings)).Fingerprint == current || await Busy(settings)) continue;
                        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " the game or the modpack changed: refreshing the arbiter");
                        break;
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " check failed: " + error.Message); }
                }
                bool crashed = arbiter.HasExited;
                Stop(arbiter); arbiter.Dispose(); arbiter = null;
                if (crashed && !ct.IsCancellationRequested)
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " arbiter exited; restarting in 10 s");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => { });
                }
            }
        }
        finally { if (arbiter is not null) Stop(arbiter); }
    }

    /// <summary>An arbiter with a battle in hand is never restarted for an update.</summary>
    private static async Task<bool> Busy(Settings settings)
    {
        using var rest = Rest(settings);
        using var status = JsonDocument.Parse(await rest.Get("guild-capture"));
        return status.RootElement.GetProperty("battles").GetArrayLength() != 0;
    }

    private static Process Start(string root, string log, string argument)
    {
        string game = Path.Combine(root, "game");
        var info = new ProcessStartInfo(Path.Combine(game, OperatingSystem.IsWindows() ? "RimWorld.exe" : "RimWorld"))
        { WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "-savedatafolder=" + Path.Combine(root, "save"), argument, "-Headless", "-NoFileLog", "-NoCrashDialog" }) info.ArgumentList.Add(value);
        var process = Process.Start(info) ?? throw new InvalidOperationException("the game did not start");
        var writer = new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => Write(writer, e.Data);
        process.ErrorDataReceived += (_, e) => Write(writer, e.Data);
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        lock (Logs) Logs[process] = writer;
        return process;
    }

    private static void Write(StreamWriter writer, string? line)
    {
        if (line is null) return;
        lock (writer) { try { writer.WriteLine(line); } catch (ObjectDisposedException) { } }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(10000); } }
        catch (InvalidOperationException) { }
        // The log file is this process's: release it, or the next start cannot rotate it.
        StreamWriter? writer;
        lock (Logs) { Logs.Remove(process, out writer); }
        if (writer is not null) lock (writer) writer.Dispose();
    }

    private static IEnumerable<Process> Running(string root)
    {
        string game = Path.Combine(root, "game");
        foreach (var process in Process.GetProcessesByName("RimWorld"))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            if (path is not null && Path.GetFullPath(path).StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) yield return process;
        }
    }

    /// <summary>Makes <paramref name="target"/> the same tree as <paramref name="source"/>, copying only what differs.</summary>
    public static void Mirror(string source, string target, Func<string, bool> skipTop)
    {
        Directory.CreateDirectory(target);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in Directory.GetFileSystemEntries(source))
        {
            string name = Path.GetFileName(entry);
            if (skipTop(name)) continue;
            names.Add(name);
            string destination = Path.Combine(target, name);
            if (Directory.Exists(entry)) { if (File.Exists(destination)) File.Delete(destination); Mirror(entry, destination, _ => false); continue; }
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            var from = new FileInfo(entry); var to = new FileInfo(destination);
            if (!to.Exists || to.Length != from.Length || to.LastWriteTimeUtc != from.LastWriteTimeUtc) File.Copy(entry, destination, true);
        }
        foreach (string entry in Directory.GetFileSystemEntries(target))
        {
            string name = Path.GetFileName(entry);
            if (names.Contains(name)) continue;
            // A skipped folder (the arbiter's own Mods) is filled separately and stays; a skipped file must not exist in the copy.
            if (Directory.Exists(entry)) { if (!skipTop(name)) Directory.Delete(entry, true); }
            else File.Delete(entry);
        }
    }

    private static string? PackageId(string modDirectory)
    {
        string about = Path.Combine(modDirectory, "About", "About.xml");
        try { return File.Exists(about) ? XDocument.Load(about).Root?.Element("packageId")?.Value.Trim().ToLowerInvariant() : null; }
        catch (System.Xml.XmlException) { return null; }
    }

    private static string Version(string modDirectory)
    {
        try { return XDocument.Load(Path.Combine(modDirectory, "About", "About.xml")).Root?.Element("modVersion")?.Value.Trim() ?? ""; }
        catch (System.Xml.XmlException) { return ""; }
    }

    private static string FileHash(string file)
    {
        using var input = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static string ReadShared(string file)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }

    private static async Task<string> CertificateSha256(string server)
    {
        var uri = new Uri(server);
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port);
        // Reading the certificate the server presents, to pin it for the arbiter; trust is the pin, not a chain.
        using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(uri.Host);
        return Convert.ToHexStringLower(SHA256.HashData(tls.RemoteCertificate!.GetRawCertData()));
    }
}
