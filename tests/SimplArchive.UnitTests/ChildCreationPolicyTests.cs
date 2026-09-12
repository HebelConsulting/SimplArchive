using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// Every calendar-shaped collection admits its own items — asked of the KIND LIST, not of a hand-written set.
//
// AdmitsCalendarEntries gates the `appointments` rel on all three surfaces that offer a create: the document
// resource, the children listing the tree's New menu reads, and GET /api/dav-collections, which the Calendar
// tab reads. It existed precisely so no single site could forget a mask — and then it listed Appointment and
// Booking and was never updated when Maintenance (ADR 0778) and Availability (ADR 0780) arrived.
//
// So an Availability folder advertised no create ANYWHERE: no New in the tree, and the Calendar tab's New
// stayed disabled however the collection was ticked. A Schedule worked the whole time, because its item mask
// is Booking — one of the two named — which is what made it read as a ticking or rights problem rather than
// as a kind the predicate had never heard of. Reported from use while testing a release.
public class ChildCreationPolicyTests
{
    // The sibling surface, so a future "simplify" cannot collapse the .ics filter away: an Addressbook admits
    // typed items too, and its rel is `contacts`.
    [Fact]
    public void An_addressbook_does_not_serve_the_appointments_surface() =>
        Assert.False(ChildCreationPolicy.AdmitsCalendarEntries(WellKnownMaskIds.Addressbook));

    [Fact]
    public void An_unclassified_folder_admits_nothing() =>
        Assert.False(ChildCreationPolicy.AdmitsCalendarEntries(null));

    // The guard that matters: driven from DavCollectionKinds itself, so ADDING a kind extends this test rather
    // than leaving it passing against the old set. A hand-listed [InlineData] here would have kept passing
    // through exactly the regression this file exists for.
    [Theory]
    [MemberData(nameof(CalendarShapedFolderMasks))]
    public void Every_calendar_shaped_collection_admits_its_own_items(Guid folderMaskId) =>
        Assert.True(
            ChildCreationPolicy.AdmitsCalendarEntries(folderMaskId),
            $"folder mask {folderMaskId} is an .ics collection kind, so it must advertise the appointments "
            + "create surface — otherwise its New is absent in the tree and disabled on the Calendar tab");

    public static TheoryData<Guid> CalendarShapedFolderMasks()
    {
        var data = new TheoryData<Guid>();
        foreach (var kind in DavCollectionKinds.All.Where(k => k.Extension == ".ics"))
        {
            data.Add(kind.FolderMaskId);
        }

        return data;
    }

    // Anti-vacuous: the theory above proves nothing if the kind list is empty or has shrunk to the two masks
    // that were hardcoded. Four .ics kinds exist today — Calendar, Schedule, Maintenance, Availability.
    [Fact]
    public void The_kind_list_still_carries_every_calendar_shaped_collection() =>
        Assert.Equal(4, DavCollectionKinds.All.Count(k => k.Extension == ".ics"));
}
