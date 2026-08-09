using System.Globalization;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of issue #424: when the SERVER refuses something, the message that reaches the user must be
// in the user's language — not the API's English Problem Details `detail`.
//
// The refusal is real, not mocked: breaking ACL inheritance on a repository ROOT is always rejected
// (CANNOT_CHANGE_ROOT_INHERITANCE, 400 — a root has no parent to inherit from), and SetInheritanceAsync routes
// its failure through ThrowIfProblemAsync, which is on the path of every failed call in the client and was the
// single biggest source of English in an otherwise German UI. The web suite provokes the SAME refusal through
// the Manage-access dialog (WebApiErrorLocalizationTests), so the two clients are held to one guarantee. That
// the dialog offers the toggle on a root at all is its own defect (#426) — when that is fixed, this test needs a
// different refusal to provoke, not deleting.
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
        var repositoryRoot = (await api.GetRepositoriesAsync())[0];

        // Only CurrentUICulture — never Culture.Apply, which sets the process-global DefaultThreadCurrentUICulture
        // and would leak German into the culture-dependent messages other tests assert on in English. The setter
        // is AsyncLocal-backed, so it flows through the awaits inside the api client and is confined to this test.
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");

            var error = await Assert.ThrowsAsync<ApiActionException>(() => api.SetInheritanceAsync(repositoryRoot.Id, true));

            Assert.Equal(
                "Die Vererbung kann an einem Archiv nicht geändert werden — es gibt keinen übergeordneten Ordner, von dem geerbt werden könnte.",
                error.Message);

            // The API's own English prose never reaches the user, and neither does the generic fallback: the code
            // is mapped, so the user gets the sentence about THIS refusal.
            Assert.DoesNotContain("Inheritance can't be changed", error.Message);
            Assert.NotEqual("Die Aktion wurde vom Server abgelehnt.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
