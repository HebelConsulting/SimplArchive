namespace SimplArchive.Presentation;

/// <summary>
/// Which part of a half-typed NAME the type-ahead is completing (ABI 0.23).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in either client because both must answer it IDENTICALLY: a NOTAM briefing folder named
/// <c>"LSZH LSAS EDGG"</c> briefs a route, and if the web client completed the trailing word while the
/// desktop replaced the whole box, the same keystrokes would produce two different folder names — and the
/// one that replaced would silently drop the rest of the route.
/// </para>
/// <para>
/// It is arithmetic over a string, not formatting, which is the line this project draws for what lives here:
/// where the caret's value begins is the same question on both surfaces; how the dropdown row is drawn is
/// not.
/// </para>
/// </remarks>
public static class NameVocabularyInput
{
    /// <summary>
    /// Splits what has been typed into the values already chosen and the one being typed now.
    /// </summary>
    /// <param name="typed">The whole content of the name box, possibly null or empty.</param>
    /// <param name="multiple">
    /// Whether this mask's name may hold several values (<c>ModuleMaskSeed.NameVocabularyIsMultiple</c>).
    /// When false the whole box is one value and the prefix is always empty — METAR/TAF and Aerodrome accept
    /// exactly one code, and a box that quietly invited a second would offer what their handlers ignore.
    /// </param>
    /// <returns>
    /// The text to keep verbatim, and the fragment to query the vocabulary with.
    /// </returns>
    /// <remarks>
    /// Split on the LAST space rather than tokenising: the only thing that matters is where the caret's word
    /// begins. A trailing space therefore yields an empty fragment, which opens the vocabulary at its first
    /// page — and that is the affordance telling a user another value may follow. Returning nothing there
    /// would read as "this name is finished".
    /// </remarks>
    public static (string Prefix, string Fragment) SplitTrailingValue(string? typed, bool multiple)
    {
        var text = typed ?? string.Empty;
        if (!multiple)
        {
            return (string.Empty, text);
        }

        var lastSpace = text.LastIndexOf(' ');
        return lastSpace < 0 ? (string.Empty, text) : (text[..(lastSpace + 1)], text[(lastSpace + 1)..]);
    }

    /// <summary>
    /// The complete name that choosing <paramref name="value"/> should produce.
    /// </summary>
    /// <remarks>
    /// Both clients' completion controls REPLACE their content with the chosen item, so each suggestion must
    /// carry the whole resulting string rather than the bare value. The trailing space is deliberate: it puts
    /// the caret where the next code goes, which is the whole point of a name that holds a route.
    /// </remarks>
    public static string Compose(string prefix, string value, bool multiple) =>
        multiple ? $"{prefix}{value} " : value;
}
