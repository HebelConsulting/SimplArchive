using System.Linq;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// See ADR "Object storage client abstraction (foundation slice)". Talks to whatever S3-compatible
// endpoint ObjectStorageOptions points at via the AWS SDK — ForcePathStyle is required for
// non-AWS-hosted, self-signed-cert-free endpoints like MinIO, which don't support the SDK's default
// virtual-hosted-style bucket addressing.
public class S3ObjectStorageClient : IObjectStorageClient
{
    // Two clients differing only in ServiceURL: _internalClient for the Api's own server-side calls
    // (GetObjectAsync), _presignClient for signing presigned URLs the browser uses. They're the same
    // instance unless PublicServiceUrl differs from ServiceUrl (a split-network deployment — see ADR
    // "Local development Docker Compose stack"). Presigning uses the client config's ServiceURL to build
    // the URL, so the host baked into a presigned URL is whichever client signs it.
    private readonly IAmazonS3 _internalClient;
    private readonly IAmazonS3 _presignClient;
    private readonly string _bucketPrefix;
    private readonly bool _presignUseHttp;
    private readonly ILogger<S3ObjectStorageClient> _logger;

    public S3ObjectStorageClient(IOptions<ObjectStorageOptions> options, ILogger<S3ObjectStorageClient> logger)
    {
        _logger = logger;
        var value = options.Value;
        // Each tenant has its own bucket `{prefix}-{tenantId}` (ADR "Per-tenant object-storage bucket"); the
        // prefix falls back to the legacy BucketName so existing config is unchanged.
        _bucketPrefix = string.IsNullOrWhiteSpace(value.BucketPrefix) ? value.BucketName : value.BucketPrefix;

        // Fail fast on a prefix that cannot produce a valid bucket name, rather than per tenant at first
        // write. S3 caps a bucket name at 63 characters and a per-tenant name is `{prefix}-{tenantId:D}`,
        // where the GUID's :D form is always 36 — so the prefix has 63 - 1 - 36 = 26 to spend. A longer one
        // (the AWS installer once derived a 33-char "{name}-{account}" prefix) is accepted by SeaweedFS but
        // rejected by S3 with "The specified bucket is not valid", once per tenant, at provisioning time.
        // Turning that into a startup error that names the limit is the difference between a five-minute fix
        // and a stack that deploys green and fails only when someone creates their first repository.
        const int maxBucketPrefixLength = 63 - 1 - 36;
        if (_bucketPrefix.Length > maxBucketPrefixLength)
        {
            throw new InvalidOperationException(
                $"ObjectStorage bucket prefix '{_bucketPrefix}' is {_bucketPrefix.Length} characters; it must "
                + $"be at most {maxBucketPrefixLength} so a per-tenant bucket name stays within S3's "
                + "63-character limit.");
        }

        var publicServiceUrl = string.IsNullOrWhiteSpace(value.PublicServiceUrl) ? value.ServiceUrl : value.PublicServiceUrl;
        _presignUseHttp = publicServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        _internalClient = CreateClient(value.ServiceUrl, value, logger);
        _presignClient = ReferenceEquals(publicServiceUrl, value.ServiceUrl) || publicServiceUrl == value.ServiceUrl
            ? _internalClient
            : CreateClient(publicServiceUrl, value, logger);
    }

    // The bucket an object key/prefix belongs to (ADR "Per-tenant object-storage bucket"). Every key/prefix is
    // tenant-scoped — `tenants/{tenantId}/...` (ObjectKeyBuilder, CheckoutStashKey, AuditWormArchiver, the intray
    // prefix, and derived artifacts inherit it) — so the tenant, and thus the bucket, is derived from the key with
    // no call-site changes. A non-tenant-scoped key is a programming error.
    private string BucketFor(string keyOrPrefix)
    {
        const string root = "tenants/";
        if (keyOrPrefix.StartsWith(root, StringComparison.Ordinal))
        {
            var rest = keyOrPrefix.AsSpan(root.Length);
            var slash = rest.IndexOf('/');
            var segment = slash >= 0 ? rest[..slash] : rest;
            if (Guid.TryParse(segment, out var tenantId))
            {
                return $"{_bucketPrefix}-{tenantId:D}";
            }
        }

        throw new InvalidOperationException(
            $"Object key '{keyOrPrefix}' is not tenant-scoped (expected 'tenants/{{tenantId}}/...').");
    }

    public string BucketForTenant(Guid tenantId) => $"{_bucketPrefix}-{tenantId:D}";

    public async Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var bucket = BucketForTenant(tenantId);

