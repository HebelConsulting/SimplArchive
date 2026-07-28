#!/usr/bin/env bash
# Restores a SimplArchive backup (produced by scripts/backup.sh) into the local docker-compose stack. The
# Postgres/object-storage targets are configurable so a restore can be validated into a THROWAWAY database +
# bucket without clobbering live data (that's how the round-trip is verified). See docs/deploy/backup-dr.md
# for the DR runbook, restore order, and WORM/Object-Lock caveats.
#
# Usage: scripts/restore.sh <input_dir> [target_db] [target_bucket]
set -euo pipefail

if [ $# -lt 1 ]; then
  echo "Usage: $0 <input_dir> [target_db] [target_bucket]" >&2
  exit 1
fi

# --- config (override via env) ---------------------------------------------------------------------------
PG_USER="${PG_USER:-postgres}"
SA_NETWORK="${SA_NETWORK:-simplarchive_default}"
MINIO_URL="${MINIO_URL:-http://minio:9000}"
MINIO_USER="${MINIO_USER:-minioadmin}"
MINIO_PASS="${MINIO_PASS:-minioadmin}"

cd "$(dirname "$0")/.."
IN="$1"
TARGET_DB="${2:-simplarchive}"
TARGET_BUCKET="${3:-simplarchive}"
IN_ABS="$(cd "$IN" && pwd)"
echo "Restoring from $IN_ABS  ->  db=$TARGET_DB bucket=$TARGET_BUCKET"

# --- Postgres: (re)create the target DB, then restore ---------------------------------------------------
echo "==> Postgres ($TARGET_DB)"
# Separate -c invocations: DROP/CREATE DATABASE can't run inside a transaction block (a single -c with two
# statements would wrap them in one).
docker compose exec -T db psql -U "$PG_USER" -v ON_ERROR_STOP=1 \
  -c "DROP DATABASE IF EXISTS \"$TARGET_DB\" WITH (FORCE);" \
  -c "CREATE DATABASE \"$TARGET_DB\";" >/dev/null
docker compose exec -T db pg_restore -U "$PG_USER" -d "$TARGET_DB" --no-owner < "$IN/db.dump"
echo "    restored"

# --- Object storage: mirror the objects back into the target bucket -------------------------------------
echo "==> Object storage ($TARGET_BUCKET)"
docker run --rm --network "$SA_NETWORK" -v "$IN_ABS/minio:/backup" --entrypoint sh minio/mc -c \
  "mc alias set local $MINIO_URL $MINIO_USER $MINIO_PASS >/dev/null && mc mb --ignore-existing local/$TARGET_BUCKET >/dev/null && mc mirror --overwrite --quiet /backup local/$TARGET_BUCKET"
echo "    restored"

# --- OpenBao: raft snapshot restore (production only; not the dev -dev server) ---------------------------
if [ -f "$IN/openbao.snap" ]; then
  echo "==> OpenBao"
  echo "    a snapshot exists; restore it against a raft-backed OpenBao with:"
  echo "      bao operator raft snapshot restore openbao.snap   # then unseal"
  echo "    (not applicable to the dev -dev server)."
fi

echo "Done."
