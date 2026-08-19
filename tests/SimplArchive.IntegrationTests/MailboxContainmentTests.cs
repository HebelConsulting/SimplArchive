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

// What may live inside a mailbox, and what may live inside its ephemeral folders (#596).
//
// Two rules that look alike and are not. Admission (a mailbox holds only IMAP Special folders and a notebook;
// a notebook lives nowhere else) is TWO-directional and rides on TypedFolderRules. "No subfolders" is
// ONE-directional and deliberately does not: expressing it as "an IMAP Special folder admits only eMail" would
// have confined every eMail in the archive to an ephemeral folder, which is the opposite of what filing means.
public class MailboxContainmentTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid UserId, Guid MailboxId, Guid InboxId)> MailboxAsync()
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
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        Guid mailboxId, inboxId;
        using (var db = Ctx(connection, accessor))
        {
            var provisioner = new PersonalMailboxProvisioner(
                db, new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance));

            inboxId = await provisioner.EnsureInboxAsync(_tenantId, userId, CancellationToken.None);
            mailboxId = (await db.Documents.SingleAsync(d => d.Id == inboxId)).ParentId!.Value;
        }

        return (connection, accessor, userId, mailboxId, inboxId);
    }

    // An eMail carries three REQUIRED fields, so a test that only wanted to prove placement was rejected for
    // an unrelated reason. Filled here rather than at each call site, since none of these tests is about them.
    private async Task<Document> AddMessageAsync(SimplArchiveDbContext db, Guid parentId, Guid userId, string name)
    {
        var document = await AddAsync(db, parentId, userId, name, WellKnownMaskIds.EMail);

        var maskVersionId = document.MaskVersionId!.Value;
        var fields = await db.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId && f.IsRequired)
            .ToListAsync();

        foreach (var field in fields)
        {
            db.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                DocumentId = document.Id,
                FieldDefinitionId = field.Id,
                Value = "someone@example.test",
            });
        }

        return document;
    }

    private async Task<Document> AddAsync(SimplArchiveDbContext db, Guid parentId, Guid userId, string name, Guid maskId)
    {
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, _tenantId, maskId, CancellationToken.None),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        return document;
    }

    [Fact]
    public async Task An_ephemeral_folder_holds_no_subfolders()
    {
        // The reason is not tidiness. An IMAP Special folder is a staging area rather than a member of the
        // repository, so an archive folder beneath it would be an archive folder whose parent is NOT in the
        // archive — a shape nothing else in the model produces and nothing downstream could detect.
        var (connection, accessor, userId, _, inboxId) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        await AddAsync(db, inboxId, userId, "Invoices", WellKnownMaskIds.Folder);

        var failure = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        Assert.Contains("holds messages, not folders", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ephemeral_folder_still_holds_messages()
    {
        // The other half of the rule, and the half a two-directional TypedFolderRules row would have broken:
        // the constraint is on the PARENT only. An eMail is admitted here AND anywhere else in the archive.
        var (connection, accessor, userId, _, inboxId) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        await AddMessageAsync(db, inboxId, userId, "Quarterly numbers");

        await db.SaveChangesAsync();
        Assert.True(await db.Documents.AnyAsync(d => d.Name == "Quarterly numbers"));
    }

    [Fact]
    public async Task An_email_is_not_confined_to_an_ephemeral_folder()
    {
        // Stated as its own test because it is the failure mode the rule's SHAPE was chosen to avoid, and it
        // would not show up in either test above: filing a message OUT of the inbox is the whole feature.
        var (connection, accessor, userId, mailboxId, _) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        // Inside My Documents, because the personal space's first level no longer takes a plain folder (#634).
        var personalId = (await db.Documents.SingleAsync(d => d.Id == mailboxId)).ParentId!.Value;
        var myDocumentsId = (await db.Documents.SingleAsync(d => d.ParentId == personalId && d.Name == PersonalFolders.MyDocuments)).Id;
        var folder = await AddAsync(db, myDocumentsId, userId, "Filed", WellKnownMaskIds.Folder);
        await db.SaveChangesAsync();

        await AddMessageAsync(db, folder.Id, userId, "Quarterly numbers");
        await db.SaveChangesAsync();

        Assert.True(await db.Documents.AnyAsync(d => d.Name == "Quarterly numbers"));
    }

    [Fact]
    public async Task A_notebook_lives_under_the_mailbox()
    {
        var (connection, accessor, userId, mailboxId, _) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        await AddAsync(db, mailboxId, userId, "Notebook", WellKnownMaskIds.Notebook);

        await db.SaveChangesAsync();
        Assert.True(await db.Documents.AnyAsync(d => d.Name == "Notebook" && d.ParentId == mailboxId));
    }

    [Fact]
    public async Task A_notebook_lives_nowhere_else()
    {
        // A notebook only means anything through a notes client speaking IMAP, so loose in a repository it is
        // a folder whose entire purpose is unreachable.
        var (connection, accessor, userId, mailboxId, _) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        // Inside My Documents, because the personal space's first level no longer takes a plain folder (#634).
        var personalId = (await db.Documents.SingleAsync(d => d.Id == mailboxId)).ParentId!.Value;
        var myDocumentsId = (await db.Documents.SingleAsync(d => d.ParentId == personalId && d.Name == PersonalFolders.MyDocuments)).Id;
        var folder = await AddAsync(db, myDocumentsId, userId, "Somewhere", WellKnownMaskIds.Folder);
        await db.SaveChangesAsync();

        await AddAsync(db, folder.Id, userId, "Notebook", WellKnownMaskIds.Notebook);

        var failure = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        Assert.Contains("can only live in a Mailbox", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mailbox_holds_at_most_one_notebook()
    {
        // Not a placement error — the second one is in exactly the right place, and is one too many. IMAP
        // projects the notebook as `NOTES`, and a client that discovers two has no way to choose.
        var (connection, accessor, userId, mailboxId, _) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        await AddAsync(db, mailboxId, userId, "Notebook", WellKnownMaskIds.Notebook);
        await db.SaveChangesAsync();

        await AddAsync(db, mailboxId, userId, "Second notebook", WellKnownMaskIds.Notebook);

        var failure = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        Assert.Contains("holds at most 1 Notebook", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mailbox_admits_nothing_else()
    {
        var (connection, accessor, userId, mailboxId, _) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        await AddAsync(db, mailboxId, userId, "A loose folder", WellKnownMaskIds.Folder);

        var failure = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        Assert.Contains("only IMAP Special or Notebook can", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensuring_the_notebook_twice_returns_the_same_folder()
    {
        // Both triggers converge here (#562), so neither may assume it owns the moment.
        var (connection, accessor, userId, _, _) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var provisioner = new PersonalMailboxProvisioner(
            db, new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance));

        var first = await provisioner.EnsureNotebookAsync(_tenantId, userId, CancellationToken.None);
        var second = await provisioner.EnsureNotebookAsync(_tenantId, userId, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task The_mailbox_is_created_by_whichever_demand_arrives_first()
    {
        // A credential generated before any mail has arrived must produce the same node the first delivery
        // would have — otherwise the second trigger creates a rival mailbox and the cardinality rule refuses
        // the very delivery it was meant to prepare for.
        var (connection, accessor, userId, mailboxId, _) = await MailboxAsync();
        using var _c = connection;

        using var db = Ctx(connection, accessor);
        var provisioner = new PersonalMailboxProvisioner(
            db, new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance));

        var mailbox = await provisioner.EnsureMailboxAsync(_tenantId, userId, CancellationToken.None);
        Assert.Equal(mailboxId, mailbox.Id);
    }
}
