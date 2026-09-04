using SimplArchive.Client.Models;

namespace SimplArchive.UnitTests;

// The web half of the DateTime-field pane fix (owner-reported 2026-09-04): a DateTime index field edits
// through a date picker AND a time picker, and a classifier-owned field is locked — shown readably, echoed
// back to the full-replacement PUT verbatim, because the server refuses any deviation
// (INDEX_FIELD_CLASSIFIER_OWNED). The desktop's MaskFieldEditViewModel is asserted separately (ADR 0511).
public class DateTimeFieldEditorTests
{
    private static MaskFieldInfo Definition(string dataType, bool classifierOwned = false) =>
        new() { Id = Guid.NewGuid(), Name = "Start", DataType = dataType, ClassifierOwned = classifierOwned };

    [Fact]
    public void A_datetime_field_edits_as_a_date_and_a_time_and_round_trips_the_instant()
    {
        var field = EditField.Create(Definition("DateTime"), ["2026-09-04T12:30:00+00:00"]);

        Assert.True(field.IsDateTime);
        Assert.False(field.IsSingleLine);
        Assert.NotNull(field.DateValue);
        Assert.NotNull(field.TimeValue);

        var value = Assert.Single(field.ToValues());
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-04T12:30:00+00:00"),
            DateTimeOffset.Parse(value));
    }

    [Fact]
    public void A_classifier_owned_field_is_locked_shown_readably_and_echoed_verbatim()
    {
        var field = EditField.Create(Definition("DateTime", classifierOwned: true), ["2026-09-04T12:30:00+00:00"]);

        Assert.True(field.Locked);
        Assert.False(field.IsDateTime); // locked falls to the read-only text rendering
        Assert.True(field.IsSingleLine);

        // Shown as a local wall clock, never the raw wire string...
        Assert.DoesNotContain("T", field.TextValue);
        Assert.Matches(@"\d{2}:\d{2}$", field.TextValue);

        // ...but the PUT gets the WIRE value back untouched — the guard reads anything else as an edit.
        Assert.Equal(["2026-09-04T12:30:00+00:00"], field.ToValues());
    }
}
