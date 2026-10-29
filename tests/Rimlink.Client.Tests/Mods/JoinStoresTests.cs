using Google.Protobuf;
using Rimlink.Client.Mods;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public class JoinStoresTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    public JoinStoresTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private JoinIntentStore Intents() => new(Path.Combine(dir, "join-intent.json"));

    [Fact]
    public void AFreshIntentIsTakenOnce()
    {
        var store = Intents();
        store.Save(new JoinIntent("127.0.0.1", 25570, "bt-local", "bt-guest", Now));

        var taken = store.Take(Now.AddMinutes(5));

        Assert.Equal(new JoinIntent("127.0.0.1", 25570, "bt-local", "bt-guest", Now), taken);
        Assert.False(File.Exists(Path.Combine(dir, "join-intent.json")));
        Assert.Null(store.Take(Now.AddMinutes(5)));
    }

    [Fact]
    public void AStaleIntentIsDroppedToo()
    {
        var store = Intents();
        store.Save(new JoinIntent("h", 1, "d", null, Now));

        Assert.Null(store.Take(Now + JoinIntentStore.MaxAge + TimeSpan.FromMinutes(1)));
        Assert.False(File.Exists(Path.Combine(dir, "join-intent.json")));
    }

    [Fact]
    public void ABrokenIntentFileIsDroppedWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(dir, "join-intent.json"), "{ not json");

        Assert.Null(Intents().Take(Now));
        Assert.False(File.Exists(Path.Combine(dir, "join-intent.json")));
    }

    [Fact]
    public void NoIntentFileIsNull()
    {
        Assert.Null(Intents().Take(Now));
    }

    private static ModConfig Config(string id, string handle, byte[] content) =>
        new() { PackageId = id, Handle = handle, Content = ByteString.CopyFrom(content), Hash = ModContent.Hash(content) };

    [Fact]
    public void ActivatedSettingsAreWrittenByteForByte()
    {
        var store = new ServerSettingsStore(Path.Combine(dir, "server-settings"));
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. "<SettingsBlock>\r\n  <x>1</x>\r\n</SettingsBlock>"u8.ToArray()];
        string server = ServerSettingsStore.ServerKey("127.0.0.1", 25570);

        store.Activate(server, "bt-local", [Config("a.one", "OneMod", withBom), Config("b.two", "TwoMod", "<b/>"u8.ToArray())]);

        var active = store.ReadActive();
        Assert.NotNull(active);
        Assert.Equal(server, active.Server);
        Assert.Equal("bt-local", active.Display);
        Assert.Equal([new SettingsKey("a.one", "OneMod"), new SettingsKey("b.two", "TwoMod")], active.Mods);
        byte[] written = File.ReadAllBytes(store.PathFor(server, new SettingsKey("a.one", "OneMod")));
        Assert.Equal(withBom, written);
        Assert.Equal(ModContent.Hash(withBom), ModContent.Hash(written));
    }

    [Theory]
    [InlineData("a.one", @"x\..\..\..\Config\ModsConfig")]
    [InlineData("a.one", "x/../../evil")]
    [InlineData(@"..\..\evil", "OneMod")]
    public void UnsafeNamesFromTheServerAreRefusedAndNothingIsWritten(string packageId, string handle)
    {
        string root = Path.Combine(dir, "server-settings");
        var store = new ServerSettingsStore(root);

        Assert.Throws<IOException>(() => store.Activate("s_1", "s", [Config(packageId, handle, "<a/>"u8.ToArray())]));

        Assert.Empty(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
        Assert.Null(store.ReadActive());
    }

    [Fact]
    public void ABrokenActiveFileIsNoSetAndClearForgetsIt()
    {
        string root = Path.Combine(dir, "server-settings");
        var store = new ServerSettingsStore(root);
        store.Activate("s_1", "s", [Config("a.one", "OneMod", "<a/>"u8.ToArray())]);

        store.Clear();
        Assert.Null(store.ReadActive());

        File.WriteAllText(Path.Combine(root, "active.json"), "[[[");
        Assert.Null(store.ReadActive());
    }

    [Fact]
    public void ServerKeysAreSafeFolderNames()
    {
        Assert.Equal("example.org_25560", ServerSettingsStore.ServerKey("Example.org", 25560));
        Assert.Equal("___1__25560", ServerSettingsStore.ServerKey("[::1]", 25560));
    }

    [Fact]
    public void SectionsComeInCategoryOrderWithDetails()
    {
        var diff = new ModpackDiff { OrderDiffers = true };
        diff.Configs.Add(new ModConfig { PackageId = "s.mod", Handle = "SMod" });
        diff.Extra.Add(new ModRef { PackageId = "x.extra" });
        diff.Files.Add(new FileMismatch { PackageId = "f.mod", Version = "1.0" });
        diff.WrongVersion.Add(new VersionMismatch { PackageId = "v.mod", ClientVersion = "2.0", ServerVersion = "1.0", Side = FixSide.Server });
        diff.Missing.Add(new ModpackEntry { PackageId = "m.steam", Version = "1.1", WorkshopId = 42 });
        diff.Missing.Add(new ModpackEntry { PackageId = "m.url", Url = "https://example.org/mod" });

        var sections = ModpackDiffView.Sections(diff);

        Assert.Equal([DiffCategory.Missing, DiffCategory.Version, DiffCategory.Files, DiffCategory.Extra, DiffCategory.Order, DiffCategory.Settings], sections.Select(s => s.Category));
        Assert.Equal(["1.1 (Steam)", "https://example.org/mod"], sections[0].Rows.Select(r => r.Detail));
        Assert.Equal(("2.0 → 1.0", FixSide.Server), (sections[1].Rows[0].Detail, sections[1].Rows[0].Side));
        Assert.Equal("1.0", sections[2].Rows[0].Detail);
        Assert.Empty(sections[4].Rows);
        Assert.Equal(("s.mod", "SMod"), (sections[5].Rows[0].PackageId, sections[5].Rows[0].Detail));
    }

    [Fact]
    public void AnEmptyDiffHasNoSections()
    {
        Assert.Empty(ModpackDiffView.Sections(new ModpackDiff()));
    }

    [Fact]
    public async Task TheScanCacheSkipsModsThatFailToScan()
    {
        string good = Path.Combine(dir, "Good");
        Directory.CreateDirectory(Path.Combine(good, "Defs"));
        File.WriteAllText(Path.Combine(good, "Defs", "a.xml"), "<Defs/>");
        var cache = new ModContentCache();

        cache.Start([("author.good", good), ("author.gone", Path.Combine(dir, "Gone"))]);
        var result = await cache.Ready;

        Assert.Equal(["author.good"], result.Keys);
        Assert.True(result["author.good"].HasDefs);
    }
}
