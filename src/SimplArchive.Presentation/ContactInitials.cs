using System.Globalization;

namespace SimplArchive.Presentation;

/// <summary>
/// The letters a contact is drawn with when it has no picture of its own.
/// </summary>
/// <remarks>
/// <para>
/// Computed in the client rather than fetched: an initials avatar costs nothing to draw and a generated one
/// would cost a request per row, which is the per-row cost ADR 0557 exists to prevent. Written once, here,
/// because both clients answer it and two copies would disagree on the awkward cases — which is every case
/// that is not "Ada Lovelace".
/// </para>
/// <para>
/// <b>The background is deliberately NOT derived from the name.</b> A hue hashed per contact makes rows more
/// scannable in the abstract, but every row in these tabs already carries a colour swatch meaning "which
/// collection", and colour that encodes identity cannot also be decoration (ADR 0581) — a reader would try to
/// decode the avatar's hue and find it means nothing. One neutral background, and colour keeps saying exactly
/// one thing.
/// </para>
/// </remarks>
public static class ContactInitials
{
    /// <summary>What a contact with no usable name at all is drawn with.</summary>
    /// <remarks>
    /// A question mark rather than an empty circle: a blank avatar reads as a rendering failure, and a card with
    /// no name is a real thing that arrives by sync — an organisation-only card, or a malformed import.
    /// </remarks>
    public const string Unknown = "?";

    /// <summary>
    /// One or two letters for <paramref name="name"/> — first and last word, or the first two letters of a
    /// single word.
    /// </summary>
    /// <remarks>
    /// Upper-cased with the INVARIANT culture on purpose. Turkish lower-case i upper-cases to a dotted İ under
    /// a Turkish culture, so a name would render differently for two users looking at the same contact, and the
    /// avatar is an identifier rather than prose.
    /// </remarks>
    public static string From(string? name)
    {
        var words = (name ?? string.Empty).Split(
            [' ', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return words.Length switch
        {
            0 => Unknown,
            1 => Letters(words[0]),
            // First and LAST, not first and second: "Jean-Paul van der Berg" is identified by its surname, and
            // the words in between are the part a reader skips.
            _ => Take(words[0], 1) + Take(words[^1], 1) is { Length: > 0 } pair ? pair : Unknown,
        };
    }

    private static string Letters(string word) => Take(word, 2) is { Length: > 0 } letters ? letters : Unknown;

    /// <summary>
    /// The first <paramref name="count"/> characters that are actually letters or digits, upper-cased.
    /// </summary>
    /// <remarks>
    /// Skipping punctuation matters more than it looks: a card named "(none)" or "@work" would otherwise be
    /// drawn with a bracket, which reads as a rendering fault rather than as a name. Enumerated by TEXT ELEMENT
    /// rather than by char, so an emoji or a combining accent is one letter and never a split surrogate pair —
    /// half a code point is a replacement glyph on screen.
    /// </remarks>
    private static string Take(string word, int count)
    {
        var taken = string.Empty;
        var elements = StringInfo.GetTextElementEnumerator(word);

        while (elements.MoveNext() && taken.Length < count)
        {
            var element = (string)elements.Current;
            if (element.Length > 0 && char.IsLetterOrDigit(element[0]))
            {
                taken += element;
            }
        }

        return taken.ToUpperInvariant();
    }
}
