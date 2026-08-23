using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// What an EmailAddress field accepts (#703). The shape is deliberately pragmatic rather than RFC 5322, so
// these cases ARE the specification — the regex is chosen to sit exactly between them, and moving it in
// either direction should break a row here rather than pass silently.
public class EmailAddressValueTests
{
    [Theory]
    [InlineData("events@demo.simplarchive.dev")]
    [InlineData("a.b+tag@example.co.uk")]
    // Internationalised, and real: the RFC-shaped patterns in circulation reject both, which is the reason
    // this one is not RFC-shaped.
    [InlineData("veranstaltungen@exämple.de")]
    [InlineData("田中@例え.jp")]
    // A quoted local part is legal, and refusing it would be refusing a deliverable address.
    [InlineData("\"odd\"local@example.com")]
    public void A_real_address_is_accepted(string value) => Assert.True(EmailAddressValue.IsWellFormed(value));

    [Theory]
    // The mistakes people actually make.
    [InlineData("events")]
    [InlineData("events@")]
    [InlineData("@demo.simplarchive.dev")]
    [InlineData("a@@b.com")]
    // A bare hostname: routable on a LAN, silently undeliverable anywhere else — which is the failure mode
    // worth refusing at the point of entry rather than discovering as mail that never arrives.
    [InlineData("events@localhost")]
    // Two addresses pasted into one value. This one matters MORE than it looks: a list is one address per
    // element, so a value containing whitespace is a list entry that was never split.
    [InlineData("a@b.com c@d.com")]
    [InlineData("a@b.com\tc@d.com")]
    // …and the acknowledged cost of that rule: a quoted local part CONTAINING a space is legal RFC 5322 and
    // is refused here. Deliberate. The whitespace rule catches a mistake people make constantly; this shape
    // is one almost nobody has, and no pattern can tell the two apart.
    [InlineData("\"odd local\"@example.com")]
    [InlineData(" events@demo.dev")]
    [InlineData("events@demo.dev ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_malformed_value_is_refused(string? value) => Assert.False(EmailAddressValue.IsWellFormed(value));
}
