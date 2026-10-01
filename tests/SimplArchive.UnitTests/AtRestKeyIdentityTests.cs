using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.UnitTests;

/// <summary>
/// An object records WHICH key wrapped it, and a key replaced under its own generation name is refused
/// rather than attempted (#1510, ADR 0867).
/// </summary>
/// <remarks>
/// <para>
/// <b>Unit-level on purpose, and the reason is worth keeping.</b> The obvious home for this was the E2E
/// suite, and it was written there first: the encryption-service stub holds real keypairs, so "replace the
/// keypair but keep the name" is a faithful reproduction of the catastrophe. It cannot live there.
/// </para>
/// <para>
/// The stub is a <b>shared collection fixture</b> — every E2E class uses one factory, because the factory
/// sets process-global environment variables. The KEK is installation state inside it, so re-provisioning it
/// is a mutation no test can scope to itself, and the standing rule is that a test which mutates data creates
/// its own. Worse than the rule, though, is the mechanism: after a re-provision the CORE caches the new
/// thumbprint for its map's TTL, so restoring the stub's keypair does not restore the core's belief. Every
/// encrypted object written in that window would then be stamped with the restored key while the cached map
/// named the replaced one — turning one test's mutation into **every** encrypted read in the suite failing
/// for half an hour. Measured as three failures on the first run, two of them in sibling tests that had
/// nothing to do with this.
/// </para>
/// <para>
/// So the E2E suite keeps only what it can assert without mutating anything — that a server-side write
/// records a thumbprint, and that the keys surface reports it — and the decision itself is pinned here,
/// where the HTTP responses are ours to shape.
/// </para>
/// </remarks>
public class AtRestKeyIdentityTests
{
    private const string ServiceUrl = "http://encryption.invalid";

    /// <summary>A thumbprint of the shape the service publishes: SHA-256 over an SPKI, lower-case hex.</summary>
    private static string ThumbprintFor(string seed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    /// <summary>Answers the two key endpoints from a script, and counts what was asked.</summary>
    private sealed class ScriptedKeyService(string generationsJson) : HttpMessageHandler
    {
        public int UnwrapCalls { get; private set; }

        public string? CurrentJson { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/kek/generations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(generationsJson, Encoding.UTF8, "application/json"),
                });
            }

