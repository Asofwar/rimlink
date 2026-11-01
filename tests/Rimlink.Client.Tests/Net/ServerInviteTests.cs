using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public sealed class ServerInviteTests : IDisposable
{
    private const string Pin = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    [Theory]
    [InlineData("EXAMPLE.org", "example.org", 25560)]
    [InlineData("127.0.0.1:443", "127.0.0.1", 443)]
    [InlineData("[2001:0db8::1]", "[2001:db8::1]", 25560)]
    [InlineData("[::1]:25561", "[::1]", 25561)]
    [InlineData("localhost:25560", "localhost", 25560)]
    public void CanonicalRoundTrip(string authority, string host, int port)
    {
        Assert.True(ServerInvite.TryParse($"rimlink://{authority}?fp={Pin.ToLowerInvariant()}", out var invite));
        Assert.Equal(host, invite!.Endpoint.Host);
        Assert.Equal(port, invite.Endpoint.Port);
        Assert.Equal(Pin, invite.Fingerprint);
        Assert.Equal($"rimlink://{host}:{port}?fp={Pin}", invite.Text);
        Assert.True(ServerInvite.TryParse(invite.Text, out var again));
        Assert.Equal(invite, again);
        Assert.Equal(host.Trim('[', ']'), invite.Endpoint.Address.Host.Trim('[', ']'));
    }

    [Theory]
    [InlineData("https://example.org")]
    [InlineData("rimlink://user@example.org")]
    [InlineData("rimlink://example.org/path")]
    [InlineData("rimlink://example.org#fragment")]
    [InlineData("rimlink://example.org:0")]
    [InlineData("rimlink://example.org:65536")]
    [InlineData("rimlink://example.org:+80")]
    [InlineData("rimlink://127.1")]
    [InlineData("rimlink://127.000.0.1")]
    [InlineData("rimlink://0x7f000001")]
    [InlineData("rimlink://2001:db8::1")]
    [InlineData("rimlink://[fe80::1%25eth0]")]
    [InlineData("rimlink://%65xample.org")]
    [InlineData("rimlink://example.org%2F..")]
    [InlineData("rimlink://-example.org")]
    [InlineData("rimlink://example..org")]
    [InlineData("rimlink://a|b")]
    [InlineData("rimlink://пример.рф")]
    [InlineData("rimlink://example.org\n")]
    public void RejectsMalformedAuthority(string prefix) => Assert.False(ServerInvite.TryParse(prefix + "?fp=" + Pin, out _));

    [Fact]
    public void RejectsExtraFieldsEscapesAndOversize()
    {
        string valid = $"rimlink://example.org?fp={Pin}";
        foreach (string bad in new[] { valid + "&fp=" + Pin, valid + "&token=secret", valid + "#x", valid + "/", valid + "\n",
                     valid.Replace("fp=", "fp=%"), valid.Replace(Pin, "G" + Pin[1..]), valid[..^1], "", new string('a', 100_000) })
            Assert.False(ServerInvite.TryParse(bad, out _));
        Assert.False(ServerInvite.TryParse(null, out _));
        Assert.False(Directory.Exists(dir)); // Parsing performs no local writes.
    }

    [Fact]
    public void ImportPersistsButNeverReplacesAnExistingPinEvenAfterReload()
    {
        string file = Path.Combine(dir, "pins.json");
        ServerInvite.TryParse($"rimlink://example.org?fp={Pin}", out var invite);
        var pins = new CertificatePinStore(file);
        Assert.Null(pins.KnownFingerprint(invite!.Endpoint));
        Assert.True(pins.TryTrustInvite(invite));
        Assert.True(pins.TryTrustInvite(invite));
        ServerInvite.TryParse(invite.Text.Replace(Pin, new string('0', 64)), out var conflicting);
        Assert.False(pins.TryTrustInvite(conflicting!));
        Assert.Equal(Pin, new CertificatePinStore(file).KnownFingerprint(invite.Endpoint));
        Assert.Equal(Pin, pins.KnownFingerprint(invite.Endpoint));
    }

    [Fact]
    public void ImportRechecksTrustChangedByAnotherStoreAfterPreview()
    {
        string file = Path.Combine(dir, "pins.json");
        var preview = new CertificatePinStore(file);
        ServerInvite.TryParse($"rimlink://example.org?fp={Pin}", out var invite);
        ServerInvite.TryParse(invite!.Text.Replace(Pin, new string('0', 64)), out var other);
        Assert.True(new CertificatePinStore(file).TryTrustInvite(other!));
        Assert.False(preview.TryTrustInvite(invite));
        Assert.Equal(other!.Fingerprint, new CertificatePinStore(file).KnownFingerprint(invite.Endpoint));
    }

    [Fact]
    public void ImportDoesNotTreatCorruptTrustFileAsEmpty()
    {
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "pins.json");
        File.WriteAllText(file, "broken trust file");
        ServerInvite.TryParse($"rimlink://example.org?fp={Pin}", out var invite);
        var pins = new CertificatePinStore(file);
        Assert.Throws<System.Text.Json.JsonException>(() => pins.TryTrustInvite(invite!));
        Assert.Equal("broken trust file", File.ReadAllText(file));
    }

    [Fact]
    public async Task ConcurrentImportsCannotReplaceEachOthersTrust()
    {
        string file = Path.Combine(dir, "pins.json");
        var stores = Enumerable.Range(0, 12).Select(_ => new CertificatePinStore(file)).ToArray();
        var invites = Enumerable.Range(0, 12).Select(i =>
        {
            Assert.True(ServerInvite.TryParse($"rimlink://example.org?fp={i:X64}", out var invite));
            return invite!;
        }).ToArray();
        var results = await Task.WhenAll(stores.Select((s, i) => Task.Run(() => s.TryTrustInvite(invites[i]))));
        int winner = Assert.Single(results.Select((ok, i) => (ok, i)), r => r.ok).i;
        Assert.Equal(invites[winner].Fingerprint, new CertificatePinStore(file).KnownFingerprint(invites[winner].Endpoint));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}
