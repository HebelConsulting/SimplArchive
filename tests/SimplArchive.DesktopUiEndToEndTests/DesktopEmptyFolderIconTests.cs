using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the empty-folder tree icon (ADR "Empty-folder tree icon", issue #352): a folder with
// nothing inside gets a pastel glyph instead of the usual gold one. The distinction that matters is HasChildren
// (any child) vs HasSubfolders (the expander caret) — a folder holding only DOCUMENTS is a leaf in the
// folders-only tree but is NOT empty.
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
    // A real folder with nothing inside — the pastel case.
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
        var node = new TreeNodeViewModel(
            Guid.NewGuid(), "Folder", hasSubfolders: false, loadChildren: null,
            isPersonal: isPersonal, syntheticIcon: syntheticIcon, personalKind: personalKind, hasChildren: hasChildren);
        var nonEmpty = new TreeNodeViewModel(Guid.NewGuid(), "Folder", false, null, hasChildren: true);

        Assert.Equal(expected, node.IsEmptyFolder);
        // The brush follows the flag — a distinct glyph colour is the whole point of the flag.
        Assert.Equal(expected, !ReferenceEquals(node.IconBrush, nonEmpty.IconBrush));
    }
}