            if (path == "/api/kek/current" && CurrentJson is { } current)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(current, Encoding.UTF8, "application/json"),
                });
            }

            if (path == "/api/unwrapped-dek")
            {
                // Reaching the oracle at all is the fact these tests measure; the 32 bytes are arbitrary.
                UnwrapCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[32]),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OneClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static AtRestKeyService ServiceWith(ScriptedKeyService handler) =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:ServiceUrl"] = ServiceUrl })
                .Build(),
            new OneClientFactory(handler),
            scopeFactory: null!, // only the tenant-name lookup uses it, and no path here resolves a tenant
            NullLogger<AtRestKeyService>.Instance);

    private static string GenerationsWith(params (string Generation, string Thumbprint)[] keys) =>
        $$"""
        {
          "current": "{{keys[^1].Generation}}",
          "currentThumbprint": "{{keys[^1].Thumbprint}}",
          "generations": [{{string.Join(", ", keys.Select(k => $"\"{k.Generation}\""))}}],
          "keys": [{{string.Join(", ", keys.Select(k =>
              $"{{\"generation\": \"{k.Generation}\", \"thumbprint\": \"{k.Thumbprint}\"}}"))}}]
        }
        """;

    [Fact]
    public async Task A_stamp_that_disagrees_with_the_generations_key_is_refused_before_the_oracle_is_called()
    {
        var held = ThumbprintFor("the key kek-v1 holds now");
        var stamped = ThumbprintFor("the key that actually wrapped the object");
        var handler = new ScriptedKeyService(GenerationsWith(("kek-v1", held)));

        var refusal = await Assert.ThrowsAsync<AtRestKeyChangedException>(() =>
            ServiceWith(handler).UnwrapDekAsync(
                Convert.ToBase64String(new byte[256]), "kek-v1", stamped,
                "tenants/t/2026/abc/content.pdf", CancellationToken.None));

        // THE ORACLE IS NOT CALLED. That is the decision, not an optimisation: the oracle unwraps with
        // whatever key is behind the label, so a mismatch cannot succeed — and its answer,
        // CKR_ENCRYPTED_DATA_INVALID, says "the ciphertext is wrong", which sends a reader looking for a
        // corrupt blob instead of a replaced key.
        Assert.Equal(0, handler.UnwrapCalls);

        // Both thumbprints and the generation, in the message an administrator reads.
        Assert.Contains("kek-v1", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(stamped, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(held, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("tenants/t/2026/abc/content.pdf", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("AT_REST_KEY_CHANGED", refusal.ErrorCode);
        Assert.Equal(500, refusal.ApiStatusCode);

        // A reader is NOT told to retry, because this never recovers — and gets no thumbprints, which they
        // could not act on.
        Assert.DoesNotContain("try again", refusal.ApiDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(stamped, refusal.ApiDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(held, refusal.ApiDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_stamp_means_UNKNOWN_and_the_oracle_still_decides()
    {
        // Every object written before ADR 0867 carries no thumbprint. Treating absent as a mismatch would
        // have refused the entire existing store — which is why this is the test that makes the change
        // additive rather than a migration.
        var handler = new ScriptedKeyService(GenerationsWith(("kek-v1", ThumbprintFor("whatever"))));

        var dek = await ServiceWith(handler).UnwrapDekAsync(
            Convert.ToBase64String(new byte[256]), "kek-v1", stampedThumbprint: null,
            "tenants/t/old-object", CancellationToken.None);

        Assert.Equal(32, dek.Length);
        Assert.Equal(1, handler.UnwrapCalls);
    }

    [Fact]
    public async Task A_stamp_is_compared_against_ITS_OWN_generation_and_never_the_current_one()
    {
        // THE SUBTLE ONE, and it would fail in the worst direction: an object's generation is routinely OLDER
        // than the current one — the normal steady state after a rotation, until the sweep finishes — so
        // comparing against the CURRENT key would refuse every correctly-wrapped object the sweep has not
        // reached yet. A rotation would then break exactly the reads it is designed to leave working.
        var first = ThumbprintFor("kek-v1's key");
        var current = ThumbprintFor("kek-v2's key");
        var handler = new ScriptedKeyService(GenerationsWith(("kek-v1", first), ("kek-v2", current)));

        var dek = await ServiceWith(handler).UnwrapDekAsync(
            Convert.ToBase64String(new byte[256]), "kek-v1", first,
            "tenants/t/not-swept-yet", CancellationToken.None);

        Assert.Equal(32, dek.Length);
        Assert.Equal(1, handler.UnwrapCalls);
    }

    [Fact]
    public async Task A_generation_the_service_does_not_list_is_unknown_rather_than_mismatched()
    {
        // A generation the map has no entry for cannot be judged — so it is not judged. The alternative,
        // treating "not listed" as a mismatch, would refuse an object whose generation had been retired from
        // the listing while the object still referenced it, which is a different fault with a different fix.
        var handler = new ScriptedKeyService(GenerationsWith(("kek-v2", ThumbprintFor("kek-v2's key"))));

        var dek = await ServiceWith(handler).UnwrapDekAsync(
            Convert.ToBase64String(new byte[256]), "kek-v1", ThumbprintFor("something"),
            "tenants/t/old-generation", CancellationToken.None);

        Assert.Equal(32, dek.Length);
        Assert.Equal(1, handler.UnwrapCalls);
    }

    [Fact]
    public async Task A_service_too_old_to_publish_the_keys_array_changes_nothing()
    {
        // Deployment order must not matter: the core may roll before the service does. With no `keys` array
        // the map stays empty, every comparison is skipped, and behaviour is exactly as it was before — which
        // is the property that made the service half safe to ship inside a release window, and keeps this
        // half safe too.
        var handler = new ScriptedKeyService(
            """{ "current": "kek-v1", "generations": ["kek-v1"] }""");

        var dek = await ServiceWith(handler).UnwrapDekAsync(
            Convert.ToBase64String(new byte[256]), "kek-v1", ThumbprintFor("a stamp nobody can check"),
            "tenants/t/content.pdf", CancellationToken.None);

        Assert.Equal(32, dek.Length);
        Assert.Equal(1, handler.UnwrapCalls);
    }

    [Fact]
    public async Task The_comparison_is_case_insensitive_so_a_hex_casing_difference_is_not_a_catastrophe()
    {
        // Both sides render lower-case hex today. If one ever rendered upper-case, a casing difference would
        // otherwise be reported as a replaced key — an alarm about an unrecoverable data loss, raised by a
        // formatting change.
        var held = ThumbprintFor("one key");
        var handler = new ScriptedKeyService(GenerationsWith(("kek-v1", held.ToUpperInvariant())));

        var dek = await ServiceWith(handler).UnwrapDekAsync(
            Convert.ToBase64String(new byte[256]), "kek-v1", held,
            "tenants/t/content.pdf", CancellationToken.None);

        Assert.Equal(32, dek.Length);
        Assert.Equal(1, handler.UnwrapCalls);
    }
}
