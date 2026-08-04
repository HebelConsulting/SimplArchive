using SimplArchive.Application.Abstractions;

namespace SimplArchive.UnitTests;

public class ObjectKeyBuilderTests
{
    [Fact]
    public void Builds_a_key_with_the_tenant_and_filing_year()
    {
        var tenantId = Guid.NewGuid();
        var filingDate = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var key = ObjectKeyBuilder.Build(tenantId, filingDate);

        Assert.StartsWith($"tenants/{tenantId}/2026/", key);
    }

    [Fact]
    public void Produces_a_different_key_each_call_even_for_the_same_tenant_and_date()
    {
        var tenantId = Guid.NewGuid();
        var filingDate = DateTimeOffset.UtcNow;

        var first = ObjectKeyBuilder.Build(tenantId, filingDate);
        var second = ObjectKeyBuilder.Build(tenantId, filingDate);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData("txt")] // a bare extension is normalized to include the leading dot
    public void Appends_the_file_extension_to_the_inner_content_filename(string extension)
    {
        var key = ObjectKeyBuilder.Build(Guid.NewGuid(), DateTimeOffset.UtcNow, extension);

        var normalized = extension.StartsWith('.') ? extension : $".{extension}";
        Assert.EndsWith($"/content{normalized}", key);
    }

    // Issue #338: the GUID is its own directory segment and content lives under it — "…/{year}/{guid}/content{ext}"
    // — so a document's files group under one folder instead of a flat pile of "{guid}.<something>" siblings.
    [Theory]
    [InlineData(".pdf")]
    [InlineData(null)]
    public void Builds_a_pure_guid_directory_segment_holding_the_content_file(string? extension)
    {
        var tenantId = Guid.NewGuid();
        var filingDate = new DateTimeOffset(2017, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var segments = ObjectKeyBuilder.Build(tenantId, filingDate, extension).Split('/');

        Assert.Equal("tenants", segments[0]);
        Assert.Equal(tenantId.ToString(), segments[1]);
        Assert.Equal("2017", segments[2]);                          // 4-digit filing year
        Assert.Equal(filingDate.Year, int.Parse(segments[2]));
        Assert.True(Guid.TryParse(segments[3], out _));             // the GUID segment is a pure GUID…
        Assert.DoesNotContain('.', segments[3]);                    // …with no extension glued to it (the #338 guard)
        Assert.StartsWith("content", segments[4]);                  // content leaf under the GUID folder
        Assert.Equal(5, segments.Length);
    }

    // Cross-cutting guard over the shared derived-artefact key scheme (renditions / per-page / text-layout all use
    // ObjectKeyBuilder.DerivedKey): every derived key nests under the SAME "{tenant}/{year}/{guid}/" folder as the
    // content and never reintroduces a "{guid}.<something>" flat sibling.
    [Theory]
    [InlineData(".preview.png")]
    [InlineData(".preview.p3.png")]
    [InlineData(".preview.pages")]
    [InlineData(".textlayout.json")]
    public void Derived_keys_nest_under_the_same_guid_folder(string suffix)
    {
        var tenantId = Guid.NewGuid();
        var filingDate = new DateTimeOffset(2017, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var contentKey = ObjectKeyBuilder.Build(tenantId, filingDate, ".pdf");
        var guidFolder = contentKey[..(contentKey.LastIndexOf('/') + 1)]; // "tenants/{t}/2017/{guid}/"

        var derived = ObjectKeyBuilder.DerivedKey(contentKey, suffix);

        Assert.StartsWith(guidFolder, derived);
        Assert.EndsWith(suffix, derived);
        // The GUID segment stays a pure directory — the derived key is never "…/{guid}.<something>".
        var guidSegment = guidFolder.TrimEnd('/').Split('/')[^1];
        Assert.DoesNotContain('.', guidSegment);
    }

    // The same helper is collision-safe for the name-based inbox staging keys (no GUID folder): it derives
    // alongside the item, keyed by the item's own name, so two inbox items don't share a sidecar.
    [Fact]
    public void Derived_key_keeps_inbox_name_based_keys_distinct()
    {
        Assert.Equal("inbox/scan.preview.png", ObjectKeyBuilder.DerivedKey("inbox/scan.tif", ".preview.png"));
        Assert.Equal("inbox/report.preview.png", ObjectKeyBuilder.DerivedKey("inbox/report.pdf", ".preview.png"));
    }
}
