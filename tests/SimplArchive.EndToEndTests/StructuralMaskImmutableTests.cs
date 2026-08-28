using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// A structural folder keeps its type, over the real API (ADR 0685).
//
// The invariant itself is covered by StructuralMaskImmutabilityTests against the DbContext. What only an
// end-to-end test can show is the WIRE CAUSE — and that is the whole reason the refusal has a type of its own.
// DocumentMetadataController wraps its save in `catch (InvalidOperationException) → RequiredFieldMissingException`,
// so an untranslated refusal reaches the user as "a required field is missing" on a folder with no missing
// field: a specific, checkable, FALSE cause. Asserting the status alone would pass either way.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StructuralMaskImmutableTests
{
    private readonly E2EApiFactory _factory;

    public StructuralMaskImmutableTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Re_typing_and_un_typing_a_notebook_are_both_refused_with_their_own_cause()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"struct-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Struct User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        // The mailbox is not provisioned with the personal space — it is materialised on first delivery or,
        // as here, by generating the IMAP credential (#562).
        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        await TestJson.Post(api, "/api/me/imap-access", new { });
        var mailboxId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox")
            .GetProperty("id").GetGuid();

        await TestJson.Post(api, $"/api/documents/{mailboxId}/children", new { name = "Notebook", folderMask = "notes" });
        var notebookId = (await TestJson.Get(api, $"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Notebook")
            .GetProperty("id").GetGuid();

        var folderMaskId = (await TestJson.Get(api, "/api/masks")).GetProperty("masks").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "Folder")
            .GetProperty("id").GetGuid();

        // Re-typing it to a plain folder.
        var retype = await api.PutAsJsonAsync($"/api/documents/{notebookId}/mask", new { maskId = folderMaskId });
        Assert.Equal(HttpStatusCode.Conflict, retype.StatusCode);
        Assert.Contains("STRUCTURAL_MASK_IMMUTABLE", await retype.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ...and clearing it, which breaks the projection just as completely.
        var clear = await api.DeleteAsync($"/api/documents/{notebookId}/mask");
        Assert.Equal(HttpStatusCode.Conflict, clear.StatusCode);
        Assert.Contains("STRUCTURAL_MASK_IMMUTABLE", await clear.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The mailbox it lives in is equally fixed — the folder mail arrives into.
        var mailbox = await api.PutAsJsonAsync($"/api/documents/{mailboxId}/mask", new { maskId = folderMaskId });
        Assert.Equal(HttpStatusCode.Conflict, mailbox.StatusCode);
        Assert.Contains("STRUCTURAL_MASK_IMMUTABLE", await mailbox.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ...and the notebook is still a notebook afterwards, so the refusal did not half-apply.
        Assert.Equal("Notebook", (await TestJson.Get(api, $"/api/documents/{mailboxId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == notebookId)
            .GetProperty("documentType").GetString());
    }

    [Fact]
    public async Task A_calendar_may_still_be_re_typed()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"cal-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Cal User", canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        // A calendar in an ORDINARY repository, not the provisioned "My Calendar". The personal space's first
        // level is closed by its own invariant (#634), so re-typing a folder there is refused for a different
        // reason entirely — which would have made this control pass for the wrong cause.
        var repoId = (await TestJson.Post(api, "/api/repositories",
            new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var calendarId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
            new { name = $"cal{Guid.NewGuid():N}"[..9], folderMask = "calendar" })).GetProperty("id").GetGuid();

        var folderMaskId = (await TestJson.Get(api, "/api/masks")).GetProperty("masks").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "Folder")
            .GetProperty("id").GetGuid();

        // The decided boundary (ADR 0685): re-typing a Calendar costs only CalDAV subscribability, and what is
        // inside stays viable — so it is a preference, not a structural fact. This is the control: without it,
        // a rule that froze every typed folder would pass the test above and be wrong.
        var response = await api.PutAsJsonAsync($"/api/documents/{calendarId}/mask", new { maskId = folderMaskId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
