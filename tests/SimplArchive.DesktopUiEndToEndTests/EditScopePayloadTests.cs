using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopUiEndToEndTests;

// The scope the dialog collects reaches the server, and only when there is something to choose (#1133).
//
// The dialog itself is a window and cannot be shown headlessly; what matters and IS testable is that the
// answer travels — an edit that names one occurrence must carry both the scope and the instant identifying
// it, and an entry that does not repeat must carry neither.
public class EditScopePayloadTests
{
    private static AppointmentEditViewModel Edited() => new()
    {
        Summary = "Stand-up",
        StartDate = new DateTime(2026, 9, 16),
        StartTime = new TimeSpan(9, 0, 0),
        EndDate = new DateTime(2026, 9, 16),
        EndTime = new TimeSpan(10, 0, 0),
    };

    // PARSED, not compared as text: System.Text.Json's default encoder escapes '+' as \u002B, so a raw
    // substring match on an offset would fail against a payload that is perfectly correct on the wire.
    private static System.Text.Json.JsonElement Sent(object payload) =>
        System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(payload));

    private static string? Field(object payload, string name) =>
        Sent(payload).TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    [Fact]
    public void An_edit_that_names_one_occurrence_sends_the_scope_and_the_instant()
    {
        var form = Edited();
        form.Scope = "this";
        form.RecurrenceId = "2026-09-16T07:00:00.0000000+00:00";

        Assert.Equal("this", Field(form.ToPayload(), "scope"));
        Assert.Equal("2026-09-16T07:00:00.0000000+00:00", Field(form.ToPayload(), "recurrenceId"));
    }

    // No scope at all for a one-off entry — which is exactly what every client sent before the choice existed,
    // so the server treats it as "all" and an older build keeps working.
    [Fact]
    public void An_ordinary_edit_names_no_scope()
    {
        Assert.Null(Field(Edited().ToPayload(), "scope"));
        Assert.Null(Field(Edited().ToPayload(), "recurrenceId"));
    }

    // The identity is carried VERBATIM, never rebuilt from the displayed time: a floating entry is stamped
    // with the SERVER's zone at index time, so the wall clock on screen is not the instant the series falls
    // on. Reconstructing it cancels a day that does not exist — which is exactly how the first version of the
    // end-to-end test reported this feature broken.
    [Fact]
    public void The_occurrence_identity_is_not_rebuilt_from_the_displayed_time()
    {
        var form = Edited();
        form.Scope = "following";
        form.RecurrenceId = "2026-09-16T07:00:00.0000000+00:00";   // 07:00Z, while the form shows 09:00

        var sent = Field(form.ToPayload(), "recurrenceId");

        Assert.Equal("2026-09-16T07:00:00.0000000+00:00", sent);
        Assert.DoesNotContain("T09:00", sent!, StringComparison.Ordinal);
    }
}
