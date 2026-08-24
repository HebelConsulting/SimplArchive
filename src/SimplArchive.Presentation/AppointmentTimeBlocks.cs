namespace SimplArchive.Presentation;

/// <summary>One line of an appointment's time block: a range, and the zone (or zones) it is stated in.</summary>
/// <param name="Range">The formatted range — already collapsed to one date where both ends share a day.</param>
/// <param name="Zone">
/// What to say about the zone: an IANA id, or <see langword="null"/> when the entry names none and the caller
/// should say so in words (a floating time means "this time, wherever you are").
/// </param>
public sealed record AppointmentTimeLine(string Range, string? Zone);

/// <summary>
/// The lines a detail pane draws for one appointment's time, in the order it draws them.
/// </summary>
/// <param name="Utc">Always present for a timed entry: the instant everything else can be compared against.</param>
/// <param name="Recorded">
/// What the organiser wrote. ONE line when both endpoints share a zone, TWO when they do not — a flight
/// leaving Zurich at 09:00 and landing in Boston at 11:30 cannot be stated on one line without naming a zone
/// that is wrong for half of it.
/// </param>
/// <param name="Viewer">
/// The reader's own zone, or empty when it would repeat <paramref name="Recorded"/> — which it does for every
/// appointment written where the reader is, so it is the common case and the pane must not spend a line on it.
/// </param>
public sealed record AppointmentTimeBlocks(
    AppointmentTimeLine Utc,
    IReadOnlyList<AppointmentTimeLine> Recorded,
    AppointmentTimeLine? Viewer)
{
    /// <summary>
    /// Turns the three readings into the lines a pane draws, formatted in <paramref name="culture"/>.
    /// </summary>
    /// <remarks>
    /// The SPLIT decision lives here rather than in either client: whether the recorded block is one line or
    /// two is a statement about the appointment, not a layout preference, and two clients deciding it
    /// separately is how the same flight comes to read two ways on two screens (ADR 0651).
    /// </remarks>
    public static AppointmentTimeBlocks From(AppointmentTimes times, IFormatProvider culture)
    {
        var recorded = times.Recorded.OneZone
            ? new List<AppointmentTimeLine> { new(times.Recorded.Format(culture), times.Recorded.StartZoneId) }
            :
            [
                new(times.Recorded.FormatStart(culture), times.Recorded.StartZoneId),
                new(times.Recorded.FormatEnd(culture), times.Recorded.EndZoneId),
            ];

        return new AppointmentTimeBlocks(
            new AppointmentTimeLine(times.Utc.Format(culture), null),
            recorded,
            times.Viewer is { } viewer ? new AppointmentTimeLine(viewer.Format(culture), viewer.StartZoneId) : null);
    }
}