        // Idempotent: create only on a fresh bucket (object lock can only be enabled at creation). CORS is
        // (re)applied every call so the browser can PUT/GET presigned URLs directly against the tenant bucket.
        if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_internalClient, bucket))
        {
            await _internalClient.PutBucketAsync(new PutBucketRequest { BucketName = bucket, ObjectLockEnabledForBucket = true }, cancellationToken);
        }

        await _internalClient.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = ["*"],
                        AllowedMethods = ["GET", "PUT", "HEAD"],
                        AllowedHeaders = ["*"],
                    },
                ],
            },
        }, cancellationToken);

        // Durable ops tags (ADR "Per-tenant bucket policy knobs") — the tenant id (stable; the name is mutable, so
        // not tagged) + the application marker, for identifying/filtering buckets in an object-store console.
        await _internalClient.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet =
            [
                new Tag { Key = "tenant-id", Value = tenantId.ToString("D") },
                new Tag { Key = "application", Value = "simplarchive" },
            ],
        }, cancellationToken);
    }

    public async Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default)
    {
        var bucket = BucketForTenant(tenantId);

        if (incompleteUploadCleanupDays <= 0)
        {
            // Disabled: remove any existing lifecycle configuration. A missing configuration is not an error.
            try
            {
                await _internalClient.DeleteLifecycleConfigurationAsync(new DeleteLifecycleConfigurationRequest { BucketName = bucket }, cancellationToken);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }

            return;
        }

        await _internalClient.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "abort-incomplete-multipart-uploads",
                        Status = LifecycleRuleStatus.Enabled,
                        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = string.Empty } },
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = incompleteUploadCleanupDays },
                    },
                ],
            },
        }, cancellationToken);
    }

    private static IAmazonS3 CreateClient(string serviceUrl, ObjectStorageOptions options, ILogger logger)
    {
        return new TracedAmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                AuthenticationRegion = options.Region,
                ForcePathStyle = true,
                UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            },
            logger);
    }

    // Installs the wire trace in the SDK's own pipeline (ADR 0626). Subclassing is the supported seam for this —
    // CustomizeRuntimePipeline is the only place a handler can be added to a client the SDK builds for us.
    private sealed class TracedAmazonS3Client : AmazonS3Client
    {
        private readonly ILogger _logger;

        public TracedAmazonS3Client(AWSCredentials credentials, AmazonS3Config config, ILogger logger)
            : base(credentials, config)
            => _logger = logger;

        protected override void CustomizeRuntimePipeline(RuntimePipeline pipeline)
        {
            base.CustomizeRuntimePipeline(pipeline);

            // After the SIGNER, which is the last handler before the request goes out. Anchoring earlier was
            // tried and measured: after the marshaller the endpoint is still unresolved and the resource path is
            // the TEMPLATE ("/{Key+}"), so the trace named neither the host nor the object — it described our
            // intent again, which is the one thing this handler exists not to do.
            //
            // The consequence is that the Authorization header IS present by the time we see the request. That
            // is precisely why the header list is a whitelist: at this position, a blacklist would be one SDK
            // release away from writing a signed credential into the log.
            pipeline.AddHandlerAfter<Signer>(new S3WireTraceHandler(() => _logger));
        }
    }

    public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default)
        => GetPresignedUrlAsync(objectKey, HttpVerb.PUT, expiry, contentDisposition: null, contentType: null, cancellationToken);

    public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default)
        => GetPresignedUrlAsync(objectKey, HttpVerb.GET, expiry, Disposition("attachment", downloadFileName), contentType: null, cancellationToken);

    public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default)
        => GetPresignedUrlAsync(objectKey, HttpVerb.GET, expiry, Disposition("inline", fileName), contentType, cancellationToken);

    // Content-Disposition value for the response-content-disposition override. RFC 5987 filename* handles
    // spaces/unicode; NO space after the ';' — the SDK leaves a literal space unencoded in the query string,
    // producing a malformed URL (RFC 6266 allows it omitted). See ADR "Download filename from Short
    // Description". "attachment" forces a download; "inline" lets the browser render it (used for preview).
    private static string Disposition(string type, string? fileName)
        => string.IsNullOrWhiteSpace(fileName) ? type : $"{type};filename*=UTF-8''{Uri.EscapeDataString(fileName)}";

    private async Task<Uri> GetPresignedUrlAsync(string objectKey, HttpVerb verb, TimeSpan expiry, string? contentDisposition, string? contentType, CancellationToken cancellationToken)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = BucketFor(objectKey),
            Key = objectKey,
            Verb = verb,
            Expires = DateTime.UtcNow.Add(expiry),
            // GetPreSignedUrlRequest.Protocol defaults to HTTPS regardless of AmazonS3Config.UseHttp/
            // ServiceURL's own scheme — without setting it explicitly, a plain "http://" endpoint (e.g.
            // MinIO without TLS) still gets an "https://" presigned URL and fails to connect.
            Protocol = _presignUseHttp ? Protocol.HTTP : Protocol.HTTPS,
        };

        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            request.ResponseHeaderOverrides.ContentDisposition = contentDisposition;
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            request.ResponseHeaderOverrides.ContentType = contentType;
        }

        var url = await _presignClient.GetPreSignedURLAsync(request);
        var uri = new Uri(url);

        // The one seam whose far side we NEVER see. Every other storage call goes through this process, so the
        // pipeline trace records both halves; here we hand the caller an address and the transfer happens
        // browser↔store, invisible to us (ADR 0006 — the Api never proxies file bytes). If that transfer stalls,
        // this line is the only record that we ever issued the address at all, so it carries what identifies the
        // exchange: which object, which verb, which host, and until when the address is valid.
        //
        // The QUERY STRING IS NEVER LOGGED, and that is not a detail — a presigned URL's signature IS the
        // credential. Anyone holding this line's output must be unable to fetch the object with it. So the parts
        // are whitelisted individually rather than the URL being stripped of the parameters we happen to know
        // about, which would leak the day the SDK adds one.
        _logger.LogTrace(
            "Issued a presigned {Verb} address for {ObjectKey} in {Bucket} via {Scheme}://{Host}{Path}, valid until "
            + "{ExpiresAt:O} ({ExpirySeconds}s); disposition {HasDisposition}, content type {HasContentType}",
            verb,
            objectKey,
            request.BucketName,
            uri.Scheme,
            uri.Authority,
            uri.AbsolutePath,
            request.Expires,
            (long)expiry.TotalSeconds,
            contentDisposition is null ? "none" : "set",
            contentType ?? "none");

        return uri;
    }

    public async Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting object {ObjectKey} from storage.", objectKey);
        var response = await _internalClient.GetObjectAsync(BucketFor(objectKey), objectKey, cancellationToken);

        return response.ResponseStream;
    }

    public async Task<Stream> GetObjectRangeAsync(string objectKey, long from, long to, CancellationToken cancellationToken = default)
    {
        var request = new GetObjectRequest
        {
            BucketName = BucketFor(objectKey),
            Key = objectKey,
            ByteRange = new ByteRange(from, to), // inclusive
        };
        var response = await _internalClient.GetObjectAsync(request, cancellationToken);
        return response.ResponseStream;
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await _internalClient.GetObjectMetadataAsync(BucketFor(objectKey), objectKey, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var metadata = await _internalClient.GetObjectMetadataAsync(BucketFor(objectKey), objectKey, cancellationToken);
        return metadata.ContentLength;
    }

    public async Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Putting object {ObjectKey} ({ContentType}) to storage.", objectKey, contentType);
        await _internalClient.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = BucketFor(objectKey),
                Key = objectKey,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var objects = new List<StorageObject>();
        string? continuationToken = null;

        do
        {
            var response = await _internalClient.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = BucketFor(prefix), Prefix = prefix, ContinuationToken = continuationToken },
                cancellationToken);

            foreach (var s3Object in response.S3Objects ?? [])
            {
                objects.Add(new StorageObject(s3Object.Key, s3Object.Size ?? 0, s3Object.LastModified ?? default, s3Object.ETag));
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        return objects;
    }

    public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Copying object {SourceKey} to {DestinationKey} in storage.", sourceKey, destinationKey);
        return _internalClient.CopyObjectAsync(
            new CopyObjectRequest
            {
                SourceBucket = BucketFor(sourceKey),
                SourceKey = sourceKey,
                DestinationBucket = BucketFor(destinationKey),
                DestinationKey = destinationKey,
            },
            cancellationToken);
    }

    public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting object {ObjectKey} from storage.", objectKey);
        return _internalClient.DeleteObjectAsync(BucketFor(objectKey), objectKey, cancellationToken);
    }

    // ---- WORM / S3 Object Lock (ADR "WORM / immutable document versions") ----

    public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, SimplArchive.Domain.Tenants.WormLockMode mode, CancellationToken cancellationToken = default)
        => _internalClient.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = BucketFor(objectKey),
            Key = objectKey,
            Retention = new ObjectLockRetention
            {
                Mode = mode == SimplArchive.Domain.Tenants.WormLockMode.Compliance
                    ? ObjectLockRetentionMode.Compliance
                    : ObjectLockRetentionMode.Governance,
                RetainUntilDate = retainUntil.UtcDateTime,
            },
        }, cancellationToken);

    public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default)
        => _internalClient.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = BucketFor(objectKey),
            Key = objectKey,
            LegalHold = new ObjectLockLegalHold
            {
                Status = held ? ObjectLockLegalHoldStatus.On : ObjectLockLegalHoldStatus.Off,
            },
        }, cancellationToken);

    public async Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        // "No lock configuration" and "no such object" arrive as the SAME 404, and answering the second one
        // "unlocked" is the most consequential sentence this class can get wrong (ADR 0702). The stores disagree
        // about which shape they use — some send NoSuchObjectLockConfiguration, some a bare 404 — so the status
        // alone cannot separate them and NARROWING the predicate would turn genuinely unlocked objects into
        // failures on the stores that use the bare form. Asking whether the object exists is the only thing that
        // actually distinguishes the two.
        //
        // The cost lands only on the 404 path, which is already the exceptional one; a locked or configured
        // object never pays it.
        var seenNotFound = false;

        DateTimeOffset? retainUntil = null;
        try
        {
            var retention = await _internalClient.GetObjectRetentionAsync(new GetObjectRetentionRequest
            {
                BucketName = BucketFor(objectKey),
                Key = objectKey,
            }, cancellationToken);
            if (retention.Retention?.RetainUntilDate is { } until)
            {
                retainUntil = new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc));
            }
        }
        catch (AmazonS3Exception e) when (IsNoLockConfiguration(e))
        {
            // No retention configuration on this object — leave retainUntil null.
            seenNotFound |= e.StatusCode == System.Net.HttpStatusCode.NotFound;
            WarnLockLookupSwallowed(objectKey, "retention", e);
        }

        var legalHold = false;
        try
        {
            var hold = await _internalClient.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest
            {
                BucketName = BucketFor(objectKey),
                Key = objectKey,
            }, cancellationToken);
            legalHold = hold.LegalHold?.Status == ObjectLockLegalHoldStatus.On;
        }
        catch (AmazonS3Exception e) when (IsNoLockConfiguration(e))
        {
            // No legal hold on this object.
            seenNotFound |= e.StatusCode == System.Net.HttpStatusCode.NotFound;
            WarnLockLookupSwallowed(objectKey, "legal hold", e);
        }

        // Only when a 404 was actually seen: if the object is gone, say so rather than describing it as an
        // object that exists and happens to carry nothing.
        if (seenNotFound && !await ExistsAsync(objectKey, cancellationToken))
        {
            throw new StorageObjectNotFoundException(objectKey);
        }

        return new ObjectLockStatus(retainUntil, legalHold);
    }

    // A fall-through that the caller cannot see the reason for (ADR 0626). The answer we return —"no retention",
    // "no legal hold" — is indistinguishable from the answer for an object that genuinely has neither, and the
    // predicate below treats a bare 404 as "no lock configuration", which a MISSING OBJECT also produces. So a
    // lock question asked about an object that is not there is answered "unlocked", confidently and wrongly.
    //
    // Warning rather than Debug because nothing else surfaces it: the caller receives a well-formed answer and
    // has no way to tell it apart from the truth. The line names the switch, because an administrator who reads
    // "swallowed" still has to guess which knob shows them the exchange.
    private void WarnLockLookupSwallowed(string objectKey, string lookup, AmazonS3Exception e) =>
        _logger.LogWarning(
            "Object storage answered {StatusCode}/{ErrorCode} to the {Lookup} lookup for {ObjectKey}; treating it "
            + "as \"not set\" unless the object turns out to be absent, which this status cannot distinguish on "
            + "its own. Enable Trace on {TraceSource} to see the exchange.",
            (int)e.StatusCode,
            e.ErrorCode ?? "(none)",
            lookup,
            objectKey,
            typeof(S3ObjectStorageClient).FullName);

    // A "does this object have a lock?" GET returns an error when the object has no such configuration yet.
    // S3/MinIO signal it inconsistently — a 404, or a 4xx with ErrorCode NoSuchObjectLockConfiguration, or a
    // MinIO message like "The specified object does not have a ObjectLock configuration". All mean "no lock".
    private static bool IsNoLockConfiguration(AmazonS3Exception e) =>
        e.StatusCode == System.Net.HttpStatusCode.NotFound
        || string.Equals(e.ErrorCode, "NoSuchObjectLockConfiguration", StringComparison.OrdinalIgnoreCase)
        || (e.Message?.Contains("ObjectLock configuration", StringComparison.OrdinalIgnoreCase) ?? false);
}
