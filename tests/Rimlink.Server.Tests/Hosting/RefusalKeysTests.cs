using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Hosting;

/// <summary>Live UX spec §5: a refusal carries a key the client translates, with its arguments.</summary>
public class RefusalKeysTests
{
    private static Dictionary<string, string> Keyed(string language) =>
        XDocument.Load(Path.Combine(RepoPaths.Root, "mod", "Languages", language, "Keyed", "Rimlink.xml")).Root!.Elements()
            .ToDictionary(e => e.Name.LocalName, e => e.Value);

    private static int Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().Count();

    [Fact]
    public void EveryTypedRefusalHasAKey()
    {
        (string File, string Snippet)[] exemptions =
        [
            ("Application/Admin/", ""), // Admin operations are only shown in admin tools.
            ("Rest/", ""), // REST refusals belong to the admin panel and bot, not the game client.
            ("Application/Feed/", ""), // The feed is consumed by the Discord bot.
            ("Grpc/AdminGrpcService.cs", ""), // Only the admin CLI calls this endpoint.
            ("Application/Chat/ChatService.cs", "author must be"), // External bot author validation is never shown to players.
            ("Application/Chat/ChatService.cs", "db.Players.FindAsync(playerId)"), // Mute/Unmute are admin operations.
            ("Application/Live/LiveSessionService.cs", "outcome index"), // Malformed host report indicates a host-client bug.
            ("Application/Live/LiveSessionService.cs", "living visitor"), // Missing host report blob indicates a host-client bug.
            ("Application/Modpack/ModpackService.cs", "ErrorCode.ModpackConfigsInvalid"), // Mod settings uploads are admin operations.
            ("Grpc/RpcErrors.cs", ""), // Helpers build/forward details; authentication helpers are handled by status.
            ("Grpc/SessionInterceptor.cs", ""), // The client handles session authentication failures by status.
        ];
        // These calls forward a structured refusal. Check its producers below rather than exempting player refusals.
        (string File, string Call)[] forwarding =
        [
            ("Application/Economy/MarketService.cs", "RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args)"),
            ("Application/Economy/TransferService.cs", "RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args)"),
            ("Application/Live/LiveSessionService.cs", "RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args)"),
            ("Application/Raids/RaidReportValidator.cs", "RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args)"),
            ("Application/Raids/RaidReportValidator.cs", "RpcErrors.Failed(survivorReason.Code, survivorReason.Key, survivorReason.Message, survivorReason.Args)"),
            ("Application/Raids/RaidService.cs", "RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args)"),
            ("Application/Raids/RaidService.cs", "RpcErrors.Failed(refusal.Code, refusal.Key, refusal.Message, refusal.Args)"),
            ("Application/Trades/TradeService.cs", "RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args)"),
            ("Grpc/AuthGrpcService.cs", "RpcErrors.With(f.Status, f.Code, f.Key, f.Message, f.Args)"),
            ("Grpc/SavesGrpcService.cs", "RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.SaveInvalid, ex.Key, ex.Message, ex.Args)"),
        ];
        var missing = new List<string>();
        string root = Path.Combine(RepoPaths.Root, "src", "Rimlink.Server");
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order())
        {
            string path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (path.StartsWith("obj/") || path.StartsWith("bin/")) continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool producer = Regex.IsMatch(line, @"LoginResult\.Failed\s*\(|new\s+(LoginResult\.Failure|SaveRejected)\s*\(")
                    || (path is "Application/Economy/ParcelValidator.cs" or "Application/Raids/RaidRules.cs" && Regex.IsMatch(line, @"return (new\(|\(ErrorCode\.)"));
                if (!producer && !Regex.IsMatch(line, @"RpcErrors\.(Failed|With)\s*\(|new\s+RpcException\s*\(")) continue;
                string statement = line;
                for (int j = i + 1; !statement.Contains(';') && j < lines.Length; j++) statement += " " + lines[j];
                if (exemptions.Any(e => (e.File.EndsWith('/') ? path.StartsWith(e.File) : path == e.File) && line.Contains(e.Snippet))) continue;
                bool unkeyed = (producer || Regex.IsMatch(statement, @"RpcErrors\.(Failed|With)\s*\("))
                    && !statement.Contains("RefusalKeys.")
                    && !forwarding.Any(f => path == f.File && statement.Contains(f.Call));
                bool raw = Regex.IsMatch(statement, @"new\s+RpcException\s*\(\s*new\s+Status\s*\(\s*StatusCode\.(InvalidArgument|NotFound|PermissionDenied|FailedPrecondition)\b");
                if (unkeyed || raw) missing.Add($"{path}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(missing.Count == 0, "Refusals without keys:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryRefusalKeyHasEnglishAndRussianText()
    {
        var keys = typeof(RefusalKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        var english = Keyed("English");
        var russian = Keyed("Russian");

        Assert.NotEmpty(keys);
        foreach (string key in keys)
        {
            string name = "Rimlink.Refusal." + key;
            Assert.True(english.ContainsKey(name), $"{name} is missing in English");
            Assert.True(russian.ContainsKey(name), $"{name} is missing in Russian");
            Assert.True(Placeholders(english[name]) == Placeholders(russian[name]), $"{name}: placeholders differ between English and Russian");
        }
    }

    private static void AssertText(ErrorDetail detail, string key, params string[] args)
    {
        Assert.Equal(key, detail.Key);
        Assert.Equal(args, detail.Args);
        Assert.Equal(detail.Message, string.Format(CultureInfo.InvariantCulture, Keyed("English")["Rimlink.Refusal." + key], detail.Args.Cast<object>().ToArray()));
        _ = string.Format(CultureInfo.InvariantCulture, Keyed("Russian")["Rimlink.Refusal." + key], detail.Args.Cast<object>().ToArray());
    }

    [Fact]
    public void ParcelCodeKeepsTheExistingReasonBasedMapping()
    {
        var validator = new ParcelValidator(Options.Create(new EconomyOptions()), new ModpackDefs());
        var refusal = validator.Validate(Items(("not in the server modpack", 0)), null)!;

        Assert.Equal(ErrorCode.DefNotInModpack, refusal.Code);
        Assert.Equal(RefusalKeys.ParcelItemCount, refusal.Key);
        Assert.Equal("item 'not in the server modpack' has count 0", refusal.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParcelValidationKeepsItsKeyAndArgumentsThroughTransfer(bool unknownDef)
    {
        using var fixture = EconomyServer();
        var alice = await fixture.RegisterAndLogin("alice");
        using (var scope = fixture.Services.CreateScope())
            if (unknownDef) scope.ServiceProvider.GetRequiredService<ModpackDefs>().Replace(["Steel"]);
        var c = Clients(fixture, alice.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(
            Send(999, Items(("WoodLog", unknownDef ? 1 : 0))), c.Auth).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        var detail = ex.ErrorDetail()!;
        Assert.Equal(unknownDef ? ErrorCode.DefNotInModpack : ErrorCode.ParcelTooBig, detail.Code);
        if (unknownDef) AssertText(detail, "Parcel.DefNotInModpack", "WoodLog");
        else AssertText(detail, "Parcel.ItemCount", "WoodLog", "0");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginBanKeepsItsKeyAndArguments(bool temporary)
    {
        using var fixture = EconomyServer();
        var alice = await fixture.RegisterAndLogin("alice");
        DateTime? until;
        using (var scope = fixture.Services.CreateScope())
            until = (await scope.ServiceProvider.GetRequiredService<BanService>().Ban(new BanRequest(
                Ip: "127.0.0.1", Reason: "test reason", Duration: temporary ? TimeSpan.FromHours(1) : null))).ExpiresAt;
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(new LoginRequest
        {
            ProtocolVersion = Protocol.ProtocolVersion.Current,
            Password = new PasswordCredential { Login = "alice", Password = "pw" },
        }).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(ErrorCode.Banned, ex.ErrorDetail()!.Code);
        if (temporary) AssertText(ex.ErrorDetail()!, "Auth.BannedUntil", until!.Value.ToString("u", CultureInfo.InvariantCulture), "test reason");
        else AssertText(ex.ErrorDetail()!, "Auth.BannedPermanently", "test reason");
    }

    [Fact]
    public async Task SaveSizeRefusalKeepsItsKeyAndArguments()
    {
        using var fixture = EconomyServer();
        var alice = await fixture.RegisterAndLogin("alice");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        using var upload = saves.Upload(Bearer(alice.SessionToken));
        await upload.RequestStream.WriteAsync(new SaveChunk { Header = new SaveHeader { TotalSize = 0 } });
        await upload.RequestStream.CompleteAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => upload.ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(ErrorCode.SaveInvalid, ex.ErrorDetail()!.Code);
        AssertText(ex.ErrorDetail()!, "Saves.Size", "0", Protocol.Limits.MaxSaveBytes.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OwnLiveSessionRefusalHasItsKey()
    {
        using var fixture = LiveServer();
        var nova = await Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.PlayerId);
        await nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveVisit), nova.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveVisit, requestId: "second"), nova.Auth).ResponseAsync);

        var detail = ex.ErrorDetail()!;
        Assert.Equal(("Live.OwnSession", ErrorCode.LiveActive, "you already have a live session"), (detail.Key, detail.Code, detail.Message));
    }

    [Fact]
    public async Task MutedRefusalCarriesTheDate()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var alice = await fixture.RegisterAndLogin("alice");
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatService>().Mute(alice.Player.PlayerId, TimeSpan.FromMinutes(10));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            new Protocol.V1.Chat.ChatClient(fixture.CreateChannel()).SendAsync(new SendRequest { Text = "hi" }, Bearer(alice.SessionToken)).ResponseAsync);

        var detail = ex.ErrorDetail()!;
        Assert.Equal("Chat.Muted", detail.Key);
        Assert.Single(detail.Args);
    }

    [Fact]
    public async Task RateLimitRefusalHasItsKey()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true").WithRateLimits();
        var alice = await fixture.RegisterAndLogin("alice");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        RpcException? refused = null;
        for (int i = 0; i < 10 && refused is null; i++)
        {
            try { await chat.SendAsync(new SendRequest { Text = "hi " + i }, Bearer(alice.SessionToken)); }
            catch (RpcException ex) { refused = ex; }
        }

        Assert.Equal(StatusCode.ResourceExhausted, refused?.StatusCode);
        Assert.Equal("RateLimited", refused!.ErrorDetail()?.Key);
    }
}
