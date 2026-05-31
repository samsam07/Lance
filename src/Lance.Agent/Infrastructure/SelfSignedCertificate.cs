using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Lance.Agent.Infrastructure;

internal static class SelfSignedCertificate
{
    internal static X509Certificate2 LoadOrCreate(string certPath)
    {
        if (File.Exists(certPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(certPath, password: null);
        }

        return Generate(certPath);
    }

    private static X509Certificate2 Generate(string certPath)
    {
        using ECDsa key = ECDsa.Create();
        CertificateRequest req = new("CN=lance-agent", key, HashAlgorithmName.SHA256);

        // Backdate by one day to tolerate minor clock skew between agent and client.
        X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx));
        return cert;
    }
}
