using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.IntegrationTests;

// A store this process cannot DECRYPT is a misconfigured installation, not a missing artefact (#1582).
//
// It surfaced as "find-in-document does not work": the stack was running without Encryption:ServiceUrl over a
// store written WITH one, every cached text layout refused to decrypt, and the degradation handler — which
// exists for "this document genuinely has no text layer" — caught the refusal with everything else and
// returned null. The endpoint answered 204, so the client rendered a find box that could never match, with
// nothing on screen saying why. The cause was in an Error line nobody was reading.
public class RefusedReadIsNotAnAbsentArtefactTests
{
    [Fact]
    public void The_refusal_carries_what_the_boundary_needs_to_say()
    {
        var refusal = new EncryptedObjectWithoutEncryptionServiceException("tenants/t/2026/abc/content.pdf");

        // 503, not 500: nothing is broken and nothing is lost — restoring the configuration makes the read
        // work again, so "this server cannot serve it right now" is the true statement.
        Assert.Equal(503, refusal.ApiStatusCode);
        Assert.Equal("ENCRYPTION_SERVICE_NOT_CONFIGURED", refusal.ErrorCode);

        // The CAUSE reaches the caller — that is the whole fix. Without it the reader is told nothing and
        // goes looking for a missing OCR pass.
        Assert.Contains("encryption service", refusal.ApiDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Encryption:ServiceUrl", refusal.ApiDetail, StringComparison.Ordinal);

        // …and the object key does NOT, because it is in the Error log line where an operator already is, and
        // an object key in a problem response is a storage-layout detail a caller has no use for.
        Assert.DoesNotContain("tenants/t/2026", refusal.ApiDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_degradation_handler_must_not_swallow_it()
    {
        // The guard, stated against the TYPE rather than against one call site: both the text-layout service
        // and the rendition service carve this exception out of their catch-all, and a third degradation
        // handler added later must do the same. The filter they use is `when (e is not …)`, so this asserts
        // the property that filter depends on — the refusal is not a subtype of anything they mean to absorb.
        var refusal = new EncryptedObjectWithoutEncryptionServiceException("k");

        Assert.IsNotType<OperationCanceledException>(refusal);
        Assert.IsAssignableFrom<InvalidOperationException>(refusal);

        // Deliberately NOT an ApiException: Infrastructure stays free of Api (the architecture tests enforce
        // it), which is why the boundary translates it by type instead.
        Assert.DoesNotContain("ApiException", refusal.GetType().BaseType!.Name, StringComparison.Ordinal);
    }
}
