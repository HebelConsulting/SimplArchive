using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.Application.Abstractions;
using SimplArchive.Auth;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Verifies AddAuthServer wires the OpenBao-sourced certificates into OpenIddict (ADR "OpenIddict certificates
// from OpenBao"): when the OpenIddict:*Pem config keys are present, resolving the server options (which triggers
// OpenIddict's own certificate validation) succeeds and the configured signing/encryption certs are used —
// rather than the dev certificates.
public class AuthServerCertificateTests
{
    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static (string CertPem, string KeyPem, string Thumbprint) GenerateCert(string cn)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return (cert.ExportCertificatePem(), rsa.ExportRSAPrivateKeyPem(), cert.Thumbprint);
    }

    [Fact]
    public void Uses_the_configured_certificates_when_present()
    {
        var signing = GenerateCert("simplarchive-signing");
        var encryption = GenerateCert("simplarchive-encryption");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenIddict:SigningCertificatePem"] = signing.CertPem,
            ["OpenIddict:SigningKeyPem"] = signing.KeyPem,
            ["OpenIddict:EncryptionCertificatePem"] = encryption.CertPem,
            ["OpenIddict:EncryptionKeyPem"] = encryption.KeyPem,
        }).Build();

        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenantAccessor>(new CurrentTenantAccessor());
        services.AddDbContext<SimplArchiveDbContext>(options => options.UseSqlite(connection));
        services.AddAuthServer(configuration, new StubEnvironment());

        using var provider = services.BuildServiceProvider();

        // Resolving the server options runs OpenIddict's certificate validation — a bad cert would throw here.
        var serverOptions = provider.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

        Assert.Contains(serverOptions.SigningCredentials, c => Thumbprint(c.Key) == signing.Thumbprint);
        Assert.Contains(serverOptions.EncryptionCredentials, c => Thumbprint(c.Key) == encryption.Thumbprint);
    }

    private static string? Thumbprint(SecurityKey key) => (key as X509SecurityKey)?.Certificate.Thumbprint;
}
