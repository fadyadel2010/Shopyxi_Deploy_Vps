using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.Tests.Infrastructure;

public sealed class ServerCertificatePolicyTests
{
    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void PlatformIdentityErrorsAreNeverOverridden(SslPolicyErrors error)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.False(new ServerCertificatePolicy(null).Validate(this, certificate, null, error));
    }

    [Fact]
    public void MissingCertificateFailsClosed() =>
        Assert.False(new ServerCertificatePolicy(null).Validate(this, null, null, SslPolicyErrors.None));

    [Fact]
    public void UntrustedSelfSignedCertificateFailsClosed()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.False(new ServerCertificatePolicy(null).Validate(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
