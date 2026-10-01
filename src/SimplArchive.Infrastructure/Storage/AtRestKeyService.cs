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
        RSAEncryptionPadding Padding, string Thumbprint, DateTimeOffset FetchedAt);

    /// <summary>What an encrypting CLIENT needs to wrap against (ADR 0818/B2): generation, SPKI PEM, and
    /// the OAEP hash both sides must use — the initiate-upload response carries this on gated tenants.</summary>
    public sealed record ClientKek(string KekGeneration, string PublicKeyPem, string OaepHash);

    private readonly ConcurrentDictionary<Guid, (string Name, DateTimeOffset FetchedAt)> _tenantNames = new();
    private readonly ConcurrentDictionary<string, (byte[] Dek, DateTimeOffset FetchedAt)> _deks = new();
    private Kek? _kek;
    private readonly SemaphoreSlim _kekGate = new(1, 1);

    /// <summary>
    /// Generation name → the thumbprint of the key now wearing it, from the service's <c>keys</c> array
    /// (ADR 0867, service ADR 0019). Empty until fetched, and empty for ever against a service too old to
    /// send the array — which is what lets the two sides deploy in either order.
    /// </summary>
    private IReadOnlyDictionary<string, string> _generationKeys = new Dictionary<string, string>();
    private DateTimeOffset _generationKeysFetchedAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan NameTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KekTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the generation → thumbprint map is trusted. Longer than the current key's TTL on purpose: a
    /// generation's key changes only when a token is re-provisioned, which is the catastrophe this map exists
    /// to name rather than a routine event — while a SHORT TTL would put an outbound call on the read path
    /// every few minutes to re-learn something that almost never moves.
    /// </summary>
    private static readonly TimeSpan GenerationKeysTtl = TimeSpan.FromMinutes(30);
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
    public async Task<(byte[] Dek, string WrappedDekBase64, string Generation, string Thumbprint)> MintDekAsync(
        CancellationToken cancellationToken)
    {
        var kek = await CurrentKekAsync(cancellationToken);
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrapped = kek.PublicKey.Encrypt(dek, kek.Padding);
        return (dek, Convert.ToBase64String(wrapped), kek.Generation, kek.Thumbprint);
    }

    /// <summary>The rotation surface (ADR 0014): the generations the token holds and which is current.</summary>
    public async Task<(string Current, IReadOnlyList<string> All)> GenerationsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        // Read explicitly rather than with GetFromJsonAsync, which throws on a non-success status while
        // discarding the body — the same loss the key calls below used to take (#1511), and worse here
        // because this is the first thing the rotation runbook asks.
        using var response = await client.GetAsync($"{ServiceUrl}/api/kek/generations", cancellationToken);
        await RefuseAsync(response, "list the KEK generations", cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        RememberGenerationKeys(json);
        return (json.GetProperty("current").GetString()!,
            [.. json.GetProperty("generations").EnumerateArray().Select(g => g.GetString()!)]);
    }

    /// <summary>
    /// The thumbprint of the key currently behind <paramref name="generation"/>, or null when it cannot be
    /// established (a service too old to publish the <c>keys</c> array, or a generation it does not list).
    /// </summary>
    /// <remarks>
    /// <b>Per generation, never "the current key".</b> An object's generation is routinely OLDER than the
    /// current one — that is the normal steady state after a rotation until the sweep finishes — so comparing
    /// an old object's stamp against the current key would refuse every correctly-wrapped object in the
    /// population the sweep has not reached. That mistake would have been silent in the worst direction:
    /// reads failing on exactly the objects a rotation was meant to leave readable (ADR 0867, decision 3).
    /// </remarks>
    public async Task<string?> GenerationThumbprintAsync(string generation, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _generationKeysFetchedAt >= GenerationKeysTtl)
        {
            // Through the listing call, which is what reads the array — so there is one parser for it rather
            // than two that can disagree about the shape.
            try
            {
                _ = await GenerationsAsync(cancellationToken);
            }
            catch (AtRestKeyRefusedException exception)
            {
                // A map we could not refresh must not fail a read that would otherwise work: an unknown
                // thumbprint is permitted (ADR 0867, decision 4), so the refusal is recorded and the caller
                // falls through to the oracle, which remains the authority on whether the DEK unwraps.
                logger.LogWarning(exception,
                    "The KEK generation list could not be refreshed, so {Generation} cannot be checked "
                    + "against the key that wrapped this object. The unwrap proceeds and the oracle decides.",
                    generation);
                return null;
            }
        }

        return _generationKeys.TryGetValue(generation, out var thumbprint) ? thumbprint : null;
    }

    /// <summary>
    /// The whole generation → thumbprint map, for the keys status surface (ADR 0867, decision 5). Empty
    /// against a service too old to publish it.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GenerationKeysAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _generationKeysFetchedAt >= GenerationKeysTtl)
        {
            _ = await GenerationsAsync(cancellationToken);
        }

        return _generationKeys;
    }

    /// <summary>Caches the <c>keys</c> array if the service sent one.</summary>
    private void RememberGenerationKeys(JsonElement json)
    {
        if (!json.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
        {
            // A service predating service ADR 0019. The map stays as it was — deliberately NOT cleared, so a
            // downgrade does not silently discard knowledge — and the fetch timestamp is not advanced either,
            // since nothing was learned.
            return;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in keys.EnumerateArray())
        {
            if (entry.TryGetProperty("generation", out var g) && g.GetString() is { Length: > 0 } name
                && entry.TryGetProperty("thumbprint", out var t) && t.GetString() is { Length: > 0 } thumbprint)
            {
                map[name] = thumbprint;
            }
        }

        _generationKeys = map;
        _generationKeysFetchedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Mints the next generation and makes it current; the sweep then re-wraps into it.</summary>
    public async Task<string> RotateAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsync($"{ServiceUrl}/api/kek/rotate", null, cancellationToken);
        await RefuseAsync(response, "mint the next KEK generation", cancellationToken);
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
    public async Task<(string WrappedDek, string Generation, string Thumbprint)> RewrapDekAsync(
        string wrappedDekBase64, string fromGeneration, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync($"{ServiceUrl}/api/rewrapped-dek",
            new { wrappedDek = wrappedDekBase64, fromGeneration }, cancellationToken);
        await RefuseAsync(response, $"re-wrap a data key out of {fromGeneration}", cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // THE THUMBPRINT COMES BACK TOO, because a re-wrap is the other operation that decides which key an
        // object belongs to, and an object the sweep moved must carry the identity of the key it moved to
        // (ADR 0867, decision 1). The re-wrap endpoint answers only the generation, so the key is the CURRENT
        // one by construction — the service wraps against its current public half and says so.
        var kek = await CurrentKekAsync(cancellationToken);
        return (json.GetProperty("wrappedDek").GetString()!, json.GetProperty("kekGeneration").GetString()!,
            kek.Thumbprint);
    }

    /// <summary>Retires a generation — IRREVERSIBLE. Only call once the sweep reports zero references.</summary>
    public async Task RetireAsync(string generation, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.DeleteAsync($"{ServiceUrl}/api/kek/{generation}", cancellationToken);
        await RefuseAsync(response, $"retire KEK generation {generation}", cancellationToken);
    }

    private string ServiceUrl => configuration["Encryption:ServiceUrl"]!.TrimEnd('/');

    /// <summary>Unwraps through the HSM oracle, cached — one HTTP+HSM round trip per cold object.</summary>
    public async Task<byte[]> UnwrapDekAsync(string wrappedDekBase64, string generation,
        CancellationToken cancellationToken)
        => await UnwrapDekAsync(wrappedDekBase64, generation, null, null, cancellationToken);

    /// <summary>
    /// As above, but checking the object's recorded key identity first (ADR 0867).
    /// </summary>
    /// <param name="stampedThumbprint">
    /// The <c>sa-kek-thumbprint</c> the object carries, or null when it carries none — which is every object
    /// written before ADR 0867 and therefore means <i>unknown</i>, not <i>mismatched</i>.
    /// </param>
    /// <param name="objectKey">Named only so a refusal can say WHICH object; not used to find anything.</param>
    /// <exception cref="AtRestKeyChangedException">
    /// The stamp and the generation's current key disagree, so the unwrap cannot succeed.
    /// </exception>
    public async Task<byte[]> UnwrapDekAsync(string wrappedDekBase64, string generation,
        string? stampedThumbprint, string? objectKey, CancellationToken cancellationToken)
    {
        // THE CACHE IS CONSULTED FIRST, AHEAD OF THE CHECK, AND THAT ORDER IS DELIBERATE. A cached DEK is one
        // the oracle already unwrapped in this process, so the key matched when it did — the read works, and
        // refusing a read that works is the one cost ADR 0867 names as its honest risk. The only way to reach
        // a cache hit under a mismatch is for the token to have changed while this process ran, which serves
        // the document rather than failing it: strictly the better outcome of the two.
        if (_deks.TryGetValue(wrappedDekBase64, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < DekTtl)
        {
            return cached.Dek;
        }

        if (stampedThumbprint is { Length: > 0 }
            && await GenerationThumbprintAsync(generation, cancellationToken) is { Length: > 0 } heldThumbprint
            && !string.Equals(stampedThumbprint, heldThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            // Logged as well as thrown, because the exception's detail reaches a READER while the two
            // thumbprints are what an administrator needs — and because this line is the one that turns a
            // store-wide question into a grep.
            logger.LogError(
                "{ObjectKey} was wrapped under {Generation} when its key was {Stamped}, but {Generation} now "
                + "holds {Held}. The token has been re-provisioned, restored or recreated; every object "
                + "wrapped by the earlier key is unreadable, and a WORM-locked one cannot be re-wrapped.",
                objectKey ?? "an object", generation, stampedThumbprint, generation, heldThumbprint);

            throw new AtRestKeyChangedException(
                objectKey ?? "an object", generation, stampedThumbprint, heldThumbprint);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"{ServiceUrl}/api/unwrapped-dek",
            new { wrappedDek = wrappedDekBase64, kekGeneration = generation }, cancellationToken);

        // THE SERVICE'S OWN DIAGNOSIS, KEPT (#1511). `EnsureSuccessStatusCode()` stood here and discarded
        // both the status and the body, so the token door answered `500 INTERNAL_ERROR` / "An unexpected
        // error occurred." while the service had said, precisely, `400 "The wrapped DEK is not usable." —
        // CKR_ENCRYPTED_DATA_INVALID`. That is a complete diagnosis of a re-minted KEK (#1510), and
        // recovering it afterwards cost a full scan of the object store.
        //
        // The generation is named because it is the one thing that makes the line actionable: the label is
        // all a generation HAS for an identity, so "kek-v1 refused this object" is the sentence that says a
        // second key has worn that name.
        // NAMING THE KEY WE HOLD FOR THAT LABEL, from the cache only — never a fresh fetch, which would add
        // an outbound call inside a failure path and could fail in its own right. When it is warm this is the
        // line that identifies the fault outright: "kek-v1 refused this object, and the key we hold for
        // kek-v1 is <thumbprint>" is a re-minted token, said in one sentence (#1510).
        var held = _kek is { } current && current.Generation == generation
            ? $"{generation} (the key we hold for it is {current.Thumbprint})"
            : generation;

        await RefuseAsync(response, $"unwrap a data key wrapped under {held}", cancellationToken);
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
    /// Logs and throws when the service refused a KEY call; a no-op on success (#1511).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One helper rather than five copies, because five copies is how one of them keeps the bare
    /// <c>EnsureSuccessStatusCode()</c> — which is exactly what happened: ADR 0862 removed the discard from
    /// <see cref="DecryptedEnvelopeAsync"/> and left it in the four key calls and the oracle beside it.
    /// </para>
    /// <para>
    /// The envelope call deliberately does <b>not</b> route through here: its refusal is caught and
    /// translated by <c>StrictEnvelopeDelivery</c>, which needs a different exception type, and its log line
    /// says something this one cannot (which recipient certificate could not be addressed).
    /// </para>
    /// </remarks>
    private async Task RefuseAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await ProblemDetailAsync(response, cancellationToken);
        logger.LogError(
            "The encryption service refused to {Operation}: {Status} {Detail}. This is {Whose} — a 4xx names "
            + "something about the request or about what we stored (a data key that does not match the key "
            + "its generation now holds) and retrying will answer the same way.",
            operation, (int)response.StatusCode, detail,
            (int)response.StatusCode < 500 ? "OURS" : "the service's");

        throw new AtRestKeyRefusedException(operation, (int)response.StatusCode, detail);
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

    /// <summary>
    /// Which KEY a generation is — SHA-256 over its SPKI DER, hex (#1510, Service ADR 0019).
    /// </summary>
    /// <remarks>
    /// The same derivation the service publishes as <c>thumbprint</c>, over the same SubjectPublicKeyInfo, so
    /// the two sides' log lines are comparable by eye. A public key's fingerprint is derived from material the
    /// service hands out on request, so it is safe to log — which is the point, since comparing two of them
    /// across restarts is the whole mechanism.
    /// </remarks>
    private static string ThumbprintOf(RSA publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo()));

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
            using var response = await client.GetAsync($"{ServiceUrl}/api/kek/current", cancellationToken);
            await RefuseAsync(response, "fetch the current KEK", cancellationToken);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var publicKey = RSA.Create();
            publicKey.ImportFromPem(json.GetProperty("publicKeyPem").GetString()!);
            // The service publishes the OAEP hash BOTH sides must use (SimplArchiveEncryptionService ADR 0011):
            // the dev HSM is SHA-1-only, and a hash we assumed would be an unwrappable DEK.
            var padding = json.GetProperty("oaepHash").GetString() == "SHA1"
                ? RSAEncryptionPadding.OaepSHA1
                : RSAEncryptionPadding.OaepSHA256;
            var loaded = new Kek(json.GetProperty("generation").GetString()!, publicKey,
                json.GetProperty("publicKeyPem").GetString()!, json.GetProperty("oaepHash").GetString()!,
                padding, ThumbprintOf(publicKey), DateTimeOffset.UtcNow);
            _kek = loaded;

            // THE KEY, NOT JUST ITS LABEL (#1510, Service ADR 0019). A generation's label is all an object
            // records, and a re-provisioned token mints a fresh `kek-v1` that is locally correct and orphans
            // everything the previous one wrapped — measured: five objects unreadable, two of them WORM audit
            // segments, discovered only as CKR_ENCRYPTED_DATA_INVALID on a read. This line is where the swap
            // becomes a grep across two restarts instead of a scan of the whole store.
            //
            // Derived here rather than read from the response's `thumbprint`, deliberately: computing it from
            // the PEM we actually wrap against means the value describes the key THIS process will use. A
            // field taken on trust would still match while we wrapped with something else.
            logger.LogInformation(
                "At-rest KEK loaded: generation {Generation}, key {Thumbprint}, OAEP {Hash}.",
                loaded.Generation, loaded.Thumbprint, loaded.OaepHash);
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
