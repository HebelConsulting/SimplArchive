using System.Text;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Comparison;

namespace SimplArchive.IntegrationTests;

// The version/checkout comparer (ADR "Document version comparison"; ADR 0517). The check-out stash key is
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
        Assert.Contains(result.Lines, l => l.Op == DiffOp.Removed && l.Text.Contains("line two"));
        Assert.Contains(result.Lines, l => l.Op == DiffOp.Added && l.Text.Contains("CHANGED"));
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
