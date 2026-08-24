using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Setting a Mailbox's eMail address from the workbench (#729).
//
// It could not be done from either client. The index editor resolves a mask's field definitions by looking the
// mask up in the CATALOGUE, which is filtered to the masks a user may freely choose (#671) — so for a typed
// folder there was no row, no address and no fields: the pencil opened an edit form with nothing in it between
// the mask picker and the tags. The whole duplicate-claim confirmation flow both clients carry (#703) hung off
// this form and was therefore unreachable in the product while its API tests passed.
//
// Driven through the UI rather than the endpoint on purpose: the endpoint was never the broken part. What was
// broken is whether a person can get to it, and only a rendered form can answer that.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTypedFolderIndexFieldsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTypedFolderIndexFieldsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_mailboxs_address_is_set_from_the_index_pane()
    {
        var folder = $"dept-{Guid.NewGuid():N}"[..12];
        var address = $"team-{Guid.NewGuid():N}"[..12] + "@demo.test";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Its OWN mailbox, seeded over the API: an address claim is unique across the tenant, so writing one
        // onto the demo's shared department mailbox would collide with whatever else is running in this leg.
        // A Mailbox is admitted by a Folder, not by a repository root, so the plain folder is not scenery.
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var folderId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = folder, folderMask = "folder" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // By mask ID, not by slug: the `folderMask` slugs cover the kinds that shipped with one, and a Mailbox
        // is not among them — the id is the general form the endpoint documents beside it.
        var mailboxMaskId = (await http.GetFromJsonAsync<JsonElement>("/api/masks")).GetProperty("masks")
            .EnumerateArray().First(m => m.GetProperty("name").GetString() == "Mailbox").GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{folderId}/children", new { name = "Mailbox", maskId = mailboxMaskId }))
            .EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var index = page.Locator("[data-pane='index']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = folder }).First.DblClickAsync();

        // SELECTED, not opened: a folder's own metadata is edited from the pane beside the listing it sits in.
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Mailbox" }).First.ClickAsync();
        await Expect(index.GetByText("Mask: Mailbox")).ToBeVisibleAsync();

        await index.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();

        // The field itself. This is the assertion that failed: the row did not exist, because the form had no
        // fields at all.
        var addresses = index.Locator(".wb-mask-row").Filter(new() { HasText = "eMail Addresses" });
        await Expect(addresses).ToBeVisibleAsync();

        var box = addresses.Locator("textarea").First; // a list field takes the multi-line editor (#703)
        await box.FillAsync(address);
        await box.EvaluateAsync("el => el.blur()"); // MudTextField commits on blur (no Immediate)

        await index.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        // Claimed, and readable back on the mailbox — the point of the whole exercise.
        await Expect(index.GetByText(address)).ToBeVisibleAsync();
    }
}
