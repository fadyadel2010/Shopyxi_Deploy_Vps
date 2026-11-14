using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PointOfSale.Infrastructure.Configuration;

/// <summary>File-only mTLS identity. No private bytes or parser exceptions reach diagnostics.</summary>
public sealed class ClientCertificateIdentity : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private ClientCertificateIdentity(X509Certificate2 certificate) => _certificate = certificate;

    public X509Certificate2 Certificate
    {
        get
        {
            var now = DateTime.UtcNow;
            if (_certificate.NotBefore.ToUniversalTime() > now || _certificate.NotAfter.ToUniversalTime() <= now)
                throw new InvalidOperationException("TLS client identity is not current.");
            return _certificate;
        }
    }

    public static ClientCertificateIdentity Load(string? certificatePath, string? privateKeyPath)
    {
        X509Certificate2? certificate = null;
        try
        {
            if (string.IsNullOrWhiteSpace(certificatePath) || string.IsNullOrWhiteSpace(privateKeyPath) ||
                !Path.IsPathFullyQualified(certificatePath) || !Path.IsPathFullyQualified(privateKeyPath))
                throw new InvalidOperationException();
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(privateKeyPath) &
                (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.GroupWrite)) != 0)
                throw new InvalidOperationException();
            using var pem = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
            var bytes = pem.Export(X509ContentType.Pkcs12);
            try { certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            using var rsa = certificate.GetRSAPrivateKey();
            using var ec = certificate.GetECDsaPrivateKey();
            if ((rsa is null || rsa.KeySize < 2048) && (ec is null || ec.KeySize < 256)) throw new InvalidOperationException();
            if (!certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e =>
                    e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.2")))
                throw new InvalidOperationException();
            var identity = new ClientCertificateIdentity(certificate);
            _ = identity.Certificate;
            return identity;
        }
        catch
        {
            certificate?.Dispose();
            throw new InvalidOperationException("TLS client identity requires readable absolute PEM certificate/key files, a matching strong private key, clientAuth usage and current validity; values are not reported.");
        }
    }

    public void Dispose() => _certificate.Dispose();
}
