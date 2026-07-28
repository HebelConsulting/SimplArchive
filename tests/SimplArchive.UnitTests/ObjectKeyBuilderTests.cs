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
    public void Appends_the_file_extension_when_provided(string extension)
    {
        var key = ObjectKeyBuilder.Build(Guid.NewGuid(), DateTimeOffset.UtcNow, extension);

        Assert.EndsWith(extension.StartsWith('.') ? extension : $".{extension}", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Omits_the_extension_when_none_is_provided(string? extension)
    {
        var key = ObjectKeyBuilder.Build(Guid.NewGuid(), DateTimeOffset.UtcNow, extension);

        // The final segment is a bare GUID with no dot when there is no extension.
        var lastSegment = key[(key.LastIndexOf('/') + 1)..];
        Assert.DoesNotContain('.', lastSegment);
    }
}
