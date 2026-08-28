using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The SaveChanges-level DAV change recorder (#806): every path that touches an item in a synced collection
// leaves a log entry, because SaveChanges is the one door they all use. Driven against the DbContext directly
// — the point is precisely that no Api handler is involved.
public class DavChangeRecorderTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor accessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, accessor);

    private static async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid BookId, Guid ContactId)> SeedAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = TenantId };

        await using (var db = CreateContext(connection, accessor))
        {
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "T", Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new SimplArchive.Domain.Users.User { Id = UserId, TenantId = TenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(TenantId);
        }

        Guid bookId, contactId;
        await using (var db = CreateContext(connection, accessor))
        {
            var bookVersion = await db.MaskVersions.SingleAsync(v => v.MaskId == WellKnownMaskIds.Addressbook && v.IsCurrent);
            var contactVersion = await db.MaskVersions.SingleAsync(v => v.MaskId == WellKnownMaskIds.Contact && v.IsCurrent);
            var book = new Document { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Book", MaskVersionId = bookVersion.Id, CreatedByUserId = UserId, CreatedAt = DateTimeOffset.UtcNow, StorageFolderId = Guid.NewGuid() };
            var contact = new Document { Id = Guid.NewGuid(), TenantId = TenantId, ParentId = book.Id, Name = "Ada", MaskVersionId = contactVersion.Id, CreatedByUserId = UserId, CreatedAt = DateTimeOffset.UtcNow, StorageFolderId = Guid.NewGuid() };
            db.Documents.AddRange(book, contact);
            var uidField = await db.FieldDefinitions.SingleAsync(f => f.Name == "Contact UID" && f.MaskVersionId == contactVersion.Id);
            db.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = TenantId, DocumentId = contact.Id, FieldDefinitionId = uidField.Id, Value = "ada-uid-1" });
            await db.SaveChangesAsync();
            (bookId, contactId) = (book.Id, contact.Id);
        }

        return (connection, accessor, bookId, contactId);
    }

    private static Task<List<DavCollectionChange>> LogAsync(SimplArchiveDbContext db, Guid bookId) =>
        db.DavCollectionChanges.IgnoreQueryFilters(["TenantFilter"]).Where(c => c.FolderId == bookId).OrderBy(c => c.Id).ToListAsync();

    [Fact]
    public async Task A_new_version_is_recorded_under_the_uid_name()
    {
        var (connection, accessor, bookId, contactId) = await SeedAsync();
        await using var _ = connection;

        await using var db = CreateContext(connection, accessor);
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            DocumentId = contactId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = "k",
            CreatedByUserId = UserId,
            CreatedAt = DateTimeOffset.UtcNow,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        await db.SaveChangesAsync();

        // The SEED itself recorded an entry (the UID value insert is a name-relevant write — correct), so
        // the assertion is on the LAST entry, which the new version produced.
        var change = (await LogAsync(db, bookId)).Last(c => c.DocumentId == contactId);
        Assert.Equal(DavChangeType.Modified, change.ChangeType);
        Assert.Equal("ada-uid-1.vcf", change.ResourceName);
    }

    [Fact]
    public async Task A_move_out_records_removed_and_a_move_in_records_modified()
    {
        var (connection, accessor, bookId, contactId) = await SeedAsync();
        await using var _ = connection;

        Guid otherBookId;
        await using (var db = CreateContext(connection, accessor))
        {
            var bookVersion = await db.MaskVersions.SingleAsync(v => v.MaskId == WellKnownMaskIds.Addressbook && v.IsCurrent);
            var other = new Document { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Other", MaskVersionId = bookVersion.Id, CreatedByUserId = UserId, CreatedAt = DateTimeOffset.UtcNow, StorageFolderId = Guid.NewGuid() };
            db.Documents.Add(other);
            await db.SaveChangesAsync();
            otherBookId = other.Id;
        }

        await using (var db = CreateContext(connection, accessor))
        {
            var contact = await db.Documents.SingleAsync(d => d.Id == contactId);
            contact.ParentId = otherBookId;
            await db.SaveChangesAsync();

            var fromOld = (await LogAsync(db, bookId)).Last(c => c.DocumentId == contactId);
            Assert.Equal(DavChangeType.Removed, fromOld.ChangeType);
            var inNew = (await LogAsync(db, otherBookId)).Last(c => c.DocumentId == contactId);
            Assert.Equal(DavChangeType.Modified, inNew.ChangeType);
            Assert.Equal("ada-uid-1.vcf", inNew.ResourceName); // the name travels with the move
        }
    }

    [Fact]
    public async Task A_soft_delete_records_removed_and_a_restore_records_modified()
    {
        var (connection, accessor, bookId, contactId) = await SeedAsync();
        await using var _ = connection;

        await using var db = CreateContext(connection, accessor);
        var contact = await db.Documents.SingleAsync(d => d.Id == contactId);
        contact.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(DavChangeType.Removed, (await LogAsync(db, bookId)).Last(c => c.DocumentId == contactId).ChangeType);

        var deleted = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).SingleAsync(d => d.Id == contactId);
        deleted.DeletedAt = null;
        await db.SaveChangesAsync();
        Assert.Equal(DavChangeType.Modified, (await LogAsync(db, bookId)).Last(c => c.DocumentId == contactId).ChangeType);
    }

    [Fact]
    public async Task A_uid_change_re_records_under_the_new_name()
    {
        var (connection, accessor, bookId, contactId) = await SeedAsync();
        await using var _ = connection;

        await using var db = CreateContext(connection, accessor);
        var value = await db.FieldValues.SingleAsync(v => v.DocumentId == contactId);
        value.Value = "ada-uid-2";
        await db.SaveChangesAsync();

        Assert.Equal("ada-uid-2.vcf", (await LogAsync(db, bookId)).Last(c => c.DocumentId == contactId).ResourceName);
    }

    [Fact]
    public async Task A_document_outside_a_synced_collection_records_nothing()
    {
        var (connection, accessor, bookId, _) = await SeedAsync();
        await using var _1 = connection;

        await using var db = CreateContext(connection, accessor);
        var folderVersion = await db.MaskVersions.SingleAsync(v => v.MaskId == WellKnownMaskIds.Folder && v.IsCurrent);
        var plain = new Document { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Plain", MaskVersionId = folderVersion.Id, CreatedByUserId = UserId, CreatedAt = DateTimeOffset.UtcNow, StorageFolderId = Guid.NewGuid() };
        var doc = new Document { Id = Guid.NewGuid(), TenantId = TenantId, ParentId = plain.Id, Name = "Memo", CreatedByUserId = UserId, CreatedAt = DateTimeOffset.UtcNow, StorageFolderId = Guid.NewGuid() };
        db.Documents.AddRange(plain, doc);
        await db.SaveChangesAsync();
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            DocumentId = doc.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = "k2",
            CreatedByUserId = UserId,
            CreatedAt = DateTimeOffset.UtcNow,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        await db.SaveChangesAsync();

        Assert.Empty(await db.DavCollectionChanges.IgnoreQueryFilters(["TenantFilter"]).Where(c => c.FolderId == plain.Id).ToListAsync());
    }
}
