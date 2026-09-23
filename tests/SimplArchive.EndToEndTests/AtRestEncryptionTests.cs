using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;

namespace SimplArchive.EndToEndTests;

// At-rest encryption (ADR 0818): on a gated tenant, every server-side write stores CIPHERTEXT with the
// wrapped DEK riding as object metadata, every read serves plaintext, and the presigned download URL is
// swapped for the core-served token door. The decisive assertions read the BUCKET raw — the whole feature
// is a difference between stored and served bytes, which no API-level read can show.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AtRestEncryptionTests
{
    private const string Marker = "ATREST-CLEARTEXT-MARKER";

    private readonly E2EApiFactory _factory;

    public AtRestEncryptionTests(E2EApiFactory factory) => _factory = factory;

    private static async Task<string> PutOverWebDavAsync(E2EApiFactory factory, HttpClient api, string email, string folder)
    {
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var name = $"atrest-{Guid.NewGuid():N}.txt";
        using var dav = factory.CreateClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{folder}/{name}")
        {
            // A plain .txt on purpose: an .eml is CLASSIFIED into an email document and renamed by its
            // subject, so the WebDAV GET below would 404 on the original path.
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes($"{Marker} stored at rest\n")),
            Headers = { Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}"))) },
        };
        Assert.Equal(System.Net.HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        return name;
    }

    // A filed document's object key is a fresh GUID content key (ObjectKeyBuilder), not the filename —
    // so the raw object is located by DIFFING the bucketscape around the write. The collection runs
    // single-threaded, so the only new content.eml is ours.
    private async Task<HashSet<(string Bucket, string Key)>> SnapshotAsync()
    {
        using var s3 = _factory.CreateRawStorageClient();
        var keys = new HashSet<(string, string)>();
        foreach (var bucket in (await s3.ListBucketsAsync()).Buckets)
        {
            var objects = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket.BucketName });
            foreach (var o in objects.S3Objects ?? [])
            {
                keys.Add((bucket.BucketName, o.Key));
            }
        }

        return keys;
    }

    private async Task<GetObjectResponse> FindNewContentAsync(HashSet<(string Bucket, string Key)> before)
    {
        var addedAll = (await SnapshotAsync()).Except(before).ToList();
        var added = addedAll.Where(o => o.Key.EndsWith(".txt", StringComparison.Ordinal)).ToList();
        if (added.Count != 1)
        {
            throw new Xunit.Sdk.XunitException(
                $"expected exactly one new .txt object, found {added.Count}; every new object: "
                + string.Join(", ", addedAll.Select(o => $"{o.Bucket}/{o.Key}")));
        }

        using var s3 = _factory.CreateRawStorageClient();
        return await s3.GetObjectAsync(added[0].Bucket, added[0].Key);
    }

    [Fact]
    public async Task A_gated_tenants_write_stores_ciphertext_and_every_door_serves_plaintext()
    {
        // crypt@ is the seeded Crypto admin — the ONE tenant Encryption__Tenants lists in this factory.
        using var api = _factory.CreateAuthedClient(
            await _factory.GetUserTokenAsync(E2EApiFactory.CryptoAdminEmail, E2EApiFactory.CryptoPassword));
        var before = await SnapshotAsync();
        var fileName = await PutOverWebDavAsync(_factory, api, E2EApiFactory.CryptoAdminEmail, E2EApiFactory.CryptoTenantName);

        // THE claim: the bucket holds ciphertext, and the wrapped DEK rides as object metadata.
        using var raw = await FindNewContentAsync(before);
        using var buffer = new MemoryStream();
        await raw.ResponseStream.CopyToAsync(buffer);
        var storedText = Encoding.ASCII.GetString(buffer.ToArray());
        Assert.DoesNotContain(Marker, storedText, StringComparison.Ordinal);
        Assert.NotNull(raw.Metadata["x-amz-meta-sa-wrapped-dek"]);
        Assert.Equal("kek-v1", raw.Metadata["x-amz-meta-sa-kek-generation"]);

        // The WebDAV door serves plaintext — the decorator decrypts on the way out...
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        using var dav = _factory.CreateClient();
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/SimplArchive/{E2EApiFactory.CryptoTenantName}/{fileName}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{E2EApiFactory.CryptoAdminEmail}:{davPassword}"))) },
        };
        var davResponse = await dav.SendAsync(get);
        Assert.Equal(System.Net.HttpStatusCode.OK, davResponse.StatusCode);
        Assert.Contains(Marker, await davResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ...and the indexer saw plaintext too: search finds the marker, which also hands us the document.
        JsonElement hit = default;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var results = await TestJson.Get(api, $"/api/search?q={Marker}");
            if (results.GetProperty("results").EnumerateArray().FirstOrDefault(
                    i => i.GetProperty("name").GetString()!.StartsWith("atrest-", StringComparison.Ordinal))
                is { ValueKind: JsonValueKind.Object } found)
            {
                hit = found;
                break;
            }

            await Task.Delay(500);
        }

        Assert.Equal(JsonValueKind.Object, hit.ValueKind);

        // The app download door: the presigned URL is SWAPPED for the core-served token door on encrypted
        // objects, and that door serves plaintext to a bare, unauthenticated GET — presigned semantics.
        // The hit advertises its versions collection; the current version carries the download link.
        var versionsHref = hit.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "versions").GetProperty("href").GetString()!;
        var versions = await TestJson.Get(api, versionsHref);
        var downloadHref = versions.GetProperty("versions").EnumerateArray().Last()
            .GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;
        Assert.StartsWith("/api/encrypted-content", downloadHref, StringComparison.Ordinal);
        using var anonymous = _factory.CreateClient();
        Assert.Contains(Marker, await anonymous.GetStringAsync(downloadHref), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unlisted_tenants_write_stays_plaintext_with_no_metadata()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Plain{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"atrest-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "atrest-1234", "AtRest User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "atrest-1234"));
        var before = await SnapshotAsync();
        await PutOverWebDavAsync(_factory, api, email, repoName);

        using var raw = await FindNewContentAsync(before);
        using var buffer = new MemoryStream();
        await raw.ResponseStream.CopyToAsync(buffer);
        Assert.Contains(Marker, Encoding.ASCII.GetString(buffer.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain("sa-wrapped-dek", raw.Metadata.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
