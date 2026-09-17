using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplArchive.Api.Serialization;

/// <summary>
/// Normalises every inbound <see cref="DateTimeOffset"/> to UTC at the API boundary.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL's <c>timestamp with time zone</c> stores an instant, not an offset, so Npgsql refuses to write a
/// <see cref="DateTimeOffset"/> whose offset is anything but zero — it throws
/// <c>Cannot write DateTimeOffset with Offset=02:00:00 … only offset 0 (UTC) is supported</c>. That turns a
/// perfectly valid request from a client in any non-UTC timezone into a <b>500</b> at <c>SaveChanges</c>, far from
/// the code that accepted it.
/// </para>
/// <para>
/// This is normalisation, not truncation: the instant is unchanged and only its representation moves, so
/// <c>2026-09-06T23:59+02:00</c> is stored as <c>2026-09-06T21:59Z</c> — the same moment.
/// </para>
/// <para>
/// Registered once for the whole API rather than fixed per endpoint, because the failure mode is invisible to the
/// people most likely to introduce it: the tests are written in <c>DateTimeOffset.UtcNow</c>, and CI runs in UTC,
/// so a per-site fix passes everything green while the next endpoint to accept a timestamp reintroduces the bug
/// for everyone east or west of Greenwich. It was found exactly that way — external-link creation worked in every
/// test and from the web client (which sent <c>TimeSpan.Zero</c>) and failed only from the desktop, in CEST.
/// </para>
/// <para>
/// <b>Writing emits <c>Z</c>, not <c>+00:00</c>.</b> Both are the same instant and both are valid ISO-8601, so
/// this changes no meaning — it changes whether the rule is <i>visible</i>. The default writer emits
/// <c>2026-09-17T09:14:35.0000000+00:00</c>, which reads as "an offset that happens to be zero" and is
/// indistinguishable at a glance from a value that escaped normalisation. <c>Z</c> says the API deals in
/// instants, so a payload can be eyeballed, grepped and asserted on. The instant is unchanged either way: a
/// value arriving from the database already carries offset zero, and <c>ToUniversalTime</c> makes that true for
/// anything constructed in the handler.
/// </para>
/// <para>
/// <b>Scope:</b> JSON only. The XML formatter (ADR 0190) has its own pipeline and does not run converters, so an
/// XML caller posting a non-UTC offset is still an open hazard. No client does — both are JSON — and closing it
/// would mean a parallel mechanism for a path nothing exercises.
/// </para>
/// <para>
/// <b>This converter is not the whole rule, and deliberately cannot be.</b> It governs values typed
/// <see cref="DateTimeOffset"/>, which are instants. Two kinds of time on this API are NOT instants and must not
/// be routed through here (ADR 0802): an appointment's <c>Start</c>/<c>End</c>, which are a <i>wall clock plus a
/// TZID</i> — converting them to an instant makes a weekly meeting drift an hour at the daylight-saving change —
/// and an indexed moment, which keeps the entry's own offset because the offset is information about the entry
/// (ADR 0647). Both are typed to say so; <c>ApiTimeContractTests</c> is what keeps a third from appearing by
/// accident.
/// </para>
/// </remarks>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    /// <remarks>
    /// <c>UtcDateTime</c> carries <see cref="DateTimeKind.Utc"/>, which is what makes the round-trip format
    /// render the trailing <c>Z</c> rather than a numeric offset. <c>ToUniversalTime</c> alone would not: it
    /// returns a <see cref="DateTimeOffset"/>, whose formatter always writes an offset.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}
