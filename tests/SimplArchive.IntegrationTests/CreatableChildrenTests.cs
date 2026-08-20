using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// What a folder tells a client it can create (#673) — the `admits` list both clients now build their New menu
// from, driven by the REAL containment rules loaded from a really-seeded tenant.
//
// Here rather than in the E2E suite on purpose. Everything worth pinning is the ANSWER — which entries a given
// folder mask produces, and what each one carries — and that answer is a pure function of the seeded rules; the
// only thing a container adds is proof that the controller assigns the property, which is one line. So the
// expensive suite is not where this belongs, and the cheap one can afford to ask about every mask rather than
// about one.
//
// The load-bearing assertions are the ABSENCES. A menu built from what containment permits would have offered
// "New Basic Entry" beside "New folder"; one built from what a folder declares, with no second table, would
// have put "New Notebook" on every mailbox — a thing the IMAP client creates and the UI never offers
// (owner-stated 2026-08-20). Neither is visible in a test that only checks the entries that ARE there.
public class CreatableChildrenTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<MaskContainmentRules> SeededRulesAsync(SqliteConnection connection)
    {
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var seed = Ctx(connection))
        {
            await new WellKnownMaskSeeder(seed, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        using var read = Ctx(connection);
        return await MaskContainmentRules.LoadAsync(read, _tenantId, CancellationToken.None);
    }

    private async Task<List<CreatableChild>> AdmitsAsync(Guid? folderMaskId, bool isPersonalRoot = false)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rules = await SeededRulesAsync(connection);
        return CreatableChildren.For(rules, _documentId, folderMaskId, isPersonalRoot);
    }

    [Fact]
    public async Task An_ordinary_folder_offers_one_create_and_says_how_to_perform_it()
    {
        var admits = await AdmitsAsync(WellKnownMaskIds.Folder);

        var entry = Assert.Single(admits);
        Assert.Equal(WellKnownMaskIds.Folder, entry.MaskId);
        Assert.Equal("Folder", entry.Name);
        Assert.True(entry.Folder);
        Assert.Equal("POST", entry.Method);
        Assert.Equal($"/api/documents/{_documentId}/children", entry.Href);

        // The two the client sends back rather than derives: `children` creates more than folders, so the body
        // carries the kind, and the server names the input because a client cannot name the mask.
        Assert.Equal("folder", entry.FolderMask);
        Assert.Equal("name", entry.Prompt);
    }

    // Containment PERMITS a Basic Entry or an eMail in an ordinary folder — a menu built from permission would
    // offer both. The list is what a folder DECLARES, and an ordinary folder declares nothing.
    [Fact]
    public async Task A_folder_offers_only_what_it_declares_not_everything_it_would_accept()
    {
        var rules = await RulesAsync();
        Assert.True(rules.Allows(WellKnownMaskIds.BasicEntry, WellKnownMaskIds.Folder));
        Assert.True(rules.Allows(WellKnownMaskIds.EMail, WellKnownMaskIds.Folder));

        var admits = CreatableChildren.For(rules, _documentId, WellKnownMaskIds.Folder, isPersonalRoot: false);

        Assert.DoesNotContain(admits, a => a.MaskId == WellKnownMaskIds.BasicEntry);
        Assert.DoesNotContain(admits, a => a.MaskId == WellKnownMaskIds.EMail);
    }

    [Fact]
    public async Task A_notebook_offers_its_two_declared_kinds_and_no_plain_folder()
    {
        var admits = await AdmitsAsync(WellKnownMaskIds.Notebook);

        Assert.Equal(
            new HashSet<Guid> { WellKnownMaskIds.NotebookSection, WellKnownMaskIds.Note },
            admits.Select(a => a.MaskId).ToHashSet());

        var section = admits.Single(a => a.MaskId == WellKnownMaskIds.NotebookSection);
        Assert.Equal($"/api/documents/{_documentId}/sections", section.Href);
        Assert.True(section.Folder);

        var note = admits.Single(a => a.MaskId == WellKnownMaskIds.Note);
        Assert.Equal($"/api/documents/{_documentId}/notes", note.Href);
        Assert.False(note.Folder);

        // Each family's own address says what it makes, so there is nothing to put in the body — and a client
        // that sent one would be inventing vocabulary the server never gave it.
        Assert.All(admits, a => Assert.Null(a.FolderMask));

        // A note is the one create that needs two answers: a title and something to write in it.
        Assert.Equal("note", note.Prompt);
        Assert.Equal("name", section.Prompt);

        // The notebook is exclusive, so "New subfolder" is genuinely unavailable here rather than merely
        // unlisted — the same question the invariant answers.
        Assert.DoesNotContain(admits, a => a.MaskId == WellKnownMaskIds.Folder);
    }

    [Fact]
    public async Task A_section_admits_more_sections_so_the_menu_recurses()
    {
        var admits = await AdmitsAsync(WellKnownMaskIds.NotebookSection);

        Assert.Contains(admits, a => a.MaskId == WellKnownMaskIds.NotebookSection);
        Assert.Contains(admits, a => a.MaskId == WellKnownMaskIds.Note);
        Assert.Equal(2, admits.Count);
    }

    // The owner's correction, made executable (2026-08-20). A Mailbox DECLARES it admits a Notebook, and it is
    // the only folder that does — so an admits list derived from declaration alone puts "New Notebook" on every
    // mailbox in both clients. The IMAP client creates that notebook automatically and the UI never offers it.
    //
    // This is the test that fails if the second table ever goes away without the decision being revisited.
    [Fact]
    public async Task A_mailbox_offers_a_plain_folder_only_never_the_notebook_it_declares()
    {
        var rules = await RulesAsync();
        Assert.Contains(WellKnownMaskIds.Notebook, rules.AdmittedBy(WellKnownMaskIds.Mailbox));
        Assert.Contains(WellKnownMaskIds.ImapSpecial, rules.AdmittedBy(WellKnownMaskIds.Mailbox));

        var admits = CreatableChildren.For(rules, _documentId, WellKnownMaskIds.Mailbox, isPersonalRoot: false);

        var entry = Assert.Single(admits);
        Assert.Equal(WellKnownMaskIds.Folder, entry.MaskId);
    }

    // The fourth fact — a folder that admits no subfolders at all (#673's AdmitsNoSubfolders). It declares
    // nothing either, so the menu is empty and the client hides the New affordance entirely.
    [Fact]
    public async Task An_ephemeral_mail_folder_offers_nothing()
    {
        Assert.Empty(await AdmitsAsync(WellKnownMaskIds.ImapSpecial));
    }

    // Addressbook and Calendar are user-creatable — from the Contacts and Calendar tabs, where the dialog for a
    // person or an event belongs. Nothing they admit is on a tree menu, and being exclusive they take no plain
    // folder either.
    [Theory]
    [InlineData("Addressbook")]
    [InlineData("Calendar")]
    public async Task A_typed_item_folder_offers_nothing_in_the_tree(string mask)
    {
        var maskId = mask == "Addressbook" ? WellKnownMaskIds.Addressbook : WellKnownMaskIds.Calendar;
        Assert.Empty(await AdmitsAsync(maskId));
    }

    // A personal space's first level holds only what provisioning put there (#634) — a separate invariant from
    // containment, which is why CreatableChildren is told about it rather than deducing it from the mask.
    [Fact]
    public async Task A_personal_space_root_offers_nothing()
    {
        Assert.NotEmpty(await AdmitsAsync(WellKnownMaskIds.UserFolder));
        Assert.Empty(await AdmitsAsync(WellKnownMaskIds.UserFolder, isPersonalRoot: true));
    }

    // A repository created before masks were stamped, and a folder caught mid-heal, both look exactly like
    // this. The plain folder is admitted everywhere that does not refuse it, so it survives the unknown.
    [Fact]
    public async Task A_folder_with_no_mask_still_offers_a_subfolder()
    {
        var entry = Assert.Single(await AdmitsAsync(null));
        Assert.Equal(WellKnownMaskIds.Folder, entry.MaskId);
    }

    // Cross-cutting, over every well-known mask rather than the handful above: an entry a client cannot act on
    // is worse than no entry, because the menu shows it and the click fails.
    [Fact]
    public async Task Every_entry_any_folder_offers_is_actionable_and_listed_once()
    {
        var rules = await RulesAsync();

        foreach (var maskId in WellKnownMaskIds.All)
        {
            var admits = CreatableChildren.For(rules, _documentId, maskId, isPersonalRoot: false);
            var where = rules.NameOf(maskId);

            Assert.Equal(admits.Select(a => a.MaskId).Distinct().Count(), admits.Count);

            foreach (var entry in admits)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Name), $"{where}: an entry with no label");
                Assert.Equal("POST", entry.Method);
                Assert.StartsWith($"/api/documents/{_documentId}/", entry.Href);
                Assert.False(string.IsNullOrWhiteSpace(entry.Prompt), $"{where}: an entry asking for nothing");

                // The list is a menu, so everything on it must be something the folder would actually accept —
                // otherwise the server is advertising a create its own invariant will refuse.
                Assert.True(rules.Allows(entry.MaskId, maskId),
                    $"{where} offers {entry.Name}, which containment would refuse.");
            }
        }
    }

    // The rules are fully materialised by LoadAsync, so the connection has done its job by the time this
    // returns — which is what lets the tests that ask several questions share one seeding.
    private async Task<MaskContainmentRules> RulesAsync()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        return await SeededRulesAsync(connection);
    }
}
