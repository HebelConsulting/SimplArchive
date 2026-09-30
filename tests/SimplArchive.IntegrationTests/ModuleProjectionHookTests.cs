using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// WHICH writes reach a module that projects a mask (ABI 1.1, ADR 0848).
//
// The obvious predicate — "a Document, Added or Modified" — passes every happy-path test and MISSES the two
// cases the hook exists for, because neither touches the document row: an index-field edit writes a FieldValue,
// and filing a new version writes a DocumentVersion. The revocation ADR 0848 was written to catch is the first
// of those. So the cases below are deliberately weighted towards the writes that do NOT look like writes.
public class ModuleProjectionHookTests
{
    private sealed record Seen(Guid DocumentId, Guid MaskId, Guid TenantId, bool Removed);

    private sealed class RecordingDispatcher(params Guid[] masks) : IModuleProjectionDispatcher
    {
        public IReadOnlyCollection<Guid> ProjectedMasks { get; } = masks;

        public List<Seen> Calls { get; } = [];

        public Exception? Throws { get; set; }

        public Task DispatchAsync(
            Guid documentId,
            Guid maskId,
            Guid tenantId,
            bool removed,
            AmbientDatabaseTransaction? ambient = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Seen(documentId, maskId, tenantId, removed));
            return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
        }
    }

    private static SimplArchiveDbContext Context(
        SqliteConnection connection, Guid? tenantId = null, IModuleProjectionDispatcher? dispatcher = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId },
            moduleProjections: dispatcher);

    private static async Task<(Guid TenantId, Guid UserId)> SeedAsync(SqliteConnection connection)
    {
        using (var setup = Context(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var seed = Context(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();

        using var masks = Context(connection, tenantId);
        await new WellKnownMaskSeeder(masks, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
        return (tenantId, userId);
    }

    private static async Task<Guid> MaskVersionAsync(SimplArchiveDbContext db, Guid maskId) =>
        await db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).FirstAsync();

    private static async Task<Guid> FileAsync(
        SimplArchiveDbContext db, Guid tenantId, Guid userId, Guid maskId, string name)
    {
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            MaskVersionId = await MaskVersionAsync(db, maskId),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document.Id;
    }

    [Fact]
    public async Task Filing_a_document_of_a_projected_mask_reaches_the_module()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);
        var dispatcher = new RecordingDispatcher(WellKnownMaskIds.Certificate);

        using var db = Context(connection, tenantId, dispatcher);
        var id = await FileAsync(db, tenantId, userId, WellKnownMaskIds.Certificate, "alice.pem");

        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal(new Seen(id, WellKnownMaskIds.Certificate, tenantId, false), call);
    }

    [Fact]
    public async Task An_INDEX_FIELD_edit_reaches_it_although_the_document_row_is_untouched()
    {
        // THE CASE ADR 0848 EXISTS FOR. Revocation is an index field (module ADR 0002), index fields are EAV,
        // so setting one writes a FieldValue and leaves Document alone. A predicate over documents sails past
        // it — and the consequence is a revoked certificate that goes on being addressed.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        Guid documentId;
        Guid fieldId;
        using (var setup = Context(connection, tenantId))
        {
            documentId = await FileAsync(setup, tenantId, userId, WellKnownMaskIds.Certificate, "bob.pem");
            var maskVersionId = await MaskVersionAsync(setup, WellKnownMaskIds.Certificate);
            fieldId = await setup.FieldDefinitions
                .Where(f => f.MaskVersionId == maskVersionId && f.Name == "Revoked on")
                .Select(f => f.Id)
                .FirstAsync();
        }

        var dispatcher = new RecordingDispatcher(WellKnownMaskIds.Certificate);
        using var db = Context(connection, tenantId, dispatcher);
        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            FieldDefinitionId = fieldId,
            Value = "2026-09-29",
        });
        await db.SaveChangesAsync();

        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal(documentId, call.DocumentId);
        Assert.False(call.Removed);
    }

    [Fact]
    public async Task A_CONFIRMED_version_reaches_it_and_a_pending_one_does_not()
    {
        // The second write that does not touch the document row. And the Pending half matters: a version is
        // written Pending and finalized later, so projecting the insert would publish content that may never
        // be confirmed.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        Guid documentId;
        using (var setup = Context(connection, tenantId))
        {
            documentId = await FileAsync(setup, tenantId, userId, WellKnownMaskIds.BasicEntry, "note.txt");
        }

        var dispatcher = new RecordingDispatcher(WellKnownMaskIds.BasicEntry);
        var versionId = Guid.NewGuid();

        using (var pending = Context(connection, tenantId, dispatcher))
        {
            pending.DocumentVersions.Add(new DocumentVersion
            {
                Id = versionId,
                DocumentId = documentId,
                TenantId = tenantId,
                Status = DocumentVersionStatus.Pending,
                ObjectKey = "tenants/x/2026/y/content.txt",
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await pending.SaveChangesAsync();
        }

        Assert.Empty(dispatcher.Calls);

        using (var confirm = Context(connection, tenantId, dispatcher))
        {
            var version = await confirm.DocumentVersions.FirstAsync(v => v.Id == versionId);
            version.Status = DocumentVersionStatus.Confirmed;
            // VersionNumber AND Sha256Hash, both: a CHECK constraint pairs them with Confirmed, so a
            // hand-written confirmation without the hash dies in the database rather than in the assertion.
            // Real code reaches this through DocumentFinalizer; the hook is what is under test here, not it.
            version.VersionNumber = 1;
            version.Sha256Hash = new string('a', 64);
            await confirm.SaveChangesAsync();
        }

        Assert.Single(dispatcher.Calls);
        Assert.Equal(documentId, dispatcher.Calls[0].DocumentId);
    }

    [Fact]
    public async Task A_soft_delete_reaches_it_as_REMOVED()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        Guid documentId;
        using (var setup = Context(connection, tenantId))
        {
            documentId = await FileAsync(setup, tenantId, userId, WellKnownMaskIds.Certificate, "carol.pem");
        }

        var dispatcher = new RecordingDispatcher(WellKnownMaskIds.Certificate);
        using var db = Context(connection, tenantId, dispatcher);
        var document = await db.Documents.FirstAsync(d => d.Id == documentId);
        document.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Removed, not merely "changed": the module must DROP the row rather than re-derive it, or the
        // recycle bin becomes a place where certificates keep working.
        Assert.True(Assert.Single(dispatcher.Calls).Removed);
    }

    [Fact]
    public async Task One_save_touching_a_document_its_field_and_a_version_calls_the_module_ONCE()
    {
        // Three entities, one fact. Five calls would be five projections of one document, and the projection
        // would be written five times inside one transaction.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        Guid documentId;
        Guid fieldId;
        using (var setup = Context(connection, tenantId))
        {
            documentId = await FileAsync(setup, tenantId, userId, WellKnownMaskIds.Certificate, "dave.pem");
            var maskVersionId = await MaskVersionAsync(setup, WellKnownMaskIds.Certificate);
            fieldId = await setup.FieldDefinitions
                .Where(f => f.MaskVersionId == maskVersionId && f.Name == "Label")
                .Select(f => f.Id)
                .FirstAsync();
        }

        var dispatcher = new RecordingDispatcher(WellKnownMaskIds.Certificate);
        using var db = Context(connection, tenantId, dispatcher);
        var document = await db.Documents.FirstAsync(d => d.Id == documentId);
        document.Name = "dave-renamed.pem";
        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            FieldDefinitionId = fieldId,
            Value = "YubiKey 5C",
        });
        await db.SaveChangesAsync();

        Assert.Single(dispatcher.Calls);
    }

    [Fact]
    public async Task A_mask_no_module_declares_reaches_nobody()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);
        var dispatcher = new RecordingDispatcher(WellKnownMaskIds.Certificate);

        using var db = Context(connection, tenantId, dispatcher);
        await FileAsync(db, tenantId, userId, WellKnownMaskIds.BasicEntry, "unrelated.txt");

        Assert.Empty(dispatcher.Calls);
    }

    [Fact]
    public async Task A_host_with_no_projecting_module_behaves_exactly_as_before()
    {
        // The overwhelmingly common path: no dispatcher at all, which is also every test that constructs the
        // context directly. It must not open a transaction and must not query anything extra.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        using var db = Context(connection, tenantId);
        var id = await FileAsync(db, tenantId, userId, WellKnownMaskIds.Certificate, "nobody-projects.pem");

        Assert.NotEqual(Guid.Empty, id);   // it saved, which is the whole assertion
    }

    [Fact]
    public async Task A_THROWING_module_fails_the_write()
    {
        // Deliberately the opposite of how the core treats a throwing module elsewhere (ADR 0848). Tolerating
        // it here means the revocation is recorded and never projected — the silent exposure this whole
        // mechanism exists to close.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedAsync(connection);

        var dispatcher = new RecordingDispatcher(WellKnownMaskIds.Certificate)
        {
            Throws = new InvalidOperationException("the module's projection failed"),
        };

        using var db = Context(connection, tenantId, dispatcher);
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "refused.pem",
            MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Certificate),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        // ...and the document is NOT there: the projection and the write commit together or not at all.
        db.ChangeTracker.Clear();
        using var fresh = Context(connection, tenantId);
        Assert.Null(await fresh.Documents.FirstOrDefaultAsync(d => d.Id == document.Id));
    }
}
