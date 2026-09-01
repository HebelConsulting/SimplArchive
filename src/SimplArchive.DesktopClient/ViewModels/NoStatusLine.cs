namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// A status reporter for a surface with no status line: the headless <c>--render-pdf</c> harness, which builds
/// a preview on its own to exercise the PDFium pipeline and has no window to report into.
/// </summary>
/// <remarks>
/// Deliberately a named type rather than a lambda or a nullable reporter. <see cref="IStatusReporter"/> is
/// constructor-injected precisely so that "nobody is listening" has to be STATED; a nullable field would let it
/// happen by omission, which is the failure this seam exists to remove (ADR 0729).
/// </remarks>
internal sealed class NoStatusLine : IStatusReporter
{
    public void Report(string status)
    {
    }
}
