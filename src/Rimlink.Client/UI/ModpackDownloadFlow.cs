using Rimlink.Client.Mods;
using Rimlink.Client.Game;
using Rimlink.Client.Mods.Delivery;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Verse;

namespace Rimlink.Client.UI;

public static class ModpackDownloadFlow
{
    private static int active;
    private static bool restartRequired;
    public static string? RecoveryError { get; private set; }
    private static string StateRoot => Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink");
    private static ModpackInstaller Installer() => new(GenFilePaths.ModsFolderPath, GenFilePaths.ModsConfigFilePath, StateRoot);
    public static string[] RunningRoots() => LoadedModManager.RunningMods.Where(m => m.assemblies.loadedAssemblies.Count > 0).Select(m => m.RootDir).ToArray();
    public static void RecoverAtStart()
    {
        if (restartRequired) { RecoveryError = "RestartRequired"; return; }
        try
        {
            using var installer = Installer(); string? before = installer.State;
            installer.Recover(RunningRoots());
            restartRequired = before is "Prepared" or "Committing" or "RecoveryRequired";
            RecoveryError = restartRequired ? "RestartRequired" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RecoveryError = ex is DeliveryException d ? d.Code : "Storage"; RimlinkLog.Instance.Warn("modpack recovery required: " + RecoveryError); }
    }
    public static string? JoinBlocker()
    {
        if (Volatile.Read(ref active) != 0) return "Busy";
        RecoverAtStart(); return RecoveryError;
    }
    public static bool HasPending
    {
        get { try { using var installer = Installer(); return installer.State is not null; } catch (IOException) { return true; } }
    }
    public static void Restore()
    {
        try { using var installer = Installer(); installer.Restore(RunningRoots()); restartRequired = true; RecoveryError = "RestartRequired"; Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Delivery.Restored".Translate())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Find.WindowStack.Add(new Dialog_MessageBox(Error(ex))); }
    }
    public static async Task<bool> VerifyLogin(ServerEndpoint endpoint)
    {
        try
        {
            using var installer = Installer(); if (!installer.IsPendingForServer(endpoint.Id)) return true;
            var mods = await ModpackActions.RunningMods();
            var settings = await MainThread.Get(ModpackActions.RunningModSettings);
            var roots = await MainThread.Get(() => LoadedModManager.RunningMods.GroupBy(m => ModpackActions.Normalize(m.PackageId)).ToDictionary(g => g.Key, g => g.First().RootDir));
            bool verified = installer.VerifyAfterRestart(mods, settings, roots, loginSucceeded: true);
            if (verified) RimlinkLog.Instance.Info("downloaded modpack verified after restart and login");
            return verified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RimlinkLog.Instance.Warn("modpack verification pending: " + (ex is DeliveryException d ? d.Code : "Storage")); return false; }
    }
    public static void Run(ServerEndpoint endpoint, string? login = null)
    {
        if (Current.ProgramState != ProgramState.Entry || Interlocked.CompareExchange(ref active, 1, 0) != 0) return;
        var cts = new CancellationTokenSource(); var wait = new Dialog_Wait("Rimlink.Delivery.Prepare".Translate(), cts); Find.WindowStack.Add(wait);
        var roots = RunningRoots();
        var installed = ModLister.AllInstalledMods.Select(m => (Id: m.PackageIdNonUnique, Activation: m.PackageId, Root: m.RootDir.FullName, Version: m.ModVersion ?? "")).ToArray();
        var activeIds = LoadedModManager.RunningMods.Select(m => m.PackageId).ToArray();
        ModpackInstaller installer;
        try { installer = Installer(); }
        catch (Exception ex) { Interlocked.Exchange(ref active, 0); wait.Close(); cts.Dispose(); Find.WindowStack.Add(new Dialog_MessageBox(Error(ex))); return; }
        var pins = new CertificatePinStore(Path.Combine(StateRoot, "pins.json"));
        _ = Task.Run(async () =>
        {
            try
            {
                using var operation = installer.Acquire();
                if (RecoveryError is not null || installer.State is not null) throw new DeliveryException("RecoveryRequired");
                using var client = new ModpackDeliveryClient(endpoint, pins, new GamePinPrompt());
                var capabilities = await client.Capabilities(cts.Token);
                if (!capabilities.DeliveryAvailable) throw new DeliveryException("Unavailable");
                // Download permission does not create an account or bypass the server's authentication policy.
                if (!capabilities.PasswordLoginAvailable && !await MainThread.Get(() => Verse.Steam.SteamManager.Initialized)) throw new DeliveryException("SteamOnly");
                string? password = null;
                if (capabilities.RequiresServerPassword)
                {
                    password = await MainThread.Ask<string?>(tcs => new Dialog_ServerPassword(endpoint.Display, tcs));
                    if (password is null) throw new OperationCanceledException();
                }
                await client.Access(password, cts.Token); password = null;
                var (manifest, settings) = await client.Preview(cts.Token);
                var inventory = installed.Select(m => new InstalledDeliveryMod(ModpackActions.Normalize(m.Id), m.Activation, m.Root, m.Version, ModContent.Scan(m.Root))).ToArray();
                var package = new DownloadedModpack(manifest, settings, ""); var plan = installer.Plan(package, inventory, activeIds);
                foreach (var retire in plan.Retire)
                    if (roots.Any(p => string.Equals(Path.GetFullPath(p), Path.Combine(GenFilePaths.ModsFolderPath, retire.Folder), StringComparison.OrdinalIgnoreCase))) throw new DeliveryException("LoadedMod");
                bool accepted = await MainThread.Ask<bool>(answer => new Dialog_ModpackDownload(endpoint, capabilities, manifest, settings, plan, answer));
                if (!accepted) throw new OperationCanceledException();
                cts.Token.ThrowIfCancellationRequested();
                var progress = new Progress<DeliveryProgress>(p => MainThread.Post(() => wait.Text = ("Rimlink.Delivery.Phase." + p.Phase).Translate((p.Bytes / 1048576.0).ToString("0.0"), (p.Total / 1048576.0).ToString("0.0"))));
                var downloaded = await client.Download(manifest, settings, Path.Combine(StateRoot, "modpack-downloads"), progress, cts.Token);
                var fresh = await client.Preview(cts.Token);
                if (!DeliveryJson.Write(fresh.Manifest).SequenceEqual(DeliveryJson.Write(manifest))) throw new DeliveryException("StaleCatalog");
                var currentRoots = await MainThread.Get(RunningRoots);
                installer.Commit(plan, downloaded, ServerSettingsStore.ServerKey(endpoint.Host, endpoint.Port), endpoint.Display,
                    new JoinIntent(endpoint.Host, endpoint.Port, endpoint.Name, login, DateTime.UtcNow), currentRoots, cts.Token);
                MainThread.Post(() => { wait.Close(); GenCommandLine.Restart(); });
            }
            catch (OperationCanceledException) { MainThread.Post(() => wait.Close()); }
            catch (Exception ex)
            {
                string message = await MainThread.Get(() => Error(ex));
                MainThread.Post(() => { wait.Close(); Find.WindowStack.Add(new Dialog_MessageBox(message)); });
            }
            finally { installer.Dispose(); MainThread.Post(cts.Dispose); Interlocked.Exchange(ref active, 0); }
        });
    }
    public static string Error(Exception ex)
    {
        string code = ex is DeliveryException d ? d.Code : "Storage";
        string key = "Rimlink.Delivery.Error." + code;
        string text = key.CanTranslate() ? key.Translate() : "Rimlink.Delivery.Error.Unknown".Translate(code);
        if (ex is DeliveryException { Code: "Conflict" or "Duplicate" or "ExternalChange" or "LoadedMod" } && ex.Message != code) text += "\n\n" + ex.Message;
        return text;
    }
}
