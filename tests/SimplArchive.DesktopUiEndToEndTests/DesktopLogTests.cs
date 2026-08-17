using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopUiEndToEndTests;

// ADR 0613 / issue #499. The client had no logging at all, and the thing that made it urgent is that the
// fallback it *did* have — Console.Error — goes nowhere in a packaged build: no terminal is attached to an
// .app bundle, a .zip or a tarball. So the property worth testing is not "a logger exists" but "a FILE exists
// on disk afterwards, where the Help menu says it is".
public class DesktopLogTests
{
    [Fact]
    public void Initialize_writes_a_file_in_the_folder_the_menu_opens()
    {
        DesktopLog.Initialize();
        DesktopLog.Info("A line written by {Test}", nameof(Initialize_writes_a_file_in_the_folder_the_menu_opens));
        DesktopLog.Shutdown();

        Assert.True(Directory.Exists(DesktopLog.Directory), $"the log folder should exist at {DesktopLog.Directory}");

        var newest = DesktopLog.NewestFile();
        Assert.NotNull(newest);
        Assert.StartsWith(DesktopLog.Directory, newest, StringComparison.Ordinal);

        // Read with sharing: the sink holds the file open, which is exactly how a user would read it too.
        using var stream = new FileStream(newest!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        Assert.Contains(nameof(Initialize_writes_a_file_in_the_folder_the_menu_opens), text, StringComparison.Ordinal);
        // Human-readable, not JSON (ADR 0613): the first reader is a support conversation.
        Assert.DoesNotContain("\"@mt\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Logging_before_initialize_does_not_throw()
    {
        // Something logs during startup before Initialize runs, or after Shutdown on the crash path. Neither may
        // take the client down: a logger that throws is worse than no logger, which is what this replaced.
        DesktopLog.Shutdown();

        var boom = Record.Exception(() =>
        {
            DesktopLog.Debug("before init {Value}", 1);
            DesktopLog.Warn("before init");
            DesktopLog.Error(new InvalidOperationException("test"), "before init");
        });

        Assert.Null(boom);
    }
}
