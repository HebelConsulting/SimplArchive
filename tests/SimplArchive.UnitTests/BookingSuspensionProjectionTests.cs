using System.Reflection;

namespace SimplArchive.UnitTests;

// The STATUS a suspended booking is SERVED with over CalDAV (ADR 0778, owner decision): the stored document
// is untouched, and the projection happens on the way out, so a pilot's phone shows a grounded aircraft's
// flight as tentative rather than as a perfectly normal confirmed booking.
//
// Tested at the text transform rather than through the DAV stack because that is where the risk is. The
// decision to edit the text instead of round-tripping through Ical.Net was made precisely so the served
// bytes differ from the stored ones in exactly one respect — and "exactly one" is a claim only a test can
// keep true.
public class BookingSuspensionProjectionTests
{
    // Internal to the Api, reached by reflection rather than by widening its visibility: the projection is an
    // implementation detail of one door (DavControllerContext.ReadItemAsync), and InternalsVisibleTo for a
    // single static method would invite it being called from elsewhere.
    private static string Project(string ics) =>
        (string)typeof(SimplArchive.Api.Controllers.AuditActions).Assembly
            .GetType("SimplArchive.Api.Documents.BookingSuspension")!
            .GetMethod("ProjectSuspended", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [ics])!;

    private const string Confirmed =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\n"
        + "UID:abc\r\nDTSTART:20260910T090000Z\r\nDTEND:20260910T100000Z\r\nSUMMARY:Training flight\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void A_suspended_booking_is_served_as_tentative()
    {
        Assert.Contains("STATUS:TENTATIVE", Project(Confirmed), StringComparison.Ordinal);
    }

    // Everything else must survive: the projection says one thing about the booking and must not quietly
    // become a rewrite of it. A client that re-uploads what it was served would otherwise post back a
    // different event than the one stored.
    [Theory]
    [InlineData("UID:abc")]
    [InlineData("DTSTART:20260910T090000Z")]
    [InlineData("DTEND:20260910T100000Z")]
    [InlineData("SUMMARY:Training flight")]
    [InlineData("PRODID:-//test//EN")]
    public void Everything_else_survives_the_projection(string line)
    {
        Assert.Contains(line, Project(Confirmed), StringComparison.Ordinal);
    }

    // An .ics that already carries a status must not end up with two — a duplicate STATUS is a parse error
    // for a strict client, and "which one wins" is not a question worth having.
    [Fact]
    public void An_existing_status_is_replaced_rather_than_doubled()
    {
        var withStatus = Confirmed.Replace("SUMMARY:Training flight", "STATUS:CONFIRMED\r\nSUMMARY:Training flight", StringComparison.Ordinal);

        var projected = Project(withStatus);

        Assert.Equal(1, projected.Split("STATUS:").Length - 1);
        Assert.Contains("STATUS:TENTATIVE", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("STATUS:CONFIRMED", projected, StringComparison.Ordinal);
    }

    // RFC 5545 says CRLF. Re-joining a file with bare LF because the transform was written against '\n' is a
    // compatibility risk taken for nothing, so the line ending the file arrived with is the one it leaves on.
    [Fact]
    public void The_line_endings_are_preserved()
    {
        Assert.Contains("\r\n", Project(Confirmed), StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", Project(Confirmed.Replace("\r\n", "\n", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    // A VTODO or VJOURNAL in the same file is not a booking, and a STATUS injected into one would be a claim
    // about something this rule says nothing about.
    [Fact]
    public void Only_events_are_touched()
    {
        var withTodo = Confirmed.Replace(
            "END:VCALENDAR",
            "BEGIN:VTODO\r\nUID:t1\r\nSTATUS:NEEDS-ACTION\r\nEND:VTODO\r\nEND:VCALENDAR",
            StringComparison.Ordinal);

        var projected = Project(withTodo);

        Assert.Contains("STATUS:NEEDS-ACTION", projected, StringComparison.Ordinal);
    }
}
