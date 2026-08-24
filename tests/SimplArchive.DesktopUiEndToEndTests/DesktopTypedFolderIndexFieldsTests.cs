using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Opening the index editor on a TYPED FOLDER must offer that folder's own fields — for a Mailbox, the address
// list #703 exists to let someone fill in.
//
// It did not, and the cause is one line away from the fix that created it. Both clients resolve a mask's field
// definitions by looking the mask up in the CATALOGUE and following that row's `self` (ADR 0555), and the
// catalogue is filtered to the freely-assignable masks (#671) — so a Mailbox, a Calendar, an Addressbook or a
// repository has no row there, no address, and therefore no fields. The picker had already been taught to name
// such a mask (#671 / DesktopFixedMaskChoiceTests); nothing had been taught where to READ it.
//
// The symptom is a pane that looks finished: the title, the mask line and the values in read mode are all
// correct, and the edit form simply has nothing between the mask picker and the tag box. Which is why this test
// asks for the FIELD rather than for the absence of an error.
[Collection(UiCollection.Name)]
public class DesktopTypedFolderIndexFieldsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTypedFolderIndexFieldsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_mailboxs_address_field_is_offered_by_the_index_editor()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(token);

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The demo tenant's DEPARTMENT mailbox (ADR 0684) — the same object, at the same path, the report came
        // from. Reached by walking the tree rather than by a composed path, which is also the only way to learn
        // its id.
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var departmentsId = await ChildAsync(http, repoId, "Departments");
        var eventsId = await ChildAsync(http, departmentsId, "Events");

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await vm.OpenFolderAsync($"/api/documents/{eventsId}");

        vm.SelectedItem = vm.Items.First(i => i.Name == "Mailbox");
        // Waited on the MASK LINE, not the title: the title is set synchronously at the top of the detail load
        // and the addresses this test is about arrive with it — beginning the edit on the title alone races the
        // load and fails for a reason that has nothing to do with the defect.
        await WaitForAsync(() => vm.DetailTitle == "Mailbox" && vm.MaskLine.Contains("Mailbox"));

        await vm.BeginEditCommand.ExecuteAsync(null);

        // The mask is named — that much already worked (#671).
        Assert.Equal("Mailbox", vm.SelectedMaskChoice?.Name);

        // And its fields are there to fill in. Before the fix MaskEditFields was EMPTY, so the address list
        // could not be set from either client.
        var addresses = vm.MaskEditFields.SingleOrDefault(f => f.Name == "eMail Addresses");
        Assert.NotNull(addresses);
        Assert.True(addresses!.IsMultiLine, "the address list is a list field, so it takes the multi-line editor");

        // Filled with what the mailbox already claims, not blank: the editor opens on the current values, and a
        // blank box over a claimed address would release every claim on the first save (#703's EnforceAsync
        // reads an omitted field as releasing them all).
        Assert.Contains("@", addresses.TextValue);
    }

    private static async Task<Guid> ChildAsync(HttpClient http, Guid parentId, string name) =>
        (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{parentId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name)
            .GetProperty("id").GetGuid();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }
}
