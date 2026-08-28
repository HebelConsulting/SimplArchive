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

    // Four since #703 PR 4 (Mailbox joined the menu — a department mailbox is created by a person, in a
    // plain folder), and the plain folder is FIRST: it is what "New subfolder" has always meant, and a menu
    // that reorders itself per folder is one the user has to read rather than aim at. The rest are ordered by
    // name, because row order out of a database is not defined.
    [Fact]
    public async Task An_ordinary_folder_offers_the_four_kinds_and_says_how_to_perform_each()
    {
        var admits = await AdmitsAsync(WellKnownMaskIds.Folder);

        Assert.Equal(["Folder", "Addressbook", "Calendar", "Mailbox"], admits.Select(a => a.Name).ToList());

        var entry = admits[0];
        Assert.Equal(WellKnownMaskIds.Folder, entry.MaskId);
        Assert.Equal("Folder", entry.Name);
        Assert.True(entry.Folder);
        Assert.Equal("POST", entry.Method);
        Assert.Equal($"/api/documents/{_documentId}/children", entry.Href);

        // The values the client sends back rather than derives: `children` creates more than folders, so the
        // body carries the kind, and the server names the input because a client cannot name the mask.
        Assert.Equal("folder", entry.FolderMask);
        Assert.Equal("name", entry.Prompt);

        // Every kind reaches the SAME address — since #678 a folder mask is made through the children
        // collection carrying its mask id, which is what lets a tenant-authored mask arrive here without a
        // table entry of its own. The slug rides along only where it predates ids; the Mailbox arrived after
        // ids were the contract, so it deliberately has none.
        Assert.All(admits, a => Assert.EndsWith("/children", a.Href));
        Assert.Equal(["folder", "addressbook", "calendar", null], admits.Select(a => a.FolderMask).ToList());
        Assert.All(admits, a => Assert.NotEqual(Guid.Empty, a.MaskId));
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

    // The fourth fact — AdmitsNoSubfolders keeps ordinary folders out of the staging mailboxes, but it now
    // yields to the ONE mask that declares a staging folder as its home (#802, decided looseness): the user
    // mail folder. So the menu offers exactly that, everywhere in the staging tier — under the archive by
    // design, under Inbox as the accepted consequence of containment that cannot name one folder instance
    // without keying on its name (the #630 trap).
    [Fact]
    public async Task An_ephemeral_mail_folder_offers_exactly_the_user_mail_folder()
    {
        var entry = Assert.Single(await AdmitsAsync(WellKnownMaskIds.ImapSpecial));
        Assert.Equal(WellKnownMaskIds.ImapFolder, entry.MaskId);
    }

    // An Addressbook offers exactly Contact, a Calendar exactly Appointment (#689) — one entry each, at the
    // family's own endpoint, asking for the dialog rather than a name.
    //
    // This test previously asserted the OPPOSITE, that both offered nothing, and the reasoning is kept because
    // it explains the shape rather than being wrong: the two masks always passed creatability and containment,
    // and failed only "is there a way to make one" — a name prompt would have produced an empty vCard. Giving
    // each a prompt naming a dialog the clients already have is what changed, not the containment.
    //
    // Being exclusive, neither takes a plain Folder, so a single entry is also an assertion that Folder did not
    // sneak in: `Assert.Equal` on the whole list says more here than a Contains would.
    [Theory]
    [InlineData("Addressbook", "Contact", "contacts", "contact")]
    [InlineData("Calendar", "Appointment", "appointments", "appointment")]
    public async Task A_typed_item_folder_offers_its_one_item(string mask, string item, string path, string prompt)
    {
        var maskId = mask == "Addressbook" ? WellKnownMaskIds.Addressbook : WellKnownMaskIds.Calendar;
        var admits = await AdmitsAsync(maskId);

        Assert.Equal([item], admits.Select(a => a.Name).ToList());
        Assert.Equal($"/api/documents/{_documentId}/{path}", admits[0].Href);
        Assert.Equal(prompt, admits[0].Prompt);

        // Not a folder, and carrying no folderMask slug: its ADDRESS already says what it makes, so a body
        // value naming a kind would be noise the reader has to work out is unused.
        Assert.False(admits[0].Folder);
        Assert.Null(admits[0].FolderMask);
    }

    // The other half of the same rule, and the one a Contains-based test would miss: these two items are NOT
    // offered anywhere else. An ordinary folder permits a Contact — containment is not what keeps it off that
    // menu — so if the reason it stays off were ever confused with a containment rule, this is where it shows.
    [Fact]
    public async Task A_contact_and_an_appointment_are_offered_only_by_their_own_collection()
    {
        var rules = await RulesAsync();

        foreach (var maskId in WellKnownMaskIds.All.Where(m =>
                     m != WellKnownMaskIds.Addressbook && m != WellKnownMaskIds.Calendar))
        {
            var admits = CreatableChildren.For(rules, _documentId, maskId, isPersonalRoot: false);
            Assert.DoesNotContain(admits, a => a.MaskId == WellKnownMaskIds.Contact);
            Assert.DoesNotContain(admits, a => a.MaskId == WellKnownMaskIds.Appointment);
        }
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
        var admits = await AdmitsAsync(null);
        Assert.Equal(WellKnownMaskIds.Folder, admits[0].MaskId);
        Assert.Contains(admits, a => a.MaskId == WellKnownMaskIds.Addressbook);
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
    // The fifth fact (#678): whether a user may make one at all is DATA on the mask, so flipping the column
    // removes the entry — no code knows the difference between a Notebook and an Addressbook any more.
    [Fact]
    public async Task A_mask_marked_not_user_creatable_disappears_from_the_menu()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeededRulesAsync(connection);

        using (var edit = Ctx(connection))
        {
            (await edit.Masks.SingleAsync(m => m.Id == WellKnownMaskIds.Addressbook)).UserCreatable = false;
            await edit.SaveChangesAsync();
        }

        using var read = Ctx(connection);
        var rules = await MaskContainmentRules.LoadAsync(read, _tenantId, CancellationToken.None);
        var admits = CreatableChildren.For(rules, _documentId, WellKnownMaskIds.Folder, isPersonalRoot: false);

        Assert.DoesNotContain(admits, a => a.MaskId == WellKnownMaskIds.Addressbook);
        Assert.Contains(admits, a => a.MaskId == WellKnownMaskIds.Calendar);
    }

    // The inverse, and the one that proves this is data rather than a shorter hardcoded list: a mask the
    // application ships as NOT creatable appears the moment the column says otherwise. If a table still gated
    // it, this would stay hidden.
    [Fact]
    public async Task A_mask_marked_creatable_appears_even_though_the_application_ships_it_closed()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeededRulesAsync(connection);

        using (var edit = Ctx(connection))
        {
            (await edit.Masks.SingleAsync(m => m.Id == WellKnownMaskIds.Notebook)).UserCreatable = true;
            await edit.SaveChangesAsync();
        }

        using var read = Ctx(connection);
        var rules = await MaskContainmentRules.LoadAsync(read, _tenantId, CancellationToken.None);

        // Under a Mailbox, which is the only folder that admits a Notebook — so containment is satisfied and
        // creatability is the only thing that was ever keeping it off the menu.
        var admits = CreatableChildren.For(rules, _documentId, WellKnownMaskIds.Mailbox, isPersonalRoot: false);
        Assert.Contains(admits, a => a.MaskId == WellKnownMaskIds.Notebook);
    }

    // Containment and creatability are asked SEPARATELY, and both must hold. A creatable mask the folder does
    // not admit stays off the menu — otherwise the menu would offer a create its own SaveChanges refuses.
    [Fact]
    public async Task Creatable_is_not_enough_the_folder_must_admit_it_too()
    {
        var rules = await RulesAsync();
        Assert.True(rules.IsUserCreatable(WellKnownMaskIds.Addressbook));

        // A Notebook is exclusive: it admits sections and notes, and nothing else.
        var admits = CreatableChildren.For(rules, _documentId, WellKnownMaskIds.Notebook, isPersonalRoot: false);
        Assert.DoesNotContain(admits, a => a.MaskId == WellKnownMaskIds.Addressbook);
    }

    // The third question, and the one that keeps an ordinary folder's menu short. Basic Entry and eMail are
    // user-creatable AND permitted in an ordinary folder — but neither is something you MAKE, you upload a
    // file and get one. A menu built from creatability alone would offer "New Basic Entry" beside "New folder".
    [Theory]
    [InlineData("BasicEntry")]
    [InlineData("EMail")]
    public async Task A_mask_with_no_way_to_create_one_stays_off_the_menu(string mask)
    {
        var rules = await RulesAsync();
        var maskId = mask == "BasicEntry" ? WellKnownMaskIds.BasicEntry : WellKnownMaskIds.EMail;

        Assert.True(rules.IsUserCreatable(maskId));
        Assert.True(rules.Allows(maskId, WellKnownMaskIds.Folder));

        var admits = CreatableChildren.For(rules, _documentId, WellKnownMaskIds.Folder, isPersonalRoot: false);
        Assert.DoesNotContain(admits, a => a.MaskId == maskId);
    }

    private async Task<MaskContainmentRules> RulesAsync()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        return await SeededRulesAsync(connection);
    }
}
