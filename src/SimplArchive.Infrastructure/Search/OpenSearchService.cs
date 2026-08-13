using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Search;

// OpenSearch full-text search (ADR "Search / full-text indexing model" 0011, "OpenSearch full-text slice 1").
// A relevance-ranked multi_match over name (boosted), index-field values, and extracted document content,
// filtered to the caller's tenant (and optionally one repository — ADR 0137). Raw HTTP + System.Text.Json
// (no client library). Any failure or a not-yet-created index yields an empty page (the controller still
// works; the metadata fallback is a separate implementation).
public sealed class OpenSearchService : ISearchService
{
    private const string Index = "documents";

    // Drops a null `highlight` from the request body (a filter-only search sends no highlight config).
    private static readonly JsonSerializerOptions NullOmittingJson =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // The keyword facet dimensions (ADR "Search facet refinements"): (aggregation name, OpenSearch field).
    // Multi-select OR within each; computed post-filter so a selected dimension keeps its other values.
    private static readonly (string Name, string Field)[] KeywordFacets =
    [
        ("documentType", "documentType"),
        ("createdBy", "createdBy"),
        ("tags", "tags"),
        ("documentYear", "documentYear"),
        ("fileType", "fileType"),
        ("sensitivityLabel", "sensitivityLabel"),
    ];

    private static readonly HashSet<string> FacetSystemFields = KeywordFacets.Select(f => f.Field).ToHashSet();

    private readonly HttpClient _http;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private readonly ILogger<OpenSearchService> _logger;

