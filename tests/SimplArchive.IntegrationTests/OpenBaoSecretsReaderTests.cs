using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SimplArchive.Api.Configuration;
using SimplArchive.Auth;

namespace SimplArchive.IntegrationTests;

// Verifies OpenBaoSecretsReader's HTTP flow + mapping (ADR "Secrets management with OpenBao") against a fake
// OpenBao — AppRole login, KV v2 reads, and a dynamic Postgres credential composed into the connection string.
// The real OpenBao integration (a live container, AppRole + database engine) is covered by OpenBaoSecretsTests
// in the E2E project.
public class OpenBaoSecretsReaderTests
{
    private sealed class FakeOpenBaoHandler : HttpMessageHandler
    {
        public string? SentRoleId { get; private set; }
        public string? SentSecretId { get; private set; }
        public string? SentToken { get; private set; }
        public bool IncludeOwner { get; init; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Headers.TryGetValues("X-Vault-Token", out var tokens))
            {
                SentToken = tokens.First();
            }

            string body;
            if (path.EndsWith("/v1/auth/approle/login"))
            {
                var login = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = System.Text.Json.JsonDocument.Parse(login);
                SentRoleId = doc.RootElement.GetProperty("role_id").GetString();
                SentSecretId = doc.RootElement.GetProperty("secret_id").GetString();
                body = """{"auth":{"client_token":"test-token"}}""";
            }
            else if (path.EndsWith("/v1/secret/data/simplarchive/objectstorage"))
            {
                body = """{"data":{"data":{"accessKey":"AK123","secretKey":"SK456"}}}""";
            }
            else if (path.EndsWith("/v1/secret/data/simplarchive/smtp"))
            {
                body = """{"data":{"data":{"user":"smtp-user","password":"smtp-pass"}}}""";
            }
            else if (path.EndsWith("/v1/secret/data/simplarchive/bootstrap"))
            {
                body = """{"data":{"data":{"clientSecret":"boot-secret"}}}""";
            }
            else if (path.EndsWith("/v1/database/creds/simplarchive"))
            {
                body = """{"data":{"username":"v-role-abc","password":"dyn-pass-xyz"}}""";
            }
            else if (IncludeOwner && path.EndsWith("/v1/database/static-creds/simplarchive-owner"))
            {
                body = """{"data":{"username":"simplarchive","password":"owner-pass","rotation_period":86400,"ttl":80000}}""";
            }
            else if (path.EndsWith("/v1/secret/data/simplarchive/openiddict"))
            {
                // Two PKI-issue JSON blobs (as openbao-init stores them), each carrying a real cert + key PEM.
                body = JsonSerializer.Serialize(new { data = new { data = new { signing = PkiIssueJson(), encryption = PkiIssueJson() } } });
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Reads_kv_secrets_and_composes_the_connection_string_from_a_dynamic_credential()
    {
        var handler = new FakeOpenBaoHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://openbao.test:8200/") };
        var options = new OpenBaoOptions
        {
            Address = "http://openbao.test:8200",
            RoleId = "the-role",
            SecretId = "the-secret",
            KvMount = "secret",
            DatabaseRole = "simplarchive",
            DatabaseOwnerStaticRole = "simplarchive-owner",
            DatabaseConnectionTemplate = "Host=db;Port=5432;Database=simplarchive",
        };

        var data = await new OpenBaoSecretsReader(http, options).ReadAsync();

        // AppRole login used the configured ids; subsequent reads carried the returned token.
        Assert.Equal("the-role", handler.SentRoleId);
        Assert.Equal("the-secret", handler.SentSecretId);
        Assert.Equal("test-token", handler.SentToken);

        // Static KV secrets mapped to their config keys.
        Assert.Equal("AK123", data["ObjectStorage:AccessKey"]);
        Assert.Equal("SK456", data["ObjectStorage:SecretKey"]);
        Assert.Equal("smtp-user", data["Smtp:User"]);
        Assert.Equal("smtp-pass", data["Smtp:Password"]);
        Assert.Equal("boot-secret", data["Bootstrap:PlatformAdministrator:ClientSecret"]);

        // Dynamic Postgres credential composed with the non-secret template.
        Assert.Equal("Host=db;Port=5432;Database=simplarchive;Username=v-role-abc;Password=dyn-pass-xyz", data["ConnectionStrings:Default"]);

        // The migration-owner credential — the OpenBao-rotated static-role password — composed into a separate
        // connection string (ADRs "Dedicated migration owner role" + "OpenBao static-role rotation").
        Assert.Equal("Host=db;Port=5432;Database=simplarchive;Username=simplarchive;Password=owner-pass", data["ConnectionStrings:Migration"]);
    }

    [Fact]
    public async Task No_static_owner_role_omits_the_migration_connection_string()
    {
        // The static role isn't provisioned (404 from static-creds): the reader falls back (no Migration key) so
        // migrations use the Default connection, unchanged.
        var handler = new FakeOpenBaoHandler { IncludeOwner = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://openbao.test:8200/") };
        var options = new OpenBaoOptions
        {
            Address = "http://openbao.test:8200",
            RoleId = "r",
            SecretId = "s",
            DatabaseOwnerStaticRole = "simplarchive-owner", // set, but the role returns 404
            DatabaseConnectionTemplate = "Host=db;Port=5432;Database=simplarchive",
        };

        var data = await new OpenBaoSecretsReader(http, options).ReadAsync();

        Assert.True(data.ContainsKey("ConnectionStrings:Default"));
        Assert.False(data.ContainsKey("ConnectionStrings:Migration"));
    }

    [Fact]
    public async Task Reads_openiddict_certificates_that_load_with_a_private_key()
    {
        var handler = new FakeOpenBaoHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://openbao.test:8200/") };
        var options = new OpenBaoOptions { Address = "http://openbao.test:8200", RoleId = "r", SecretId = "s" };

        var data = await new OpenBaoSecretsReader(http, options).ReadAsync();

        Assert.Contains("-----BEGIN CERTIFICATE-----", data["OpenIddict:SigningCertificatePem"]);
        Assert.Contains("-----BEGIN CERTIFICATE-----", data["OpenIddict:EncryptionCertificatePem"]);
        using var signing = OpenIddictCertificateLoader.FromPem(data["OpenIddict:SigningCertificatePem"]!, data["OpenIddict:SigningKeyPem"]!);
        Assert.True(signing.HasPrivateKey);
        using var encryption = OpenIddictCertificateLoader.FromPem(data["OpenIddict:EncryptionCertificatePem"]!, data["OpenIddict:EncryptionKeyPem"]!);
        Assert.True(encryption.HasPrivateKey);
    }

    // Builds a `bao write -format=json pki/issue/...` response body carrying a fresh self-signed cert + key.
    private static string PkiIssueJson()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=simplarchive-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return JsonSerializer.Serialize(new { data = new { certificate = cert.ExportCertificatePem(), private_key = rsa.ExportRSAPrivateKeyPem() } });
    }

    [Fact]
    public async Task No_database_template_skips_the_dynamic_credential()
    {
        var handler = new FakeOpenBaoHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://openbao.test:8200/") };
        var options = new OpenBaoOptions
        {
            Address = "http://openbao.test:8200",
            RoleId = "r",
            SecretId = "s",
            DatabaseConnectionTemplate = "", // no dynamic DB cred
        };

        var data = await new OpenBaoSecretsReader(http, options).ReadAsync();

        Assert.False(data.ContainsKey("ConnectionStrings:Default"));
        Assert.Equal("AK123", data["ObjectStorage:AccessKey"]);
    }
}
