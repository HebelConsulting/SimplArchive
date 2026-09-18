using System.Collections;
using System.Reflection;

namespace SimplArchive.Api.Serialization;

/// <summary>
/// Normalises every <see cref="DateTimeOffset"/> reachable from a bound model to UTC, for the representations
/// that cannot go through <see cref="UtcDateTimeOffsetConverter"/>.
/// </summary>
/// <remarks>
/// <para>
/// The JSON converter runs inside System.Text.Json and therefore covers the JSON surface only. The XML
/// formatter (ADR 0190) has its own pipeline and runs no STJ converters at all, so an XML caller posting
/// <c>2026-09-17T11:14:35+02:00</c> reached <c>SaveChanges</c> with the offset intact — where Npgsql refuses
/// anything but offset zero for <c>timestamp with time zone</c> and the request died as a bare <b>500</b>, far
/// from the code that accepted it (#1259, the same failure ADR 0548 was written about).
/// </para>
/// <para>
/// <b>Only the inbound half was broken, which is not what the issue predicted.</b> Measured before fixing:
/// <c>XmlSerializer</c> already writes a zero-offset value as <c>...35Z</c>, not <c>+00:00</c>, so the
/// outbound half of ADR 0802 held on this path all along. Normalising on the way in is therefore also what
/// makes the way out correct — a value stored as an instant is written as one.
/// </para>
/// <para>
/// <b>Normalisation, not truncation:</b> the instant is unchanged and only its representation moves, exactly
/// as in the JSON converter.
/// </para>
/// <para>
/// <b>Scope is deliberately narrow.</b> Only types from this assembly's own namespace are walked: the models
/// bound here are this API's DTOs, and recursing into framework or third-party graphs would buy nothing while
/// risking surprise. Cycles are impossible in the DTOs (they are flat data) but are guarded anyway with a
/// reference set, because "impossible" is a property of today's DTOs rather than of this method.
/// </para>
/// </remarks>
internal static class UtcModelNormalizer
{
    private const int MaxDepth = 8;

    /// <summary>Rewrites in place every <see cref="DateTimeOffset"/> on <paramref name="model"/> to UTC.</summary>
    public static void Normalize(object? model) =>
        Walk(model, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);

    private static void Walk(object? node, HashSet<object> seen, int depth)
    {
        if (node is null || depth > MaxDepth || !seen.Add(node))
        {
            return;
        }

        if (node is IEnumerable sequence and not string)
        {
            foreach (var item in sequence)
            {
                Walk(item, seen, depth + 1);
            }

            return;
        }

        var type = node.GetType();
        if (!IsOurs(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (propertyType == typeof(DateTimeOffset))
            {
                // A settable property only: a computed or read-only timestamp is not something a caller sent.
                if (property.CanRead && property.CanWrite && property.GetValue(node) is DateTimeOffset value
                    && value.Offset != TimeSpan.Zero)
                {
                    property.SetValue(node, value.ToUniversalTime());
                }

                continue;
            }

            if (propertyType.IsPrimitive || propertyType.IsEnum || propertyType == typeof(string)
                || propertyType == typeof(decimal) || propertyType == typeof(Guid)
                || propertyType == typeof(DateTime) || propertyType == typeof(DateOnly)
                || propertyType == typeof(TimeOnly) || propertyType == typeof(TimeSpan))
            {
                continue;
            }

            if (property.CanRead)
            {
                Walk(property.GetValue(node), seen, depth + 1);
            }
        }
    }

    // This assembly's own DTOs, and nothing else — see the scope note above.
    private static bool IsOurs(Type type) =>
        type.Namespace?.StartsWith("SimplArchive.", StringComparison.Ordinal) == true;
}