    public OpenSearchService(HttpClient http, ICurrentTenantAccessor tenantAccessor, ILogger<OpenSearchService> logger)
    {
        _http = http;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    // Hits are pre-filtered by the caller's indexed-ACL tokens (ADR "Indexed ACL in search") — accurate
    // paging, no per-hit CanSee post-filter needed.
    public bool PreFiltersByAcl => true;

    public async Task<SearchPage> SearchAsync(
        string query, Guid? repositoryId, SearchAccess access, SearchFilters filters,
        int skip, int take, CancellationToken cancellationToken)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        if (_tenantAccessor.TenantId is not { } tenantId || (!hasQuery && filters.IsEmpty))
        {
            return new SearchPage([], false);
        }

        var clauses = new List<object> { new { term = new { tenantId = tenantId.ToString() } } };
        if (repositoryId is { } repository)
        {
            clauses.Add(new { term = new { repositoryId = repository.ToString() } });
        }

        // Indexed ACL: unless the caller is a tenant admin (bypass), a document is visible only if its
        // allowedPrincipals intersects the caller's tokens. An empty token set (non-admin with no grants)
        // yields a terms filter that matches nothing — the caller sees no results, as intended.
        if (!access.BypassAcl)
        {
            clauses.Add(new { terms = new { allowedPrincipals = access.PrincipalTokens } });
        }

        // Data-classification clearance (ADR "Sensitivity clearance enforcement"): drop any hit whose indexed
        // sensitivityRank exceeds the caller's clearance ceiling. Unlabelled documents index as rank 0, so they
        // always pass. Null ceiling ⇒ not enforced (or the caller bypasses it), no clause added.
        if (access.MaxSensitivityRank is int maxSensitivityRank)
        {
            clauses.Add(new { range = new { sensitivityRank = new { lte = maxSensitivityRank } } });
        }

        // ---- Facet dimensions + post-filter faceting (ADR "Search facet refinements") -------------------
        // A facet selection (an eq/in on a facet dimension) narrows the hits (via post_filter) and every OTHER
        // dimension's counts, but NOT its own dimension's counts — so multi-selecting within a dimension keeps
        // its other values visible. Everything else is a base filter (narrows the hits AND all facet counts).
        var baseClauses = clauses; // tenant / repository / ACL
        var facetSelections = new List<(string Dim, object Clause)>();

        foreach (var systemFilter in filters.System)
        {
            if (FacetSystemFields.Contains(systemFilter.Field) && systemFilter.Operator is "eq" or "in")
            {
                facetSelections.Add((systemFilter.Field, BuildSystemFilterClause(systemFilter)));
            }
            else
            {
                baseClauses.Add(BuildSystemFilterClause(systemFilter)); // date ranges, contains, sensitivity, …
            }
        }

        // Typed index-field filters (ADR 0043): a Select eq/in is a facet selection; everything else is a base
        // filter (Number/Date ranges, Text contains, Boolean).
        foreach (var fieldFilter in filters.Fields)
        {
            if (fieldFilter.Kind == FieldFilterKind.Select && fieldFilter.Operator is "eq" or "in")
            {
                facetSelections.Add(("field:" + fieldFilter.Name, BuildFieldFilterClause(fieldFilter)));
            }
            else
            {
                baseClauses.Add(BuildFieldFilterClause(fieldFilter));
            }
        }

        // Each dimension's terms agg is wrapped in a `filter` agg applying the OTHER dimensions' selections.
        object[] OtherSelections(string dim) => facetSelections.Where(s => s.Dim != dim).Select(s => s.Clause).ToArray();

        var aggs = new Dictionary<string, object>();
        foreach (var (name, field) in KeywordFacets)
        {
            aggs[name] = new
            {
                filter = new { @bool = new { filter = OtherSelections(name) } },
                aggs = new { v = new { terms = new { field, size = 30 } } },
            };
        }

        // One nested facet per configured Select index-field name (ADR "Search facet refinements").
        var facetFieldNames = (filters.FacetFields ?? []).ToList();
        for (var i = 0; i < facetFieldNames.Count; i++)
        {
            var fieldName = facetFieldNames[i];
            aggs[$"field_{i}"] = new
            {
                filter = new { @bool = new { filter = OtherSelections("field:" + fieldName) } },
                aggs = new
                {
                    n = new
                    {
                        nested = new { path = "fields" },
                        aggs = new
                        {
                            byname = new
                            {
                                filter = new { term = new Dictionary<string, object> { ["fields.name"] = fieldName } },
                                aggs = new { v = new { terms = new { field = "fields.text", size = 30 } } },
                            },
                        },
                    },
                },
            };
        }

        // A free-text query when present; otherwise a filter-only search (match_all + the base filters above).
        object mustClause = hasQuery
            ? new { multi_match = new { query, fields = new[] { "name^3", "indexValues^2", "annotations^2", "content" } } }
            : new { match_all = new { } };

        // Result highlighting (ADR "Search result highlighting"): only when there's a free-text query — a
        // filter-only search has no term to highlight. encoder=html escapes any real markup in the stored text
        // so the only tags in a fragment are the <em> highlight tags (safe for the clients to render/parse). A
        // single content fragment; name/index-field values highlighted whole (number_of_fragments=0 returns the
        // full field with matches marked, since those are short).
        object? highlight = hasQuery
            ? new
            {
                encoder = "html",
                fields = new Dictionary<string, object>
                {
                    ["content"] = new { fragment_size = 160, number_of_fragments = 1 },
                    ["indexValues"] = new { number_of_fragments = 0 },
                    ["annotations"] = new { number_of_fragments = 0 },
                    ["name"] = new { number_of_fragments = 0 },
                },
            }
            : null;

        var body = new Dictionary<string, object?>
        {
            ["from"] = skip,
            ["size"] = take + 1,
            ["_source"] = new[] { "name", "isFolder", "parentId" },
            ["highlight"] = highlight,
            // The aggs run in the query context (base filters + free-text), so counts respect tenant/ACL and the
            // base filters but NOT the facet selections (those go to post_filter below).
            ["query"] = new { @bool = new { must = new[] { mustClause }, filter = baseClauses.ToArray() } },
            ["aggs"] = aggs,
        };

        // Post-filter = the facet selections — narrows the hits only, so each dimension's agg stays "open".
        if (facetSelections.Count > 0)
        {
            body["post_filter"] = new { @bool = new { filter = facetSelections.Select(s => s.Clause).ToArray() } };
        }

        try
        {
            var page = await ExecuteAsync(body, tenantId, take, facetFieldNames, cancellationToken);

            // Partial-word fallback (ADR "Partial-word search fallback"). The index is analyzed with the standard
            // analyzer, so every field holds WHOLE WORDS: a query that is only part of one — "montage" against
            // "Montagehalterung", "sechskant" inside a longer compound — matches nothing whatsoever, which reads
            // as a broken search rather than a strict one. Rather than ngram-index every field (a permanently
            // larger index, and a reindex, to serve a minority of queries), retry ONCE with each term wrapped in
            // wildcards. The expensive pass runs only when the precise one already came back empty, so an
            // ordinary search pays nothing for it.
            if (hasQuery && page.Hits.Count == 0 && WildcardClause(query) is { } wildcard)
            {
                _logger.LogDebug("No whole-word hits for tenant {TenantId}; retrying as a partial-word search.", tenantId);
                body["query"] = new { @bool = new { must = new[] { wildcard }, filter = baseClauses.ToArray() } };
                page = await ExecuteAsync(body, tenantId, take, facetFieldNames, cancellationToken);
            }

            return page;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Search query failed for tenant {TenantId}; returning an empty page.", tenantId);
            return new SearchPage([], false);
        }
    }

