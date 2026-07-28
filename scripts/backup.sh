#!/usr/bin/env bash
# Backs up SimplArchive's three stateful stores (Postgres, object storage, OpenBao) from the local
# docker-compose stack into a timestamped directory. First slice of the backup/DR story
# (docs/adr/0344-backup-and-disaster-recovery.md); see docs/deploy/backup-dr.md for the runbook and how to
# adapt these commands for a managed/production deployment (point the same tools at the real endpoints).
#
# Usage: scripts/backup.sh [output_dir]
set -euo pipefail

# --- config (override via env) ---------------------------------------------------------------------------
PG_USER="${PG_USER:-postgres}"
PG_DB="${PG_DB:-simplarchive}"
S3_BUCKET="${S3_BUCKET:-simplarchive}"
SA_NETWORK="${SA_NETWORK:-simplarchive_default}"
MINIO_URL="${MINIO_URL:-http://minio:9000}"
MINIO_USER="${MINIO_USER:-minioadmin}"
MINIO_PASS="${MINIO_PASS:-minioadmin}"

cd "$(dirname "$0")/.."
OUT="${1:-backups/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT/minio"
OUT_ABS="$(cd "$OUT" && pwd)"
echo "Backing up to $OUT_ABS"

# --- Postgres: a custom-format dump (pg_restore-friendly) ------------------------------------------------
echo "==> Postgres ($PG_DB)"
docker compose exec -T db pg_dump -U "$PG_USER" -Fc "$PG_DB" > "$OUT/db.dump"
echo "    $(du -h "$OUT/db.dump" | cut -f1) -> db.dump"

# --- Object storage: mirror the bucket (a mc container on the compose network) ---------------------------
echo "==> Object storage ($S3_BUCKET)"
docker run --rm --network "$SA_NETWORK" -v "$OUT_ABS/minio:/backup" --entrypoint sh minio/mc -c \
  "mc alias set local $MINIO_URL $MINIO_USER $MINIO_PASS >/dev/null && mc mirror --overwrite --quiet local/$S3_BUCKET /backup"
echo "    $(find "$OUT/minio" -type f | wc -l | tr -d ' ') object(s) mirrored"

# --- OpenBao: a raft snapshot (production/raft only; the dev -dev server is in-memory) -------------------
echo "==> OpenBao"
if docker compose exec -T openbao sh -c "BAO_ADDR=http://127.0.0.1:8200 BAO_TOKEN=root bao operator raft snapshot save /tmp/bao.snap" >/dev/null 2>&1; then
  docker compose exec -T openbao cat /tmp/bao.snap > "$OUT/openbao.snap"
  echo "    $(du -h "$OUT/openbao.snap" | cut -f1) -> openbao.snap"
else
  echo "    skipped: OpenBao is not running or is a dev (-dev, in-memory) server with no raft storage."
  echo "    In production run against a raft-backed OpenBao; in dev, re-provision via 'docker compose up openbao-init'."
fi

echo "Done. Backup at $OUT_ABS"
