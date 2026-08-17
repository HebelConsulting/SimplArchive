using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Typed-folder containment once admission became a SET (#564). Two things broke the old one-folder-one-item
// shape at once: a Notebook admits Sections AND Notes, and a Section admits ITSELF — so the relation is
// neither one-to-one nor acyclic. These drive the invariant directly against the DbContext, which is the sole
// enforcement point, so what holds here holds for every surface (workbench, import, WebDAV, IMAP, DAV).
public class NotebookSectionContainmentTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid UserId)> SeedAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        Guid userId;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
        }

        return (connection, accessor, userId);
    }

    private static async Task<Guid> MaskVersionAsync(SimplArchiveDbContext db, Guid maskId) =>
        (await db.MaskVersions.SingleAsync(v => v.MaskId == maskId && v.IsCurrent)).Id;

    private static Document Doc(Guid tenantId, Guid? parentId, string name, Guid? maskVersionId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ParentId = parentId,
        Name = name,
        MaskVersionId = maskVersionId,
        CreatedByUserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // A mask with REQUIRED fields validates on assignment, so a bare document wearing Note or Contact fails on
    // a different invariant than the one under test. Filling them keeps each test measuring containment.
    private static async Task AddWithRequiredFieldsAsync(SimplArchiveDbContext db, Document document)
    {
        db.Documents.Add(document);
        if (document.MaskVersionId is not { } maskVersionId)
        {
            return;
        }

        var required = await db.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId && f.IsRequired)
            .ToListAsync();

        foreach (var field in required)
        {
            db.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = field.Id,
                Value = Guid.NewGuid().ToString("N"),
            });
        }
    }

    [Fact]
    public async Task A_notebook_holds_sections_and_notes_and_a_section_holds_both_again()
    {
        var (connection, accessor, userId) = await SeedAsync();
        using var _ = connection;

        using var db = Ctx(connection, accessor);
        var notebookMask = await MaskVersionAsync(db, WellKnownMaskIds.Notebook);
        var sectionMask = await MaskVersionAsync(db, WellKnownMaskIds.NotebookSection);
        var noteMask = await MaskVersionAsync(db, WellKnownMaskIds.Note);

        var notebook = Doc(_tenantId, null, "Notebook", notebookMask, userId);
        db.Documents.Add(notebook);
        await db.SaveChangesAsync();

        // Both admitted masks, directly in the notebook.
        var section = Doc(_tenantId, notebook.Id, "Work", sectionMask, userId);
        db.Documents.Add(section);
        await AddWithRequiredFieldsAsync(db, Doc(_tenantId, notebook.Id, "Loose note", noteMask, userId));
        await db.SaveChangesAsync();

        // …and again inside the section, including another section: the family is recursive, which is the
        // whole reason admission stopped being a single mask.
        var nested = Doc(_tenantId, section.Id, "2026", sectionMask, userId);
        db.Documents.Add(nested);
        await db.SaveChangesAsync();

        await AddWithRequiredFieldsAsync(db, Doc(_tenantId, nested.Id, "Deep note", noteMask, userId));
        await db.SaveChangesAsync();

        Assert.Equal(5, await db.Documents.CountAsync());
    }

    [Fact]
    public async Task A_section_cannot_live_outside_a_notebook()
    {
        var (connection, accessor, userId) = await SeedAsync();
        using var _ = connection;

        using var db = Ctx(connection, accessor);
        var sectionMask = await MaskVersionAsync(db, WellKnownMaskIds.NotebookSection);
        var folderMask = await MaskVersionAsync(db, WellKnownMaskIds.Folder);

        // At the archive root. A Section satisfies the folder side of the rule as well as the child side, so
        // an implementation that treated the two checks as if/else would let exactly this through.
        db.Documents.Add(Doc(_tenantId, null, "Orphan section", sectionMask, userId));
        var atRoot = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        Assert.Contains("Section", atRoot.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        // …and in an ordinary folder.
        var folder = Doc(_tenantId, null, "Ordinary", folderMask, userId);
        db.Documents.Add(folder);
        await db.SaveChangesAsync();

        db.Documents.Add(Doc(_tenantId, folder.Id, "Section", sectionMask, userId));
        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_notebook_still_refuses_what_it_does_not_admit_and_says_what_it_takes()
    {
        var (connection, accessor, userId) = await SeedAsync();
        using var _ = connection;

        using var db = Ctx(connection, accessor);
        var notebookMask = await MaskVersionAsync(db, WellKnownMaskIds.Notebook);
        var basicMask = await MaskVersionAsync(db, WellKnownMaskIds.BasicEntry);

        var notebook = Doc(_tenantId, null, "Notebook", notebookMask, userId);
        db.Documents.Add(notebook);
        await db.SaveChangesAsync();

        db.Documents.Add(Doc(_tenantId, notebook.Id, "A spreadsheet", basicMask, userId));
        var refused = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());

        // The message must list BOTH admitted masks: naming only one would send the reader to the wrong place
        // half the time, which is the failure the set-valued rule exists to avoid.
        Assert.Contains("Section or Note", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_contact_family_is_the_same_rule_with_a_set_of_one()
    {
        var (connection, accessor, userId) = await SeedAsync();
        using var _ = connection;

        using var db = Ctx(connection, accessor);
        var addressbookMask = await MaskVersionAsync(db, WellKnownMaskIds.Addressbook);
        var contactMask = await MaskVersionAsync(db, WellKnownMaskIds.Contact);
        var sectionMask = await MaskVersionAsync(db, WellKnownMaskIds.NotebookSection);

        var book = Doc(_tenantId, null, "Addressbook", addressbookMask, userId);
        db.Documents.Add(book);
        await db.SaveChangesAsync();

        await AddWithRequiredFieldsAsync(db, Doc(_tenantId, book.Id, "Zora", contactMask, userId));
        await db.SaveChangesAsync();

        // A section is not a universal folder — it belongs to the notebook family only.
        db.Documents.Add(Doc(_tenantId, book.Id, "Section", sectionMask, userId));
        var refused = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        Assert.Contains("Contact", refused.Message, StringComparison.Ordinal);
    }
}
