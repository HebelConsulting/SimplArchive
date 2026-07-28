namespace SimplArchive.Infrastructure.Storage;

// Bound from the "ObjectStorage" configuration section — see ADR "Object storage client abstraction
// (foundation slice)". ServiceUrl points at whatever S3-compatible endpoint is configured for the
// deployment (self-hosted MinIO, AWS S3, etc. — see ADR "Document storage model"); Region is required by
// the AWS SDK's request-signing even against a non-AWS endpoint, so a placeholder like "us-east-1" is
// expected for MinIO.
public class ObjectStorageOptions
{
    // The endpoint the Api itself uses for server-side S3 calls (e.g. re-hashing the uploaded object at
    // finalize). In a split-network deployment (Docker Compose: the Api reaches MinIO at minio:9000, the
    // browser at localhost:9000) this is the internal address.
    public required string ServiceUrl { get; set; }

    // Optional: the endpoint used only to *sign presigned URLs*, which are handed to the browser and must
    // therefore point at an address the browser can reach. Falls back to ServiceUrl when unset — so
    // single-endpoint deployments (and every existing test) are unaffected. See ADR "Local development
    // Docker Compose stack".
    public string? PublicServiceUrl { get; set; }

    public required string Region { get; set; }

    // Legacy single shared bucket (the migration source under ADR "Per-tenant object-storage bucket") + the
    // default bucket-name prefix. No longer the operational bucket: each tenant's objects live in its own bucket
    // `{BucketPrefix}-{tenantId}`.
    public required string BucketName { get; set; }

    // The per-tenant bucket-name prefix (ADR "Per-tenant object-storage bucket") — a tenant's bucket is
    // `{BucketPrefix}-{tenantId}`. Falls back to BucketName when unset, so existing config needs no change
    // (BucketName "simplarchive" → buckets "simplarchive-{tenantId}").
    public string? BucketPrefix { get; set; }

    public required string AccessKey { get; set; }

    public required string SecretKey { get; set; }
}
