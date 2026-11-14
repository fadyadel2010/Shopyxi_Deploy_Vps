using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace PointOfSale.API.Configuration;

/// <summary>Explicit persistent storage and certificate-wrapped production key rings.</summary>
public static class ProductionDataProtection
{
    public static void Configure(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration["DataProtection:KeysPath"];
        var name = configuration["DataProtection:ApplicationName"];
        if (environment.IsProduction())
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path) ||
                string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Production DataProtection requires an existing absolute persistent key directory and explicit application name.");

            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
                (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException("Production DataProtection key directory must not grant access to other users.");
            if (!Directory.EnumerateFiles(path, "key-*.xml", SearchOption.TopDirectoryOnly).Any())
                throw new InvalidOperationException("Production DataProtection requires the preserved existing key ring; empty-directory initialization is not permitted.");

            // One API instance uses a retained local RWO volume. Directory existence
            // alone cannot prove persistence, permissions or off-node backup safety.
            var active = LoadCertificate(configuration.GetSection("DataProtection"), requireCurrent: true);
            var decryptors = new List<X509Certificate2> { active };
            try
            {
                foreach (var retired in configuration.GetSection("DataProtection:DecryptionCertificates").GetChildren())
                    decryptors.Add(LoadCertificate(retired, requireCurrent: false));
            }
            catch
            {
                foreach (var certificate in decryptors) certificate.Dispose();
                throw;
            }

            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(path))
                .SetApplicationName(name)
                .SetDefaultKeyLifetime(TimeSpan.FromDays(90))
                .ProtectKeysWithCertificate(active)
                .UnprotectKeysWithAnyCertificate(decryptors.ToArray());
            // Certificates must remain alive for the lifetime of these registrations.
            // Dispose on container shutdown, never serialize or log private key material.
            services.AddSingleton(_ => new CertificateLifetime(decryptors));
            services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
                .Configure<CertificateLifetime>((_, _) => { });
            return;
        }

        path = string.IsNullOrWhiteSpace(path)
            ? environment.IsEnvironment("Test")
                ? Path.Combine(environment.ContentRootPath, "..", "temp", "test-environment", "data-protection-keys")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PointOfSale", "DataProtection-Keys")
            : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
        Directory.CreateDirectory(path);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(path))
            .SetApplicationName(string.IsNullOrWhiteSpace(name) ? "PointOfSale" : name);
    }

    private static X509Certificate2 LoadCertificate(IConfiguration section, bool requireCurrent)
    {
        X509Certificate2? certificate = null;
        try
        {
            var publicPath = section["CertificatePath"];
            var privatePath = section["PrivateKeyPath"];
            if (string.IsNullOrWhiteSpace(publicPath) || string.IsNullOrWhiteSpace(privatePath) ||
                !Path.IsPathFullyQualified(publicPath) || !Path.IsPathFullyQualified(privatePath))
                throw new InvalidOperationException();
            using var pem = X509Certificate2.CreateFromPemFile(publicPath, privatePath);
            // Windows PEM keys can be ephemeral handles unsuitable for XML decryption.
            // Reimport in memory only; nothing is exported to disk or diagnostics.
            var bytes = pem.Export(X509ContentType.Pkcs12);
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            using var rsa = certificate.GetRSAPrivateKey();
            var now = DateTime.UtcNow;
            if (rsa is null || rsa.KeySize < 2048 ||
                (requireCurrent && (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)))
                throw new InvalidOperationException();
            return certificate;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            certificate?.Dispose();
            // No inner exception: file/crypto parsers can include sensitive input.
            throw new InvalidOperationException("Production DataProtection requires readable certificate/private-key pairs with RSA >= 2048; the active certificate must be current.");
        }
    }

    private sealed class CertificateLifetime(List<X509Certificate2> certificates) : IDisposable
    {
        public void Dispose()
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }
}
