using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// A server-side write records WHICH key wrapped it, and the keys surface reports that identity
// (#1510, ADR 0867).
//
// The failure this exists for was measured on a development stack: 5 of 48 encrypted objects permanently
// unreadable, two of them WORM audit segments that can never be re-wrapped, because a re-provisioned token
// had minted a second RSA keypair still labelled `kek-v1`. Nothing detected it — the write succeeded, the
// generation list answered `{"current":"kek-v1","generations":["kek-v1"]}` (true, and useless), and the only
// symptom was CKR_ENCRYPTED_DATA_INVALID on a read months later.
//
// WHAT IS DELIBERATELY NOT TESTED HERE: the mismatch itself. Reproducing it means replacing the stub's
// keypair while keeping its name, and the stub is a SHARED collection fixture — so that is a mutation no
// test can scope to itself. Worse than the rule is the mechanism: the core caches the generation's
// thumbprint for its map's TTL, so restoring the stub's keypair does not restore the core's belief, and
// every encrypted object written in that window would be stamped with the restored key while the cached map
// named the replaced one. One test's mutation becomes EVERY encrypted read in the suite failing for half an
// hour — observed on the first run as three failures, two of them in sibling tests with nothing to do with
// this. The refusal is pinned in SimplArchive.UnitTests.AtRestKeyIdentityTests instead, where the service's
// HTTP answers are the test's to shape.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AtRestKeyIdentityTests
{
    private readonly E2EApiFactory _factory;

    public AtRestKeyIdentityTests(E2EApiFactory factory) => _factory = factory;

    /// <summary>Files a text document through the ordinary WebDAV write path, which encrypts server-side.</summary>
    /// <remarks>
    /// WebDAV rather than the API's upload, because an upload is PRESIGNED and client-encrypted — a
    /// third-party caller's bytes land as plaintext-without-metadata and would exercise none of this. A
    /// server-side write goes through the encrypting decorator, which is the thing under test.
    /// </remarks>
    private async Task<(string Bucket, string Key, string Name)> FileEncryptedDocumentAsync(HttpClient api)
    {
        var before = await RawKeysAsync();
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { }))
            .GetProperty("password").GetString()!;
        var name = $"key-identity-{Guid.NewGuid():N}.txt";

        using (var dav = _factory.CreateClient())
        {
            using var put = new HttpRequestMessage(HttpMethod.Put,
                $"/SimplArchive/{E2EApiFactory.CryptoTenantName}/{name}")
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("which key wrapped me?\n")),
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{E2EApiFactory.CryptoAdminEmail}:{davPassword}"))) },
            };
            Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        }

        var added = (await RawKeysAsync()).Except(before)
            .Where(o => o.Key.EndsWith(".txt", StringComparison.Ordinal)).ToList();
        var one = Assert.Single(added);
        return (one.Bucket, one.Key, name);
    }

    private async Task<HashSet<(string Bucket, string Key)>> RawKeysAsync()
    {
        using var s3 = _factory.CreateRawStorageClient();
        var keys = new HashSet<(string, string)>();
        foreach (var bucket in (await s3.ListBucketsAsync()).Buckets)
        {
            var objects = await s3.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request
            {
                BucketName = bucket.BucketName,
            });
            foreach (var o in objects.S3Objects ?? [])
            {
                keys.Add((bucket.BucketName, o.Key));
            }
        }

        return keys;
    }

    private async Task<IDictionary<string, string>> RawMetadataAsync(string bucket, string key)
    {
        using var s3 = _factory.CreateRawStorageClient();
        using var response = await s3.GetObjectAsync(bucket, key);
        return response.Metadata.Keys.ToDictionary(k => k, k => response.Metadata[k], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A server-side write records the KEK's thumbprint beside its generation, and it is the thumbprint of
    /// the key the service actually published.
    /// </summary>
    /// <remarks>
    /// Asserted against the STUB's own computation of the same SPKI, so this is a cross-side agreement rather
    /// than a round trip against one implementation — the property ADR 0867 depends on, since a thumbprint
    /// each side derived its own way would agree until it did not.
    /// </remarks>
    [Fact]
    public async Task A_written_object_records_which_key_wrapped_it()
    {
        using var api = _factory.CreateAuthedClient(
            await _factory.GetUserTokenAsync(E2EApiFactory.CryptoAdminEmail, E2EApiFactory.CryptoPassword));

        var (bucket, key, _) = await FileEncryptedDocumentAsync(api);
        var metadata = await RawMetadataAsync(bucket, key);

        var generation = Assert.Contains("x-amz-meta-sa-kek-generation", metadata);
        var stamped = Assert.Contains("x-amz-meta-sa-kek-thumbprint", metadata);

        Assert.Equal(_factory.KekThumbprint(generation), stamped);
        Assert.Equal(64, stamped.Length); // SHA-256 as lower-case hex, so a truncation is visible here
        Assert.Equal(stamped.ToLowerInvariant(), stamped);
    }

    /// <summary>
    /// The keys status surface reports which key each generation is, and that a sampled object disagrees.
    /// </summary>
    /// <remarks>
    /// This is the detection half (ADR 0867, decision 5): the loss is otherwise found when somebody opens a
    /// document, which in the measured case was months later and could have been after retention purged the
    /// source rows that made recovery possible. Reported on the surface the rotation runbook already tells an
    /// administrator to call first, so it reaches the person deciding whether to rotate or retire.
    /// </remarks>
    [Fact]
    public async Task The_keys_surface_reports_which_key_each_generation_is()
    {
        using var api = _factory.CreateAuthedClient(
            await _factory.GetUserTokenAsync(E2EApiFactory.CryptoAdminEmail, E2EApiFactory.CryptoPassword));
        await FileEncryptedDocumentAsync(api);

        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var status = await TestJson.Get(admin, "/api/encryption/keys");
        var keys = status.GetProperty("keys").EnumerateArray().ToList();
        Assert.NotEmpty(keys);

        // Each generation names the key it holds, as lower-case hex of the right length — so a truncation or
        // a casing change is visible here rather than as a false mismatch alarm later.
        Assert.All(keys, k =>
        {
            var thumbprint = k.GetProperty("thumbprint").GetString()!;
            Assert.Equal(64, thumbprint.Length);
            Assert.Equal(thumbprint.ToLowerInvariant(), thumbprint);
            Assert.Equal(_factory.KekThumbprint(k.GetProperty("generation").GetString()!), thumbprint);
        });

        // And the sample agrees, which is the healthy answer. It is asserted because "agrees" is the only
        // verdict that proves the sampler actually READ an object: `unstamped` is what a sampler that found
        // nothing would also report, so a broken sampler would look calm.
        Assert.Contains(keys, k => k.GetProperty("sampled").GetString() == "agrees");

        // `generations` is unchanged beside it — the list that was "true and useless" still says the same
        // thing, which is the point: a name cannot reveal this, so the thumbprint had to be added beside it.
        Assert.Equal(
            keys.Select(k => k.GetProperty("generation").GetString()).OrderBy(g => g, StringComparer.Ordinal),
            status.GetProperty("generations").EnumerateArray().Select(g => g.GetString())
                .OrderBy(g => g, StringComparer.Ordinal));
    }
}
