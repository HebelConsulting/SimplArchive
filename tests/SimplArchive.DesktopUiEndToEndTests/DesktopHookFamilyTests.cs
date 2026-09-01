namespace SimplArchive.UiEndToEndTests;

// Every argument-free headless verification hook still RUNS, and still reports a pass (issue #925).
//
// The desktop client carries ~28 `--*-test` hooks because a native GUI needs a display and these stand in for
// looking at it (CLAUDE.md, "Headless verification hooks"). Until this test, exactly two of them were driven by
// anything other than a human typing a flag — so a hook that had stopped working was indistinguishable from one
// nobody had tried lately.
//
// That is not hypothetical. `--pdf-opaque-test` is the regression guard for #196 (a PDF drawing no page
// background must render as opaque white paper, not a transparent page showing the dark surface behind it). It
// worked when written, then threw `InvalidCastException` on every invocation for over two weeks: it cast the
// result of `RenderPdfFirstPage` to `WriteableBitmap`, and #522 changed that method to construct an IMMUTABLE
// Bitmap on purpose — Skia's ResizeBitmap accepts only immutable sources, so the writable one made
// CreateScaledBitmap throw for every consumer that scales a page.
//
// So the failure this test exists to catch is NOT rot or neglect. It is a guard switched off by a change that
// was correct, made by someone with no reason to look at it, against a declared return type the cast was legal
// against — which is why the compiler said nothing and why only running the thing could have found it.
//
// Deliberately a WEAK assertion: exit code, no FAILED, no unhandled exception. What each hook checks is the
// hook's own business and is asserted inside it; this asks only the question none of them can ask about
// themselves — "did you run at all?". A stronger shared assertion would have to know what each hook prints,
// and the family does not agree on that (`--hitcopy-test` prints hit labels, `--diff-test` prints DIFF-TEST OK).
//
// Hooks that TAKE arguments are out of scope: they need a token, a path or a running Api, which is a different
// fixture. `--profile-screenshot` and `--sort-thumbs-test` are already covered by their own tests.
public class DesktopHookFamilyTests
{
    [Theory]
    [InlineData("--annotation-save-test")]
    [InlineData("--column-drag-test")]
    [InlineData("--columns-test")]
    [InlineData("--connlost-signout-test")]
    [InlineData("--datepicker-test")]
    [InlineData("--diff-test")]
    [InlineData("--hitcopy-test")]
    [InlineData("--icon-test")]
    [InlineData("--intray-collapse-test")]
    [InlineData("--intray-insert-test")]
    [InlineData("--list-scroll-test")]
    [InlineData("--pdf-opaque-test")]
    [InlineData("--reset-layout-test")]
    [InlineData("--searchclear-test")]
    [InlineData("--shortcut-test")]
    [InlineData("--zoom-test")]
    public async Task The_hook_runs_and_does_not_report_failure(string flag)
    {
        var (exitCode, output) = await DesktopProc.RunAsync(flag);

        // Named in the message because a bare "expected 0, got 134" gives the next reader nothing to act on.
        Assert.True(exitCode == 0, $"{flag} exited {exitCode}. Output:\n{output}");
        Assert.False(output.Contains("FAILED", StringComparison.Ordinal), $"{flag} reported FAILED. Output:\n{output}");
        Assert.False(
            output.Contains("Unhandled exception", StringComparison.Ordinal),
            $"{flag} threw. Output:\n{output}");
    }
}
