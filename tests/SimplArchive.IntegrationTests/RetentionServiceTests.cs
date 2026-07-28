using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.LegalHolds;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.LegalHolds;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Retention;

namespace SimplArchive.IntegrationTests;

// Verifies the retention sweep (ADR "Retention policies (auto-disposition)"): a leaf document whose assigned
// mask's retention period has elapsed is auto-soft-deleted; a not-yet-expired document, a legal-held one, and a
// document-with-children are all left alone; the document-date (not the filed date) drives eligibility.
public class RetentionServiceTests
{
    private sealed class NoOpIndexQueue : IDocumentIndexQueue
    {
        public Task EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnqueueManyAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<Guid> DisposedTargets { get; } = [];
        public Task RecordAsync(string action, string? targetType = null, Guid? targetId = null, string? targetName = null, string? details = null, Guid? tenantId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordForActorAsync(AuditActorType actorType, Guid actorId, string actorName, Guid tenantId, string action, string? targetType = null, Guid? targetId = null, string? targetName = null, string? details = null, CancellationToken cancellationToken = default)
        {
            if (targetId is { } id) DisposedTargets.Add(id);
            return Task.CompletedTask;
        }
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    [Fact]
    public async Task Disposes_expired_leaf_documents_but_spares_held_recent_and_parent_documents()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "u@acme.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenant.Id, CreatedAt = DateTimeOffset.UtcNow };
        var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenant.Id, MaskId = mask.Id, Name = "Retained", RetentionYears = 5, CreatedAt = DateTimeOffset.UtcNow };

        var old = DateTimeOffset.UtcNow.AddYears(-10);

        // expired leaf (no version → anchor = CreatedAt 10y ago, +5 < today) → disposed
        var expired = Doc(tenant.Id, user.Id, maskVersion.Id, "expired", old);
        // not expired (filed today) → kept
        var recent = Doc(tenant.Id, user.Id, maskVersion.Id, "recent", DateTimeOffset.UtcNow);
        // expired but under an active legal hold → kept
        var held = Doc(tenant.Id, user.Id, maskVersion.Id, "held", old);
        // expired but has a child (not a leaf) → kept
        var parent = Doc(tenant.Id, user.Id, maskVersion.Id, "parent", old);
        var child = Doc(tenant.Id, user.Id, null, "child", DateTimeOffset.UtcNow, parent.Id);
        // filed 10y ago BUT its version's DocumentDate is today → anchor = DocumentDate → kept
        var reissued = Doc(tenant.Id, user.Id, maskVersion.Id, "reissued", old);
        var reissuedVersion = new DocumentVersion { Id = Guid.NewGuid(), TenantId = tenant.Id, DocumentId = reissued.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = new string('0', 64), ObjectKey = "k", DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow), CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };

        var hold = new LegalHold { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Hold", PlacedByUserId = user.Id, PlacedAt = DateTimeOffset.UtcNow };
        var holdItem = new LegalHoldItem { Id = Guid.NewGuid(), TenantId = tenant.Id, LegalHoldId = hold.Id, DocumentId = held.Id, CreatedAt = DateTimeOffset.UtcNow };

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Masks.Add(mask);
            seed.MaskVersions.Add(maskVersion);
            seed.Documents.AddRange(expired, recent, held, parent, child, reissued);
            seed.DocumentVersions.Add(reissuedVersion);
            seed.LegalHolds.Add(hold);
            seed.LegalHoldItems.Add(holdItem);
            await seed.SaveChangesAsync();
        }

        var audit = new RecordingAuditRecorder();
        int disposed;
        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = new RetentionService(act, tenantAccessor, new LegalHoldService(act), new NoOpIndexQueue(), audit);
            disposed = await service.SweepAsync();
        }

        Assert.Equal(1, disposed);
        Assert.Equal(new[] { expired.Id }, audit.DisposedTargets);

        using var read = CreateContext(connection, tenantAccessor);
        var deleted = await read.Documents.IgnoreQueryFilters().Where(d => d.DeletedAt != null).Select(d => d.Id).ToListAsync();
        Assert.Equal(new[] { expired.Id }, deleted);
    }

    [Fact]
    public async Task Review_mode_and_a_retention_extension_both_spare_an_expired_leaf()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        // Two tenants: one in review mode (never auto-disposes), one in auto mode.
        var reviewTenant = new Tenant { Id = Guid.NewGuid(), Name = "Review", RequireDispositionReview = true, CreatedAt = DateTimeOffset.UtcNow };
        var autoTenant = new Tenant { Id = Guid.NewGuid(), Name = "Auto", CreatedAt = DateTimeOffset.UtcNow };
        var old = DateTimeOffset.UtcNow.AddYears(-10);
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(3);

        var seeds = new List<(Tenant Tenant, Document Expired, Document Extended)>();
        var docs = new List<Document>();
        foreach (var tenant in new[] { reviewTenant, autoTenant })
        {
            var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = $"u@{tenant.Name}.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
            var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenant.Id, CreatedAt = DateTimeOffset.UtcNow };
            var mv = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenant.Id, MaskId = mask.Id, Name = "Retained", RetentionYears = 5, CreatedAt = DateTimeOffset.UtcNow };
            var expired = Doc(tenant.Id, user.Id, mv.Id, "expired", old);
            var extended = Doc(tenant.Id, user.Id, mv.Id, "extended", old);
            extended.RetentionOverrideUntil = future; // retained past its disposition date

            using var seed = CreateContext(connection, tenantAccessor);
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Masks.Add(mask);
            seed.MaskVersions.Add(mv);
            seed.Documents.AddRange(expired, extended);
            await seed.SaveChangesAsync();
            seeds.Add((tenant, expired, extended));
            docs.AddRange(expired, extended);
        }

        int disposed;
        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = new RetentionService(act, tenantAccessor, new LegalHoldService(act), new NoOpIndexQueue(), new RecordingAuditRecorder());
            disposed = await service.SweepAsync();
        }

        // Only the auto tenant's non-extended expired doc is disposed.
        Assert.Equal(1, disposed);
        using var read = CreateContext(connection, tenantAccessor);
        var deleted = await read.Documents.IgnoreQueryFilters().Where(d => d.DeletedAt != null).Select(d => d.Id).ToListAsync();
        Assert.Equal(new[] { seeds[1].Expired.Id }, deleted); // seeds[1] = autoTenant
    }

    private static Document Doc(Guid tenantId, Guid userId, Guid? maskVersionId, string name, DateTimeOffset createdAt, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ParentId = parentId,
        MaskVersionId = maskVersionId,
        Name = name,
        CreatedByUserId = userId,
        CreatedAt = createdAt,
    };
}
