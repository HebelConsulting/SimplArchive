using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop Contacts tab (#564): the real api client reads the caller's addressbooks from the
// `davCollections` rel, and the tab's view-model turns them into a checkbox list whose ticked collections'
// contacts are merged into one list. CardDAV serves the same folders to external clients (ADR 0619) — this is
// the in-app half of that surface, so the two must agree about what a "collection" is.
[Collection(UiCollection.Name)]
public class DesktopContactsTabTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopContactsTabTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Tab_opens_on_the_personal_addressbook_and_lists_only_addressbooks()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // The personal space is provisioned on demand, and brings "My Addressbook" with it.
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var vm = new ContactsTabViewModel();
        vm.Setup(api);
        await vm.LoadAsync();

        var mine = Assert.Single(vm.Collections, c => c.Collection.IsPersonalDefault);
        Assert.Equal("My Addressbook", mine.Collection.Name);
        Assert.EndsWith("My Addressbook", mine.DisplayName, StringComparison.Ordinal); // parent-qualified
        Assert.True(mine.Writable);

        // The personal book opens ticked: a tab that needs a click before it shows anything reads as broken.
        Assert.True(mine.IsChecked);
        Assert.True(vm.CanCreate);

        // Only addressbooks — the caller's calendars answer the same shape but must not appear here.
        Assert.All(vm.Collections, c => Assert.Equal("addressbook", c.Collection.Kind));
        Assert.DoesNotContain(vm.Collections, c => c.Collection.Name == "My Calendar");

        // Every collection carries the addresses the tab acts from, so it never composes one (ADR 0543).
        Assert.All(vm.Collections, c =>
        {
            Assert.NotNull(c.Collection.Href("children"));
            Assert.NotNull(c.Collection.Href("collection-color"));
        });

        // Unticking every book empties the list and disables New — the affordance must not claim it can write
        // somewhere the user has not chosen.
        mine.IsChecked = false;
        await vm.OnCollectionToggledAsync();
        Assert.Empty(vm.Contacts);
        Assert.False(vm.CanCreate);
    }

    // A .vcf reaching an addressbook through the ORDINARY upload path, not only through CardDAV. This used to
    // be impossible: the children endpoint stamped the Folder mask, which defeated typed-folder containment's
    // own exemption for a document the finalizer has not classified yet — and the refusal was reported as
    // "a document with this name already exists", for a name that was a fresh GUID.
    [Fact]
    public async Task A_vcard_uploaded_into_an_addressbook_becomes_a_contact_the_tab_lists()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var book = (await api.DavCollections.ListAsync("addressbook")).Single(b => b.IsPersonalDefault);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var card = $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{suffix}\r\nFN:Zora Zimmer\r\nN:Zimmer;Zora;;;\r\nEND:VCARD\r\n";
        await api.Documents.UploadFileAsync(book.Href("children"), $"zora-{suffix}.vcf", Encoding.UTF8.GetBytes(card));

        var vm = new ContactsTabViewModel();
        vm.Setup(api);
        await vm.LoadAsync();

        // Named by the card, not by the file: the finalizer classified it as a Contact and took its FN. That
        // rename is the proof the classifier ran — a document still called "zora-…" would mean it landed as a
        // plain Basic Entry, which containment would then have refused.
        Assert.Contains(vm.Contacts, c => c.FullName == "Zora Zimmer");

        // The filter is a VIEW over the loaded rows, not a re-read: clearing it restores them with no request,
        // or every keystroke would cost a round trip.
        vm.Filter = "zzz-no-such-contact";
        Assert.DoesNotContain(vm.VisibleContacts, c => c.FullName == "Zora Zimmer");
        vm.Filter = string.Empty;
        Assert.Contains(vm.VisibleContacts, c => c.FullName == "Zora Zimmer");
    }

    // The other half of the same rule: a typed folder holds items, never folders. A caller that SAYS it wants a
    // folder is told why it cannot have one — rather than getting a name-conflict for a name that is free, or a
    // folder that the next save would refuse.
    //
    // A bare create carries no such statement (the endpoint serves both "make a folder" and step one of an
    // upload with the same body), so inside a typed folder it is taken as an item-to-be and left for the
    // finalizer to classify. Keeping "New folder" off the menu there is the clients' job, not this endpoint's.
    [Fact]
    public async Task Asking_for_a_folder_inside_an_addressbook_is_refused_with_the_reason()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(token);
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var book = (await api.DavCollections.ListAsync("addressbook")).Single(b => b.IsPersonalDefault);

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PostAsJsonAsync(
            book.Href("children"), new { name = $"nope-{Guid.NewGuid():N}", folderMask = "folder" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TYPED_FOLDER_CONTAINMENT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("already exists", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_calendar_side_of_the_same_listing_answers_calendars()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var calendars = await api.DavCollections.ListAsync("calendar");
        var mine = Assert.Single(calendars, c => c.IsPersonalDefault);
        Assert.Equal("My Calendar", mine.Name);
        Assert.All(calendars, c => Assert.Equal("calendar", c.Kind));
    }
}
