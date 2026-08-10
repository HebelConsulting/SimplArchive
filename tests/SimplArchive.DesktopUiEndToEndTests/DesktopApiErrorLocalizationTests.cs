using System.Globalization;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of issue #424: when the SERVER refuses something, the message that reaches the user must be
// in the user's language — not the API's English Problem Details `detail`.
//
// The refusal is real, not mocked: a document frozen by a legal hold refuses an index-data edit
// (DOCUMENT_UNDER_LEGAL_HOLD, 409), and SetIndexDataAsync routes its failure through ThrowIfProblemAsync, which
// is on the path of every failed call in the client and was the single biggest source of English in an otherwise
// German UI. The web suite provokes the SAME refusal (WebApiErrorLocalizationTests), so the two clients are held
// to one guarantee.
//
// This used to provoke CANNOT_CHANGE_ROOT_INHERITANCE by toggling inheritance on a repository root. That is no
// longer reachable: the server stopped advertising the acl-inheritance rel on a root, so a conforming client
// never offers the action (#426). Re-pointed rather than deleted — the guarantee under test is about LANGUAGE,
// not about which refusal carries it.
//
// Asserts the German sentence itself, not merely "not the English one": an inequality assertion also passes when
// the message is empty or has silently fallen back, which are the two most likely ways this breaks.
[Collection(UiCollection.Name)]
public class DesktopApiErrorLocalizationTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopApiErrorLocalizationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_server_refusal_reaches_the_user_in_their_language()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A throwaway folder, frozen by a hold the demo admin may place (CanLegalHold via the demo seed).
        var repo = (await api.GetRepositoriesAsync())[0];
        var folderName = $"i18n-{suffix}";
        await api.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == folderName);
        var hold = await api.CreateLegalHoldAsync($"Matter {suffix}", "localisation guard");
        await api.AddLegalHoldItemAsync(hold, folder.Id);

        // Only CurrentUICulture — never Culture.Apply, which sets the process-global DefaultThreadCurrentUICulture
        // and would leak German into the culture-dependent messages other tests assert on in English. The setter
        // is AsyncLocal-backed, so it flows through the awaits inside the api client and is confined to this test.
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");

            var error = await Assert.ThrowsAsync<ApiActionException>(
                () => api.SetIndexDataAsync(folder.Id, []));

            Assert.Equal(
                "Dieses Dokument unterliegt einem Legal Hold und kann nicht geändert werden.",
                error.Message);

            // The API's own English prose never reaches the user, and neither does the generic fallback: the code
            // is mapped, so the user gets the sentence about THIS refusal.
            // Not "legal hold" — the GERMAN sentence uses that term too ("unterliegt einem Legal Hold"). The
            // English detail's own prose is what must never appear.
            Assert.DoesNotContain("This document is under", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("cannot be changed", error.Message, StringComparison.Ordinal);
            Assert.NotEqual("Die Aktion wurde vom Server abgelehnt.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
            await api.ReleaseLegalHoldAsync(hold);
            await api.DeleteAsync(folder.Id);
        }
    }
}
