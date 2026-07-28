using System.Text;

namespace SimplArchive.Api.Pagination;

/// <summary>
/// Opaque cursor for keyset pagination — see ADR "Pagination for list endpoints". Encodes a
/// (CreatedAt, Id) position; purely mechanical, no knowledge of any specific entity. Every list endpoint
/// sorts CreatedAt ascending, Id ascending as tiebreaker.
/// </summary>
public static class Cursor
{
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt.UtcTicks}|{id}"));
    }

    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split('|');

            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var parsedId))
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            id = parsedId;

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Callers fetch limit + 1 rows (ordered by the sort key) and pass the result here — the extra row
    // (if present) proves a further page exists without a separate COUNT query, and is trimmed off the
    // returned page.
    public static (List<T> Page, bool HasMore) Split<T>(List<T> fetched, int limit)
    {
        var hasMore = fetched.Count > limit;

        if (hasMore)
        {
            fetched.RemoveAt(fetched.Count - 1);
        }

        return (fetched, hasMore);
    }
}
