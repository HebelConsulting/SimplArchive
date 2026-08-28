using System.Net.Http.Json;
using System.Net;

namespace SimplArchive.EndToEndTests;

// Creating what a notebook holds (#564): sections, and notes stored as the .eml a notes client expects.
//
// The rels are the contract these tests really guard. A client shows "New section" / "New note" because the
// document ADVERTISED them, so a notebook that stops advertising them silently disarms both clients — and an
// ordinary folder that starts advertising them offers an action that cannot work.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class NotebookSectionsAndNotesTests
{
    private readonly E2EApiFactory _factory;

    public NotebookSectionsAndNotesTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, Guid NotebookId, Guid PersonalId)> SeedAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"nb-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "nb-1234", "Notebook User");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "nb-1234"));

        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var personalId = personal.GetProperty("id").GetGuid();

        // The notebook is created the way the product creates one, rather than found: it is not provisioned,
        // and it lives under the MAILBOX rather than loose in Personal (#596). Generating an IMAP credential
        // materialises the mailbox — the second of the two triggers — so this is also the shortest honest way
        // to have one at all.
        await TestJson.Post(api, "/api/me/imap-access", new { });
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox")
            .GetProperty("id").GetGuid();

        var notebook = await TestJson.Post(api, $"/api/documents/{mailboxId}/children",
            new { name = $"NB {Guid.NewGuid():N}"[..12], folderMask = "notes" });
        return (api, notebook.GetProperty("id").GetGuid(), personalId);
    }

    private static async Task<List<string>> RelsAsync(HttpClient api, Guid documentId)
    {
        var doc = await TestJson.Get(api, $"/api/documents/{documentId}");
        return [.. doc.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!)];
    }

    [Fact]
    public async Task Only_a_notebook_or_a_section_offers_sections_and_notes()
    {
        var (api, notebookId, personalId) = await SeedAsync();
        using var _ = api;

        var notebookRels = await RelsAsync(api, notebookId);
        Assert.Contains("sections", notebookRels);
        Assert.Contains("notes", notebookRels);

        // The personal root is an ordinary folder — the affordance must be absent, which is what tells a
        // client not to offer it (ADR 0543). Asserting BOTH directions is the point: a rel that is always
        // present gates nothing.
        var rootRels = await RelsAsync(api, personalId);
        Assert.DoesNotContain("sections", rootRels);
        Assert.DoesNotContain("notes", rootRels);

        // An addressbook is typed too, but holds Contacts — it must not offer notebook affordances.
        var book = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Addressbook");
        var bookRels = await RelsAsync(api, book.GetProperty("id").GetGuid());
        Assert.DoesNotContain("sections", bookRels);
        Assert.DoesNotContain("notes", bookRels);
    }

    [Fact]
    public async Task A_section_nests_and_holds_the_same_two_things()
    {
        var (api, notebookId, _) = await SeedAsync();
        using var _1 = api;

        var section = await TestJson.Post(api, $"/api/documents/{notebookId}/sections", new { name = "Work" });
        var sectionId = section.GetProperty("id").GetGuid();

        // The create response carries the addresses for what the section can hold — a response with only an id
        // is what forces the next call to be composed from it.
        var offered = section.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToList();
        Assert.Contains("sections", offered);
        Assert.Contains("notes", offered);

        // The family is RECURSIVE — a section inside a section is the case that broke the old one-folder-one-item
        // model, so it is the case worth driving through the real endpoint.
        var nested = await TestJson.Post(api, $"/api/documents/{sectionId}/sections", new { name = "2026" });
        var nestedId = nested.GetProperty("id").GetGuid();

        var note = await TestJson.Post(api, $"/api/documents/{nestedId}/notes",
            new { title = "Deep note", body = "Filed three levels down." });
        Assert.NotEqual(Guid.Empty, note.GetProperty("id").GetGuid());

        var contents = (await TestJson.Get(api, $"/api/documents/{nestedId}/children"))
            .GetProperty("children").EnumerateArray().ToList();
        Assert.Single(contents);
        Assert.Equal("Deep note", contents[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_note_is_stored_as_the_message_a_notes_client_reads()
    {
        var (api, notebookId, _) = await SeedAsync();
        using var _1 = api;

        var created = await TestJson.Post(api, $"/api/documents/{notebookId}/notes",
            new { title = "Shopping", body = "Milk\nBread" });
        var noteId = created.GetProperty("id").GetGuid();

        // It wears the Note mask — which is also what typed-folder containment required for it to be filed here
        // at all, so a wrong mask would have been refused rather than silently stored. Read from the `mask`
        // rel rather than a field on the document: the document resource does not carry the mask NAME, and
        // following the rel is what a client would do anyway.
        var mask = await TestJson.Get(api, $"/api/documents/{noteId}/mask");
        Assert.Equal("Note", mask.GetProperty("name").GetString());

        // …and it carries the correlation key an edit from any client matches on. Without it, editing the note
        // in Apple Notes would create a SECOND note rather than a new version of this one.
        var indexData = await TestJson.Get(api, $"/api/documents/{noteId}/index-data");
        var fields = indexData.GetProperty("fields").EnumerateArray().ToList();
        var uuid = fields.Single(f => f.GetProperty("fieldName").GetString() == "Note UUID")
            .GetProperty("values").EnumerateArray().Single().GetString();
        Assert.False(string.IsNullOrWhiteSpace(uuid));

        // A second note gets its OWN key — a shared or empty one would make every edit from a notes client
        // land on whichever note matched first.
        var other = await TestJson.Post(api, $"/api/documents/{notebookId}/notes", new { title = "Other", body = "b" });
        var otherData = await TestJson.Get(api, $"/api/documents/{other.GetProperty("id").GetGuid()}/index-data");
        var otherUuid = otherData.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldName").GetString() == "Note UUID")
            .GetProperty("values").EnumerateArray().Single().GetString();
        Assert.NotEqual(uuid, otherUuid);
    }

    [Fact]
    public async Task A_note_needs_a_title_and_an_ordinary_folder_has_no_notes_at_all()
    {
        var (api, notebookId, personalId) = await SeedAsync();
        using var _1 = api;

        // The title becomes both the tree name and the message Subject, so an empty one is refused rather than
        // producing a note that is unnameable in one place and unidentifiable in the other.
        var blank = await api.PostAsJsonAsync($"/api/documents/{notebookId}/notes", new { title = "  ", body = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("NOTE_TITLE_REQUIRED", await blank.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // On an ordinary folder the sub-resource does not EXIST — 404, not 403: a refusal would imply the
        // caller might be granted it, and no grant makes a plain folder hold notes.
        var wrongPlace = await api.PostAsJsonAsync($"/api/documents/{personalId}/notes", new { title = "N", body = "b" });
        Assert.Equal(HttpStatusCode.NotFound, wrongPlace.StatusCode);

        var wrongSection = await api.PostAsJsonAsync($"/api/documents/{personalId}/sections", new { name = "S" });
        Assert.Equal(HttpStatusCode.NotFound, wrongSection.StatusCode);
    }
}
