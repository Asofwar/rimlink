using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rimlink.Discord.Api;
using Rimlink.Discord.Options;
using Xunit;

namespace Rimlink.Discord.Tests.Api;

public class ServerHandlerTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=rimlink", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static string Pin(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    [Fact]
    public void TrustedChainIsAccepted() =>
        Assert.True(ServerHandler.Accepts(new ServerSection(), SelfSigned(), SslPolicyErrors.None));

    [Fact]
    public void PinMatchesAnyCase()
    {
        var cert = SelfSigned();

        Assert.True(ServerHandler.Accepts(new ServerSection { CertificateSha256 = Pin(cert) }, cert, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(ServerHandler.Accepts(new ServerSection { CertificateSha256 = Pin(cert).ToLowerInvariant() }, cert, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void WrongPinIsRefused()
    {
        var cert = SelfSigned();

        Assert.False(ServerHandler.Accepts(new ServerSection { CertificateSha256 = Pin(SelfSigned()) }, cert, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(ServerHandler.Accepts(new ServerSection(), cert, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void InsecureAcceptsAnything() =>
        Assert.True(ServerHandler.Accepts(new ServerSection { Insecure = true }, SelfSigned(), SslPolicyErrors.RemoteCertificateChainErrors));
}
