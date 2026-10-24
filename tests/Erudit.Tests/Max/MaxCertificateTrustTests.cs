using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Erudit.Infrastructure.Max;

namespace Erudit.Tests.Max;

public class MaxCertificateTrustTests
{
    [Fact]
    public void Root_IsEmbeddedRussianTrustedRootCa()
    {
        var root = MaxCertificateTrust.RussianTrustedRoot;

        Assert.Equal(MaxCertificateTrust.RootThumbprint, root.Thumbprint);
        Assert.Contains("CN=Russian Trusted Root CA", root.Subject);
        Assert.Equal(root.Subject, root.Issuer); // корневой: выдан сам себе
    }

    [Fact]
    public void Validate_AcceptsCertificateTrustedBySystem()
    {
        Assert.True(MaxCertificateTrust.Validate(null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_RejectsWrongServerName()
    {
        using var certificate = SelfSigned("CN=platform-api2.max.ru");

        Assert.False(MaxCertificateTrust.Validate(certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(MaxCertificateTrust.Validate(certificate, null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Validate_RejectsCertificateNotIssuedByRussianRoot()
    {
        // Чужой сертификат с правильным именем: наш корень его не выдавал — отказ.
        using var certificate = SelfSigned("CN=platform-api2.max.ru");

        Assert.False(MaxCertificateTrust.Validate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
