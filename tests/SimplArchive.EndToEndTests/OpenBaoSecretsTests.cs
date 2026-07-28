using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using SimplArchive.Api.Configuration;
using Testcontainers.PostgreSql;

namespace SimplArchive.EndToEndTests;

// End-to-end against a real OpenBao + Postgres (ADR "Secrets management with OpenBao"): provisions OpenBao the
// same way docker-compose's openbao-init does (KV v2 secrets, the Postgres database secrets engine, an AppRole),
// then drives the real OpenBaoSecretsReader — AppRole login → KV read → a DYNAMIC Postgres credential — and
// proves the minted credential actually connects to Postgres. Self-contained (its own throwaway containers), so
// it doesn't slow the shared E2E collection. Needs Docker.
public class OpenBaoSecretsTests : IAsyncLifetime
{
    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private IContainer _openBao = null!;

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().Build();

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithDatabase("simplarchive")
            .WithNetwork(_network)
            .WithNetworkAliases("db")
            .Build();

        _openBao = new ContainerBuilder()
            .WithImage("openbao/openbao:latest")
            .WithNetwork(_network)
            .WithEnvironment("BAO_ADDR", "http://127.0.0.1:8200")
            .WithEnvironment("BAO_TOKEN", "root")
            .WithCommand("server", "-dev", "-dev-root-token-id=root", "-dev-listen-address=0.0.0.0:8200")
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("bao", "status"))
            .Build();

        await _network.CreateAsync();
        await Task.WhenAll(_postgres.StartAsync(), _openBao.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await _openBao.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DeleteAsync();
    }

    [Fact]
    public async Task Reads_kv_secrets_and_mints_a_working_dynamic_postgres_credential()
    {
        // Provision OpenBao exactly like compose's openbao-init (KV + database engine against `db` + AppRole).
        var script = """
            set -e
            bao kv put secret/simplarchive/objectstorage accessKey=storageadmin secretKey=storageadmin
            bao kv put secret/simplarchive/smtp user= password=
            bao kv put secret/simplarchive/bootstrap clientSecret=dev-bootstrap-secret
            bao secrets enable database
            bao write database/config/simplarchive plugin_name=postgresql-database-plugin allowed_roles=simplarchive connection_url='postgresql://{{username}}:{{password}}@db:5432/simplarchive?sslmode=disable' username=postgres password=postgres
            bao write database/roles/simplarchive db_name=simplarchive creation_statements="CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}'; GRANT ALL ON SCHEMA public TO \"{{name}}\"; GRANT ALL ON ALL TABLES IN SCHEMA public TO \"{{name}}\";" default_ttl=1h max_ttl=4h
            bao auth enable approle
            printf 'path "secret/data/simplarchive/*" { capabilities = ["read"] }\npath "database/creds/simplarchive" { capabilities = ["read"] }\npath "transit/encrypt/simplarchive-mfa" { capabilities = ["update"] }\npath "transit/decrypt/simplarchive-mfa" { capabilities = ["update"] }\n' | bao policy write simplarchive -
            bao write auth/approle/role/simplarchive token_policies=simplarchive token_ttl=1h token_max_ttl=4h
            bao write auth/approle/role/simplarchive/role-id role_id=simplarchive-role
            bao write auth/approle/role/simplarchive/custom-secret-id secret_id=simplarchive-secret
            bao secrets enable transit
            bao write -f transit/keys/simplarchive-mfa
            bao secrets enable pki
            bao secrets tune -max-lease-ttl=87600h pki
            bao write -field=certificate pki/root/generate/internal common_name='SimplArchive Dev CA' ttl=87600h >/dev/null
            bao write pki/roles/openiddict allow_any_name=true max_ttl=8760h key_type=rsa key_bits=2048
            bao write -format=json pki/issue/openiddict common_name=simplarchive-signing ttl=8760h > /tmp/signing.json
            bao write -format=json pki/issue/openiddict common_name=simplarchive-encryption ttl=8760h > /tmp/encryption.json
            bao kv put secret/simplarchive/openiddict signing=@/tmp/signing.json encryption=@/tmp/encryption.json
            """;
        var exec = await _openBao.ExecAsync(["/bin/sh", "-c", script]);
        Assert.True(exec.ExitCode == 0, $"provisioning failed: {exec.Stderr}");

        var baoUrl = $"http://{_openBao.Hostname}:{_openBao.GetMappedPublicPort(8200)}";
        // The reader connects to Postgres through its HOST-mapped port (OpenBao reached it as `db` on the network).
        var template = $"Host={_postgres.Hostname};Port={_postgres.GetMappedPublicPort(5432)};Database=simplarchive";
        var options = new OpenBaoOptions
        {
            Address = baoUrl,
            RoleId = "simplarchive-role",
            SecretId = "simplarchive-secret",
            KvMount = "secret",
            DatabaseRole = "simplarchive",
            DatabaseConnectionTemplate = template,
        };

        using var http = new HttpClient { BaseAddress = new Uri(baoUrl.TrimEnd('/') + "/") };
        var data = await new OpenBaoSecretsReader(http, options).ReadAsync();

        // Static KV secrets.
        Assert.Equal("storageadmin", data["ObjectStorage:AccessKey"]);
        Assert.Equal("storageadmin", data["ObjectStorage:SecretKey"]);
        Assert.Equal("dev-bootstrap-secret", data["Bootstrap:PlatformAdministrator:ClientSecret"]);

        // The dynamic Postgres credential actually connects (proves the whole AppRole → database-engine path).
        var connectionString = data["ConnectionStrings:Default"]!;
        Assert.Contains("Username=v-", connectionString); // OpenBao-minted role names are prefixed "v-"
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());

        // The PKI-issued OpenIddict certs load with usable private keys (the real path AddAuthServer uses).
        using var signing = SimplArchive.Auth.OpenIddictCertificateLoader.FromPem(
            data["OpenIddict:SigningCertificatePem"]!, data["OpenIddict:SigningKeyPem"]!);
        using var encryption = SimplArchive.Auth.OpenIddictCertificateLoader.FromPem(
            data["OpenIddict:EncryptionCertificatePem"]!, data["OpenIddict:EncryptionKeyPem"]!);
        Assert.True(signing.HasPrivateKey);
        Assert.True(encryption.HasPrivateKey);

        // The transit engine encrypts/decrypts (used to protect the TOTP secret at rest) — round-trips a value.
        using var transit = new SimplArchive.Infrastructure.Secrets.OpenBaoTransitEncryptor(baoUrl, "simplarchive-role", "simplarchive-secret", "simplarchive-mfa");
        var ciphertext = await transit.EncryptAsync("JBSWY3DPEHPK3PXP");
        Assert.StartsWith("vault:", ciphertext);
        Assert.Equal("JBSWY3DPEHPK3PXP", await transit.DecryptAsync(ciphertext));
    }
}
