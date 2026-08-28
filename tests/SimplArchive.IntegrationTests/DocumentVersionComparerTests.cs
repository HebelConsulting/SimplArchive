using System.Text;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Comparison;

namespace SimplArchive.IntegrationTests;

// The version/checkout comparer (ADRs 0712/0517) — extraction only; the diff itself is the clients'
// (SimplArchive.Presentation.TextDiff, pinned by TextDiffTests). The check-out stash key is
// extensionless (tenants/{t}/users/{u}/checkout/{doc}), so without a hint a text-file working copy would fall back
// to Tika. These tests prove the toExtensionHint lets an extensionless text side decode directly — no Tika needed.
public class DocumentVersionComparerTests
{
    // A text extractor that always yields nothing — stands in for "Tika not configured / can't extract".
    private sealed class NullTextExtractor : ITextExtractor
    {
        public Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default) => Task.FromResult("");
    }

    // Minimal in-memory object store — only the members the comparer touches do anything.
    private static InMemoryObjectStorage StorageWith(string versionKey, string versionText, string stashKey, string stashText)
    {
        var storage = new InMemoryObjectStorage();
        storage.Objects[versionKey] = Encoding.UTF8.GetBytes(versionText);
        storage.Objects[stashKey] = Encoding.UTF8.GetBytes(stashText);
        return storage;
    }

    [Fact]
    public async Task Extension_hint_lets_an_extensionless_text_stash_diff_without_Tika()
    {
        const string versionKey = "tenants/t/2026/abc.txt";
        const string stashKey = "tenants/t/users/u/checkout/doc"; // no extension
        var comparer = new DocumentVersionComparer(
            StorageWith(versionKey, "line one\nline two\nline three\n", stashKey, "line one\nline two CHANGED\nline three\n"),
            new NullTextExtractor());

        var result = await comparer.CompareAsync(versionKey, stashKey, toExtensionHint: ".txt");

        Assert.True(result.Available);
        Assert.Contains("line two\n", result.FromText);
        Assert.Contains("line two CHANGED", result.ToText);
    }

    [Fact]
    public async Task An_eml_side_extracts_its_body_not_its_mime_envelope()
    {
        // A note edited from a mail client is HTML in an .eml (#803) — the comparison must yield the PROSE.
        const string fromKey = "tenants/t/2026/a.eml";
        const string toKey = "tenants/t/2026/b.eml";
        const string emlA = "From: a@x\r\nSubject: n\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<div>Hello <b>world</b></div><div>second line</div>\r\n";
        const string emlB = "From: a@x\r\nSubject: n\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nplain body\r\n";
        var storage = new InMemoryObjectStorage();
        storage.Objects[fromKey] = Encoding.UTF8.GetBytes(emlA);
        storage.Objects[toKey] = Encoding.UTF8.GetBytes(emlB);
        var comparer = new DocumentVersionComparer(storage, new NullTextExtractor());

        var result = await comparer.CompareAsync(fromKey, toKey);

        Assert.True(result.Available);
        Assert.Contains("Hello world", result.FromText);       // tags stripped, entities decoded
        Assert.Contains("second line", result.FromText);       // block boundary became a line break
        Assert.DoesNotContain("Content-Type", result.FromText); // the envelope is not the text
        Assert.DoesNotContain("<div>", result.FromText);
        Assert.Contains("plain body", result.ToText);           // a text body is taken as-is
    }

    [Fact]
    public async Task Without_the_hint_an_extensionless_stash_is_unavailable_when_Tika_cannot_extract()
    {
        const string versionKey = "tenants/t/2026/abc.txt";
        const string stashKey = "tenants/t/users/u/checkout/doc";
        var comparer = new DocumentVersionComparer(
            StorageWith(versionKey, "original\n", stashKey, "edited\n"),
            new NullTextExtractor());

        // No hint → the extensionless side routes to the (null) extractor → no text → not available.
        var result = await comparer.CompareAsync(versionKey, stashKey);

        Assert.False(result.Available);
    }
}