    // Sends one prepared search body and parses the response into a page. Its own method because the
    // partial-word fallback above runs the identical shape a second time with a different `query` clause.
    private async Task<SearchPage> ExecuteAsync(
        Dictionary<string, object?> body, Guid tenantId, int take, List<string> facetFieldNames, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(body, NullOmittingJson); // omit a null `highlight` on a filter-only search

        _logger.LogDebug("Querying the search index for tenant {TenantId}.", tenantId);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Index}/_search")
        {
            Content = new StringContent(request, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // A 404 before the index exists is normal; anything else is a degraded search backend.
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Search query returned {Status} for tenant {TenantId}.", response.StatusCode, tenantId);
            }

            return new SearchPage([], false);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var hits = json.GetProperty("hits").GetProperty("hits");

        var candidates = new List<SearchCandidate>();
        foreach (var hit in hits.EnumerateArray())
        {
            if (!Guid.TryParse(hit.GetProperty("_id").GetString(), out var id))
            {
                continue;
            }

            var source = hit.GetProperty("_source");
            var name = source.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var isFolder = source.TryGetProperty("isFolder", out var f) && f.GetBoolean();
            Guid? parentId = source.TryGetProperty("parentId", out var p)
                && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var g) ? g : null;
            candidates.Add(new SearchCandidate(id, name, isFolder, parentId, ExtractHighlight(hit)));
        }

        var hasMore = candidates.Count > take;

        SearchFacets? facets = null;
        if (json.TryGetProperty("aggregations", out var aggregations))
        {
            var fieldFacets = new List<SearchFieldFacet>();
            for (var i = 0; i < facetFieldNames.Count; i++)
            {
                var fieldBuckets = ParseNestedTerms(aggregations, $"field_{i}");
                if (fieldBuckets.Count > 0)
                {
                    fieldFacets.Add(new SearchFieldFacet(facetFieldNames[i], fieldBuckets));
                }
            }

            facets = new SearchFacets(
                ParseFilteredTerms(aggregations, "documentType"),
                ParseFilteredTerms(aggregations, "createdBy"),
                SortYearsDescending(ParseFilteredTerms(aggregations, "documentYear")),
                ParseFilteredTerms(aggregations, "tags"),
                ParseFilteredTerms(aggregations, "fileType"),
                ParseFilteredTerms(aggregations, "sensitivityLabel"),
                fieldFacets);
        }

