using SimplArchive.Application.Abstractions;

namespace SimplArchive.UnitTests;

public class ObjectKeyBuilderTests
{
    [Fact]
    public void Builds_a_key_with_the_tenant_and_filing_year()
    {
        var tenantId = Guid.NewGuid();
        var filingDate = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var key = ObjectKeyBuilder.Build(tenantId, filingDate, Guid.NewGuid(), Guid.NewGuid());

        Assert.StartsWith($"tenants/{tenantId}/2026/", key);
    }

    [Fact]
    public void Produces_a_different_key_for_each_version_in_the_same_document_folder()
    {
        // Uniqueness now comes from the version id leaf, not an internally-generated GUID (ADR 0530): two versions
        // of the same document share the tenant/year/storageFolder directory but differ by their version id.
        var tenantId = Guid.NewGuid();
        var filingDate = DateTimeOffset.UtcNow;
        var storageFolderId = Guid.NewGuid();

        var first = ObjectKeyBuilder.Build(tenantId, filingDate, storageFolderId, Guid.NewGuid());
        var second = ObjectKeyBuilder.Build(tenantId, filingDate, storageFolderId, Guid.NewGuid());

        Assert.NotEqual(first, second);
        // …but both nest under the same document folder.
        Assert.Equal(first[..(first.LastIndexOf('/') + 1)], second[..(second.LastIndexOf('/') + 1)]);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData("txt")] // a bare extension is normalized to include the leading dot
    public void Appends_the_file_extension_to_the_inner_content_filename(string extension)
    {
        var versionId = Guid.NewGuid();
        var key = ObjectKeyBuilder.Build(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), versionId, extension);

        var normalized = extension.StartsWith('.') ? extension : $".{extension}";
        Assert.EndsWith($"/{versionId}{normalized}", key);
    }

    // Issue #338 / ADR 0530: the storage folder is its own directory segment and the version's content lives under
    // it — "…/{year}/{storageFolderId}/{versionId}{ext}" — so a document's files (every version + derived artifact)
    // group under one folder instead of a flat pile of "{guid}.<something>" siblings.
    [Theory]
    [InlineData(".pdf")]
    [InlineData(null)]
    public void Builds_a_pure_guid_directory_segment_holding_the_content_file(string? extension)
    {
        var tenantId = Guid.NewGuid();
        var filingDate = new DateTimeOffset(2017, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var storageFolderId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var segments = ObjectKeyBuilder.Build(tenantId, filingDate, storageFolderId, versionId, extension).Split('/');

        Assert.Equal("tenants", segments[0]);
        Assert.Equal(tenantId.ToString(), segments[1]);
        Assert.Equal("2017", segments[2]);                          // 4-digit filing year
        Assert.Equal(filingDate.Year, int.Parse(segments[2]));
        Assert.Equal(storageFolderId.ToString(), segments[3]);      // the storage-folder segment is a pure GUID…
        Assert.DoesNotContain('.', segments[3]);                    // …with no extension glued to it (the #338 guard)
        Assert.StartsWith(versionId.ToString(), segments[4]);       // the version content leaf under the folder
        Assert.Equal(5, segments.Length);
    }

    // Cross-cutting guard over the shared derived-artefact key scheme (renditions / per-page / text-layout all use
    // ObjectKeyBuilder.DerivedKey): every derived key nests under the SAME "{tenant}/{year}/{storageFolderId}/" folder
    // as the content and never reintroduces a "{guid}.<something>" flat sibling.
    [Theory]
    [InlineData(".preview.png")]
    [InlineData(".preview.p3.png")]
    [InlineData(".preview.pages")]
    [InlineData(".textlayout.json")]
    public void Derived_keys_nest_under_the_same_guid_folder(string suffix)
    {
        var tenantId = Guid.NewGuid();
        var filingDate = new DateTimeOffset(2017, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var contentKey = ObjectKeyBuilder.Build(tenantId, filingDate, Guid.NewGuid(), Guid.NewGuid(), ".pdf");
        var folder = contentKey[..(contentKey.LastIndexOf('/') + 1)]; // "tenants/{t}/2017/{storageFolderId}/"

        var derived = ObjectKeyBuilder.DerivedKey(contentKey, suffix);

        Assert.StartsWith(folder, derived);
        Assert.EndsWith(suffix, derived);
        // The storage-folder segment stays a pure directory — the derived key is never "…/{guid}.<something>".
        var folderSegment = folder.TrimEnd('/').Split('/')[^1];
        Assert.DoesNotContain('.', folderSegment);
    }

    // The same helper is collision-safe for the name-based intray staging keys (no GUID folder): it derives
    // alongside the item, keyed by the item's own name, so two intray items don't share a sidecar.
    [Fact]
    public void Derived_key_keeps_intray_name_based_keys_distinct()
    {
        Assert.Equal("intray/scan.preview.png", ObjectKeyBuilder.DerivedKey("intray/scan.tif", ".preview.png"));
        Assert.Equal("intray/report.preview.png", ObjectKeyBuilder.DerivedKey("intray/report.pdf", ".preview.png"));
    }
}
