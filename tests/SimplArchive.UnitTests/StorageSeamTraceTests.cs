using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.UnitTests;

// The presigned-URL line is the object-storage seam's only record of an exchange we never see the far side of
// (ADR 0626): the browser transfers the bytes directly, so if that stalls, this line is the sole evidence the
// address was ever issued. It therefore has to carry enough to identify the exchange — and none of the secret.
//
// A presigned URL's signature IS the credential. Anyone who can read this log must NOT be able to fetch the
// object with what they read. Worth a test rather than a careful reading, for the same reason the IMAP one is:
// the failure is silent, survives review, and is discovered by finding working credentials in a log aggregator.
public class StorageSeamTraceTests
{
    private const string Key = "tenants/8a1f6d3e-0000-4000-8000-000000000001/2026/b2c3d4e5-0000-4000-8000-000000000002/content.pdf";

    [Fact]
    public async Task A_presigned_address_is_logged_without_the_query_that_makes_it_work()
    {
        var (client, log) = Build();

        var url = await client.GetPresignedDownloadUrlAsync(Key, TimeSpan.FromMinutes(5), "Invoice 2026.pdf");

        // Anti-vacuous, and the assertion this whole test rests on: if the URL carried no signature there would
        // be no secret to leak, and every assertion below would pass while proving nothing.
        Assert.NotEqual(string.Empty, url.Query);
        var signature = System.Web.HttpUtility.ParseQueryString(url.Query)["X-Amz-Signature"];
        Assert.False(string.IsNullOrWhiteSpace(signature), $"expected a signed URL, got query '{url.Query}'");

        var logged = string.Join("\n", log);
        Assert.DoesNotContain(signature!, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(url.Query, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Amz-Signature", logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Amz-Credential", logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-key-value", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_line_still_says_which_object_was_addressed_and_until_when()
    {
        // The counterpart, and the one that keeps the redaction honest: logging nothing at all would pass the
        // test above and destroy the only reason this line exists. What survives must be enough to answer "did
        // we hand this user an address for that object, and had it expired?".
        var (client, log) = Build();

        await client.GetPresignedDownloadUrlAsync(Key, TimeSpan.FromMinutes(5), "Invoice 2026.pdf");

        var logged = string.Join("\n", log);
        Assert.Contains(Key, logged, StringComparison.Ordinal);
        Assert.Contains("simplarchive-8a1f6d3e-0000-4000-8000-000000000001", logged, StringComparison.Ordinal);
        Assert.Contains("storage.invalid", logged, StringComparison.Ordinal);
        Assert.Contains("GET", logged, StringComparison.Ordinal);
        Assert.Contains("300", logged, StringComparison.Ordinal); // the expiry, in seconds
    }

    [Fact]
    public async Task Nothing_is_logged_when_trace_is_off()
    {
        // Trace is off in every environment by default (ADR 0430). The seam's completeness must not cost
        // anything on the path every stored byte takes.
        var (client, log) = Build(LogLevel.Debug);

        await client.GetPresignedDownloadUrlAsync(Key, TimeSpan.FromMinutes(5), "Invoice 2026.pdf");

        Assert.Empty(log);
    }

    [Fact]
    public void A_marshalled_path_is_resolved_to_the_object_it_addresses()
    {
        // The SDK hands the path as a TEMPLATE with the values beside it, and its dictionary keys already carry
        // their braces. Wrapping them again matches nothing and leaves "/{Key+}" in the log — which looks like a
        // working trace and names no object at all. Measured against a real store; pinned here so it stays fixed.
        var resolved = S3WireTraceHandler.ResolvePath(
            "/{Key+}",
            new Dictionary<string, string> { ["{Key+}"] = "tenants/t/2026/g/content.pdf" });

        Assert.Equal("/tenants/t/2026/g/content.pdf", resolved);
        Assert.DoesNotContain("{", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_no_placeholders_is_left_alone()
        => Assert.Equal("/", S3WireTraceHandler.ResolvePath("/", new Dictionary<string, string>()));

    private static (S3ObjectStorageClient Client, List<string> Log) Build(LogLevel minimum = LogLevel.Trace)
    {
        var log = new List<string>();
        var options = Options.Create(new ObjectStorageOptions
        {
            // Never resolved: presigning is computed locally, so this test needs no endpoint and no network.
            ServiceUrl = "http://storage.invalid:8333",
            Region = "us-east-1",
            BucketName = "simplarchive",
            AccessKey = "access-key-value",
            SecretKey = "secret-key-value",
        });

        return (new S3ObjectStorageClient(options, new CapturingLogger<S3ObjectStorageClient>(log, minimum)), log);
    }

    private sealed class CapturingLogger<T>(List<string> sink, LogLevel minimum) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                sink.Add(formatter(state, exception));
            }
        }
    }
}
