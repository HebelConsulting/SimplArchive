using System.Diagnostics;

namespace SimplArchive.UiEndToEndTests;

// The "Edit profile" window renders (#464), driven through the desktop client's own headless screenshot hook.
//
// Why a rendering check at all: this is the one surface where the photo crop is hosted INLINE rather than as its
// own dialog, so the window composes a UserControl that was extracted from a Window in the same change. A
// mistake there — an unresolved control, a missing resource, a broken binding — throws at CONSTRUCTION, which no
// api-client or view-model test in this suite would see, and which nothing else exercises because a Window
// cannot appear in a full-app screenshot.
//
// It shells out rather than building Avalonia headlessly in-process: this suite is deliberately display-free and
// VM-level, and the desktop client already owns that hook (CLAUDE.md lists it among the headless verification
// flags). Reusing it keeps one implementation of "render this window without a display".
public class DesktopEditProfileTests
{
    [Fact]
    public async Task The_edit_profile_window_renders_headlessly()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"edit-profile-{Guid.NewGuid():N}.png");

        try
        {
            var (exitCode, output) = await DesktopProc.RunAsync("--profile-screenshot", outPath);

            Assert.True(exitCode == 0, $"The desktop client exited {exitCode}:\n{output}");
            Assert.True(File.Exists(outPath), $"No screenshot was produced at {outPath}.\n{output}");

            // A blank or near-blank frame means the window built but laid out nothing — which is what a broken
            // inline host looks like, and it would pass a mere "file exists" check.
            Assert.True(new FileInfo(outPath).Length > 4096,
                $"The screenshot is {new FileInfo(outPath).Length} bytes — the window rendered essentially nothing.");
        }
        finally
        {
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
        }
    }

}
