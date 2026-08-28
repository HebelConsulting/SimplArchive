using System.Text;
using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// The combined-export composer (#658), pinned pure. The iCalendar half is the one that can go quietly wrong:
// a naïve concatenation of VCALENDAR wrappers is a file some clients accept and others reject — invisible in
// testing, broken in the field. So the pins are structural: ONE wrapper, zones deduplicated, components and
// their UIDs byte-verbatim.
public class CombinedItemExportTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void Vcards_concatenate_verbatim()
    {
        var a = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:volmet-geneva-6f1c2e40\r\nFN:VOLMET Geneva\r\nEND:VCARD";
        var b = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:volmet-zurich-6f1c2e40\r\nFN:VOLMET Zurich\r\nEND:VCARD";
        var combined = S(CombinedItemExport.CombineVcf([B(a), B(b)]));

        Assert.Equal(2, combined.Split("BEGIN:VCARD").Length - 1);
        Assert.Contains("UID:volmet-geneva-6f1c2e40", combined);
        Assert.Contains("UID:volmet-zurich-6f1c2e40", combined);
    }

    [Fact]
    public void Calendars_merge_into_one_wrapper_with_zones_deduplicated_and_uids_untouched()
    {
        var zone = "BEGIN:VTIMEZONE\r\nTZID:Europe/Zurich\r\nBEGIN:STANDARD\r\nTZOFFSETFROM:+0200\r\nEND:STANDARD\r\nEND:VTIMEZONE";
        string Event(string uid) =>
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//x//y//EN\r\n{zone}\r\nBEGIN:VEVENT\r\nUID:{uid}\r\n"
            + "DTSTART;TZID=Europe/Zurich:20260901T200000\r\nSUMMARY:Concert\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR";

        var combined = S(CombinedItemExport.CombineIcs([B(Event("demo-concert-1")), B(Event("demo-concert-2"))]));

        // ONE wrapper — the property that decides whether every client accepts the file.
        Assert.Equal(1, combined.Split("BEGIN:VCALENDAR").Length - 1);
        Assert.Equal(1, combined.Split("END:VCALENDAR").Length - 1);
        Assert.StartsWith("BEGIN:VCALENDAR", combined);

        // The zone once, however many events reference it; both events verbatim, UIDs intact, VALARM nested.
        Assert.Equal(1, combined.Split("BEGIN:VTIMEZONE").Length - 1);
        Assert.Equal(2, combined.Split("BEGIN:VEVENT").Length - 1);
        Assert.Contains("UID:demo-concert-1", combined);
        Assert.Contains("UID:demo-concert-2", combined);
        Assert.Equal(2, combined.Split("BEGIN:VALARM").Length - 1);
        Assert.Contains("DTSTART;TZID=Europe/Zurich:20260901T200000", combined);
    }

    [Fact]
    public void Two_different_zones_both_survive()
    {
        string WithZone(string tzid, string uid) =>
            $"BEGIN:VCALENDAR\r\nBEGIN:VTIMEZONE\r\nTZID:{tzid}\r\nEND:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var combined = S(CombinedItemExport.CombineIcs([B(WithZone("Europe/Zurich", "a")), B(WithZone("America/New_York", "b"))]));
        Assert.Contains("TZID:Europe/Zurich", combined);
        Assert.Contains("TZID:America/New_York", combined);
        Assert.Equal(2, combined.Split("BEGIN:VTIMEZONE").Length - 1);
    }

    [Fact]
    public void Bare_lf_input_comes_out_crlf()
    {
        // Stored bytes may carry bare LF (a hand-authored fixture, a lenient client); RFC 5545/6350 want CRLF,
        // and a mixed-ending file is exactly the accepted-here-rejected-there shape this composer exists to avoid.
        var combined = S(CombinedItemExport.CombineVcf([B("BEGIN:VCARD\nUID:x\nEND:VCARD")]));
        Assert.Contains("BEGIN:VCARD\r\nUID:x\r\nEND:VCARD", combined);
        Assert.DoesNotContain("\n\n", combined.Replace("\r\n", "\n") + "sentinel-guard");
    }
}
