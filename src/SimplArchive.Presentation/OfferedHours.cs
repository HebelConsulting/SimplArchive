namespace SimplArchive.Presentation;

/// <summary>
/// Renders the hours a resource is on offer, for a refusal that named them as data (#1135).
/// </summary>
/// <remarks>
/// Shared so the two clients word it identically, and so neither is tempted to quote the server's English
/// message instead — which is what issue #424 forbids and `NoServerDetailInClientsTests` guards. The refusal
/// sends INSTANTS; the sentence is composed here, in the reader's own language and their own zone.
/// </remarks>
public static class OfferedHours
{
    /// <summary>"08:00–22:00", or several windows joined, in the LOCAL zone of whoever is reading.</summary>
    /// <remarks>
    /// Local, because the question is "when may I book this?" and the answer has to be in the clock the
    /// person is looking at. The instants are unambiguous; the rendering is theirs.
    /// </remarks>
    public static string Describe(IEnumerable<(DateTimeOffset StartsAt, DateTimeOffset EndsAt)> windows) =>
        string.Join(", ", windows
            .Select(window => $"{window.StartsAt.ToLocalTime():HH:mm}–{window.EndsAt.ToLocalTime():HH:mm}"));
}
