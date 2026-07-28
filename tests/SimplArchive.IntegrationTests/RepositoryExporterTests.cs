using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Verifies RepositoryExporter (ADR "Repository export"): the .zip carries the subtree, versions + deduped blobs,
// index values, mask definitions, principals, comments; the filters (active-only version selection, document
// date) apply; and ancestor folders of an included document are kept while empty branches are pruned.
public class RepositoryExporterTests
{
    private sealed class FakeStorage : IObjectStorageClient
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Objects.TryGetValue(objectKey, out var b) ? b : []));
        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(Objects.ContainsKey(objectKey));
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult((long)(Objects.TryGetValue(objectKey, out var __b) ? __b.Length : 0));
        public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StorageObject>>([]);
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private readonly Guid _tenantId = Guid.NewGuid();
    private SimplArchiveDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor { TenantId = _tenantId });

    // A seeded fixture shared by the tests: Repo → Folder → {DocA (2 versions, mask, index value, comment),
    // DocB (1 version, service-account author)} + an EmptyFolder with no documents.
    private sealed record Seed(Guid RepoId, Guid FolderId, Guid EmptyFolderId, Guid DocAId, Guid DocBId, Guid V1Id, Guid V2Id, string V1Sha, string V2Sha, string DocBSha);

    private async Task<Seed> SeedAsync(SqliteConnection connection, FakeStorage storage, DateOnly v2DocDate)
    {
        using var context = CreateContext(connection);
        var userId = Guid.NewGuid();
        var svcId = Guid.NewGuid();
        context.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = _tenantId, Email = "jane@acme.test", DisplayName = "Jane Doe", CreatedAt = DateTimeOffset.UtcNow });
        context.ServiceAccounts.Add(new ServiceAccount { Id = svcId, TenantId = _tenantId, Name = "Importer", OpenIddictApplicationClientId = "imp", CreatedAt = DateTimeOffset.UtcNow });

        var mask = new Mask { Id = Guid.NewGuid(), TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow };
        var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = _tenantId, MaskId = mask.Id, Name = "Invoice", RetentionYears = 7, CreatedAt = DateTimeOffset.UtcNow };
        var field = new FieldDefinition { Id = Guid.NewGuid(), TenantId = _tenantId, MaskVersionId = maskVersion.Id, Name = "Keywords", DataType = FieldDataType.Text, CreatedAt = DateTimeOffset.UtcNow };
        context.Masks.Add(mask);
        context.MaskVersions.Add(maskVersion);
        context.FieldDefinitions.Add(field);

        var repo = new Document { Id = Guid.NewGuid(), TenantId = _tenantId, Name = "Repo", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        var folder = new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = repo.Id, Name = "Folder", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        var emptyFolder = new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = repo.Id, Name = "EmptyFolder", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        var docA = new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = folder.Id, Name = "DocA", MaskVersionId = maskVersion.Id, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        var docB = new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = folder.Id, Name = "DocB", CreatedByServiceAccountId = svcId, CreatedAt = DateTimeOffset.UtcNow };
        context.Documents.AddRange(repo, folder, emptyFolder, docA, docB);

        var (v1Sha, v2Sha, bSha) = (Sha('1'), Sha('2'), Sha('3'));
        storage.Objects["k/v1.pdf"] = Encoding.UTF8.GetBytes("v1-bytes");
        storage.Objects["k/v2.pdf"] = Encoding.UTF8.GetBytes("v2-bytes");
        storage.Objects["k/b.txt"] = Encoding.UTF8.GetBytes("b-bytes");

        var v1 = new DocumentVersion { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentId = docA.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = v1Sha, ObjectKey = "k/v1.pdf", DocumentDate = new DateOnly(2024, 1, 1), CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        var v2 = new DocumentVersion { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentId = docA.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 2, Sha256Hash = v2Sha, ObjectKey = "k/v2.pdf", DocumentDate = v2DocDate, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        var bVer = new DocumentVersion { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentId = docB.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = bSha, ObjectKey = "k/b.txt", DocumentDate = new DateOnly(2024, 6, 1), CreatedByServiceAccountId = svcId, CreatedAt = DateTimeOffset.UtcNow };
        context.DocumentVersions.AddRange(v1, v2, bVer);

        context.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentId = docA.Id, FieldDefinitionId = field.Id, Value = "contract" });
        context.DocumentComments.Add(new DocumentComment { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentId = docA.Id, Body = "Looks good", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });

        await context.SaveChangesAsync();
        return new Seed(repo.Id, folder.Id, emptyFolder.Id, docA.Id, docB.Id, v1.Id, v2.Id, v1Sha, v2Sha, bSha);
    }

    private static string Sha(char c) => new(c, 64);

    private static async Task<Dictionary<string, string>> ReadArchiveAsync(byte[] zipBytes)
    {
        var entries = new Dictionary<string, string>();
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return entries;
    }

    private async Task<byte[]> ExportAsync(SqliteConnection connection, FakeStorage storage, Guid rootId, RepositoryExportFilters filters)
    {
        using var context = CreateContext(connection);
        var output = new MemoryStream();
        await new RepositoryExporter(context, storage).ExportAsync(rootId, filters, false, output, CancellationToken.None);
        return output.ToArray();
    }

    private static RepositoryExportFilters AllVersions => new(null, null, null, null, ExportVersionSelection.All, null);

    [Fact]
    public async Task Exports_the_subtree_with_metadata_masks_principals_and_blobs()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var storage = new FakeStorage();
        var seed = await SeedAsync(connection, storage, new DateOnly(2025, 1, 1));

        var entries = await ReadArchiveAsync(await ExportAsync(connection, storage, seed.RepoId, AllVersions));

        // Manifest counts.
        var manifest = JsonDocument.Parse(entries["manifest.json"]).RootElement;
        Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
        Assert.Equal("Acme", manifest.GetProperty("source").GetProperty("tenantName").GetString());
        var counts = manifest.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("versions").GetInt32());     // v1, v2, docB
        Assert.Equal(3, counts.GetProperty("blobs").GetInt32());
        Assert.Equal(1, counts.GetProperty("comments").GetInt32());
        Assert.Equal(1, counts.GetProperty("indexValues").GetInt32());

        // Documents: Repo, Folder, DocA, DocB — the EmptyFolder is pruned (no included descendant).
        var docNames = entries["tree/documents.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("name").GetString()!).ToHashSet();
        Assert.Equal(new HashSet<string> { "Repo", "Folder", "DocA", "DocB" }, docNames);

        // Mask definition + its field are exported, flagged not-well-known, retention preserved.
        var mask = JsonDocument.Parse(entries["masks/masks.json"]).RootElement[0];
        Assert.False(mask.GetProperty("wellKnown").GetBoolean());
        Assert.Equal(7, mask.GetProperty("version").GetProperty("retentionYears").GetInt32());
        Assert.Equal("Keywords", mask.GetProperty("fields")[0].GetProperty("name").GetString());

        // Principals: the user (by email) + the service account (by name).
        var principals = JsonDocument.Parse(entries["principals/principals.json"]).RootElement;
        Assert.Equal("jane@acme.test", principals.GetProperty("users")[0].GetProperty("email").GetString());
        Assert.Equal("Importer", principals.GetProperty("serviceAccounts")[0].GetProperty("name").GetString());

        // Blobs are content-addressed and round-trip byte-for-byte.
        Assert.Equal("v1-bytes", entries[$"blobs/{seed.V1Sha}"]);
        Assert.Equal("v2-bytes", entries[$"blobs/{seed.V2Sha}"]);
        Assert.Equal("b-bytes", entries[$"blobs/{seed.DocBSha}"]);
    }

    [Fact]
    public async Task Active_only_exports_the_current_version_not_a_gated_newer_one()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var storage = new FakeStorage();
        var seed = await SeedAsync(connection, storage, new DateOnly(2025, 1, 1));

        // Put DocA v2 into review (gated) — so the "current" version an end user sees is v1.
        using (var ctx = CreateContext(connection))
        {
            ctx.WorkflowStates.Add(new WorkflowState { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentVersionId = seed.V2Id, Status = WorkflowStatus.InReview, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var filters = new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.ActiveOnly, null);
        var entries = await ReadArchiveAsync(await ExportAsync(connection, storage, seed.RepoId, filters));

        var exportedVersionIds = entries["tree/versions.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(seed.V1Id, exportedVersionIds);       // v1 = the current (Released/never-submitted) version
        Assert.DoesNotContain(seed.V2Id, exportedVersionIds); // v2 is gated in review
        Assert.False(entries.ContainsKey($"blobs/{seed.V2Sha}"));
    }

    [Fact]
    public async Task Document_date_filter_excludes_out_of_range_versions_and_prunes_empty_branches()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var storage = new FakeStorage();
        // DocA v2 dated 2025; both DocA v1 (2024) and DocB (2024-06) fall outside a 2025-only window.
        var seed = await SeedAsync(connection, storage, new DateOnly(2025, 3, 1));

        var filters = new RepositoryExportFilters(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), null, null, ExportVersionSelection.All, null);
        var entries = await ReadArchiveAsync(await ExportAsync(connection, storage, seed.RepoId, filters));

        // Only DocA v2 survives → Repo, Folder, DocA kept; DocB (and EmptyFolder) pruned.
        var docNames = entries["tree/documents.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("name").GetString()!).ToHashSet();
        Assert.Equal(new HashSet<string> { "Repo", "Folder", "DocA" }, docNames);

        var versionIds = entries["tree/versions.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Equal([seed.V2Id], versionIds);
    }
}
