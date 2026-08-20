using System.Globalization;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Search;

// Builds the nested typed `fields` array indexed per document for typed filtering (ADR 0043 / "Typed field
// filters in search"). Each index-field value becomes { name, text, and — when the value parses for its type
// — number/date/bool }. `text` is always present (raw value); a value that doesn't parse for its declared
// type simply has no typed sub-field, so a typed filter won't match it (fail-closed). Shared by the per-doc
// indexer and the full rebuilder so both produce identical documents.
public static class SearchFieldMapper
{
    public static List<Dictionary<string, object>> BuildTypedFields(
        IEnumerable<(string Name, FieldDataType Type, string Value)> values)
    {
        var result = new List<Dictionary<string, object>>();

        foreach (var (name, type, value) in values)
        {
            var field = new Dictionary<string, object> { ["name"] = name, ["text"] = value };

            switch (type)
            {
                case FieldDataType.Number:
                    if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    {
                        field["number"] = number;
                    }
                    break;

                case FieldDataType.Date:
                case FieldDataType.DateTime:
                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    {
                        field["date"] = date.ToString("o", CultureInfo.InvariantCulture);
                    }
                    break;

                case FieldDataType.Boolean:
                    if (bool.TryParse(value, out var boolean))
                    {
                        field["bool"] = boolean;
                    }
                    break;
            }

            result.Add(field);
        }

        return result;
    }

    // The file-type facet value (ADR "Search facet refinements") — the current version's extension without the
    // dot, lowercased (e.g. "pdf"); null when there's no extension. Shared by the indexer and the rebuilder.
    public static string? FileType(string objectKey)
    {
        var extension = Path.GetExtension(objectKey).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(extension) ? null : extension;
    }
}
