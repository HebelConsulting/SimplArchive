using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// "New section" / "New note" on the tree context menu (#564).
//
// The gating is the part worth testing. The client does not decide where a section may live — the server
// advertises `sections`/`notes` only on a notebook or a section, and the menu entries follow that. So the
// test asserts BOTH directions: a rel that is always present gates nothing, and one that never arrives
// disables a feature silently.
[Collection(UiCollection.Name)]
public class DesktopNotebookAffordanceTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopNotebookAffordanceTests(SelfHostedAppFixture app) => _app = app;

    private async Task<(MainWindowViewModel Vm, SimplArchiveApiClient Api)> OpenAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        return (vm, api);
    }

    /// <summary>The personal root's child folders, by name, as the tree holds them.</summary>
    private static async Task<Dictionary<string, TreeNodeViewModel>> PersonalChildrenAsync(MainWindowViewModel vm)
    {
        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        return personal.Children
            .Where(c => !c.IsSynthetic && !c.IsLauncher)
            .ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Only_a_notebook_offers_the_two_creates()
    {
        var (vm, _) = await OpenAsync();
        var children = await PersonalChildrenAsync(vm);

        // The notebook advertises both. It is the folder the affordance exists for.
        var notebook = children["Notebook"];
        Assert.True(notebook.HasRel("sections"));
        Assert.True(notebook.HasRel("notes"));

        // An ordinary folder advertises neither — this is what keeps both entries off its menu. Asserting the
        // absence matters more than asserting the presence: a rel published on every row would light the menu
        // up everywhere and the feature would still "work" in the happy path.
        Assert.False(children["My Documents"].HasRel("sections"));
        Assert.False(children["My Documents"].HasRel("notes"));

        // A typed folder that holds something ELSE must not offer them either — the gate is "admits notes",
        // not merely "is typed".
        Assert.False(children["My Contacts"].HasRel("sections"));
        Assert.False(children["My Calendar"].HasRel("notes"));
    }

    [Fact]
    public async Task A_section_is_created_from_the_advertised_rel_and_offers_them_in_turn()
    {
        var (vm, _) = await OpenAsync();
        var notebook = (await PersonalChildrenAsync(vm))["Notebook"];

        var name = $"Work {Guid.NewGuid():N}"[..12];
        await vm.CreateSectionAsync(notebook.Id, notebook.Href("sections"), name);

        await notebook.ReloadChildrenAsync();
        var section = Assert.Single(notebook.Children, c => c.Name == name);

        // The family is RECURSIVE, and the tree must know it: a section that did not advertise the two creates
        // would let a user nest one level and no further.
        Assert.True(section.HasRel("sections"));
        Assert.True(section.HasRel("notes"));
    }

    [Fact]
    public async Task A_note_is_created_with_its_title_and_body()
    {
        var (vm, api) = await OpenAsync();
        var notebook = (await PersonalChildrenAsync(vm))["Notebook"];

        var title = $"Shopping {Guid.NewGuid():N}"[..16];
        await vm.CreateNoteAsync(notebook.Id, notebook.Href("notes"), title, "Milk\nBread");

        // It lands in the folder under its title — the title is both the tree name and the message Subject.
        var contents = await api.Documents.GetChildrenAsync(notebook.Href("children"));
        Assert.Contains(contents, c => c.Name == title);

        // The status line reports the outcome rather than staying on whatever it last said, which is how the
        // user learns the create happened at all (the tree refresh is asynchronous).
        Assert.Contains(title, vm.Status, StringComparison.Ordinal);
    }
}
