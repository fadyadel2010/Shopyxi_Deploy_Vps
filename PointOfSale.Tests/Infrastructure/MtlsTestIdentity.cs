using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PointOfSale.Tests.Infrastructure;

/// <summary>Disposable test identity only; never reads production configuration.</summary>
internal sealed class MtlsTestIdentity : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "shopixy-app43b-" + Guid.NewGuid().ToString("N"));
    public string CertificatePath => Path.Combine(DirectoryPath, "certificate.pem");
    public string PrivateKeyPath => Path.Combine(DirectoryPath, "private-key.pem");
    public MtlsTestIdentity(bool expired = false)
    {
        Directory.CreateDirectory(DirectoryPath);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=APP43B-disposable", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 2));
        File.WriteAllText(CertificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(PrivateKeyPath, key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(PrivateKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
