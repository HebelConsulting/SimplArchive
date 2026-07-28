using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Secrets;

// Encrypts/decrypts via OpenBao's transit engine (ADR "MFA require-policy + TOTP secret encryption"): the key
// never leaves OpenBao, so the app stores only ciphertext (vault:v1:…). Authenticates with the same AppRole as
// the config provider, caching the client token until shortly before it expires. Registered when OpenBao is
// configured; a value with no "vault:" prefix is passed through on decrypt (pre-encryption plaintext).
public sealed class OpenBaoTransitEncryptor : ITransitEncryptor, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _roleId;
    private readonly string _secretId;
    private readonly string _keyName;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public OpenBaoTransitEncryptor(string address, string roleId, string secretId, string keyName)
    {
        _http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        _roleId = roleId;
        _secretId = secretId;
        _keyName = keyName;
    }

    public async Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/transit/encrypt/{_keyName}")
        {
            Content = JsonContent.Create(new { plaintext = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext)) }),
        };
        request.Headers.Add("X-Vault-Token", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response, cancellationToken);
        return json.RootElement.GetProperty("data").GetProperty("ciphertext").GetString()!;
    }

    public async Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default)
    {
        // Backward-compatible: a pre-encryption plaintext secret has no OpenBao ciphertext prefix.
        if (!ciphertext.StartsWith("vault:", StringComparison.Ordinal))
        {
            return ciphertext;
        }

        var token = await GetTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/transit/decrypt/{_keyName}")
        {
            Content = JsonContent.Create(new { ciphertext }),
        };
        request.Headers.Add("X-Vault-Token", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response, cancellationToken);
        var base64 = json.RootElement.GetProperty("data").GetProperty("plaintext").GetString()!;
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            {
                return _token;
            }

            using var response = await _http.PostAsJsonAsync("v1/auth/approle/login", new { role_id = _roleId, secret_id = _secretId }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = await ReadJsonAsync(response, cancellationToken);
            var auth = json.RootElement.GetProperty("auth");
            _token = auth.GetProperty("client_token").GetString()!;
            var leaseSeconds = auth.TryGetProperty("lease_duration", out var lease) ? lease.GetInt32() : 3600;
            // Renew a minute before expiry (and never trust a 0 lease).
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, leaseSeconds) - 60);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
