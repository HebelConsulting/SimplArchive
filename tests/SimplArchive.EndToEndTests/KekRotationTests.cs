using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;

namespace SimplArchive.EndToEndTests;

// KEK rotation end to end (ADR 0014). The claim that makes rotation affordable is that re-wrapping
// touches METADATA ONLY — so the decisive assertion reads the raw bucket twice, around a rotation, and
// requires the ciphertext bytes to be IDENTICAL while the wrapped DEK and its generation both change.
// A test that only checked "the document still opens" would pass just as happily if the sweep had
// re-encrypted every blob, which is the expensive mistake this design exists to avoid.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class KekRotationTests
{
    private const string Marker = "KEK-ROTATION-MARKER";

    private readonly E2EApiFactory _factory;

    public KekRotationTests(E2EApiFactory factory) => _factory = factory;

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

    private async Task<(string Bucket, string Key)> NewTxtAsync(HashSet<(string Bucket, string Key)> before)
    {
        var added = (await SnapshotAsync()).Except(before)
            .Where(o => o.Key.EndsWith(".txt", StringComparison.Ordinal)).ToList();
        return Assert.Single(added);
    }

    private async Task<(byte[] Bytes, string WrappedDek, string Generation)> ReadRawAsync(string bucket, string key)
    {
        using var s3 = _factory.CreateRawStorageClient();
        using var response = await s3.GetObjectAsync(bucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return (buffer.ToArray(),
            response.Metadata["x-amz-meta-sa-wrapped-dek"],
            response.Metadata["x-amz-meta-sa-kek-generation"]);
    }

    [Fact]
    public async Task Rotation_rewraps_without_rewriting_a_single_blob_and_only_then_allows_retirement()
    {
        using var api = _factory.CreateAuthedClient(
            await _factory.GetUserTokenAsync(E2EApiFactory.CryptoAdminEmail, E2EApiFactory.CryptoPassword));

        // An encrypted object to rotate, filed through the ordinary WebDAV write path.
        var before = await SnapshotAsync();
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var name = $"kek-{Guid.NewGuid():N}.txt";
        using (var dav = _factory.CreateClient())
        {
            using var put = new HttpRequestMessage(HttpMethod.Put,
                $"/SimplArchive/{E2EApiFactory.CryptoTenantName}/{name}")
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes($"{Marker} rotate me\n")),
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{E2EApiFactory.CryptoAdminEmail}:{davPassword}"))) },
            };
            Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        }

        var (bucket, key) = await NewTxtAsync(before);
        var before_ = await ReadRawAsync(bucket, key);

        // Platform administrator: the KEK is an INSTALLATION property, so a tenant admin may not rotate it.
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/encryption/keys")).StatusCode);

        var status = await TestJson.Get(admin, "/api/encryption/keys");
        Assert.True(status.GetProperty("available").GetBoolean());
        var firstGeneration = status.GetProperty("current").GetString()!;
        Assert.Equal(firstGeneration, before_.Generation);

        // Retiring the CURRENT generation is refused — and so is retiring anything while objects remain.
        var rotate = await admin.PostAsync("/api/encryption/keys/rotate", null);
        Assert.Equal(HttpStatusCode.Accepted, rotate.StatusCode);
        var secondGeneration = (await TestJson.Read(rotate)).GetProperty("generation").GetString()!;
        Assert.NotEqual(firstGeneration, secondGeneration);

        // Wait for the background sweep to reach our object.
        (byte[] Bytes, string WrappedDek, string Generation) after = default;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            after = await ReadRawAsync(bucket, key);
            if (after.Generation == secondGeneration)
            {
                break;
            }

            await Task.Delay(500);
        }

        // THE claim: the wrapping moved, the CIPHERTEXT did not. A sweep that re-encrypted blobs would
        // fail here even though every document still opened.
        Assert.Equal(secondGeneration, after.Generation);
        Assert.NotEqual(before_.WrappedDek, after.WrappedDek);
        Assert.Equal(before_.Bytes, after.Bytes);

        // And the document still serves plaintext — the re-wrapped DEK is the same DEK.
        using (var dav = _factory.CreateClient())
        {
            using var get = new HttpRequestMessage(HttpMethod.Get,
                $"/SimplArchive/{E2EApiFactory.CryptoTenantName}/{name}")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{E2EApiFactory.CryptoAdminEmail}:{davPassword}"))) },
            };
            var body = await (await dav.SendAsync(get)).Content.ReadAsStringAsync();
            Assert.Contains(Marker, body, StringComparison.Ordinal);
        }

        // Retirement is gated on the sweep having finished — the one check the service cannot make.
        var remaining = (await TestJson.Get(admin, "/api/encryption/keys")).GetProperty("remaining").GetInt32();
        var retire = await admin.DeleteAsync($"/api/encryption/keys/{firstGeneration}");
        if (remaining > 0)
        {
            Assert.Equal(HttpStatusCode.Conflict, retire.StatusCode);
            Assert.Equal("KEK_RETIREMENT_REFUSED",
                JsonSerializer.Deserialize<JsonElement>(await retire.Content.ReadAsStringAsync())
                    .GetProperty("errorCode").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, retire.StatusCode);
        }
    }
}
