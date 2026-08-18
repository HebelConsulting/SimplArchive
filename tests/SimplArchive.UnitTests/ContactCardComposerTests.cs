using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// The contact editor rewrites a stored vCard, and the whole question is what happens to the parts of that card
// we do not model. A user edits a phone number here; their contact's photo, custom labels and anniversary must
// still be on their phone tomorrow.
//
// Ported from SimplCalCon (its ADR 0082) rather than re-derived, per ADR 0621 — including the reason it exists:
// rebuilding the card from a parsed model, which is the obvious implementation, silently drops all of that.
// These tests pin the behaviour that distinguishes the two, because the failure is invisible at the moment it
// happens and only surfaces on someone else's device days later.
public class ContactCardComposerTests
{
    private readonly ContactCardComposer _composer = new();

    // Shaped like a card a phone would write: properties we model, standard ones we do not, vendor extensions,
    // and a FOLDED line — folding is where a naive line-based merge corrupts a photo rather than dropping it.
    private const string PhoneAuthored =
        "BEGIN:VCARD\r\n"
        + "VERSION:3.0\r\n"
        + "UID:urn:uuid:11111111-2222-3333-4444-555555555555\r\n"
        + "FN:Anna Meyer\r\n"
        + "N:Meyer;Anna;;;\r\n"
        + "EMAIL;TYPE=WORK:anna@example.test\r\n"
        + "TEL;TYPE=CELL:+41790000000\r\n"
        + "ORG:Contoso\r\n"
        + "BDAY:1990-02-15\r\n"
        + "NOTE:Met at the trade fair.\r\n"
        + "ANNIVERSARY:2015-06-20\r\n"
        + "CATEGORIES:Suppliers,VIP\r\n"
        + "IMPP:xmpp:anna@example.test\r\n"
        + "X-ABLabel:_$!<Work>!$_\r\n"
        + "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQ\r\n"
        + " EAAAAAAD/2wBDAAYEBQYFBAYG\r\n"
        + "END:VCARD\r\n";

    [Fact]
    public void Editing_a_modelled_field_leaves_every_unmodelled_property_intact()
    {
        var card = _composer.Read(PhoneAuthored);
        var edited = card with { Emails = [new ContactField("anna.meyer@example.test", "work")] };

        var merged = _composer.Merge(PhoneAuthored, edited, "urn:uuid:11111111-2222-3333-4444-555555555555");

        Assert.Contains("anna.meyer@example.test", merged);

        // Each of these is something a user would notice missing, and none is in our five index fields.
        Assert.Contains("ANNIVERSARY:2015-06-20", merged);
        Assert.Contains("CATEGORIES:Suppliers,VIP", merged);
        Assert.Contains("IMPP:xmpp:anna@example.test", merged);
        Assert.Contains("X-ABLabel:_$!<Work>!$_", merged);
        Assert.Contains("PHOTO", merged);

        // The photo's payload must survive UNFOLDED as one logical line — a merge that re-emitted the raw
        // physical lines would leave a stray continuation and corrupt the image rather than lose it, which is
        // worse: the property is still there, so nothing looks wrong.
        Assert.Contains("/9j/4AAQSkZJRgABAQEAAAAAAD/2wBDAAYEBQYFBAYG", merged);
    }

    [Fact]
    public void The_uid_and_version_survive_so_a_later_sync_matches_rather_than_duplicates()
    {
        // The UID is the correlation key a DAV PUT matches on. Losing it forks the contact into a second
        // document on the next sync, silently, on every edited card.
        var merged = _composer.Merge(PhoneAuthored, _composer.Read(PhoneAuthored), "urn:uuid:11111111-2222-3333-4444-555555555555");

        Assert.Contains("UID:urn:uuid:11111111-2222-3333-4444-555555555555", merged);
        Assert.Contains("VERSION:3.0", merged);
        Assert.StartsWith("BEGIN:VCARD", merged);
        Assert.EndsWith("END:VCARD\r\n", merged);
    }

    [Fact]
    public void Reading_extracts_the_modelled_fields()
    {
        var card = _composer.Read(PhoneAuthored);

        Assert.Equal("Anna Meyer", card.FormattedName);
        Assert.Equal("Meyer", card.FamilyName);
        Assert.Equal("Anna", card.GivenName);
        Assert.Equal("Contoso", card.Organization);
        Assert.Equal("Met at the trade fair.", card.Note);

        // TYPE is normalised to the three the form offers: CELL becomes mobile.
        Assert.Equal("anna@example.test", card.Emails.Single().Value);
        Assert.Equal("work", card.Emails.Single().Type);
        Assert.Equal("mobile", card.Phones.Single().Type);
    }

    [Fact]
    public void A_card_composed_from_nothing_is_well_formed()
    {
        // New Contact: there is no existing blob to merge into.
        var merged = _composer.Merge(null, ContactCard.Empty with
        {
            GivenName = "Tom",
            FamilyName = "Fischer",
            Emails = [new ContactField("tom@example.test", "work")],
        }, "urn:uuid:abcdef00-0000-0000-0000-000000000001");

        Assert.StartsWith("BEGIN:VCARD", merged);
        Assert.Contains("UID:urn:uuid:abcdef00-0000-0000-0000-000000000001", merged);
        Assert.Contains("N:Fischer;Tom;;;", merged);
        Assert.Contains("EMAIL;TYPE=WORK:tom@example.test", merged);
        Assert.EndsWith("END:VCARD\r\n", merged);

        // FN is derived when the form did not supply one — a card without it displays as blank in most clients.
        Assert.Contains("FN:Tom Fischer", merged);
    }

    [Fact]
    public void A_value_carrying_vcard_delimiters_survives_a_round_trip()
    {
        // ';' and ',' are structural in vCard, so an unescaped one in a company name silently truncates the
        // property or invents extra components.
        var merged = _composer.Merge(null, ContactCard.Empty with
        {
            Organization = "Meyer; Fischer, and Partners",
            Note = "Line one\nline two",
        }, "uid-1");

        Assert.Contains("Meyer\\; Fischer\\, and Partners", merged);
        Assert.Contains("Line one\\nline two", merged);

        // …and reading it back gives the original text, not the escaped form.
        var reread = _composer.Read(merged);
        Assert.Equal("Meyer; Fischer, and Partners", reread.Organization);
        Assert.Equal("Line one\nline two", reread.Note);
    }

    [Fact]
    public void Clearing_a_modelled_field_removes_it_rather_than_writing_an_empty_property()
    {
        var card = _composer.Read(PhoneAuthored);
        var merged = _composer.Merge(PhoneAuthored, card with { Note = null, Organization = null }, "uid-1");

        Assert.DoesNotContain("NOTE:", merged);
        Assert.DoesNotContain("ORG:", merged);

        // …while the unmodelled ones are still untouched, which is the point.
        Assert.Contains("CATEGORIES:Suppliers,VIP", merged);
    }
}
