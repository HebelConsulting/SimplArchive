namespace SimplArchive.Presentation;

/// <summary>
/// Which sentence a compare surface shows when the server answers <c>available: false</c>.
/// </summary>
/// <remarks>
/// The server names the reason in <c>unavailableReason</c>, and the reasons need different sentences because
/// they send the reader to different remedies. One sentence once covered all of them: "only text-extractable
/// documents can be diffed" — which on an installation that runs no text-extraction service blamed the
/// document's FORMAT for what was the installation's choice, and on a check-out with no saved working copy
/// blamed a format that was perfectly comparable. Chosen here, once, so the two clients cannot drift.
/// </remarks>
public static class ComparisonUnavailableMessage
{
    /// <param name="reason">The server's <c>unavailableReason</c>; null or unknown falls back to the format sentence.</param>
    public static string KeyOf(string? reason) => reason switch
    {
        "no-text-extraction" => "CompareNeedsTextExtraction",
        "no-working-copy" => "CompareNoWorkingCopy",
        _ => "CompareNotAvailable",
    };
}
