using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Sticky notes / positional annotations (ADR "Document annotations") end to end via the real desktop api
// client: create a note on a confirmed version, read it back (author can edit + delete), edit it (position +
// text, with the embedded ETag as If-Match), then delete it.
[Collection(UiCollection.Name)]
public class DesktopAnnotationTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAnnotationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Note_create_edit_delete_round_trip()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        await api.UploadFileAsync(repo.Id, $"noted-{suffix}.txt", Encoding.UTF8.GetBytes("some content"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).First(n => n.Name == $"noted-{suffix}");

        var preview = await api.GetPreviewAsync(doc.Id);
        Assert.NotNull(preview.AnnotationsUrl);
        var url = preview.AnnotationsUrl!;

        // Create a note.
        await api.CreateAnnotationAsync(url, 0, 0.2, 0.3, "Review this", "#FFEB3B");
        var afterCreate = await api.GetAnnotationsAsync(url);
        Assert.True(afterCreate.CanCreate);   // the admin has CanAnnotate (ADR "CanAnnotate right")
        var note = Assert.Single(afterCreate.Items);
        Assert.Equal("Review this", note.Text);
        Assert.True(note.CanEdit);   // the author
        Assert.True(note.CanDelete);
        Assert.False(string.IsNullOrEmpty(note.Etag));

        // Edit it (move + retext) using the embedded ETag as If-Match.
        await api.UpdateAnnotationAsync(url, note.Id, 0, 0.5, 0.6, "Reviewed", "#8BC34A", note.Etag);
        var afterEdit = await api.GetAnnotationsAsync(url);
        var edited = Assert.Single(afterEdit.Items);
        Assert.Equal("Reviewed", edited.Text);
        Assert.Equal("#8BC34A", edited.Color);
        Assert.Equal(0.6, edited.PositionY, 3);
        Assert.NotEqual(note.Etag, edited.Etag); // the token rotated on update

        // Delete it (fresh ETag).
        await api.DeleteAnnotationAsync(url, edited.Id, edited.Etag);
        Assert.Empty((await api.GetAnnotationsAsync(url)).Items);

        // Clean up the throwaway document.
        await api.DeleteAsync(doc.Id);
    }

    [Fact]
    public async Task Note_box_carries_a_size_and_can_be_resized()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        await api.UploadFileAsync(repo.Id, $"sized-{suffix}.txt", Encoding.UTF8.GetBytes("some content"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).First(n => n.Name == $"sized-{suffix}");
        var url = (await api.GetPreviewAsync(doc.Id)).AnnotationsUrl!;

        // A note is created as a sized box (kind 0 + width/height) so it renders as an always-visible box
        // (ADR "Post-it note boxes").
        await api.CreateAnnotationAsync(url, 0, 0, 0.2, 0.3, 0.25, 0.1, "Sized note", "#FFEB3B");
        var note = Assert.Single((await api.GetAnnotationsAsync(url)).Items);
        Assert.Equal(0, note.Kind);
        Assert.Equal(0.25, note.Width!.Value, 3);
        Assert.Equal(0.1, note.Height!.Value, 3);
        Assert.Equal("Sized note", note.Text);

        // Resize it — a size-only update (position/text/colour preserved), the exact path the corner-grip drag uses.
        await api.UpdateAnnotationAsync(url, note.Id, note.PageIndex, note.PositionX, note.PositionY, 0.4, 0.2, note.Text, note.Color, note.Etag);
        var resized = Assert.Single((await api.GetAnnotationsAsync(url)).Items);
        Assert.Equal(0.4, resized.Width!.Value, 3);
        Assert.Equal(0.2, resized.Height!.Value, 3);
        Assert.Equal("Sized note", resized.Text);

        await api.DeleteAnnotationAsync(url, resized.Id, resized.Etag);
        await api.DeleteAsync(doc.Id);
    }

    [Fact]
    public async Task Multi_select_copy_paste_and_delete()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        await api.UploadFileAsync(repo.Id, $"multi-{suffix}.txt", Encoding.UTF8.GetBytes("some content"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).First(n => n.Name == $"multi-{suffix}");
        var url = (await api.GetPreviewAsync(doc.Id)).AnnotationsUrl!;

        // Two notes + a highlight on page 0.
        await api.CreateAnnotationAsync(url, 0, 0, 0.2, 0.2, 0.2, 0.06, "note one", "#FFEB3B");
        await api.CreateAnnotationAsync(url, 0, 0, 0.5, 0.5, 0.2, 0.06, "note two", "#B3E5FC");
        await api.CreateAnnotationAsync(url, 0, 1, 0.1, 0.8, 0.3, 0.05, "", "#F44336");

        // Drive the multi-select commands the overlay fires (ADR "Annotation multi-select").
        var vm = new PreviewViewModel { Api = api };
        await vm.LoadAnnotationsForTestAsync(url);
        var all = (await api.GetAnnotationsAsync(url)).Items;
        var noteOne = all.First(a => a.Text == "note one");
        var noteTwo = all.First(a => a.Text == "note two");

        // Select both notes (click one, Ctrl-click the other).
        vm.SelectAnnotationCommand.Execute(new AnnotationSelect(noteOne.Id, Toggle: false));
        vm.SelectAnnotationCommand.Execute(new AnnotationSelect(noteTwo.Id, Toggle: true));
        Assert.True(vm.HasSelectedAnnotations);
        Assert.Equal(2, vm.SelectedAnnotationIdsForTest.Count);

        // Copy → paste duplicates them (offset), leaving 5 total (3 originals + 2 pasted copies).
        vm.CopySelectedAnnotationsCommand.Execute(null);
        Assert.True(vm.HasClipboardAnnotations);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.PasteAnnotationsCommand).ExecuteAsync(null);
        var afterPaste = (await api.GetAnnotationsAsync(url)).Items;
        Assert.Equal(5, afterPaste.Count);
        // A pasted copy of "note one" sits at the offset position, not the original.
        Assert.Contains(afterPaste, a => a.Text == "note one" && Math.Abs(a.PositionX - 0.23) < 0.001 && Math.Abs(a.PositionY - 0.23) < 0.001);

        // Re-select the two originals and delete them.
        vm.SelectAnnotationCommand.Execute(new AnnotationSelect(noteOne.Id, Toggle: false));
        vm.SelectAnnotationCommand.Execute(new AnnotationSelect(noteTwo.Id, Toggle: true));
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.DeleteSelectedAnnotationsCommand).ExecuteAsync(null);
        var afterDelete = (await api.GetAnnotationsAsync(url)).Items;
        Assert.Equal(3, afterDelete.Count); // 5 - 2 deleted
        Assert.DoesNotContain(afterDelete, a => a.Id == noteOne.Id || a.Id == noteTwo.Id);
        Assert.False(vm.HasSelectedAnnotations);

        await api.DeleteAsync(doc.Id);
    }

    [Fact]
    public async Task Highlight_recolour_move_and_resize()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        await api.UploadFileAsync(repo.Id, $"hl-{suffix}.txt", Encoding.UTF8.GetBytes("some content"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).First(n => n.Name == $"hl-{suffix}");
        var url = (await api.GetPreviewAsync(doc.Id)).AnnotationsUrl!;

        // A highlight (kind 1) drawn in the default colour.
        await api.CreateAnnotationAsync(url, 0, 1, 0.1, 0.2, 0.3, 0.05, "", "#FFEB3B");
        var vm = new PreviewViewModel { Api = api };
        await vm.LoadAnnotationsForTestAsync(url);
        var shape = Assert.Single((await api.GetAnnotationsAsync(url)).Items);

        // Select it + recolour via the toolbar palette (ADR "Highlighting redesign" — no dialog for shapes).
        vm.SelectAnnotationCommand.Execute(new AnnotationSelect(shape.Id, Toggle: false));
        Assert.True(vm.ShowAnnotationColorPalette);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SetAnnotationColorCommand).ExecuteAsync("#4FC3F7");
        Assert.Equal("#4FC3F7", Assert.Single((await api.GetAnnotationsAsync(url)).Items).Color);

        // Move the shape (the overlay reuses NoteMoved for a shape — sets the start point, keeps the extent).
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.NoteMovedCommand).ExecuteAsync(new NoteMove(shape.Id, 0, 0.4, 0.5));
        var moved = Assert.Single((await api.GetAnnotationsAsync(url)).Items);
        Assert.Equal(0.4, moved.PositionX, 3);
        Assert.Equal(0.5, moved.PositionY, 3);
        Assert.Equal(0.3, moved.Width!.Value, 3); // extent preserved

        // Resize the shape (reuses NoteResized — sets the extent, keeps the position).
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.NoteResizedCommand).ExecuteAsync(new NoteResize(shape.Id, 0, 0.5, 0.2));
        var resized = Assert.Single((await api.GetAnnotationsAsync(url)).Items);
        Assert.Equal(0.5, resized.Width!.Value, 3);
        Assert.Equal(0.2, resized.Height!.Value, 3);
        Assert.Equal(0.4, resized.PositionX, 3); // position preserved

        await api.DeleteAnnotationAsync(url, resized.Id, resized.Etag);
        await api.DeleteAsync(doc.Id);
    }

    [Fact]
    public async Task Shape_tool_resets_after_one_draw()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        await api.UploadFileAsync(repo.Id, $"tool-{suffix}.txt", Encoding.UTF8.GetBytes("some content"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).First(n => n.Name == $"tool-{suffix}");
        var url = (await api.GetPreviewAsync(doc.Id)).AnnotationsUrl!;

        var vm = new PreviewViewModel { Api = api };
        await vm.LoadAnnotationsForTestAsync(url);

        // Arm the highlight tool, then draw one shape — the tool deactivates (ADR "Draw-tool behaviour").
        vm.SelectShapeToolCommand.Execute("1");
        Assert.Equal(1, vm.AnnotationTool);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ShapeDrawnCommand).ExecuteAsync(new ShapeDraw(0, 1, 0.1, 0.1, 0.25, 0.05));
        Assert.Equal(0, vm.AnnotationTool);                        // reset after one draw
        Assert.Equal(1, Assert.Single((await api.GetAnnotationsAsync(url)).Items).Kind);

        await api.DeleteAsync(doc.Id);
    }

    [Fact]
    public async Task Markup_shape_create_read_delete_round_trip()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        await api.UploadFileAsync(repo.Id, $"markup-{suffix}.txt", Encoding.UTF8.GetBytes("some content"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).First(n => n.Name == $"markup-{suffix}");
        var url = (await api.GetPreviewAsync(doc.Id)).AnnotationsUrl!;

        // A highlight box (kind 1) carries a width/height and no text.
        await api.CreateAnnotationAsync(url, 0, 1, 0.1, 0.2, 0.3, 0.05, "", "#FFEB3B");
        var shape = Assert.Single((await api.GetAnnotationsAsync(url)).Items);
        Assert.Equal(1, shape.Kind);
        Assert.Equal(0.3, shape.Width!.Value, 3);
        Assert.Equal(0.05, shape.Height!.Value, 3);

        // Recolour the highlight — a colour-only update on a text-less shape (the palette-for-highlights fix,
        // ADR "Annotation shape recolour"): the empty text is valid for a shape, and the colour changes.
        await api.UpdateAnnotationAsync(url, shape.Id, shape.PageIndex, shape.PositionX, shape.PositionY, shape.Width, shape.Height, "", "#4FC3F7", shape.Etag);
        var recoloured = Assert.Single((await api.GetAnnotationsAsync(url)).Items);
        Assert.Equal("#4FC3F7", recoloured.Color);
        Assert.Equal("", recoloured.Text);

        await api.DeleteAnnotationAsync(url, recoloured.Id, recoloured.Etag);
        Assert.Empty((await api.GetAnnotationsAsync(url)).Items);
        await api.DeleteAsync(doc.Id);
    }
}
