using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The contents list's columns on the desktop (#768) — including the OWNER column, and including a REFERENCE
// row, which is where the defect lived: a reference was projected as a stub, so a shortcut row drew blank
// Type / Doc date / Size / Tags cells beside a real row that filled them.
//
// Driven through the real view-model against the real Api, because the screenshot hooks run on synthetic demo
// data where every one of these values is empty by construction — a picture would show the column existing and
// prove nothing about it carrying anything.
[Collection(UiCollection.Name)]
public class DesktopContentsColumnsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopContentsColumnsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_child_and_a_reference_to_it_carry_the_same_columns()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        // A folder holding a real document, and a second folder holding a REFERENCE to that same document —
        // so the two rows describe one document and any difference between them is the defect.
        var homeName = $"cc{Guid.NewGuid():N}"[..8];
        var homeId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = homeName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var elsewhereName = $"ce{Guid.NewGuid():N}"[..8];
        var elsewhereId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = elsewhereName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var docName = $"cd{Guid.NewGuid():N}"[..8];
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{homeId}/children", new { name = docName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("column contents")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync($"/api/documents/{elsewhereId}/references", new { targetId = docId })).EnsureSuccessStatusCode();

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(new SimplArchiveApiClient(token), SelfHostedAppFixture.AdminEmail);

        await vm.OpenFolderAsync($"/api/documents/{homeId}");
        var child = vm.Items.First(i => i.Name == docName);

        await vm.OpenFolderAsync($"/api/documents/{elsewhereId}");
        var shortcut = vm.Items.First(i => i.Name == docName);

        // The owner column, absent entirely before this.
        Assert.Equal(SelfHostedAppFixture.AdminDisplayName, child.CreatedBy);

        // …and the shortcut says the same things about the same document. Asserted as EQUALITY rather than
        // as "not empty": the point is that one document does not describe itself differently depending on
        // which of its two appearances you are looking at.
        Assert.True(shortcut.IsReference, "the row in the second folder should be the reference");
        Assert.Equal(child.CreatedBy, shortcut.CreatedBy);
        Assert.Equal(child.DocumentType, shortcut.DocumentType);
        Assert.Equal(child.DocumentDate, shortcut.DocumentDate);
        Assert.Equal(child.SizeBytes, shortcut.SizeBytes);

        // And they are not equal by both being empty, which "same columns" would otherwise be satisfied by.
        Assert.False(string.IsNullOrWhiteSpace(child.DocumentType), "Type is blank on the child row");
        Assert.NotNull(child.DocumentDate);
        Assert.NotNull(child.SizeBytes);
    }
}
