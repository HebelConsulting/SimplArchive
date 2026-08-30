namespace SimplArchive.Api.Documents;

/// <summary>
/// When a document falls due for disposition — the one place that rule is written.
/// </summary>
/// <remarks>
/// <para>
/// It was written THREE times before (#871): the schedule listing computing <c>Overdue</c>, the dispose
/// endpoint's own eligibility check, and the document resource's retention block. All three agreed, which is
/// exactly why nobody noticed — three copies of a rule that agree are indistinguishable from one rule until the
/// day someone changes two of them.
/// </para>
/// <para>
/// The reason it matters here rather than being ordinary tidiness: the schedule's <c>dispose</c> rel now
/// promises that the endpoint will accept the call (ADR 0543). That promise is only true while the rel's
/// condition and the endpoint's enforcement are the SAME answer — and "two copies that agree today" is not the
/// same answer, it is a coincidence with a maintenance schedule.
/// </para>
/// </remarks>
public static class RetentionSchedule
{
    /// <summary>The scheduled disposition date: the retention period counted from the document's anchor date.</summary>
    public static DateOnly DispositionDateOf(DateOnly anchor, int retentionYears) => anchor.AddYears(retentionYears);

    /// <summary>
    /// The date disposition actually becomes possible — a manager's extension pushes it out, but never pulls it
    /// in, so an override EARLIER than the scheduled date is ignored rather than shortening retention.
    /// </summary>
    public static DateOnly EffectiveDateOf(DateOnly dispositionDate, DateOnly? retentionOverrideUntil) =>
        retentionOverrideUntil is { } until && until > dispositionDate ? until : dispositionDate;

    /// <summary>Whether this document is due for disposition on the given day.</summary>
    public static bool IsDue(DateOnly anchor, int retentionYears, DateOnly? retentionOverrideUntil, DateOnly today) =>
        EffectiveDateOf(DispositionDateOf(anchor, retentionYears), retentionOverrideUntil) <= today;

    /// <summary>Today, as the retention rule reckons it — UTC, date-only.</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
}
