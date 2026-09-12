using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopUiEndToEndTests;

// Where a new entry will be filed is always visible, and never guessed when the candidates disagree (#1125).
//
// Reported from use: a window was published for a room, then an entry meant for that room's SCHEDULE was
// filed into its AVAILABILITY — so it became a second offer of free time rather than a booking, and the
// availability rule (#1124) never ran because nothing had been booked.
//
// Two causes, both here:
//
//   * The dialog pre-selected the FIRST candidate. Collections are ordered personal-first then
//     alphabetically, and a bookable resource contributes Availability / Maintenance / Schedule — so
//     "Availability" is first for every room, and the most likely intent, booking, sorts last. The old
//     reasoning ("the first is the caller's personal collection") is sound for a set of calendars and wrong
//     the moment the candidates differ in MEANING.
//   * With one candidate the destination was shown NOWHERE, so "New appointment" never said where it landed.
public class CreateTargetSelectionTests
{
    private static CreateTarget Target(string name, string kind) =>
        new(name, $"/api/documents/{Guid.NewGuid()}/appointments", Guid.NewGuid(), kind);

    // The reported case. Guessing here is not a convenience: it files the entry into a collection that means
    // something else entirely, and nothing downstream can tell that was not intended.
    [Fact]
    public void Candidates_of_different_kinds_are_not_guessed_between()
    {
        var form = new AppointmentEditViewModel();
        form.OpenForCreate([
            Target("Room / Availability", "availability"),
            Target("Room / Maintenance", "maintenance"),
            Target("Room / Schedule", "schedule"),
        ]);

        Assert.Null(form.SelectedTarget);
        Assert.False(form.HasTarget);
    }

    // ...and the ordinary case keeps its convenience: several calendars all mean the same thing, so the first
    // is a default rather than a guess.
    [Fact]
    public void Candidates_of_one_kind_still_default_to_the_first()
    {
        var form = new AppointmentEditViewModel();
        form.OpenForCreate([
            Target("Personal / My Calendar", "calendar"),
            Target("Team / Releases", "calendar"),
        ]);

        Assert.Equal("Personal / My Calendar", form.SelectedTarget?.DisplayName);
        Assert.True(form.HasTarget);
    }

    // Choosing resolves it — the dialog asks, and Save waits until it is answered.
    [Fact]
    public void Choosing_a_destination_releases_the_commit()
    {
        var form = new AppointmentEditViewModel { CanEdit = true };
        form.OpenForCreate([Target("Room / Availability", "availability"), Target("Room / Schedule", "schedule")]);
        Assert.False(form.CanCommit);

        form.SelectedTarget = form.Targets.Single(t => t.DisplayName.EndsWith("Schedule", StringComparison.Ordinal));

        Assert.True(form.CanCommit);
    }

    // BOTH gates. Binding Save to the destination alone would drop the read-only gate and let an entry the
    // caller cannot edit be saved — which is what the first version of this change did.
    [Fact]
    public void A_read_only_entry_cannot_be_committed_even_with_a_destination()
    {
        var form = new AppointmentEditViewModel { CanEdit = false };
        form.OpenForCreate([Target("Personal / My Calendar", "calendar")]);

        Assert.True(form.HasTarget);
        Assert.False(form.CanCommit);
    }

    // One candidate: nothing to choose, but the destination must still be SHOWN — as text, so nothing implies
    // a choice that does not exist.
    [Fact]
    public void A_single_destination_is_stated_rather_than_offered()
    {
        var form = new AppointmentEditViewModel();
        form.OpenForCreate([Target("Room / Availability", "availability")]);

        Assert.False(form.ShowTargetPicker);
        Assert.True(form.ShowTargetName);
        Assert.Equal("Room / Availability", form.TargetName);
    }

    // On EDIT the entry's own collection is the answer, so there is nothing to guess even across kinds — and
    // the commit is never gated, because an edit that moves nothing is still a valid edit.
    [Fact]
    public void An_edit_opens_on_the_collection_the_entry_is_in()
    {
        var availability = Target("Room / Availability", "availability");
        var schedule = Target("Room / Schedule", "schedule");
        var form = new AppointmentEditViewModel { CanEdit = true };

        form.OpenForMove([availability, schedule], schedule.CollectionId);

        Assert.Equal(schedule.CollectionId, form.SelectedTarget?.CollectionId);
        Assert.False(form.TargetChanged);
        Assert.True(form.CanCommit);
    }
}
