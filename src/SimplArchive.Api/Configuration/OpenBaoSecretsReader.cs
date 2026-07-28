using System.Net.Http.Json;
using System.Text.Json;
using Serilog;

namespace SimplArchive.Api.Configuration;

// The raw-HTTP OpenBao client that reads the app's secrets (ADR "Secrets management with OpenBao"). Mirrors the
// no-client-library approach used for OpenSearch: plain HttpClient + System.Text.Json against OpenBao's REST
// API. AppRole login → client token → read the static KV v2 secrets + a dynamic Postgres credential, mapped to
// their IConfiguration keys. Kept separate from the ConfigurationProvider so it's unit-testable with a fake
// HttpClient, and reusable in the E2E against a real OpenBao container.
public sealed class OpenBaoSecretsReader
{
    private readonly HttpClient _http;
    private readonly OpenBaoOptions _options;

    public OpenBaoSecretsReader(HttpClient http, OpenBaoOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<IDictionary<string, string?>> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Config-provider time — the DI logging pipeline isn't built yet, so use Serilog's bootstrap logger
        // (Log.Logger, set at the very top of Program.cs) directly. OpenBao being unreachable/misconfigured is
        // service-impairing (the app can't source its secrets), so a failure logs Fatal before it propagates.
        string token;
        try
        {
            token = await LoginAsync(cancellationToken);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "OpenBao AppRole login failed at {Address}; the app cannot source its secrets.", _options.Address);
            throw;
        }

        var data = new Dictionary<string, string?>();

        var objectStorage = await ReadKvAsync(token, "simplarchive/objectstorage", cancellationToken);
        if (objectStorage.TryGetValue("accessKey", out var accessKey)) data["ObjectStorage:AccessKey"] = accessKey;
        if (objectStorage.TryGetValue("secretKey", out var secretKey)) data["ObjectStorage:SecretKey"] = secretKey;

        var smtp = await ReadKvAsync(token, "simplarchive/smtp", cancellationToken);
        if (smtp.TryGetValue("user", out var smtpUser)) data["Smtp:User"] = smtpUser;
        if (smtp.TryGetValue("password", out var smtpPassword)) data["Smtp:Password"] = smtpPassword;

        var bootstrap = await ReadKvAsync(token, "simplarchive/bootstrap", cancellationToken);
        if (bootstrap.TryGetValue("clientSecret", out var clientSecret)) data["Bootstrap:PlatformAdministrator:ClientSecret"] = clientSecret;

        // OpenIddict signing/encryption certs (ADR "OpenIddict certificates from OpenBao"). Optional — a
        // deployment provisioned before this slice won't have them, so the app falls back to dev certs. Each KV
        // field is the full JSON of a `bao write pki/issue/...` response; extract certificate + private_key.
        var openiddict = await ReadKvAsync(token, "simplarchive/openiddict", cancellationToken, optional: true);
        if (openiddict.TryGetValue("signing", out var signingJson) && TryParsePkiIssue(signingJson, out var signingCert, out var signingKey))
        {
            data["OpenIddict:SigningCertificatePem"] = signingCert;
            data["OpenIddict:SigningKeyPem"] = signingKey;
        }
        if (openiddict.TryGetValue("encryption", out var encryptionJson) && TryParsePkiIssue(encryptionJson, out var encryptionCert, out var encryptionKey))
        {
            data["OpenIddict:EncryptionCertificatePem"] = encryptionCert;
            data["OpenIddict:EncryptionKeyPem"] = encryptionKey;
        }

        // Dynamic Postgres credential — OpenBao mints a short-lived role; compose the connection string from the
        // non-secret template + the issued username/password.
        if (!string.IsNullOrWhiteSpace(_options.DatabaseConnectionTemplate))
        {
            var (username, password) = await ReadDatabaseCredentialAsync(token, cancellationToken);
            var template = _options.DatabaseConnectionTemplate.TrimEnd(';');
            data["ConnectionStrings:Default"] = $"{template};Username={username};Password={password}";

            // Dedicated migration-owner credential (ADRs "Dedicated migration owner role" +
            // "OpenBao static-role rotation for the migration owner"): the schema-owning `simplarchive` role,
            // whose password OpenBao owns + rotates as a database *static role*. Read the current password from
            // database/static-creds/<role> and compose ConnectionStrings:Migration. Optional — when the static
            // role isn't configured (empty option / not provisioned) migrations fall back to the Default
            // connection, so tests / non-OpenBao deployments are unaffected.
            if (!string.IsNullOrWhiteSpace(_options.DatabaseOwnerStaticRole)
                && await ReadStaticDatabaseCredentialAsync(token, _options.DatabaseOwnerStaticRole, cancellationToken) is { } owner)
            {
                data["ConnectionStrings:Migration"] = $"{template};Username={owner.Username};Password={owner.Password}";
            }
        }

        Log.Debug("Read {Count} secret(s) from OpenBao at {Address}.", data.Count, _options.Address);
        return data;
    }

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/auth/approle/login",
            new { role_id = _options.RoleId, secret_id = _options.SecretId },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response, cancellationToken);
        return json.RootElement.GetProperty("auth").GetProperty("client_token").GetString()
            ?? throw new InvalidOperationException("OpenBao AppRole login returned no client token.");
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadKvAsync(string token, string path, CancellationToken cancellationToken, bool optional = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/{_options.KvMount}/data/{path}");
        request.Headers.Add("X-Vault-Token", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (optional && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response, cancellationToken);

        // KV v2 nests the secret under data.data.
        var secret = json.RootElement.GetProperty("data").GetProperty("data");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in secret.EnumerateObject())
        {
            result[property.Name] = property.Value.GetString() ?? "";
        }

        return result;
    }

    private async Task<(string Username, string Password)> ReadDatabaseCredentialAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/database/creds/{_options.DatabaseRole}");
        request.Headers.Add("X-Vault-Token", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response, cancellationToken);

        var creds = json.RootElement.GetProperty("data");
        return (creds.GetProperty("username").GetString() ?? "", creds.GetProperty("password").GetString() ?? "");
    }

    // Reads a database *static* credential (database/static-creds/<role>) — an OpenBao-managed, periodically
    // rotated password for a fixed DB user. Returns null when the static role isn't provisioned (404), so the
    // caller falls back to the Default connection.
    private async Task<(string Username, string Password)?> ReadStaticDatabaseCredentialAsync(string token, string role, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/database/static-creds/{role}");
        request.Headers.Add("X-Vault-Token", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response, cancellationToken);

        var creds = json.RootElement.GetProperty("data");
        return (creds.GetProperty("username").GetString() ?? "", creds.GetProperty("password").GetString() ?? "");
    }

    // Extracts the certificate + private_key PEMs from a stored `bao write -format=json pki/issue/...` response
    // (openbao-init stores the whole issue JSON in KV so no jq is needed in the init container).
    private static bool TryParsePkiIssue(string issueJson, out string certificate, out string privateKey)
    {
        certificate = "";
        privateKey = "";
        try
        {
            using var doc = JsonDocument.Parse(issueJson);
            var data = doc.RootElement.GetProperty("data");
            certificate = data.GetProperty("certificate").GetString() ?? "";
            privateKey = data.GetProperty("private_key").GetString() ?? "";
            return certificate.Length > 0 && privateKey.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
