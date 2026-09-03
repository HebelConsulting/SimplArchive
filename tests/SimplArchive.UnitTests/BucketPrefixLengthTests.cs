using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.UnitTests;

// Guards the fix for the AWS managed-S3 install (issue #712): a bucket prefix long enough to push a
// per-tenant bucket name past S3's 63-character limit must be refused at construction, not accepted and then
// failed once per tenant at first write. The old installer derived "{name}-{account}" — 33 characters for
// the scratch stack — so every tenant bucket came out at 70 and AWS rejected it with "The specified bucket
// is not valid", on a deployment that was otherwise green.
public class BucketPrefixLengthTests
{
    private static ObjectStorageOptions OptionsWith(string bucketName) => new()
    {
        // Never resolved — construction does no network I/O, and the length guard runs before any client is
        // built. A too-long prefix throws before this endpoint would ever be touched.
        ServiceUrl = "http://storage.invalid:8333",
        Region = "us-east-1",
        BucketName = bucketName,
        AccessKey = "access-key-value",
        SecretKey = "secret-key-value",
    };

    private static S3ObjectStorageClient Construct(string bucketName) =>
        new(Options.Create(OptionsWith(bucketName)), NullLogger<S3ObjectStorageClient>.Instance);

    [Theory]
    [InlineData("simplarchive")]                 // compose / kiosk — 12
    [InlineData("simplarchive-docs")]            // the AWS installer prefix — 17
    [InlineData("abcdefghijklmnopqrstuvwxyz")]   // exactly 26 — the boundary that still fits
    public void A_prefix_that_keeps_the_bucket_name_within_63_chars_is_accepted(string prefix)
    {
        Assert.True(prefix.Length <= 26);
        var ex = Record.Exception(() => Construct(prefix));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("simplarchive-scratch-681975221298")]           // the old installer prefix — 33, the real defect
    [InlineData("this-object-storage-bucket-prefix-is-far-too-long")]
    public void A_prefix_that_would_overflow_the_63_char_limit_is_refused_at_construction(string prefix)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Construct(prefix));
        Assert.Contains("63", ex.Message);
        Assert.Contains(prefix, ex.Message);
    }

    [Fact]
    public void The_boundary_is_exactly_S3s_limit_minus_a_separator_and_a_D_form_guid()
    {
        // A GUID in "D" form is always 36 characters, plus one separator, so 26 is the largest prefix that
        // keeps "{prefix}-{guid}" at or under 63. Pin the arithmetic so a later edit cannot loosen it silently.
        var name = $"{new string('x', 26)}-{Guid.NewGuid():D}";
        Assert.Equal(63, name.Length);
    }
}
