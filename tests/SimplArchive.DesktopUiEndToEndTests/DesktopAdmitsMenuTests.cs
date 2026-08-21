using System.Net.Http.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The tree's New menu, built from what the folder says it admits (#673).
//
// WHAT this covers, and what it deliberately leaves to the cheap suite. Which entries a given folder mask
// produces is a pure function of the tenant's containment rules, and CreatableChildrenTests asks that question
// of every well-known mask in about a second with no container. What only a real server can answer is the
// chain: that the controller populates `admits` at all, that it survives the wire and the parse, and — the one
// worth the container — that the address the entry carries is an address that actually ACCEPTS a create.
//
// That last one matters because those addresses are the part of #673 still hardcoded. The model says where a
// mask may live; the table saying who may create one, and at which path, is a fifth fact living in code. A
// wrong path there is invisible until a user clicks the menu entry, which is exactly the failure a test should
// not leave to a user.
[Collection(UiCollection.Name)]
public class DesktopAdmitsMenuTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdmitsMenuTests(SelfHostedAppFixture app) => _app = app;

    private async Task<(MainWindowViewModel Vm, SimplArchiveApiClient Api)> OpenAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        return (vm, api);
    }

    private static async Task<Dictionary<string, TreeNodeViewModel>> PersonalChildrenAsync(MainWindowViewModel vm)
    {
        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        return personal.Children
            .Where(c => !c.IsSynthetic && !c.IsLauncher)
            .ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_folder_carries_its_creates_across_the_wire()
    {
        var (vm, _) = await OpenAsync();
        var documents = (await PersonalChildrenAsync(vm))["My Documents"];

        // Three since #678: creatability became data, and Addressbook and Calendar are creatable and admitted
        // anywhere. The plain folder stays FIRST — it is what "New subfolder" has always meant.
        Assert.Equal(["Folder", "Addressbook", "Calendar"], documents.Admits.Select(a => a.Name).ToList());

        var entry = documents.Admits[0];
        Assert.Equal("Folder", entry.Name);
        Assert.True(entry.Folder);

        // The label is the MASK's name as this tenant has it, not a client string — which is the whole point:
        // a tenant that renames the mask gets the new name on the menu with no client change.
        Assert.Equal("folder", entry.FolderMask);
        Assert.Equal("name", entry.Prompt);

        // Advertised, and therefore followable as-is. A client that had to append anything to this would be
        // composing (ADR 0543), and the assertion below is what proves it does not have to.
        Assert.Contains("/api/documents/", entry.Href);
    }

    // The one thing no unit or integration test can reach: the hardcoded path is a real endpoint.
    [Fact]
    public async Task The_address_the_entry_carries_actually_creates_the_thing()
    {
        var (vm, _) = await OpenAsync();
        var documents = (await PersonalChildrenAsync(vm))["My Documents"];
        var entry = documents.Admits.Single(a => a.Name == "Folder");

        var name = $"Admits {Guid.NewGuid():N}"[..14];
        await vm.CreateSubfolderAsync(documents.Id, entry.Href, name, entry.MaskId);

        await documents.ReloadChildrenAsync();
        var created = Assert.Single(documents.Children, c => c.Name == name);

        // And the thing it created offers the same create in turn — a folder inside a folder, which is what
        // makes the menu usable more than one level deep.
        Assert.Contains(created.Admits, a => a.Name == "Folder");
    }

    // The fifth fact end to end (#678): a mask that is NOT a plain folder, reaching the server by its ID
    // rather than by a slug the endpoint hardcodes. This is the path a tenant-authored mask will take — the
    // only difference being that Addressbook happens to also have a legacy slug.
    [Fact]
    public async Task A_typed_folder_is_created_from_the_entry_by_its_mask_id()
    {
        var (vm, _) = await OpenAsync();
        var documents = (await PersonalChildrenAsync(vm))["My Documents"];
        var entry = documents.Admits.Single(a => a.Name == "Addressbook");

        var name = $"Book {Guid.NewGuid():N}"[..12];
        await vm.CreateSubfolderAsync(documents.Id, entry.Href, name, entry.MaskId);

        await documents.ReloadChildrenAsync();
        var created = Assert.Single(documents.Children, c => c.Name == name);

        // It really is an ADDRESSBOOK, not a plain folder wearing the name: it draws as one, and — being
        // exclusive — it offers exactly one create, its own Contact, with no plain Folder beside it.
        //
        // This assertion used to be Assert.Empty, which was the whole of #689: you could make the container
        // and then nothing to put in it. What changed is the PROMPT, not the containment — a contact needs a
        // dialog, not a name, and until there was a way to ask, offering the entry would have produced empty
        // vCards.
        Assert.Equal("addressbook", created.MaskIconToken);
        Assert.Equal(["Contact"], created.Admits.Select(a => a.Name).ToList());
        Assert.Equal("contact", created.Admits[0].Prompt);
    }

    // The rich create, end to end on a real server (#689): the address the entry carries accepts a whole
    // person, and what comes back is a Contact filed in that addressbook.
    //
    // Driven through the view-model rather than the dialog, because the dialog is a Window and this suite has
    // no display — so what is proved here is the half a test can prove: the address, the payload shape and the
    // filing. That the MENU reaches this, and that the dialog opens, is what the screenshots are for.
    [Fact]
    public async Task A_contact_is_created_from_the_addressbook_entry_that_offered_it()
    {
        var (vm, _) = await OpenAsync();
        var documents = (await PersonalChildrenAsync(vm))["My Documents"];

        var bookName = $"Book {Guid.NewGuid():N}"[..12];
        var bookEntry = documents.Admits.Single(a => a.Name == "Addressbook");
        await vm.CreateSubfolderAsync(documents.Id, bookEntry.Href, bookName, bookEntry.MaskId);
        await documents.ReloadChildrenAsync();
        var book = Assert.Single(documents.Children, c => c.Name == bookName);

        var entry = Assert.Single(book.Admits);
        Assert.Equal("Contact", entry.Name);

        // Open the addressbook first, so the create refreshes the listing the way it does for a user standing
        // in the folder they are filing into.
        vm.SelectedTreeNode = book;
        await WaitForAsync(() => vm.Items.Count == 0 || vm.Items.Count >= 0);

        var surname = $"Lovelace{Guid.NewGuid():N}"[..12];
        var form = new ContactEditViewModel { GivenName = "Ada", FamilyName = surname };
        await vm.CreateStructuredChildAsync(
            book.Id, entry.Href, form.ToPayload(), "StContactCreated", "StErrCreateContact", "Ada", bookName);

        // Filed where it was aimed. The name is the SERVER's — it derives one from the card — so this asks the
        // listing rather than assuming the form's guess survived (ADR 0559).
        await WaitForAsync(() => vm.Items.Any(i => i.Name.Contains(surname, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(vm.Items, i => i.Name.Contains(surname, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }

    // What stops an id on the wire being a licence. Mailbox is a real folder mask this tenant HAS — so the
    // refusal can only come from UserCreatable, not from the mask being unknown.
    [Fact]
    public async Task A_mask_provisioning_owns_is_refused_even_when_asked_for_by_id()
    {
        var (vm, _) = await OpenAsync();
        var documents = (await PersonalChildrenAsync(vm))["My Documents"];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var response = await http.PostAsJsonAsync(documents.Href("children"),
            new { name = $"Nope {Guid.NewGuid():N}"[..12], maskId = SimplArchive.Domain.Masks.WellKnownMaskIds.Mailbox });

        Assert.False(response.IsSuccessStatusCode);
    }

    // The guard for the mistake this change actually made: `admits` was added to the CHILDREN listing only, so
    // every repository ROOT — where a tree starts, and the one node nothing ever drills into — offered no
    // creates at all. A client never re-fetches a tree node to fill in its menu, so a payload that reaches one
    // listing reaches half the tree (issue #416, and the same lesson `create-child` already taught).
    //
    // Asked of the ROOTS collection rather than of one known repository: a listing that lost the payload would
    // otherwise still pass on whichever row a test happened to name.
    [Fact]
    public async Task Every_repository_root_carries_its_creates_too_not_only_the_children_listing()
    {
        var (_, api) = await OpenAsync();

        var roots = await api.Documents.GetRepositoriesAsync();
        Assert.NotEmpty(roots);
        Assert.All(roots, r => Assert.Contains(r.Admits ?? [], a => a.Name == "Folder"));
    }

    // The list is PER ROW, not a constant published on every one — which is the half a happy-path test cannot
    // see, because a list broadcast everywhere makes the happy path look right.
    //
    // This used to assert that My Addressbook and My Calendar admitted NOTHING, which was true until #689 gave
    // each of them its one item. Rather than hunt for a replacement folder that admits nothing, the assertion
    // moved to what actually distinguishes the rows: three different folders in one personal space, each
    // carrying a different list, and neither typed collection carrying the plain Folder that My Documents does.
    // The admits-nothing case is not lost — CreatableChildrenTests asks it of every well-known mask, including
    // the two mail folders, without needing a container.
    [Fact]
    public async Task Each_folder_carries_its_own_list_rather_than_a_broadcast_one()
    {
        var (vm, _) = await OpenAsync();
        var children = await PersonalChildrenAsync(vm);

        Assert.Equal(["Folder", "Addressbook", "Calendar"], children["My Documents"].Admits.Select(a => a.Name).ToList());
        Assert.Equal(["Contact"], children["My Addressbook"].Admits.Select(a => a.Name).ToList());
        Assert.Equal(["Appointment"], children["My Calendar"].Admits.Select(a => a.Name).ToList());
    }
}
