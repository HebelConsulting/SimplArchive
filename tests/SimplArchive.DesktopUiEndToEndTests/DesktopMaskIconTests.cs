using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Domain.Masks;

namespace SimplArchive.UiEndToEndTests;

// Per-mask icons: the desktop half of the vocabulary guard, and the proof that the token survives the wire.
//
// The vocabulary is a wire contract with TWO independent tables — the server names a thing and each client
// answers from its own icon set, because Material and Material Design Icons share no glyph name. Two tables
// that must agree is the shape that drifts, and the drift is silent: a token with no glyph here draws a plain
// folder in the desktop and the right icon on the web, which nobody notices until they use both. The web half
// lives in MaskIconVocabularyTests; this project is the only one that can reference the Avalonia client.
[Collection(UiCollection.Name)]
public class DesktopMaskIconTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopMaskIconTests(SelfHostedAppFixture app) => _app = app;

    private async Task<MainWindowViewModel> OpenAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        return vm;
    }

    private static async Task<Dictionary<string, TreeNodeViewModel>> PersonalChildrenAsync(MainWindowViewModel vm)
    {
        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        return personal.Children
            .Where(c => !c.IsSynthetic && !c.IsLauncher)
            .ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_token_the_server_ships_has_a_glyph_in_this_client()
    {
        var missing = WellKnownMaskIds.IconTokens.Values
            .Distinct()
            .Where(token => MaskIcon.For(token) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            $"The server ships icon token(s) this client has no glyph for: {string.Join(", ", missing)}. "
            + "Add them to DesktopClient/Services/MaskIcon.cs — an unmapped token falls back to the generic "
            + "folder, so the mask looks right on the web and wrong here.");
    }

    // The tree builds an empty folder's icon by APPENDING "-outline" rather than listing outline names, so a
    // glyph whose set has no outline partner renders nothing at all — silently, and only for empty folders,
    // which is a bug that hides until someone makes a folder and looks at it before filling it.
    //
    // The names were checked against the packaged set when they were chosen; this is what keeps that true. It
    // asserts against the icon provider rather than a list copied from it, so a package upgrade that drops or
    // renames a glyph fails here rather than in front of a user.
    [Fact]
    public void Every_glyph_has_the_outline_partner_the_empty_folder_rule_needs()
    {
        var provider = new Projektanker.Icons.Avalonia.MaterialDesign.MaterialDesignIconProvider();

        // The provider's contract for an unknown name is not "return null" — it throws — so a resolvable name
        // is one that comes back without an exception. Asked of the provider rather than of a list copied out
        // of it, so a package upgrade that renames or drops a glyph fails here rather than in front of a user.
        static bool Resolves(Projektanker.Icons.Avalonia.IIconProvider provider, string name)
        {
            try
            {
                return provider.GetIcon(name) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        foreach (var token in WellKnownMaskIds.IconTokens.Values.Distinct())
        {
            var glyph = MaskIcon.For(token)!;
            Assert.True(Resolves(provider, glyph), $"{token}: '{glyph}' is not in the icon set at all.");
            Assert.True(Resolves(provider, $"{glyph}-outline"),
                $"{token}: '{glyph}' has no '-outline' partner, so an EMPTY folder wearing it renders nothing.");
        }
    }

    [Fact]
    public void No_two_masks_are_drawn_the_same()
    {
        var shared = WellKnownMaskIds.IconTokens
            .GroupBy(t => MaskIcon.For(t.Value))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" + ", g.Select(t => t.Value)))
            .ToList();

        Assert.True(shared.Count == 0, $"Masks sharing one glyph: {string.Join("; ", shared)}");
    }

    // The chain no unit test can reach: the seeder wrote the token, the listing carried it, the client parsed
    // it, and the view-model turned it into a glyph.
    [Fact]
    public async Task A_typed_folder_wears_its_own_glyph_all_the_way_from_the_database()
    {
        var vm = await OpenAsync();
        var children = await PersonalChildrenAsync(vm);

        Assert.Equal("mdi-book-account", MaskIcon.For(children["My Addressbook"].MaskIconToken));
        Assert.Equal("mdi-calendar", MaskIcon.For(children["My Calendar"].MaskIconToken));

        // A plain folder is untouched — it has no token, so it keeps the glyph it always had. Without this the
        // suite would pass even if every mask resolved to something, which is the failure that looks like
        // success.
        Assert.Null(children["My Documents"].MaskIconToken);
    }

    // The empty-folder rule survives the change: a typed folder with nothing in it outlines its OWN glyph
    // rather than flattening to a plain folder, so being empty does not cost the node what it is.
    [Fact]
    public async Task An_empty_typed_folder_outlines_its_own_glyph()
    {
        var vm = await OpenAsync();
        var children = await PersonalChildrenAsync(vm);

        var addressbook = children["My Addressbook"];
        var expected = addressbook.IsEmptyFolder ? "mdi-book-account-outline" : "mdi-book-account";
        Assert.Equal(expected, addressbook.IconValue);

        // Whichever state it was in, the glyph is the ADDRESSBOOK's — never the plain folder's.
        Assert.StartsWith("mdi-book-account", addressbook.IconValue);
    }
}
