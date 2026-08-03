using System.Text;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Comparison;

namespace SimplArchive.IntegrationTests;

// The version/checkout comparer (ADR "Document version comparison"; ADR 0517). The check-out stash key is
// extensionless (tenants/{t}/users/{u}/checkout/{doc}), so without a hint a text-file working copy would fall back
// to Tika. These tests prove the toExtensionHint lets an extensionless text side decode directly — no Tika needed.
public class DocumentVersionComparerTests
{
    // A text extractor that always yields nothing — stands in for "Tika not configured / can't extract".
    private sealed class NullTextExtractor : ITextExtractor
    {
        public Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default) => Task.FromResult("");
    }

    // Minimal in-memory object store — only the members the comparer touches do anything.
    private sealed class DictStorage : IObjectStorageClient
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Objects[objectKey]));
        public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(Objects.ContainsKey(objectKey));
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult((long)(Objects.TryGetValue(objectKey, out var b) ? b.Length : 0));
        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StorageObject>>([]);
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private static DictStorage StorageWith(string versionKey, string versionText, string stashKey, string stashText)
    {
        var storage = new DictStorage();
        storage.Objects[versionKey] = Encoding.UTF8.GetBytes(versionText);
        storage.Objects[stashKey] = Encoding.UTF8.GetBytes(stashText);
        return storage;
    }

    [Fact]
    public async Task Extension_hint_lets_an_extensionless_text_stash_diff_without_Tika()
    {
        const string versionKey = "tenants/t/2026/abc.txt";
        const string stashKey = "tenants/t/users/u/checkout/doc"; // no extension
        var comparer = new DocumentVersionComparer(
            StorageWith(versionKey, "line one\nline two\nline three\n", stashKey, "line one\nline two CHANGED\nline three\n"),
            new NullTextExtractor());

        var result = await comparer.CompareAsync(versionKey, stashKey, toExtensionHint: ".txt");

        Assert.True(result.Available);
        Assert.Contains(result.Lines, l => l.Op == DiffOp.Removed && l.Text.Contains("line two"));
        Assert.Contains(result.Lines, l => l.Op == DiffOp.Added && l.Text.Contains("CHANGED"));
    }

    [Fact]
    public async Task Without_the_hint_an_extensionless_stash_is_unavailable_when_Tika_cannot_extract()
    {
        const string versionKey = "tenants/t/2026/abc.txt";
        const string stashKey = "tenants/t/users/u/checkout/doc";
        var comparer = new DocumentVersionComparer(
            StorageWith(versionKey, "original\n", stashKey, "edited\n"),
            new NullTextExtractor());

        // No hint → the extensionless side routes to the (null) extractor → no text → not available.
        var result = await comparer.CompareAsync(versionKey, stashKey);

        Assert.False(result.Available);
    }
}
