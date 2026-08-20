using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// The card a .vcf/.ics is previewed as. Tested at the HTML rather than through Gotenberg: what can go wrong
// here is the document we hand over — an unescaped name, a photo that never made it in, a span rendered a day
// long — and none of that is a question about the converter.
public class StructuredItemRendererTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static readonly StructuredItemRenderer Renderer =
        new(new ContactCardComposer(), new AppointmentComposer());

    private static string Vcf(params string[] lines) =>
        string.Join("\r\n", ["BEGIN:VCARD", "VERSION:3.0", "UID:u-1", .. lines, "END:VCARD"]);

    private static string Ics(params string[] lines) =>
        string.Join("\r\n",
            ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//test//EN", "BEGIN:VEVENT", "UID:e-1", .. lines, "END:VEVENT", "END:VCALENDAR"]);

    [Fact]
    public void A_contact_card_renders_its_fields()
    {
        var html = Renderer.ToHtml(
            Vcf("FN:Ada Lovelace", "ORG:Northwind Trading", "EMAIL;TYPE=work:ada@northwind.example", "TEL;TYPE=cell:+41 44 555 01 22"),
            ".vcf");

        Assert.NotNull(html);
        Assert.Contains("Ada Lovelace", html!, StringComparison.Ordinal);
        Assert.Contains("Northwind Trading", html, StringComparison.Ordinal);
        Assert.Contains("ada@northwind.example", html, StringComparison.Ordinal);

        // The vCard TYPE rides in the label, so two phone numbers are tellable apart. The composer normalises
        // it — TYPE=cell is read as "mobile" — and the label shows what was READ, not what the file spelled.
        Assert.Contains("Phone (mobile)", html, StringComparison.Ordinal);

        // No picture: the initials stand in, the same two letters the tabs draw.
        Assert.Contains(">AL<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_looks_like_markup_is_escaped()
    {
        // Every value here comes out of a file somebody else wrote — imported, synced from a phone, dropped in
        // by a colleague — and the converter RUNS the document we hand it. This is the boundary that matters.
        var html = Renderer.ToHtml(Vcf("FN:<script>alert(1)</script>", "ORG:<img src=x onerror=alert(2)>"), ".vcf");

        Assert.NotNull(html);

        // The text "onerror=" survives — as TEXT, inside a table cell, which is harmless and is what escaping
        // MEANS. What must not survive is the markup around it: no tag the card asked for may reach the
        // converter as a tag.
        Assert.DoesNotContain("<script>", html!, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_photo_is_inlined_rather_than_linked()
    {
        // The converter renders in its own sandbox with no credentials and no route back to us, so a linked
        // image would simply not load and the card would show a broken box where a face should be.
        var html = Renderer.ToHtml(
            Vcf("FN:Ada Lovelace", $"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(Jpeg)}"), ".vcf");

        Assert.NotNull(html);
        Assert.Contains("src='data:image/jpeg;base64,", html!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_appointment_renders_its_when_and_where()
    {
        var html = Renderer.ToHtml(
            Ics("SUMMARY:The Iron Horse", "DTSTART:20260829T190000", "DTEND:20260829T210000", "LOCATION:Northampton, MA"),
            ".ics");

        Assert.NotNull(html);
        Assert.Contains("The Iron Horse", html!, StringComparison.Ordinal);
        Assert.Contains("Northampton", html, StringComparison.Ordinal);
        Assert.Contains("19:00", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_all_day_span_stops_on_its_last_day_not_on_its_exclusive_end()
    {
        // DTEND is exclusive, so a two-day festival starting on the 24th carries DTEND of the 26th. Printing
        // the raw end would claim a third day — the same off-by-one the month grid had to get right.
        var html = Renderer.ToHtml(Ics("SUMMARY:Festival week", "DTSTART;VALUE=DATE:20260824", "DTEND;VALUE=DATE:20260827"), ".ics");

        Assert.NotNull(html);
        Assert.Contains("all day", html!, StringComparison.Ordinal);
        Assert.Contains("26", html, StringComparison.Ordinal);
        Assert.DoesNotContain("27", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeating_appointment_shows_its_rule_verbatim()
    {
        var html = Renderer.ToHtml(
            Ics("SUMMARY:Weekly rehearsal", "DTSTART:20260901T190000", "RRULE:FREQ=WEEKLY;BYDAY=TU"), ".ics");

        Assert.NotNull(html);
        Assert.Contains("FREQ=WEEKLY", html!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_item_has_no_card_at_all()
    {
        // "No preview available" is the honest answer for a malformed file, and is what every other converter's
        // failure already produces — better than a card of empty rows, which asserts the fields are blank.
        Assert.Null(Renderer.ToHtml("this is not a vCard", ".vcf"));
        Assert.Null(Renderer.ToHtml("BEGIN:VCARD\r\nVERSION:3.0\r\nEND:VCARD", ".vcf"));
        Assert.Null(Renderer.ToHtml("nonsense", ".ics"));
    }
}
