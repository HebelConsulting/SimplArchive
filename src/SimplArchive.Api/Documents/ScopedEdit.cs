namespace SimplArchive.Api.Documents;

/// <summary>
/// Which occurrences of a repeating entry an edit changes (#1133).
/// </summary>
/// <remarks>
/// Editing a series without this choice applies every change to the WHOLE series, including occurrences
/// already past — which is why both editors showed recurrence read-only until the choice existed.
/// </remarks>
public static class ScopedEdit
{
    /// <summary>The three answers, and what each does to the stored series.</summary>
    public enum Scope
    {
        /// <summary>Rewrite the series. What a PUT of a repeating entry has always meant, and the default.</summary>
        All,

        /// <summary>
        /// Cancel this one occurrence and file the edited values as an entry of their own.
        /// </summary>
        /// <remarks>
        /// An <c>EXDATE</c> on the series plus a new single entry, which is what iCalendar already says this
        /// is — any CalDAV client writes the same thing, so a series edited here reads correctly everywhere.
        /// </remarks>
        ThisOccurrence,

        /// <summary>
        /// End the series just before this occurrence and start a new one carrying the edited values.
        /// </summary>
        /// <remarks>
        /// A split, not a rewrite: the occurrences already held keep the values they were held with, which is
        /// the whole reason this choice exists.
        /// </remarks>
        ThisAndFollowing,
    }

    /// <summary>The scope a request asked for, or null when it named none.</summary>
    /// <remarks>
    /// An unrecognised value is null rather than an error, and a null scope means <see cref="Scope.All"/> —
    /// the behaviour every client had before this existed, so an older one keeps working unchanged.
    /// </remarks>
    public static Scope? Parse(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "this" or "this-occurrence" => Scope.ThisOccurrence,
        "following" or "this-and-following" => Scope.ThisAndFollowing,
        "all" => Scope.All,
        _ => null,
    };
}