        return new SearchPage(candidates.Take(take).ToList(), hasMore, facets);
    }

    // The same fields the whole-word query searches, with the same boosts.
    private static readonly (string Field, double Boost)[] FreeTextFields =
        [("name", 3), ("indexValues", 2), ("annotations", 2), ("content", 1)];

    /// <summary>Each term of <paramref name="query"/> as <c>*term*</c> across the free-text fields, or null if
    /// there is nothing to match on.</summary>
    /// <remarks>
    /// <para>
    /// Built as individual <c>wildcard</c> clauses rather than a <c>query_string</c> so the user's text is never
    /// parsed as query syntax — a stray <c>(</c> or <c>:</c> would otherwise turn a search into a 400, and
    /// escaping a mini-language correctly is a recurring source of exactly that.
    /// </para>
    /// <para>
    /// Terms are ANDed (each must match SOME field) while fields are ORed within a term. A fallback is already
    /// the loose pass; ORing the terms as well would make a two-word search return everything containing either.
    /// The term is lowercased because the standard analyzer lowercases what it indexed, and <c>wildcard</c>
    /// matches the stored token as-is — an uppercase pattern would match nothing.
    /// </para>
    /// </remarks>
    private static object? WildcardClause(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return null;
        }

        var perTerm = terms.Select(object (term) => new
        {
            @bool = new
            {
                should = FreeTextFields.Select(object (f) => new
                {
                    wildcard = new Dictionary<string, object>
                    {
                        [f.Field] = new { value = $"*{term.ToLowerInvariant()}*", boost = f.Boost },
                    },
                }).ToArray(),
                minimum_should_match = 1,
            },
        }).ToArray();

        return new { @bool = new { must = perTerm } };
    }

    // Parses a filter-wrapped keyword terms aggregation (`<name>.v.buckets`) into facet buckets. Each dimension
    // is wrapped in a `filter` agg for post-filter faceting, so the terms buckets live one level down under `v`.
    private static IReadOnlyList<SearchFacetBucket> ParseFilteredTerms(JsonElement aggs, string name) =>
        aggs.TryGetProperty(name, out var agg) && agg.TryGetProperty("v", out var v)
            ? ParseTermsBuckets(v)
            : [];

    // Parses a nested Select-field facet (`<key>.n.byname.v.buckets`).
    private static IReadOnlyList<SearchFacetBucket> ParseNestedTerms(JsonElement aggs, string key) =>
        aggs.TryGetProperty(key, out var agg)
            && agg.TryGetProperty("n", out var n)
            && n.TryGetProperty("byname", out var byname)
            && byname.TryGetProperty("v", out var v)
            ? ParseTermsBuckets(v)
            : [];

    private static IReadOnlyList<SearchFacetBucket> ParseTermsBuckets(JsonElement termsAgg)
    {
        if (!termsAgg.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SearchFacetBucket>();
        foreach (var bucket in buckets.EnumerateArray())
        {
            var value = bucket.TryGetProperty("key", out var k)
                ? (k.ValueKind == JsonValueKind.String ? k.GetString() : k.ToString())
                : null;
            if (!string.IsNullOrEmpty(value) && bucket.TryGetProperty("doc_count", out var dc))
            {
                result.Add(new SearchFacetBucket(value, dc.GetInt64()));
            }
        }

        return result;
    }

    // The year facet is a keyword terms agg (count-ordered); present it newest-year-first instead.
    private static IReadOnlyList<SearchFacetBucket> SortYearsDescending(IReadOnlyList<SearchFacetBucket> years) =>
        years.OrderByDescending(b => int.TryParse(b.Value, out var y) ? y : 0).ToList();

    // Picks the best highlight fragment from a hit's `highlight` object (ADR "Search result highlighting"):
    // a content excerpt if the match was in the document text, else a matched index-field value, else the name.
    // Each is a string with the matched terms wrapped in <em>…</em>; null if the hit has no highlight.
    private static string? ExtractHighlight(JsonElement hit)
    {
        if (!hit.TryGetProperty("highlight", out var highlight) || highlight.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var field in new[] { "content", "indexValues", "annotations", "name" })
        {
            if (highlight.TryGetProperty(field, out var fragments)
                && fragments.ValueKind == JsonValueKind.Array
                && fragments.GetArrayLength() > 0)
            {
                return fragments[0].GetString();
            }
        }

        return null;
    }

    // A typed field filter → a nested query on the `fields` array: match the field name AND a type-appropriate
    // clause on its typed sub-value. The controller has already validated the operator/value for the kind.
    private static object BuildFieldFilterClause(FieldFilter filter)
    {
        object typedClause = filter.Kind switch
        {
            FieldFilterKind.Number => Comparison("fields.number", filter.Operator, double.Parse(filter.Values[0], CultureInfo.InvariantCulture), isDate: false),
            FieldFilterKind.Date => Comparison("fields.date", filter.Operator, filter.Values[0], isDate: true),
            FieldFilterKind.Boolean => Term("fields.bool", bool.Parse(filter.Values[0])),
            FieldFilterKind.Select => filter.Operator == "in"
                ? new { terms = new Dictionary<string, object> { ["fields.text"] = filter.Values } }
                : Term("fields.text", filter.Values[0]),
            _ => filter.Operator == "contains"
                ? new { wildcard = new Dictionary<string, object> { ["fields.text"] = new { value = $"*{filter.Values[0]}*", case_insensitive = true } } }
                : Term("fields.text", filter.Values[0]),
        };

        return new
        {
            nested = new
            {
                path = "fields",
                query = new
                {
                    @bool = new
                    {
                        must = new object[] { Term("fields.name", filter.Name), typedClause },
                    },
                },
            },
        };
    }

    private static object Term(string field, object value) =>
        new { term = new Dictionary<string, object> { [field] = value } };

    // eq → exact term (a date eq becomes an inclusive same-value range so a stored timestamp still matches a
    // date-only bound); gt/gte/lt/lte → a range.
    private static object Comparison(string field, string op, object value, bool isDate)
    {
        if (op == "eq")
        {
            return isDate
                ? new { range = new Dictionary<string, object> { [field] = new Dictionary<string, object> { ["gte"] = value, ["lte"] = value } } }
                : Term(field, value);
        }

        return new { range = new Dictionary<string, object> { [field] = new Dictionary<string, object> { [op] = value } } };
    }

    // A system-field filter → a flat clause on a top-level field (ADR "System-field search"): a date
    // range/term, or a keyword clause on a resolved creator name (eq/contains/in).
    private static object BuildSystemFilterClause(SystemFilter filter)
    {
        if (filter.Kind == FieldFilterKind.Date)
        {
            return Comparison(filter.Field, filter.Operator, filter.Values[0], isDate: true);
        }

        return filter.Operator switch
        {
            "in" => new { terms = new Dictionary<string, object> { [filter.Field] = filter.Values } },
            "contains" => new { wildcard = new Dictionary<string, object> { [filter.Field] = new { value = $"*{filter.Values[0]}*", case_insensitive = true } } },
            _ => Term(filter.Field, filter.Values[0]),
        };
    }
}
