using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The key half of at-rest encryption (ADR 0818): which tenants encrypt (the SAME `Encryption:Tenants`
/// gate as the IMAP enveloping, ADR 0813 — owner-decided), the current KEK fetched from the encryption
/// service and cached, DEK wrapping in-process (RSA-OAEP needs only the public half), and unwrapping
/// through the service's HSM oracle (SimplArchiveEncryptionService ADR 0013) with a bounded DEK cache.
/// </summary>
public sealed class AtRestKeyService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<AtRestKeyService> logger)
{
    public const string HttpClientName = "at-rest-keys";

    /// <summary>
    /// The decrypt-and-envelope call's own client (ADR 0862) — a far longer timeout than the key calls.
    /// </summary>
    /// <remarks>
    /// Separate because the two operations differ by orders of magnitude in what crosses the wire: the
    /// oracle exchanges 32 bytes, while this waits for the service to fetch a whole document from storage,
    /// decrypt it and envelope it. On one client, the key calls' modest timeout would fail every large
    /// document — as a cancellation naming neither the size nor the limit it hit.
    /// </remarks>
    public const string EnvelopeHttpClientName = "at-rest-envelope";

    private sealed record Kek(string Generation, RSA PublicKey, string PublicKeyPem, string OaepHash,
        RSAEncryptionPadding Padding, DateTimeOffset FetchedAt);

    /// <summary>What an encrypting CLIENT needs to wrap against (ADR 0818/B2): generation, SPKI PEM, and
    /// the OAEP hash both sides must use — the initiate-upload response carries this on gated tenants.</summary>
    public sealed record ClientKek(string KekGeneration, string PublicKeyPem, string OaepHash);

    private readonly ConcurrentDictionary<Guid, (string Name, DateTimeOffset FetchedAt)> _tenantNames = new();
    private readonly ConcurrentDictionary<string, (byte[] Dek, DateTimeOffset FetchedAt)> _deks = new();
    private Kek? _kek;
    private readonly SemaphoreSlim _kekGate = new(1, 1);

    private static readonly TimeSpan NameTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KekTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DekTtl = TimeSpan.FromMinutes(10);
    private const int DekCacheCap = 1000;

    /// <summary>At-rest encryption exists at all — the same fully-inert switch as the envelope hook.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(configuration["Encryption:ServiceUrl"]);

    /// <summary>Whether the object at <paramref name="objectKey"/> belongs to a gated tenant. Every key is
    /// tenant-rooted (ObjectKeyPrefixes), so the tenant rides in the key; the NAME the gate matches
    /// (ADR 0813) is resolved once per TTL. A key outside the tenant root is never encrypted.</summary>
    public async Task<bool> GatedAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (!Enabled || !TryParseTenant(objectKey, out var tenantId))
        {
            return false;
        }

