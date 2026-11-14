using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.Tests.Infrastructure;

public sealed class ClientCertificateIdentityTests
{
    [Fact]
    public void ValidDisposablePemHasPrivateKey()
    {
        using var files = new MtlsTestIdentity();
        using var identity = ClientCertificateIdentity.Load(files.CertificatePath, files.PrivateKeyPath);
        Assert.True(identity.Certificate.HasPrivateKey);
    }
    [Fact]
    public void ExpiredIdentityFailsWithoutFileDetails()
    {
        using var files = new MtlsTestIdentity(expired: true);
        var error = Assert.Throws<InvalidOperationException>(() => ClientCertificateIdentity.Load(files.CertificatePath, files.PrivateKeyPath));
        Assert.DoesNotContain(files.DirectoryPath, error.ToString());
        Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData(null, null)]
    [InlineData("relative.pem", "relative.key")]
    public void MissingOrRelativeFilesFailClosed(string? certificate, string? key) =>
        Assert.Throws<InvalidOperationException>(() => ClientCertificateIdentity.Load(certificate, key));
}
