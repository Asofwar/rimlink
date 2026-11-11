using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;

namespace Rimlink.GuildAcceptanceHost;

internal static class GuildStandProgram
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if(args is [] or ["--help"])
            {Console.WriteLine("GuildCaptureIsolatedAcceptanceV1; Product OFF. prepare <request.json>, provision <root>, inspect <root>, run <root>, admit-slice <root> <evidence.json>, restore <source-root> <target-root> <archive-relative-path> <archive-sha256>, source-identity.");return 0;}
            if(args is ["source-identity"]){Console.WriteLine(OutpostProductQualification.SourceIdentity);return 0;}
            if(args is ["prepare",var request]){Prepare(GuildStandFiles.Read<GuildStandRequest>(Path.GetFullPath(request)));return 0;}
            if(args is ["restore",var sourceRoot,var targetRoot,var archive,var sha256])
            {await GuildAcceptanceComposition.RestoreOfflineAsync(sourceRoot,targetRoot,archive,sha256);return 0;}
            if(args is ["admit-slice",var sliceRoot,var checksPath])
            {
                using var owned=GuildAcceptanceComposition.Open(sliceRoot);var stand=owned.Bootstrap.Stand;
                if(owned.IsRestore)throw new InvalidDataException("guild-stand-restore-scope-fixed");
                var world=GuildStandFiles.Read<GuildStandWorld>(GuildStandFiles.PathIn(stand,"world.json"));
                await using var verified=await RimlinkApplication.BuildIsolatedAsync([],owned);
                var cut=verified.Services.GetRequiredService<OutpostFeature>().GuildCut;
                if(cut?.Acceptance is not{} identity || identity.StandId!=stand.StandId || identity.BootstrapDigest!=owned.BootstrapDigest
                    || cut.WorldId!=world.WorldId || cut.Boundary!=world.Boundary)throw new InvalidDataException("guild-stand-promotion-cut-unverified");
                var checks=GuildStandFiles.Read<GuildStandCheck[]>(Path.GetFullPath(checksPath));GuildStandFiles.Checks(stand,checks,GuildStandFiles.SliceCases,owned.Bootstrap.SourceIdentity);
                GuildStandFiles.Create(GuildStandFiles.PathIn(stand,"slice.json"),new GuildStandSlice(1,stand.StandId,owned.BootstrapDigest,GuildStandFiles.Hash(GuildStandFiles.PathIn(stand,"world.json")),checks));
                Console.WriteLine("Isolated SLICE admission retained; Product OFF.");return 0;
            }
            if(args is not[var command,var root] || command is not("provision" or "inspect" or "run"))throw new InvalidDataException("guild-stand-command-invalid");
            using var composition=GuildAcceptanceComposition.Open(root);var s=composition.Bootstrap.Stand;
            if(command=="inspect")
            {var common=composition.Observe();Console.WriteLine(JsonSerializer.Serialize(new{Mode=GuildStandFiles.Mode,StandId=s.StandId,ProductEnabled=false,common,composition.LastRefusal},GuildStandFiles.Json));return common.AutomatedPrerequisitesVerified&&common.IsolationVerified?0:2;}
            if(command=="provision")
            {
                if(composition.IsRestore)throw new InvalidDataException("guild-stand-restore-scope-fixed");
                string data=GuildStandFiles.PathIn(s,"data"),anchor=GuildStandFiles.PathIn(s,"anchor");
                using var server=new FileStream(Path.Combine(data,"server.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                foreach(string dir in new[]{data,anchor})foreach(string file in Directory.EnumerateFiles(dir,"*",SearchOption.AllDirectories))
                    if(Path.GetFileName(file) is not("server.lock" or "save-storage.lock") || new FileInfo(file).Length!=0)throw new InvalidDataException("guild-stand-provision-not-empty");
                new OutpostAnchorStore(DataDirectory.Existing(data),Options.Create(new OutpostOptions{AnchorDirectory=anchor,StorageContract="sqlite-local-v1"})).Provision();
                Console.WriteLine("New stand physically provisioned; logical authority is not initialized. Product OFF.");return 0;
            }
            Console.WriteLine("GuildCaptureIsolatedAcceptanceV1; Product OFF; stand "+s.StandId);
            await using WebApplication app=await RimlinkApplication.BuildIsolatedAsync([],composition);
            await composition.SynchronizeClockAsync(app.Services,CancellationToken.None);app.MapGuildStandClock();await app.RunAsync();return 0;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or BadImageFormatException or OutpostEvidenceUnavailableException)
        {Console.Error.WriteLine("Isolated stand refused: "+error.Message);return 2;}
    }
    internal static void Prepare(GuildStandRequest stand)
    {
        GuildStandFiles.ValidateLayout(stand,true);
        string lockPath=GuildStandFiles.PathIn(stand,"stand.lock");
        using var owner=new FileStream(lockPath,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        GuildStandFiles.ValidateLayout(stand,true);
        if(stand.ExcludedRoots.Length<3)throw new InvalidDataException("guild-stand-exclusion-inventory-incomplete");
        foreach(string name in new[]{"saves-executor","saves-attacker","saves-defender"})
            foreach(string file in Directory.EnumerateFiles(GuildStandFiles.PathIn(stand,name),"*",SearchOption.AllDirectories))
            {
                string relative=Path.GetRelativePath(GuildStandFiles.PathIn(stand,name),file).Replace('\\','/');
                if(!GuildStandFiles.InitialSaveDataFile(relative))
                    throw new InvalidDataException("guild-stand-save-or-custody-import-refused");
            }
        string source=OutpostProductQualification.SourceIdentity;
        GuildStandFiles.Checks(stand,stand.Prerequisites,GuildStandFiles.HomeCases,source);GuildStandFiles.Binaries(stand);
        foreach(string name in new[]{"data","anchor"})Directory.CreateDirectory(GuildStandFiles.PathIn(stand,name));
        string executorToken=RandomNumberGenerator.GetHexString(64),serverPassword=RandomNumberGenerator.GetHexString(64),adminToken=RandomNumberGenerator.GetHexString(64);
        GuildStandFiles.Create(GuildStandFiles.PathIn(stand,"credentials.json"),new{executorToken,serverPassword,adminToken,attackerPassword=RandomNumberGenerator.GetHexString(64),defenderPassword=RandomNumberGenerator.GetHexString(64)});
        GuildStandFiles.Create(GuildStandFiles.PathIn(stand,"config.json"),new{
            Server=new{DataDir=GuildStandFiles.PathIn(stand,"data"),stand.Port,MaxPlayers=2,ServerPassword=serverPassword},Tls=new{Required=true},
            Auth=new{AllowPassword=true,AdminLogins=new[]{stand.AttackerLogin}},Admin=new{ApiToken=adminToken},
            Outposts=new{Enabled=false,AnchorDirectory=GuildStandFiles.PathIn(stand,"anchor"),StorageContract="sqlite-local-v1"},
            GuildCapture=new{Enabled=false,stand.ExecutorId,ExecutorTokenSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(executorToken))),ApprovedProfileDigest=stand.ProfileDigest}});
        var clockOrigin=DateTime.UtcNow;
        GuildStandFiles.Create(GuildStandFiles.PathIn(stand,"bootstrap.json"),new GuildStandBootstrap(1,GuildStandFiles.Mode,Guid.NewGuid().ToString("D"),source,3,stand,GuildStandFiles.Hash(GuildStandFiles.PathIn(stand,"config.json")),
            new(1,clockOrigin,clockOrigin,0,GuildClockBudget.V1)));
        Console.WriteLine("Owned stand bootstrap written; no database created, no native acceptance claimed, Product OFF.");
    }
}
