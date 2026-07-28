#!/usr/bin/env bash
# One-time migration to per-tenant object-storage buckets (docs/adr/0372-per-tenant-object-storage-bucket.md).
# Before this change, every tenant's objects lived in a single shared bucket under the key prefix
# `tenants/{tenantId}/...`. Now each tenant has its own bucket `{PREFIX}-{tenantId}` (object-lock-enabled + CORS),
# with the SAME keys inside it. This script, for every tenant found in the shared bucket, creates its per-tenant
# bucket and copies its objects across — the object keys are unchanged, so it's a straight copy.
#
# Idempotent + non-destructive: it re-creates buckets only when missing (object lock can only be enabled at
# creation) and leaves the shared-bucket copies in place. Verify the new buckets, then delete the shared objects
# yourself once satisfied. Uses the engine-neutral AWS CLI (path-style S3), like storage-init/backup.sh.
#
# Usage (dev compose):   scripts/migrate-to-per-tenant-buckets.sh
# Usage (production):    point S3_ENDPOINT / AWS creds at the real endpoint and run once.
set -euo pipefail

# --- config (override via env) ---------------------------------------------------------------------------
S3_ENDPOINT="${S3_ENDPOINT:-http://localhost:8333}"   # dev: SeaweedFS S3 on the host
SHARED_BUCKET="${SHARED_BUCKET:-simplarchive}"        # the legacy single bucket (== the new bucket prefix)
BUCKET_PREFIX="${BUCKET_PREFIX:-$SHARED_BUCKET}"       # per-tenant bucket = {PREFIX}-{tenantId}
export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-${S3_ACCESS_KEY:-storageadmin}}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-${S3_SECRET_KEY:-storageadmin}}"
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"

aws configure set default.s3.addressing_style path
awss3api() { aws --endpoint-url "$S3_ENDPOINT" s3api "$@"; }
awss3()    { aws --endpoint-url "$S3_ENDPOINT" s3 "$@"; }

echo "==> Discovering tenants in s3://$SHARED_BUCKET/tenants/"
# The common prefixes under tenants/ are the tenant ids.
tenants=$(awss3api list-objects-v2 --bucket "$SHARED_BUCKET" --prefix "tenants/" --delimiter "/" \
  --query 'CommonPrefixes[].Prefix' --output text 2>/dev/null | tr '\t' '\n' | sed -n 's#tenants/\(.*\)/#\1#p' || true)

if [ -z "${tenants}" ]; then
  echo "    no tenants found under the shared bucket — nothing to migrate."
  exit 0
fi

for tenant in $tenants; do
  bucket="${BUCKET_PREFIX}-${tenant}"
  echo "==> Tenant $tenant -> bucket $bucket"

  if awss3api head-bucket --bucket "$bucket" 2>/dev/null; then
    echo "    bucket exists (object lock already configured); skipping create"
  else
    echo "    creating object-lock-enabled bucket"
    awss3api create-bucket --bucket "$bucket" --object-lock-enabled-for-bucket
  fi

  # Browser CORS (re)applied every run — presigned PUT/GET go direct to the tenant bucket.
  awss3api put-bucket-cors --bucket "$bucket" \
    --cors-configuration 'CORSRules=[{AllowedOrigins=["*"],AllowedMethods=[GET,PUT,HEAD],AllowedHeaders=["*"]}]'

  # Copy this tenant's objects across, keys unchanged (tenants/{tenant}/... -> same key in the tenant bucket).
  echo "    copying objects (keys unchanged)…"
  awss3 cp --recursive "s3://$SHARED_BUCKET/tenants/$tenant/" "s3://$bucket/tenants/$tenant/"
done

echo "==> Done. Verify the per-tenant buckets, then remove the shared-bucket copies once satisfied:"
echo "    aws --endpoint-url $S3_ENDPOINT s3 rm --recursive s3://$SHARED_BUCKET/tenants/"