        // Read per call like the envelope client, so the list is editable without a restart (ADR 0813).
        // Both tiers are read from ONE map (EncryptionModes) — this used to be a second, subtly different
        // copy of the same list logic.
        var name = await TenantNameAsync(tenantId, cancellationToken);
        return name is not null
            && new SimplArchive.Infrastructure.Encryption.EncryptionModes(configuration).WrapsAtRest(name);
    }

    /// <summary>True when this object belongs to a STRICT-tier tenant, which never serves plaintext.</summary>
    /// <remarks>
    /// Beside <see cref="GatedAsync"/> rather than in a second service, because both answer the same question
    /// from the same three steps — key to tenant id, id to name, name to mode — and two copies of that walk is
    /// how the tiers would come to disagree about which tenant an object belongs to.
    /// </remarks>
    public async Task<bool> StrictAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (!Enabled || !TryParseTenant(objectKey, out var tenantId))
        {
            return false;
        }

        var name = await TenantNameAsync(tenantId, cancellationToken);
        return name is not null
            && new SimplArchive.Infrastructure.Encryption.EncryptionModes(configuration).RefusesPlaintextDoors(name);
    }

    /// <summary>The current KEK in the client-facing shape — for the initiate-upload response.</summary>
    public async Task<ClientKek> ClientKekAsync(CancellationToken cancellationToken)
    {
        var kek = await CurrentKekAsync(cancellationToken);
        return new ClientKek(kek.Generation, kek.PublicKeyPem, kek.OaepHash);
    }

    /// <summary>Mints and wraps a fresh DEK against the cached current KEK.</summary>
    public async Task<(byte[] Dek, string WrappedDekBase64, string Generation)> MintDekAsync(CancellationToken cancellationToken)
    {
        var kek = await CurrentKekAsync(cancellationToken);
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrapped = kek.PublicKey.Encrypt(dek, kek.Padding);
        return (dek, Convert.ToBase64String(wrapped), kek.Generation);
    }

    /// <summary>The rotation surface (ADR 0014): the generations the token holds and which is current.</summary>
    public async Task<(string Current, IReadOnlyList<string> All)> GenerationsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var json = await client.GetFromJsonAsync<JsonElement>(
            $"{ServiceUrl}/api/kek/generations", cancellationToken);
        return (json.GetProperty("current").GetString()!,
            [.. json.GetProperty("generations").EnumerateArray().Select(g => g.GetString()!)]);
    }

    /// <summary>Mints the next generation and makes it current; the sweep then re-wraps into it.</summary>
    public async Task<string> RotateAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsync($"{ServiceUrl}/api/kek/rotate", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // The cached KEK is now stale by construction — drop it so the very next write wraps against the
        // new generation rather than the one we just rotated away from.
        _kek = null;
        return json.GetProperty("generation").GetString()!;
    }

    /// <summary>
    /// Re-wraps one DEK into the current generation (ADR 0014). The DEK is unchanged, so the caller
    /// rewrites METADATA only — no blob is ever read or rewritten by a rotation.
    /// </summary>
    public async Task<(string WrappedDek, string Generation)> RewrapDekAsync(
        string wrappedDekBase64, string fromGeneration, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync($"{ServiceUrl}/api/rewrapped-dek",
            new { wrappedDek = wrappedDekBase64, fromGeneration }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (json.GetProperty("wrappedDek").GetString()!, json.GetProperty("kekGeneration").GetString()!);
    }

    /// <summary>Retires a generation — IRREVERSIBLE. Only call once the sweep reports zero references.</summary>
    public async Task RetireAsync(string generation, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.DeleteAsync($"{ServiceUrl}/api/kek/{generation}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private string ServiceUrl => configuration["Encryption:ServiceUrl"]!.TrimEnd('/');

    /// <summary>Unwraps through the HSM oracle, cached — one HTTP+HSM round trip per cold object.</summary>
    public async Task<byte[]> UnwrapDekAsync(string wrappedDekBase64, string generation, CancellationToken cancellationToken)
    {
        if (_deks.TryGetValue(wrappedDekBase64, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < DekTtl)
        {
            return cached.Dek;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"{ServiceUrl}/api/unwrapped-dek",
            new { wrappedDek = wrappedDekBase64, kekGeneration = generation }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dek = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (_deks.Count >= DekCacheCap)
        {
            _deks.Clear(); // crude but bounded; a refill costs one oracle call per live object
        }

        _deks[wrappedDekBase64] = (dek, DateTimeOffset.UtcNow);
        return dek;
    }

    /// <summary>
    /// The whole decrypt-and-envelope, performed INSIDE the service (ADR 0862): the wrapped DEK, a ciphertext
    /// address the service fetches itself, and every certificate the reader is addressed by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The point is what this method does NOT return.</b> <see cref="UnwrapDekAsync"/> hands this process
    /// a raw data key, and the caller then holds both the key and the cleartext for the length of a request.
    /// This returns a CMS envelope: the key never leaves the service's process and the plaintext never
    /// enters ours. Same service, same HSM — a different division of labour.
    /// </para>
    /// <para>
    /// <b>Nothing is cached here, deliberately.</b> The DEK cache above is keyed on the wrapped DEK and is
    /// safe because a data key is the same for every reader of that object; an envelope is addressed to a
    /// PARTICULAR set of certificates, so caching it by object key would serve one reader's envelope to
    /// another — who could not open it, and whose failure would look like a broken certificate.
    /// </para>
    /// <para>
    /// The inner-part headers are optional and describe the enveloped entity rather than this request
    /// (service ADR 0017): with them, the service wraps the plaintext as a named MIME part before
    /// enveloping, which is what lets a fabricated mail message carry a filename it never held in clear
    /// here.
    /// </para>
    /// </remarks>
    public async Task<byte[]> DecryptedEnvelopeAsync(
        string wrappedDekBase64,
        string generation,
        Uri ciphertextUrl,
        IReadOnlyList<string> recipientCertificatePems,
        string? innerContentType,
        string? innerContentDisposition,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(EnvelopeHttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"{ServiceUrl}/api/decrypted-envelope",
            new
            {
                wrappedDek = wrappedDekBase64,
                kekGeneration = generation,
                ciphertextUrl = ciphertextUrl.ToString(),
                recipientCertificatePems,
                innerContentType,
                innerContentDisposition,
            },
            cancellationToken);

        // No fail-open branch, and that is the tier's whole contract: this path exists because the caller must
        // not be able to serve plaintext, so a service that will not envelope means the read REFUSES.
        //
        // But the REASON has to survive. EnsureSuccessStatusCode stood here with a comment saying "the status
        // is the diagnosis (400 our data, 502 the ciphertext fetch, 503 the service)" — and then discarded
        // both the status and the body, so nothing in this process ever learned which of the three it was.
        // Every one of them reached the reader as "the encryption service is unavailable, try again shortly".
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ProblemDetailAsync(response, cancellationToken);
            logger.LogError(
                "The encryption service refused to envelope: {Status} {Detail}. This is {Whose} — a 4xx names "
                + "something about the request (a recipient certificate it cannot address, a DEK that does not "
                + "match the blob) and retrying will answer the same way.",
                (int)response.StatusCode, detail, (int)response.StatusCode < 500 ? "OURS" : "the service's");

            throw new EnvelopeServiceRefusedException((int)response.StatusCode, detail);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>
    /// The service's own words for a refusal — the problem document's <c>detail</c>, else its whole body.
    /// </summary>
    /// <remarks>
    /// Capped, because this goes into a log line and a misbehaving counterparty could otherwise write a
    /// megabyte into it. Never the request: the recipient certificates and the wrapped DEK went the other way,
    /// and neither belongs in a log.
    /// </remarks>
    private static async Task<string> ProblemDetailAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length == 0)
            {
                return "(no body)";
            }

            try
            {
                var problem = JsonSerializer.Deserialize<JsonElement>(body);
                var title = problem.TryGetProperty("title", out var t) ? t.GetString() : null;
                var detail = problem.TryGetProperty("detail", out var d) ? d.GetString() : null;
                if (title is not null || detail is not null)
                {
                    return Truncated($"{title} {detail}".Trim());
                }
            }
            catch (JsonException)
            {
                // Not a problem document; the raw body is still the best answer available.
            }

            return Truncated(body);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return "(the body could not be read)";
        }
    }

    private static string Truncated(string text) =>
        text.Length <= 500 ? text : $"{text[..500]}…";

    private async Task<Kek> CurrentKekAsync(CancellationToken cancellationToken)
    {
        if (_kek is { } kek && DateTimeOffset.UtcNow - kek.FetchedAt < KekTtl)
        {
            return kek;
        }

        await _kekGate.WaitAsync(cancellationToken);
        try
        {
            if (_kek is { } fresh && DateTimeOffset.UtcNow - fresh.FetchedAt < KekTtl)
            {
                return fresh;
            }

            var client = httpClientFactory.CreateClient(HttpClientName);
            var json = await client.GetFromJsonAsync<JsonElement>(
                $"{ServiceUrl}/api/kek/current", cancellationToken);
            var publicKey = RSA.Create();
            publicKey.ImportFromPem(json.GetProperty("publicKeyPem").GetString()!);
            // The service publishes the OAEP hash BOTH sides must use (SimplArchiveEncryptionService ADR 0011):
            // the dev HSM is SHA-1-only, and a hash we assumed would be an unwrappable DEK.
            var padding = json.GetProperty("oaepHash").GetString() == "SHA1"
                ? RSAEncryptionPadding.OaepSHA1
                : RSAEncryptionPadding.OaepSHA256;
            var loaded = new Kek(json.GetProperty("generation").GetString()!, publicKey,
                json.GetProperty("publicKeyPem").GetString()!, json.GetProperty("oaepHash").GetString()!,
                padding, DateTimeOffset.UtcNow);
            _kek = loaded;
            logger.LogInformation("At-rest KEK loaded: generation {Generation}, OAEP {Hash}.",
                loaded.Generation, loaded.OaepHash);
            return loaded;
        }
        finally
        {
            _kekGate.Release();
        }
    }

    private async Task<string?> TenantNameAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenantNames.TryGetValue(tenantId, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < NameTtl)
        {
            return cached.Name;
        }

        // Storage runs on startup and background paths with no ambient tenant — bypass the filter
        // explicitly, the standing rule for pre-tenant lookups.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var name = await db.Tenants.IgnoreQueryFilters(["TenantFilter"])
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .SingleOrDefaultAsync(cancellationToken);
        if (name is not null)
        {
            _tenantNames[tenantId] = (name, DateTimeOffset.UtcNow);
        }

        return name;
    }

    private static bool TryParseTenant(string objectKey, out Guid tenantId)
    {
        tenantId = default;
        var root = SimplArchive.Application.Abstractions.ObjectKeyPrefixes.Root;
        if (!objectKey.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        var end = objectKey.IndexOf('/', root.Length);
        return end > 0 && Guid.TryParse(objectKey.AsSpan(root.Length, end - root.Length), out tenantId);
    }
}
