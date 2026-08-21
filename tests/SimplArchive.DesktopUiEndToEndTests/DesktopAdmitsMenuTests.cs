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

        // It really is an ADDRESSBOOK, not a plain folder wearing the name: it draws as one, and being
        // exclusive it offers no creates of its own in the tree.
        Assert.Equal("addressbook", created.MaskIconToken);
        Assert.Empty(created.Admits);
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

    // A typed folder whose items are made elsewhere offers nothing here, so the menu is HIDDEN rather than
    // shown empty. Asserting the absence is the load-bearing half: a list published on every row would light
    // the menu up everywhere and the happy path would still look correct.
    [Fact]
    public async Task A_folder_that_admits_nothing_creatable_hides_the_menu()
    {
        var (vm, _) = await OpenAsync();
        var children = await PersonalChildrenAsync(vm);

        Assert.Empty(children["My Addressbook"].Admits);
        Assert.Empty(children["My Calendar"].Admits);

        // …and an ordinary folder still shows it, so the assertion above is about these folders rather than
        // about the payload being absent everywhere.
        Assert.NotEmpty(children["My Documents"].Admits);
    }
}
