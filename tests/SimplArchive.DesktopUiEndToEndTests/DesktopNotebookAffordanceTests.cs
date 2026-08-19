using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    /// <summary>A notebook, created the way the product creates one: under the mailbox (#596).</summary>
    /// <remarks>
    /// Two steps, both of them the real path. Generating an IMAP credential materialises the mailbox — the
    /// second of the two triggers, and the only one that does not require waiting for mail to arrive — and the
    /// notebook is then asked for by MASK, which is what the notes client's <c>CREATE "Notes"</c> ends up
    /// doing. The desktop client itself has no "new notebook" affordance and should not: a notebook without a
    /// notes client speaking IMAP is a folder whose purpose is unreachable.
    /// </remarks>
    private async Task<TreeNodeViewModel> NotebookAsync(MainWindowViewModel vm, SimplArchiveApiClient api)
    {
        await api.Profile.GenerateImapPasswordAsync(await api.Profile.GetImapAccessAsync());

        var mailbox = (await PersonalChildrenAsync(vm))["My Mailbox"];
        await mailbox.ReloadChildrenAsync();

        // Get-or-create, because these tests share one user and a mailbox holds at most ONE notebook — the
        // second caller must find the first one's rather than be refused. The product's own EnsureNotebookAsync
        // is idempotent for exactly this reason.
        if (mailbox.Children.FirstOrDefault(c => c.Name == "Notebook") is { } existing)
        {
            return existing;
        }

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        (await http.PostAsJsonAsync(mailbox.Href("children"), new { name = "Notebook", folderMask = "notes" }))
            .EnsureSuccessStatusCode();

        await mailbox.ReloadChildrenAsync();
        return mailbox.Children.Single(c => c.Name == "Notebook");
    }

    [Fact]
    public async Task Only_a_notebook_offers_the_two_creates()
    {
        var (vm, api) = await OpenAsync();
        var notebook = await NotebookAsync(vm, api);
        var children = await PersonalChildrenAsync(vm);

        // The notebook advertises both. It is the folder the affordance exists for.
        Assert.True(notebook.HasRel("sections"));
        Assert.True(notebook.HasRel("notes"));

        // An ordinary folder advertises neither — this is what keeps both entries off its menu. Asserting the
        // absence matters more than asserting the presence: a rel published on every row would light the menu
        // up everywhere and the feature would still "work" in the happy path.
        Assert.False(children["My Documents"].HasRel("sections"));
        Assert.False(children["My Documents"].HasRel("notes"));

        // A typed folder that holds something ELSE must not offer them either — the gate is "admits notes",
        // not merely "is typed".
        Assert.False(children["My Addressbook"].HasRel("sections"));
        Assert.False(children["My Calendar"].HasRel("notes"));
    }

    [Fact]
    public async Task A_section_is_created_from_the_advertised_rel_and_offers_them_in_turn()
    {
        var (vm, api) = await OpenAsync();
        var notebook = await NotebookAsync(vm, api);

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
        var notebook = await NotebookAsync(vm, api);

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
