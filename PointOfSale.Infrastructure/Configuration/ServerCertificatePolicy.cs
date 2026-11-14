using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PointOfSale.Infrastructure.Configuration;

/// <summary>
/// Server TLS verification with optional explicit PEM trust anchors. Hostname
/// checks remain the TLS platform's responsibility and are never overridden.
/// Revocation is checked online, excluding the root; unknown/offline status
/// fails closed. Private CAs must publish reachable revocation information.
/// </summary>
public sealed class ServerCertificatePolicy
{
    private readonly byte[][] _roots;

    public ServerCertificatePolicy(string? caCertificatePath)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
            if (!string.IsNullOrWhiteSpace(caCertificatePath))
            {
                certificates.ImportFromPemFile(caCertificatePath);
                if (certificates.Count == 0 || certificates.Cast<X509Certificate2>().Any(c =>
                    !c.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)))
                    throw new InvalidOperationException("TLS CA file must contain PEM CA certificates.");
            }

            // Keep only public DER bytes. Every validation owns/disposes its
            // certificates and chain; no captured native certificate lifetime.
            _roots = certificates.Cast<X509Certificate2>().Select(c => c.RawData).ToArray();
        }
        catch
        {
            throw new InvalidOperationException("TLS trust requires a readable PEM CA bundle; input values are not reported.");
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? suppliedChain, SslPolicyErrors errors)
    {
        if (certificate is null ||
            (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;

        var roots = new List<X509Certificate2>();
        try
        {
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(3);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // serverAuth
            if (_roots.Length > 0)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (var bytes in _roots)
                {
                    var root = X509CertificateLoader.LoadCertificate(bytes);
                    roots.Add(root);
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                }
            }

            if (suppliedChain is not null)
                foreach (var element in suppliedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);

            return chain.Build(leaf);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var root in roots) root.Dispose();
        }
    }
}
