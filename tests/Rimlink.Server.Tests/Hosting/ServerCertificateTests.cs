using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Hosting;

public class ServerCertificateTests
{
    [Fact]
    public void FingerprintMatchesTheClientPinFormat()
    {
        using var fixture = new ServerFixture();

        var cert = fixture.Services.GetRequiredService<ServerCertificate>();

        Assert.Matches("^[0-9A-F]{64}$", cert.Fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.Certificate.RawData)), cert.Fingerprint);
        Assert.True(cert.SelfSigned);
        Assert.True(File.Exists(Path.Combine(fixture.DataDir, "tls", "server.pfx")));
    }
}
