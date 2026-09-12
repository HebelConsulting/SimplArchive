namespace SimplArchive.Presentation;

/// <summary>One way an entry can repeat, as an editor offers it.</summary>
/// <param name="Key">The stable identity a client stores in its picker — never shown to anyone.</param>
/// <param name="Rule">The <c>RRULE</c> value, or null for "does not repeat".</param>
public readonly record struct RepeatChoice(string Key, string? Rule);

/// <summary>
/// The repeats an appointment, availability window, booking or maintenance block may be given (#1133).
/// </summary>
/// <remarks>
/// <para>
/// Shared for this project's usual reason: two clients offering two different sets of repeats is two answers
/// to one question, and only one of them would get fixed. The LABELS stay each client's own — they come from
/// the localized resources — while the rules that go on the wire are decided once, here.
/// </para>
/// <para>
/// A deliberately short list. RRULE can express "the last Thursday of every second month", and an editor that
/// tried to offer that becomes a rule builder; what people actually publish is daily hours, working days, a
/// weekly slot. Anything richer still arrives intact over CalDAV and is expanded correctly — it simply has no
/// button here, and the editor shows it read-only rather than pretending the list covers it.
/// </para>
/// </remarks>
public static class RepeatChoices
{
    /// <summary>Does not repeat.</summary>
    public const string None = "none";

    /// <summary>Every day, including weekends.</summary>
    public const string Daily = "daily";

    /// <summary>
    /// Monday to Friday — the case a room's opening hours and a school's timetable are actually written in.
    /// </summary>
    /// <remarks>
    /// <c>FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR</c> rather than <c>FREQ=DAILY</c> with exceptions: the rule says
    /// what is meant, so a client reading it back shows "every weekday" instead of a daily series riddled with
    /// cancellations, and adding a week to the series does not mean adding two more EXDATEs.
    /// </remarks>
    public const string Weekdays = "weekdays";

    /// <summary>Every week, on the entry's own weekday.</summary>
    public const string Weekly = "weekly";

    /// <summary>Every month, on the entry's own day of the month.</summary>
    public const string Monthly = "monthly";

    /// <summary>Every year.</summary>
    public const string Yearly = "yearly";

    /// <summary>The choices in the order an editor lists them.</summary>
    public static IReadOnlyList<RepeatChoice> All { get; } =
    [
        new(None, null),
        new(Daily, "FREQ=DAILY"),
        new(Weekdays, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"),
        new(Weekly, "FREQ=WEEKLY"),
        new(Monthly, "FREQ=MONTHLY"),
        new(Yearly, "FREQ=YEARLY"),
    ];

    /// <summary>
    /// The key whose rule matches <paramref name="rule"/>, or null when nothing in the list does.
    /// </summary>
    /// <remarks>
    /// Null means the stored rule is richer than this list — a fortnightly series, a BYSETPOS — and the editor
    /// must show it read-only rather than silently offering to replace it with the nearest button, which would
    /// turn "every second Tuesday" into "every Tuesday" on the next save.
    /// </remarks>
    public static string? KeyFor(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return None;
        }

        var normalized = Normalize(rule);
        foreach (var choice in All)
        {
            if (choice.Rule is { } candidate && Normalize(candidate) == normalized)
            {
                return choice.Key;
            }
        }

        return null;
    }

    /// <summary>The rule for a key, or null for <see cref="None"/> and for anything unrecognised.</summary>
    public static string? RuleFor(string? key) =>
        All.FirstOrDefault(choice => choice.Key == key).Rule;

    /// <summary>
    /// <paramref name="rule"/> with an <c>UNTIL</c> for <paramref name="until"/>, or unchanged when there is none.
    /// </summary>
    /// <remarks>
    /// The end of the day, not its start: a series told to run "until 30 September" means the 30th is included,
    /// and an UNTIL at midnight would drop that day's occurrence — an off-by-one nobody notices until the last
    /// day of a booking series is missing.
    /// </remarks>
    public static string? WithUntil(string? rule, DateOnly? until)
    {
        if (string.IsNullOrWhiteSpace(rule) || until is not { } last)
        {
            return rule;
        }

        var endOfDay = new DateTime(last, new TimeOnly(23, 59, 59), DateTimeKind.Utc);
        return $"{rule};UNTIL={endOfDay:yyyyMMdd'T'HHmmss'Z'}";
    }

    /// <summary>The <c>UNTIL</c> date a rule carries, or null when it does not end that way.</summary>
    public static DateOnly? UntilOf(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return null;
        }

        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part["UNTIL=".Length..];
            if (DateTime.TryParseExact(
                    value,
                    ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return DateOnly.FromDateTime(parsed);
            }
        }

        return null;
    }

    /// <summary>The rule without its <c>UNTIL</c>, so a picker can match it against the list above.</summary>
    public static string? WithoutUntil(string? rule) =>
        string.IsNullOrWhiteSpace(rule)
            ? rule
            : string.Join(';', rule
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => !part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase)));

    // Order-insensitive and case-insensitive: BYDAY=MO,TU and byday=TU,MO are the same rule, and a client or a
    // server that reorders the parts must not make the picker fall through to "something richer than the list".
    private static string Normalize(string rule) =>
        string.Join(';', rule
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToUpperInvariant())
            .Select(part => part.StartsWith("BYDAY=", StringComparison.Ordinal)
                ? "BYDAY=" + string.Join(',', part["BYDAY=".Length..].Split(',').OrderBy(day => day, StringComparer.Ordinal))
                : part)
            .OrderBy(part => part, StringComparer.Ordinal));
}
