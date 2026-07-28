using Amazon.Runtime;
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

        var publicServiceUrl = string.IsNullOrWhiteSpace(value.PublicServiceUrl) ? value.ServiceUrl : value.PublicServiceUrl;
        _presignUseHttp = publicServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        _internalClient = CreateClient(value.ServiceUrl, value);
        _presignClient = ReferenceEquals(publicServiceUrl, value.ServiceUrl) || publicServiceUrl == value.ServiceUrl
            ? _internalClient
            : CreateClient(publicServiceUrl, value);
    }

    // The bucket an object key/prefix belongs to (ADR "Per-tenant object-storage bucket"). Every key/prefix is
    // tenant-scoped — `tenants/{tenantId}/...` (ObjectKeyBuilder, CheckoutStashKey, AuditWormArchiver, the inbox
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
                        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "" } },
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = incompleteUploadCleanupDays },
                    },
                ],
            },
        }, cancellationToken);
    }

    private static IAmazonS3 CreateClient(string serviceUrl, ObjectStorageOptions options)
    {
        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                AuthenticationRegion = options.Region,
                ForcePathStyle = true,
                UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            });
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

        return new Uri(url);
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
                objects.Add(new StorageObject(s3Object.Key, s3Object.Size ?? 0, s3Object.LastModified ?? default));
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
        }

        return new ObjectLockStatus(retainUntil, legalHold);
    }

    // A "does this object have a lock?" GET returns an error when the object has no such configuration yet.
    // S3/MinIO signal it inconsistently — a 404, or a 4xx with ErrorCode NoSuchObjectLockConfiguration, or a
    // MinIO message like "The specified object does not have a ObjectLock configuration". All mean "no lock".
    private static bool IsNoLockConfiguration(AmazonS3Exception e) =>
        e.StatusCode == System.Net.HttpStatusCode.NotFound
        || string.Equals(e.ErrorCode, "NoSuchObjectLockConfiguration", StringComparison.OrdinalIgnoreCase)
        || (e.Message?.Contains("ObjectLock configuration", StringComparison.OrdinalIgnoreCase) ?? false);
}
