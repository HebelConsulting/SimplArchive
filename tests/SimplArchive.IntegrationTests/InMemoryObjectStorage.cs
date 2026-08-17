using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.IntegrationTests;

// The one in-memory object store for the integration tests that construct export/import/version services directly.
// It keeps objects in a dictionary, so a test can seed a blob, assert one round-tripped byte-for-byte, or count
// what was written — without SeaweedFS and without a container.
//
// This exists because there were SIX private copies of it, and they had already drifted apart in exactly the way
// copies do: one `PutObjectAsync` was a no-op that stored nothing, one `GetObjectSizeAsync` always answered 0, and
// one `GetObjectAsync` tolerated a missing key where the others threw. Nothing pointed any of that out — the
// copies were only ever read one at a time. The behaviours below are the SUPERSET (store on put, real sizes,
// strict reads), so a test that used a weaker copy is not weakened by the move; the drifted behaviours were
// checked to be incidental rather than load-bearing before being dropped.
//
// `FailOnPut` is the one deliberate variation, folded in rather than copied out: it makes the Nth upload throw, so
// a test can fail an import midway and assert what survives (ADR 0614). Zero — the default — never fails.
internal sealed class InMemoryObjectStorage : IObjectStorageClient
{
    public Dictionary<string, byte[]> Objects { get; } = [];

    // 1-based index of the upload that should throw; 0 disables. Stands in for the store going away partway
    // through — a network drop, a full disk, an expired credential.
    public int FailOnPut { get; set; }

    public int Puts { get; private set; }

    public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (++Puts == FailOnPut)
        {
            throw new IOException("object storage went away mid-write");
        }

        using var ms = new MemoryStream();
        content.CopyTo(ms);
        Objects[objectKey] = ms.ToArray();
        return Task.CompletedTask;
    }

    // Strict on a missing key: a read of something never written is a bug in the test or the code under test, and
    // answering with empty bytes would hide it.
    public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(Objects[objectKey]));

    public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) =>
        Task.FromResult((long)(Objects.TryGetValue(objectKey, out var bytes) ? bytes.Length : 0));

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Objects.ContainsKey(objectKey));

    public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
    {
        if (Objects.TryGetValue(sourceKey, out var bytes))
        {
            Objects[destinationKey] = bytes;
        }

        return Task.CompletedTask;
    }

    public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        Objects.Remove(objectKey);
        return Task.CompletedTask;
    }

    // Lists what it actually holds — the copies this replaces all returned an empty list regardless of their
    // contents, which is a double that lies. The timestamp is fixed rather than "now" so a listing is
    // deterministic; no test asserts on it today.
    public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StorageObject>>(
            Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => new StorageObject(o.Key, o.Value.Length, DateTimeOffset.UnixEpoch))
                .ToList());

    // Presigned URLs are never fetched by these tests — nothing runs an HTTP client against them — so a constant
    // stands in for the signing the real client does.
    public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
    public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
    public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));

    public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
}
