using System.Security.Cryptography;
using System.Text;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// A module's presigned uploads (ABI 1.12, ADR 0910): no row, the object is the state, the module segment comes from
// the request, and the module's own limit rides in the id. The full cycle against real storage is
// ModuleControllerTests.A_module_receives_a_presigned_upload_reads_it_and_files_it_without_proxying_the_bytes.
public class ModuleUploadsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly InMemoryObjectStorage _storage = new();

    private ModuleUploads As(string moduleId) =>
        new(_storage, new CurrentTenantAccessor { TenantId = Tenant }, new ModuleIdentityAccessor { ModuleId = moduleId });

    private void Upload(string moduleId, ModuleUpload upload, byte[] bytes) =>
        _storage.Objects[$"{ObjectKeyPrefixes.ModuleUploads(Tenant, moduleId)}{upload.UploadId.Split('.')[0]}"] = bytes;

    [Fact]
    public async Task The_lifetime_defaults_to_half_an_hour_and_is_clamped_to_one()
    {
        var before = DateTimeOffset.UtcNow;

        var defaulted = await As("m").BeginAsync(10, null, CancellationToken.None);
        var clamped = await As("m").BeginAsync(10, TimeSpan.FromHours(5), CancellationToken.None);

        Assert.InRange(defaulted.ExpiresAt - before, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        Assert.InRange(clamped.ExpiresAt - before, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.EndsWith(".10", defaulted.UploadId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_upload_opens_as_a_seekable_copy_and_moves_with_its_hash_and_size()
    {
        var uploads = As("m");
        var upload = await uploads.BeginAsync(100, null, CancellationToken.None);
        var bytes = Encoding.UTF8.GetBytes("hello upload");
        Upload("m", upload, bytes);

        await using (var read = await uploads.OpenAsync(upload.UploadId, CancellationToken.None))
        {
            Assert.True(read.CanSeek);
            Assert.Equal(bytes.Length, read.Length);
        }

        var (sha256, size) = await uploads.MoveAsync(upload.UploadId, "tenants/x/archive/key", CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha256);
        Assert.Equal(bytes.Length, size);
        Assert.Equal(bytes, _storage.Objects["tenants/x/archive/key"]);
        Assert.DoesNotContain(_storage.Objects.Keys, k => k.Contains("module-uploads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Larger_than_allowed_is_refused_and_discarded()
    {
        var uploads = As("m");
        var upload = await uploads.BeginAsync(4, null, CancellationToken.None);
        Upload("m", upload, new byte[5]);

        await Assert.ThrowsAsync<ModuleUploadTooLargeException>(() => uploads.OpenAsync(upload.UploadId, CancellationToken.None));
        Assert.Empty(_storage.Objects);
    }

    [Fact]
    public async Task Another_module_cannot_open_an_upload_it_did_not_begin()
    {
        var upload = await As("first").BeginAsync(100, null, CancellationToken.None);
        Upload("first", upload, [1, 2, 3]);

        // The same id, presented by a different module: its own area holds nothing under it.
        await Assert.ThrowsAsync<ModuleUploadMissingException>(() => As("second").OpenAsync(upload.UploadId, CancellationToken.None));
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("0123456789abcdef0123456789abcdef.0")]
    [InlineData("0123456789abcdef0123456789abcdef.-5")]
    [InlineData("../../other.10")]
    public async Task An_id_this_host_did_not_issue_is_refused(string uploadId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => As("m").OpenAsync(uploadId, CancellationToken.None));
    }
}
