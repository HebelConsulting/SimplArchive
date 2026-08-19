namespace SimplArchive.Client.Services;

/// <summary>
/// The "Advanced: the stored item" disclosure's state, shared by the contact and appointment forms (#648).
/// </summary>
/// <remarks>
/// <para>
/// Held by composition rather than inherited: the two forms have nothing else in common, and one small piece
/// of state both delegate to is easier to read than a base class that exists to carry it.
/// </para>
/// <para>
/// The web twin of the desktop's fields on <c>StructuredEditFormViewModel</c> — the pair is one surface
/// (ADR 0511), so what one client can show and change, the other must too.
/// </para>
/// </remarks>
public sealed class RawSourceState
{
    /// <summary>What the user sees and edits; empty until the disclosure is opened.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>What was loaded, so the dirty check compares against the SERVER's text rather than a guess.</summary>
    private string _original = string.Empty;

    /// <summary><c>vCard</c> or <c>iCalendar</c>.</summary>
    public string Format { get; private set; } = string.Empty;

    /// <summary>The token a raw save goes back under — its own read's, not the structured read's.</summary>
    public string ETag { get; private set; } = string.Empty;

    /// <summary>False before the disclosure has been opened: the text is fetched on demand, not up front.</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// True once the text has actually changed. This decides WHICH save happens — a raw save replaces the whole
    /// item, so it must not run merely because somebody opened the box to look at it.
    /// </summary>
    public bool IsDirty => Loaded && !string.Equals(Text, _original, StringComparison.Ordinal);

    /// <summary>Takes the loaded source as the baseline, so opening the box is not itself an edit.</summary>
    public void Set(string text, string format, string etag)
    {
        _original = text;
        Text = text;
        Format = format;
        ETag = etag;
        Loaded = true;
    }
}
