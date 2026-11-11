using System.Text.Json;
using Rimlink.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Rimlink.OutpostAcceptanceHost;

internal static class AcceptanceProgram
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["help"] or [])
        {
            Console.WriteLine("Rimlink.OutpostAcceptanceHost — ISOLATED ACCEPTANCE, NOT PRODUCT QUALIFIED\nCommands: prepare <stand.json>, provision <stand.json>, inspect <stand.json>, run <stand.json>, restart <stand.json> <previous-run-id> <downtime-ticks>, control <run.control.json> <command.json>\nOffline: offline <stand.json> <invocation.json>, offline-inspect <stand.json> <archive>, offline-backout-inspect <stand.json> <restore-id>\nprepare requires new empty state; provision is explicit stopped empty-stand physical maintenance only; run is reserved for explicitly authorized Task16 acceptance.");
            return 0;
        }
        try
        {
            if (args is ["build-identity"])
            { Console.WriteLine(Rimlink.Server.Application.Outposts.OutpostProductQualification.SourceIdentity); return 0; }
            if (args is ["build-candidate" or "build-seal", var evidence, var output])
            { BuildQualificationStep.Write(args[0], evidence, output); Console.WriteLine("Build artifact written; no product runtime was enabled."); return 0; }
            if (args is ["build-candidate" or "build-seal", var guildEvidence, var guildOutput, "--guild-capture"])
            { BuildQualificationStep.Write(args[0], guildEvidence, guildOutput, guildCapture: true); Console.WriteLine("Guild capture build artifact written."); return 0; }
            if (args is ["control", var bindingPath, var commandPath])
            {
                var binding = AcceptanceStandFiles.Read<AcceptanceControlBinding>(Path.GetFullPath(bindingPath));
                var request = AcceptanceStandFiles.Read<AcceptanceControlCommand>(Path.GetFullPath(commandPath));
                var reply = await AcceptanceControlPipe.SendAsync(binding, request, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(reply, AcceptanceStandFiles.Json)); return reply.Success ? 0 : 2;
            }
            if (args is [var offlineCommand, var offlineDescriptor, var offlineValue]
                && offlineCommand is "offline" or "offline-inspect" or "offline-backout-inspect")
            {
                var offlineStand = ReadStand(offlineDescriptor);
                return offlineCommand == "offline" ? await AcceptanceOffline.RunAsync(offlineStand, offlineValue)
                    : await AcceptanceOffline.InspectAsync(offlineStand, offlineValue, offlineCommand == "offline-backout-inspect");
            }
            string? previous = null; TimeSpan downtime = TimeSpan.Zero;
            if (args is ["restart", var restartDescriptor, var previousId, var ticks])
            {
                if (!long.TryParse(ticks, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value))
                    throw new InvalidDataException("acceptance-restart-downtime-invalid");
                previous = previousId; downtime = TimeSpan.FromTicks(value); args = ["run", restartDescriptor];
            }
            if (args is not [var command, var descriptor] || command is not ("prepare" or "provision" or "inspect" or "run"))
                throw new InvalidDataException("acceptance-command-invalid");
            var stand = ReadStand(descriptor);
            if (command == "prepare")
            {
                AcceptanceStandFiles.PrepareOwner(stand); Console.WriteLine("Owned empty stand prepared; authority is not provisioned or initialized."); return 0;
            }
            if (command == "provision")
            {
                AcceptanceProvision.Execute(stand);
                Console.WriteLine("Owned empty stand physically provisioned; Record=NULL, no logical authority initialized."); return 0;
            }
            using var composition = AcceptanceComposition.Open(stand);
            if (command == "inspect")
            {
                var observed = composition.Observe();
                Console.WriteLine(JsonSerializer.Serialize(new { observed.Mode, observed.State, observed.SourceIdentity,
                    observed.AutomatedPrerequisitesVerified, observed.IsolationVerified, composition.LastRefusal }, AcceptanceStandFiles.Json));
                return observed.AutomatedPrerequisitesVerified && observed.IsolationVerified ? 0 : 2;
            }
            Console.WriteLine("ISOLATED ACCEPTANCE — NOT PRODUCT QUALIFIED");
            using var runtime = new AcceptanceRuntime(stand, composition, previous, downtime);
            composition.Runtime = runtime;
            WebApplication? application = null;
            try
            {
                application = await RimlinkApplication.BuildIsolatedAsync([], composition);
                await runtime.BindAsync(application);
            }
            catch (Exception error)
            {
                try { runtime.RecordStartupRefusal(error); }
                finally { if (application is not null) await application.DisposeAsync(); }
                throw;
            }
            await using var app = application;
            using var stop = new CancellationTokenSource();
            var listener = new AcceptanceControlPipe(runtime.Binding, runtime).ServeAsync(stop.Token);
            Console.WriteLine("Private control binding: " + runtime.BindingPath);
            try
            {
                await app.StartAsync();
                var shutdown = app.WaitForShutdownAsync();
                if (await Task.WhenAny(shutdown, listener) == listener) { app.Lifetime.StopApplication(); await listener; }
                await shutdown;
            }
            finally
            {
                stop.Cancel();
                try { await listener; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                await app.StopAsync();
            }
            return 0;
        }
        catch (OperationCanceledException)
        { Console.Error.WriteLine("Acceptance execution cancelled; retain all evidence and maintenance fences. No no-effect assertion."); return 2; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        { Console.Error.WriteLine("Acceptance host refused: " + error.Message); return 2; }
    }
    private static AcceptanceStand ReadStand(string descriptor)
    {
        var stand = AcceptanceStandFiles.Read<AcceptanceStand>(Path.GetFullPath(descriptor));
        if (!Path.GetFullPath(descriptor).Equals(Path.Combine(stand.Root, "stand.json"), AcceptancePaths.Comparison))
            throw new InvalidDataException("acceptance-descriptor-location-invalid");
        return stand;
    }
}
