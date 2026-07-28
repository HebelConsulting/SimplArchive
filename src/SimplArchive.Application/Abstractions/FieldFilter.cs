namespace SimplArchive.Application.Abstractions;

// The type family a typed field filter operates on (ADR 0043 / "Typed field filters in search") — the six
// FieldDataTypes collapse here (SingleSelect/MultiSelect → Select), since the query shape only depends on
// the value family, not the selection cardinality.
public enum FieldFilterKind
{
    Text,
    Number,
    Date,
    Boolean,
    Select,
}

// A typed index-field filter parsed from ?fields[Name][op]=value (ADR 0043). Operator is one of
// eq/contains/gt/gte/lt/lte/in; Values holds one entry, or several for `in`. Only the OpenSearch path honors
// these (nested typed query); the Postgres metadata fallback ignores them (ADR "Typed field filters in
// search").
public sealed record FieldFilter(string Name, FieldFilterKind Kind, string Operator, IReadOnlyList<string> Values);
