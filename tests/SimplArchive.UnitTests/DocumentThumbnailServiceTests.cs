using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.UnitTests;

// Drawing the external-link landing page's thumbnail (issue #476).
//
// Unit tests rather than end-to-end, because neither rasteriser exists in a test run: NetVips ships musl-only
// natives for the Alpine image, and the PDF path needs the OCR sidecar, which no test fixture starts. What CAN
// be pinned down here is everything around the raster — which documents are eligible, what a failing sidecar
// does to a share, whether the cache is honoured, and how the page count is read — and those are where the
// behaviour that matters lives.
public class DocumentThumbnailServiceTests
{
    private const string VersionKey = "tenants/t/2026/abc/content.pdf";
    private const string ThumbnailKey = "tenants/t/2026/abc/content.thumb.png";

    [Fact]
    public async Task A_pdf_is_drawn_once_stored_beside_the_version_and_its_page_count_read()
    {
        var storage = new FakeStorage();
        var sidecar = new FakeSidecar(Png, pageCount: "12");
        var service = Build(storage, sidecar, displayKey: VersionKey);

        var result = await service.EnsureThumbnailAsync(VersionKey);

        Assert.NotNull(result);
        Assert.Equal(ThumbnailKey, result!.ObjectKey);
        Assert.Equal(12, result.PageCount);

        // Cached beside the version's other derived artifacts, not at a flat sibling key.
        Assert.True(storage.Objects.ContainsKey(ThumbnailKey));
        Assert.Equal("image/png", storage.ContentTypes[ThumbnailKey]);
    }

    [Fact]
    public async Task An_already_drawn_thumbnail_is_not_drawn_again()
    {
        var storage = new FakeStorage();
        storage.Objects[ThumbnailKey] = Png;
        var sidecar = new FakeSidecar(Png, pageCount: "12");
        var service = Build(storage, sidecar, displayKey: VersionKey);

        var result = await service.EnsureThumbnailAsync(VersionKey);

        Assert.NotNull(result);
        Assert.Equal(0, sidecar.Calls); // the whole point of the cache

        // Null, not a re-read: the count cannot be recovered from a cached PNG, and the caller already stored it
        // the first time. Reporting a wrong number would be worse than reporting none.
        Assert.Null(result!.PageCount);
    }

    [Theory]
    [InlineData("tenants/t/2026/abc/content.bin")]
    [InlineData("tenants/t/2026/abc/content.zip")]
    [InlineData("tenants/t/2026/abc/content.txt")]
    public async Task A_format_with_no_pdf_and_no_image_form_gets_nothing(string displayKey)
    {
        var storage = new FakeStorage();
        var sidecar = new FakeSidecar(Png, pageCount: "1");
        var service = Build(storage, sidecar, displayKey);

        Assert.Null(await service.EnsureThumbnailAsync(VersionKey));
        Assert.Equal(0, sidecar.Calls); // never even offered to the sidecar
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task A_failing_sidecar_costs_the_thumbnail_and_nothing_else()
    {
        var storage = new FakeStorage();
        var sidecar = new FakeSidecar(Png, pageCount: "3", status: System.Net.HttpStatusCode.InternalServerError);
        var service = Build(storage, sidecar, displayKey: VersionKey);

        // Null rather than a throw: this runs inside link CREATION, and a share that failed because a picture
        // could not be drawn would be a poor trade.
        Assert.Null(await service.EnsureThumbnailAsync(VersionKey));
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task An_unreadable_page_count_still_yields_a_thumbnail()
    {
        var storage = new FakeStorage();
        var sidecar = new FakeSidecar(Png, pageCount: "0"); // the sidecar's "I could not tell"
        var service = Build(storage, sidecar, displayKey: VersionKey);

        var result = await service.EnsureThumbnailAsync(VersionKey);

        // The badge is an extra; the picture is the point.
        Assert.NotNull(result);
        Assert.Null(result!.PageCount);
        Assert.True(storage.Objects.ContainsKey(ThumbnailKey));
    }

    [Fact]
    public async Task The_read_path_never_draws()
    {
        var storage = new FakeStorage();
        var sidecar = new FakeSidecar(Png, pageCount: "5");
        var service = Build(storage, sidecar, displayKey: VersionKey);

        // Nothing cached: the anonymous landing page must not be a lever for making the server rasterise.
        Assert.Null(await service.GetThumbnailUrlAsync(VersionKey, TimeSpan.FromMinutes(2)));
        Assert.Equal(0, sidecar.Calls);

        storage.Objects[ThumbnailKey] = Png;
        Assert.NotNull(await service.GetThumbnailUrlAsync(VersionKey, TimeSpan.FromMinutes(2)));
    }

    private static DocumentThumbnailService Build(FakeStorage storage, FakeSidecar sidecar, string displayKey) =>
        new(new HttpClient(sidecar) { BaseAddress = new Uri("http://ocr.test/") },
            storage,
            new FakePreview(displayKey),
            NullLogger<DocumentThumbnailService>.Instance);

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private sealed class FakeSidecar : HttpMessageHandler
    {
        private readonly byte[] _png;
        private readonly string _pageCount;
        private readonly System.Net.HttpStatusCode _status;

        public FakeSidecar(byte[] png, string pageCount, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        {
            _png = png;
            _pageCount = pageCount;
            _status = status;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(_status) { Content = new ByteArrayContent(_png) };
            response.Headers.TryAddWithoutValidation("X-Page-Count", _pageCount);
            return Task.FromResult(response);
        }
    }

    private sealed class FakePreview : IDocumentPreviewService
    {
        private readonly string _displayKey;

        public FakePreview(string displayKey) => _displayKey = displayKey;

        public Task<string> GetDisplayObjectKeyAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_displayKey);

        public Task<IReadOnlyList<string>> GetDisplayObjectKeysAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([_displayKey]);

        public Task<DocumentPreview?> GetPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<DocumentPreview?>(null);

        public Task<PreviewPages?> GetPreviewPagesAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<PreviewPages?>(null);
    }

    private sealed class FakeStorage : IObjectStorageClient
    {
        public Dictionary<string, byte[]> Objects { get; } = [];

        public Dictionary<string, string> ContentTypes { get; } = [];

        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.ContainsKey(objectKey));

        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Objects.TryGetValue(objectKey, out var bytes) ? bytes : Png));

        public async Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[objectKey] = buffer.ToArray();
            ContentTypes[objectKey] = contentType;
        }

        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri($"http://storage.test/{objectKey}"));

        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri($"http://storage.test/{objectKey}"));

        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri($"http://storage.test/{objectKey}"));

        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            Objects.Remove(objectKey);
            return Task.CompletedTask;
        }

        // The rest of the storage surface, which drawing a thumbnail has no business touching. They throw rather
        // than returning a default, so a future change that starts calling one fails loudly here instead of
        // passing against a silently empty answer.
        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
