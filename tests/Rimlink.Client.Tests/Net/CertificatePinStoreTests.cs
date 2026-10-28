using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class CertificatePinStoreTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"), "pins.json");
    private static readonly ServerEndpoint Endpoint = new("example.org", 25560);

    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); } catch (IOException) { } }

    private static X509Certificate2 MakeCert(string cn)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void FirstSeenThenTrustedThenChanged()
    {
        var store = new CertificatePinStore(file);
        using var first = MakeCert("one");
        using var second = MakeCert("two");

        var seen = store.Check(Endpoint, first);
        Assert.Equal(PinStatus.FirstSeen, seen.Status);
        Assert.Equal(CertificatePinStore.Fingerprint(first), seen.Fingerprint);

        store.Save(Endpoint, seen.Fingerprint);
        Assert.Equal(PinStatus.Trusted, store.Check(Endpoint, first).Status);

        var changed = store.Check(Endpoint, second);
        Assert.Equal(PinStatus.Changed, changed.Status);
        Assert.Equal(CertificatePinStore.Fingerprint(first), changed.Previous);
    }

    [Fact]
    public void PinsSurviveReload()
    {
        using var cert = MakeCert("one");
        new CertificatePinStore(file).Save(Endpoint, CertificatePinStore.Fingerprint(cert));

        Assert.Equal(PinStatus.Trusted, new CertificatePinStore(file).Check(Endpoint, cert).Status);
        Assert.Equal(PinStatus.FirstSeen, new CertificatePinStore(file).Check(new ServerEndpoint("other.org", 25560), cert).Status);
    }

    [Fact]
    public void FingerprintIsSha256HexOfDer()
    {
        using var cert = MakeCert("one");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.RawData)), CertificatePinStore.Fingerprint(cert));
    }
}
