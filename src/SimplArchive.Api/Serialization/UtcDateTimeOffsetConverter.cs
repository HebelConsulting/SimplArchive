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
/// Writing is left to the default behaviour: responses already carry UTC values, because that is what came out of
/// the database.
/// </para>
/// <para>
/// <b>Scope:</b> JSON only. The XML formatter (ADR 0190) has its own pipeline and does not run converters, so an
/// XML caller posting a non-UTC offset is still an open hazard. No client does — both are JSON — and closing it
/// would mean a parallel mechanism for a path nothing exercises.
/// </para>
/// </remarks>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
