#!/usr/bin/env bash
# Daily kiosk reset (ADR 0489). Nukes the app + infra data volumes and brings the stack back up fresh — so the
# demo tenant + sample tree are reseeded and yesterday's visitor changes are gone. Everything (Postgres,
# SeaweedFS, OpenSearch, OpenBao) is wiped together, so there's no OpenBao<->Postgres credential drift (both
# start empty). The Caddy Let's Encrypt certs live in ./caddy_data (a HOST bind-mount, not a named volume), so
# `down -v` preserves them and we never re-request certs (avoiding LE rate limits).
#
# Pulls :latest first, so pushing a new release refreshes the live demo within a day.
#
# Cron (daily 04:00):  0 4 * * * /opt/simplarchive/reset.sh >> /var/log/kiosk-reset.log 2>&1
set -euo pipefail
cd "$(dirname "$0")"

echo "=== kiosk reset $(date -u +%FT%TZ) ==="
docker compose down -v --remove-orphans
docker compose pull --quiet
docker compose up -d
echo "=== reset done; stack coming up ==="
