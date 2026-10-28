using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rimlink.Server.Hosting;

public static class SelfSignedCertificate
{
    /// <summary>Loads the PFX at <paramref name="pfxPath"/> or generates a new RSA-2048 self-signed certificate (CN=rimlink, 10 years).</summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=rimlink", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("rimlink");
        sanBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var cert = request.CreateSelfSigned(notBefore, notBefore.AddYears(10));
        byte[] pfx = cert.Export(X509ContentType.Pfx);

        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, pfx);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(pfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }
}
