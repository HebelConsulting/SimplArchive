using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The headless check behind <c>--sort-thumbs-test &lt;token&gt; &lt;itemName&gt; &lt;apiBaseUrl&gt;</c>: the sort dialog's page
/// pictures (#522).
/// </summary>
/// <remarks>
/// <para>
/// "The sort dialog comes up empty" (#522) could not be reproduced by a plain unit test, because
/// <see cref="InboxPageThumbnails"/> decodes into Avalonia bitmaps, and a bare test process has no render
/// platform — every load fails there with <c>IPlatformRenderInterface</c> missing, which says nothing about
/// the product. So the check runs where the product runs: headless Avalonia + Skia, the same platform the
/// screenshot hooks use, against the real Api. It loads the named staged item's thumbnails, hands them to the
/// REAL <see cref="SortPagesDialog"/>, and prints one machine-readable line with both counts — the pipeline's
/// and the dialog's — so a failure names which half lost the pages.
/// </para>
/// <para>
/// In its own file rather than <c>Program.cs</c>, which is on the 1000-line standing-debt list (issue #466)
/// and may only get smaller: a hook that needs a home takes one with it.
/// </para>
/// </remarks>
public static class SortThumbnailsCheck
{
    /// <summary>Prints <c>SORT-THUMBS loaded=&lt;n&gt; dialog=&lt;n&gt;</c> or the failure. Returns true when pages arrived.</summary>
    public static bool RunAsync(string accessToken, string itemName)
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        // The async work runs on the THREAD POOL, never on this thread: SetupWithoutStarting installs
        // Avalonia's synchronization context here, but nothing pumps the dispatcher in a hook — the first
        // await that resumed onto it would deadlock (the synchronous hooks like --icon-test never hit this
        // because they call RunJobs by hand). Task.Run gives the awaits a context-free thread; the bitmap
        // decodes only need the PLATFORM to exist, not the UI thread.
        var (name, thumbnails, failure) = Task.Run(async () =>
        {
            var api = new SimplArchiveApiClient(accessToken);
            var item = (await api.Inbox.ListAsync()).Items.SingleOrDefault(i => i.Name == itemName);
            if (item is null)
            {
                return (itemName, (IReadOnlyList<Bitmap>)[], $"no inbox item named '{itemName}'");
            }

            return (item.Name, await InboxPageThumbnails.LoadAsync(api, item), (string?)null);
        }).GetAwaiter().GetResult();

        if (failure is not null)
        {
            Console.WriteLine($"SORT-THUMBS FAILED: {failure}");
            return false;
        }

        // The dialog is a Control, and a Control's constructor VerifyAccesses — so it is built HERE, on the
        // thread that owns the (unpumped) dispatcher, not inside the Task.Run above.
        var dialog = new SortPagesDialog(name, thumbnails.Cast<Bitmap?>().ToList());

        // The rotation state machine (#522), through the same members the tile buttons call: two turns right
        // and one left must come out at a single quarter turn, and an un-turned page must not be reported.
        var rotations = "skipped";
        if (dialog.Pages.FirstOrDefault() is { } first)
        {
            first.RotateRight();
            first.RotateRight();
            first.RotateLeft();
            rotations = dialog.CurrentRotations.TryGetValue(first.OriginalNumber, out var degrees) && degrees == 90
                && dialog.CurrentRotations.Count == 1 ? "ok" : $"WRONG:{string.Join(',', dialog.CurrentRotations)}";
        }

        Console.WriteLine($"SORT-THUMBS loaded={thumbnails.Count} dialog={dialog.CurrentOrder.Count} rotations={rotations}");
        return thumbnails.Count > 0 && dialog.CurrentOrder.Count == thumbnails.Count && rotations == "ok";
    }
}
