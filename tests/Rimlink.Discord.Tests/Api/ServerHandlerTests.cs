using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rimlink.Discord.Api;
using Rimlink.Discord.Options;
using Xunit;

namespace Rimlink.Discord.Tests.Api;

public class ServerHandlerTests
{
    private static X509Certificate2 SelfSigned(string name = "rimlink")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void RealCertificateIsCheckedAgainstItsPublicName()
    {
        // Compose reaches the server as https://rimlink:25560; a real certificate is issued for the public name.
        var cert = SelfSigned("play.example.com");
        var server = new ServerSection { CertificateName = "play.example.com" };

        Assert.True(ServerHandler.Accepts(server, cert, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(ServerHandler.Accepts(new ServerSection { CertificateName = "other.example.com" }, cert, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(ServerHandler.Accepts(server, cert, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(ServerHandler.Accepts(new ServerSection(), cert, SslPolicyErrors.RemoteCertificateNameMismatch));
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
