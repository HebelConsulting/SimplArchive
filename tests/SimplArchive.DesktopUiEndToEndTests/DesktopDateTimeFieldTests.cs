using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the DateTime-field pane fix (owner-reported 2026-09-04): the same cases as the web's
// DateTimeFieldEditorTests, deliberately (ADR 0511 — one surface, two renderings). Before this, a DateTime
// field fell into the single-line text box showing the raw wire instant, which is what read as "only a
// date" — the time was in the value and nowhere usable on the screen.
public class DesktopDateTimeFieldTests
{
    private static MasksClient.MaskFieldInfo Definition(string dataType, bool classifierOwned = false) =>
        new(Guid.NewGuid(), "Start", dataType, IsRequired: false, ClassifierOwned: classifierOwned);

    [Fact]
    public void A_datetime_field_edits_as_a_date_and_a_time_and_round_trips_the_instant()
    {
        var field = MaskFieldEditViewModel.Create(Definition("DateTime"), ["2026-09-04T12:30:00+00:00"]);

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
        var field = MaskFieldEditViewModel.Create(Definition("DateTime", classifierOwned: true), ["2026-09-04T12:30:00+00:00"]);

        Assert.True(field.Locked);
        Assert.False(field.IsDateTime); // locked falls to the read-only text rendering
        Assert.True(field.IsSingleLine);

        // Shown as a local wall clock, never the raw wire string...
        Assert.DoesNotContain("T", field.TextValue);
        Assert.Matches(@"\d{2}:\d{2}$", field.TextValue);

        // ...but the PUT gets the WIRE value back untouched — the server's classifier-owned guard reads
        // anything else, including a formatted rendering, as an attempted edit.
        Assert.Equal(["2026-09-04T12:30:00+00:00"], field.ToValues());
    }

    [Fact]
    public void The_read_row_renders_a_datetime_value_as_a_local_wall_clock()
    {
        var row = IndexFieldViewModel.From(new DocumentsClient.IndexField("Start", ["2026-09-04T12:30:00+00:00"], "DateTime"));

        Assert.DoesNotContain("T", row.Values);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", row.Values);

        // A text field is left exactly as stored — display never reinterprets somebody's words.
        Assert.Equal("2026-09-04T12:30", IndexFieldViewModel.From(new DocumentsClient.IndexField("Note", ["2026-09-04T12:30"], "Text")).Values);
    }
}
