using System.Text.RegularExpressions;

namespace SimplArchive.Domain.Masks;

/// <summary>
/// What counts as a well-formed value for a <see cref="FieldDataType.EmailAddress"/> field (#703).
/// </summary>
/// <remarks>
/// <para>
/// One answer, in one place, because more than one thing asks the question: the ADR 0162 validation seam
/// rejects a badly-shaped value on write, and a later slice matches a delivery recipient against the
/// addresses a mailbox claims. Two regexes that disagree would mean an address that stores but never
/// receives — a fault visible only as mail that silently fails to arrive.
/// </para>
/// <para>
/// <b>The shape is deliberately pragmatic, not RFC 5322.</b> Exactly one <c>@</c>, no whitespace on either
/// side, and at least one dot in the domain part. That catches what people actually get wrong — a missing
/// <c>@</c>, two addresses pasted into one value, a bare hostname that is unroutable off a LAN — while
/// still accepting what is legal and real: <c>a.b+tag@example.co.uk</c>, and internationalised domains such
/// as <c>veranstaltungen@exämple.de</c>, both of which the RFC-shaped patterns in circulation reject.
/// Validating an address by pattern can never prove it is deliverable, so a stricter regex buys refusals of
/// legal addresses rather than certainty.
/// </para>
/// <para>
/// <b>The one acknowledged cost:</b> a quoted local part containing a space (<c>"odd local"@example.com</c>)
/// is legal RFC 5322 and is refused here, because no pattern can distinguish it from two addresses pasted
/// into one value — and on a list field, where each element IS one address, an unsplit pair is the mistake
/// that actually happens. The rare shape loses to the common one on purpose.
/// </para>
/// </remarks>
public static partial class EmailAddressValue
{
    /// <summary>Whether <paramref name="value"/> is shaped like an e-mail address.</summary>
    public static bool IsWellFormed(string? value) => value is not null && ShapeRegex().IsMatch(value);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex ShapeRegex();
}
