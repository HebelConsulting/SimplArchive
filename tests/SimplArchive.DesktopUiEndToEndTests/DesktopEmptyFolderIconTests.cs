using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the empty-folder tree icon (ADR "Empty-folder tree icon", issue #352): a folder with
// nothing inside is drawn differently from one that holds something. The distinction that matters is HasChildren
// (any child) vs HasSubfolders (the expander caret) — a folder holding only DOCUMENTS is a leaf in the
// folders-only tree but is NOT empty.
//
// What "differently" means is ADR "Folder icon scheme": the outline glyph in the same gold at reduced alpha,
// rather than a second flat colour. Gold is reserved for containers, so the launchers and admin nodes go muted.
[Collection(UiCollection.Name)]
public class DesktopEmptyFolderIconTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopEmptyFolderIconTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_new_folder_is_empty_until_a_document_is_filed_into_it()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var (emptyWhenNew, notEmptyWithADocument) = await vm.EmptyFolderIconSelfTestAsync(await Ui.GetUserTokenAsync(_app.BaseUrl));

        Assert.True(emptyWhenNew);
        Assert.True(notEmptyWithADocument);
    }

    [Theory]
    // A real folder with nothing inside — the faded outline case.
    [InlineData(false, null, null, false, true)]
    // Holds something (a subfolder or a document) — the normal gold glyph.
    [InlineData(true, null, null, false, false)]
    // The pseudo-nodes are never "empty": a synthetic admin branch and an Inbox / Check-out launcher aren't
    // folders at all, so the flag must not reach them even though they carry no children.
    [InlineData(false, "mdi-shield-account", null, false, false)]
    [InlineData(false, null, "inbox", false, false)]
    // An admin-browsed OTHER user's personal repository is a normal folder — it has no launchers, so an empty one
    // reads as empty. (The caller's OWN Personal root never gets here: it is constructed with the default
    // hasChildren: true precisely because it always holds the launchers.)
    [InlineData(false, null, null, true, true)]
    public void Only_a_real_folder_with_no_children_reads_as_empty(bool hasChildren, string? syntheticIcon, string? personalKind, bool isPersonal, bool expected)
    {
        var node = Node(hasChildren, syntheticIcon, personalKind, isPersonal);

        Assert.Equal(expected, node.IsEmptyFolder);
        // The glyph follows the flag — the outline variant is what carries "empty" without relying on colour,
        // whatever the node's glyph happens to be (a folder, a shortcut, a person). Compared against the SAME
        // node with children rather than matched on "-outline": the Check-out launcher's glyph is natively an
        // outline one, so a suffix test would call it empty.
        var withChildren = Node(hasChildren: true, syntheticIcon, personalKind, isPersonal);
        Assert.Equal(expected, node.IconValue != withChildren.IconValue);
    }

    // Which theme brush each kind of node takes (ADR "Folder icon scheme"). Gold is not decoration: it marks a
    // place documents live, which is why the two launchers and the admin branch — real nodes, but not containers
    // — deliberately fall to the muted text colour instead.
    [Theory]
    // A folder holding something, and the personal root: containers, so gold.
    [InlineData(true, null, null, false, "WbFolder")]
    [InlineData(true, null, null, true, "WbFolder")]
    // An empty folder: the same gold, faded (App.axaml sets the alpha per theme).
    [InlineData(false, null, null, false, "WbFolderEmpty")]
    // Not containers at all.
    [InlineData(false, null, "inbox", false, "WbMuted")]
    [InlineData(false, null, "checkout", false, "WbMuted")]
    [InlineData(false, "mdi-shield-account", null, false, "WbMuted")]
    public void Gold_marks_a_container_and_nothing_else(bool hasChildren, string? syntheticIcon, string? personalKind, bool isPersonal, string expectedKey)
    {
        var node = Node(hasChildren, syntheticIcon, personalKind, isPersonal);

        Assert.Equal(expectedKey, node.IconBrushKey);
        // Exactly one style class applies — the view has no way to bind a resource key, so they must not overlap.
        Assert.Equal(1, new[] { node.UsesFolderBrush, node.UsesEmptyFolderBrush, node.UsesMutedBrush }.Count(on => on));
    }

    private static TreeNodeViewModel Node(bool hasChildren, string? syntheticIcon, string? personalKind, bool isPersonal) =>
        new(Guid.NewGuid(), "Folder", hasSubfolders: false, loadChildren: null,
            isPersonal: isPersonal, syntheticIcon: syntheticIcon, personalKind: personalKind, hasChildren: hasChildren);
}
