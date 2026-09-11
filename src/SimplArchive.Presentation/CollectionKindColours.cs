namespace SimplArchive.Presentation;

/// <summary>
/// The colour a DAV collection falls back to when neither the caller nor the collection chose one — by what
/// the collection MEANS (ADRs 0744/0778/0780).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in either client because it is the same question on both surfaces and must have the same
/// answer: a grounding drawn red in the desktop and blue in the web is two clients disagreeing about what a
/// colour means, which is precisely what <c>SimplArchive.Presentation</c> exists to prevent (ADR 0650).
/// </para>
/// <para>
/// <b>Only a fallback.</b> A collection with its own <c>Colour</c> field keeps it, and a caller's personal
/// override still wins over both — this answers what to draw when nobody has said, which before was "the
/// same grey as everything else". A resource with three calendars in one overlay then rendered as one
/// undifferentiated mass, which is the state that makes an overlay useless.
/// </para>
/// <para>
/// The three are chosen to be tellable apart rather than to be pretty, and to carry the right connotation:
/// a claim is ordinary, a grounding is a warning, offered time is an invitation.
/// </para>
/// </remarks>
public static class CollectionKindColours
{
    /// <summary>
    /// Time that is spoken for — the ordinary case, so the least shouty of the three.
    /// </summary>
    /// <remarks>
    /// Slate rather than a blue, which is what it was until the overlay was actually LOOKED at: blue is the
    /// commonest colour a personal calendar carries, so a Schedule drawn in it sat beside "My Calendar" in
    /// two shades of the same hue and the swatches stopped distinguishing anything. A fallback colour whose
    /// job is telling meanings apart must not collide with the colour everything else defaults to.
    /// </remarks>
    public const string Schedule = "#4A5568";

    /// <summary>Time the resource is unavailable. Warning-coloured on purpose: it is the one that stops work.</summary>
    public const string Maintenance = "#B4541E";

    /// <summary>Time that is OFFERED — an invitation rather than a commitment, so it reads as open.</summary>
    public const string Availability = "#2E8B70";

    /// <summary>A personal calendar or addressbook: no meaning to encode, so nothing is imposed.</summary>
    public const string? None = null;
}
