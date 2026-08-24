namespace SimplArchive.Api.Documents;

/// <summary>
/// A structured, editable view of an appointment for the rich edit form (ADR 0631). Read from and merged back
/// into the stored iCalendar blob so that everything the form doesn't model — <c>VALARM</c> above all, plus
/// <c>CATEGORIES</c>, <c>STATUS</c>, <c>ATTENDEE</c>, <c>X-</c> extensions and any vendor sub-component —
/// survives an edit.
/// </summary>
/// <param name="Summary">The title.</param>
/// <param name="Start">
/// The start as it is written in the file — a wall-clock value in <paramref name="TimeZoneId"/>, never
/// converted. For an all-day appointment, the date with a zero time.
/// </param>
/// <param name="End">The end, on the same terms as <paramref name="Start"/>.</param>
/// <param name="IsAllDay">True when the stored value is a DATE rather than a DATE-TIME.</param>
/// <param name="StartTimeZoneId">
/// The zone <paramref name="Start"/> is written in (ADR 0631 decision 5): a TZID like <c>Europe/Zurich</c>,
/// <c>"UTC"</c> for a Z-suffixed value, or <see langword="null"/> for a floating time — which stays floating.
/// </param>
/// <param name="EndTimeZoneId">
/// The zone <paramref name="End"/> is written in, which iCalendar allows to DIFFER from the start's — a flight
/// that leaves Zurich at 09:00 and lands in Boston at 11:30 is one appointment with two zones, and collapsing
/// them into one is how it comes to read as two and a half hours in the wrong place (ADR 0690).
/// </param>
/// <param name="Location">The location.</param>
/// <param name="Description">The notes.</param>
/// <param name="Url">The <c>URL</c> property — where the event lives online (a meeting link, a ticket page).</param>
/// <param name="RecurrenceRule">
/// The RRULE as raw text (<c>FREQ=WEEKLY;BYDAY=TU</c>), kept opaque — the server never expands a recurrence
/// set, so this travels through the form unparsed.
/// </param>
public sealed record Appointment(
    string? Summary,
    DateTime? Start,
    DateTime? End,
    bool IsAllDay,
    string? StartTimeZoneId,
    string? EndTimeZoneId,
    string? Location,
    string? Description,
    string? RecurrenceRule,
    string? Url = null)
{
    public static Appointment Empty { get; } = new(null, null, null, false, null, null, null, null, null);
}

/// <summary>
/// Lossless structured read/merge of an appointment (ADR 0631). <see cref="Merge"/> updates only the six
/// modelled fields on the stored component and leaves everything else as it stands.
/// </summary>
/// <remarks>
/// The contact side does this with a line-level merge because a vCard is a flat property list and a library
/// round-trip drops what it does not model. iCalendar is a component tree, and Ical.Net was measured to
/// round-trip one faithfully — folding, escapes, non-ASCII, override components, floating and all-day values,
/// unknown sub-components and quoted parameters all survive — so here the merge is a structured edit rather
/// than line surgery. Do not unify the two: the formats differ, and so do their failure modes.
/// </remarks>
public interface IAppointmentComposer
{
    /// <summary>Parses an iCalendar blob into the structured editable view. Reads the master component.</summary>
    Appointment Read(string blob);

    /// <summary>
    /// Applies <paramref name="appointment"/> onto <paramref name="existingBlob"/> (or a fresh calendar when
    /// null), preserving everything unmodelled, and returns the serialized iCalendar.
    /// <paramref name="uid"/> is kept so a later sync matches rather than duplicates.
    /// </summary>
    string Merge(string? existingBlob, Appointment appointment, string uid);
}
