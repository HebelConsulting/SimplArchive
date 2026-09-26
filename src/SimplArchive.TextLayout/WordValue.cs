namespace SimplArchive.TextLayout;

/// <summary>Which characters are part of a word's VALUE, and which belong to the sentence around it (#788).</summary>
/// <remarks>
/// <para>
/// The overlay exists so a value can be lifted out of a scanned document and pasted where a value is expected —
/// a search box, an index field, a mail. Punctuation is an artefact of the sentence the word happened to sit
/// in, so <c>Rechnungsnummer:</c> and <c>4711,</c> made every paste a paste-then-delete, undoing the whole
/// saving. It bites hardest on exactly the tokens people copy: invoice numbers, dates and reference codes are
/// the ones that terminate a line or precede a colon.
/// </para>
/// <para>
/// Applied where the word is PRODUCED, rather than at each clipboard. Both clients draw from these boxes
/// and both would otherwise need their own copy of the rule — one of them in JavaScript, where a shared C#
/// helper cannot reach without an interop round trip on every click. Trimming at the source also keeps the
/// overlay's tooltip and the copied value in agreement for free, and makes FIND match the value rather than
/// the punctuation that followed it.
/// </para>
/// <para>
/// Trimmed at BOTH ends and uniformly: a leading <c>(</c> or <c>„</c> has the same problem, and a rule that
/// depended on the token's shape — keeping a trailing comma after digits for German decimals, say — would make
/// two visually similar words copy differently with nothing on screen to explain why. A number truncated at a
/// comma is a broken token either way. Predictable beats clever.
/// </para>
/// </remarks>
public static class WordValue
{
    // The characters that end or open a phrase.
    //
    // NOT the hyphen-minus, '/' or '_', which appear INSIDE reference codes and dates — trimming only at the
    // ends is what keeps `AB-1234/X` and `2026-08-27` whole. The EN and EM dashes are here though: they are
    // punctuation a typesetter added, never part of a code, so a lone one is a box that would copy nothing
    // useful. And not '%' or a currency sign, which are part of what was written.
    private static readonly char[] Punctuation = ['.', ',', ':', ';', '!', '?', '…', '(', ')', '[', ']', '{', '}',
        '"', '\'', '„', '“', '”', '«', '»', '‚', '‘', '’', '–', '—'];

    /// <summary>The word with surrounding punctuation removed; empty when nothing else remains.</summary>
    public static string Trim(string raw) => raw.Trim().Trim(Punctuation);
}
