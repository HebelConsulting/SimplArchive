using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// Which folder masks may not be re-typed once set.
//
// The set is DERIVED from containment, which keeps it from being a hand-maintained list that drifts. But the
// derivation is a proxy for the real principle — what re-typing COSTS — and the two are not the same question.
// So today's answer is pinned here: a future mask that becomes location-constrained will fail this test rather
// than silently joining (or leaving) the rule, and someone will have to decide which it is.
public class ImmutableStructuralMaskTests
{
    [Fact]
    public void The_derivation_still_yields_exactly_the_masks_that_were_decided()
    {
        Guid[] expected =
        [
            WellKnownMaskIds.Mailbox,
            WellKnownMaskIds.ImapSpecial,
            WellKnownMaskIds.Notebook,
            WellKnownMaskIds.NotebookSection,
            // ADR 0744: a room's Schedule is structural — re-type it and its bookings' claims are orphaned
            // (the .ics inside stop being bookings), which is the "destroys the meaning of what is inside"
            // half of the boundary, unlike the plain Calendar that stays deliberately re-typeable below.
            WellKnownMaskIds.Schedule,
        ];

        Assert.Equal(
            expected.Order(),
            WellKnownMaskIds.ImmutableStructuralMasks.Order());
    }

    [Fact]
    public void A_folder_a_user_may_re_type_is_not_in_it()
    {
        // The decided boundary, stated as a test because it is the half most likely to be "tidied" later:
        // re-typing a Calendar or an Addressbook costs only subscribability through CalDAV/CardDAV. What is
        // inside stays viable, so it is a preference a user may change their mind about.
        Assert.DoesNotContain(WellKnownMaskIds.Calendar, WellKnownMaskIds.ImmutableStructuralMasks);
        Assert.DoesNotContain(WellKnownMaskIds.Addressbook, WellKnownMaskIds.ImmutableStructuralMasks);

        // ...and the ordinary folders, which have no type to lose.
        Assert.DoesNotContain(WellKnownMaskIds.Folder, WellKnownMaskIds.ImmutableStructuralMasks);
        Assert.DoesNotContain(WellKnownMaskIds.MyDocuments, WellKnownMaskIds.ImmutableStructuralMasks);
    }

    [Fact]
    public void It_holds_only_folder_masks()
    {
        // Note, Contact and Appointment are location-constrained too — they live only inside their typed
        // folder — but they are ITEMS. Re-typing a note is a user's business; the rule is about the folders
        // whose type gives the content its meaning.
        Assert.All(
            WellKnownMaskIds.ImmutableStructuralMasks,
            id => Assert.Contains(id, WellKnownMaskIds.FolderMasks));
    }
}
