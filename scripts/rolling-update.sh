#!/usr/bin/env bash
# Replace the app one instance at a time, so an update is not an outage (#1245, ADR 0808).
#
# WHY A SCRIPT AND NOT `docker compose up -d`. Compose recreates every changed service at once. With two api
# instances that means both go down together, which is exactly the outage this exists to remove. The order —
# migrate once, then one instance at a time, waiting for each to report ready before touching the next — is the
# whole value, and it is not expressible in the compose file.
#
# THIS SCRIPT IS THE READINESS GATE, not the proxy. Caddy gates HTTP on /health/ready, but its layer-4 IMAP
# proxy can only make a TCP connection attempt (caddy-l4 has no health_uri). So "is the new instance actually
# serving?" is answered here, by polling the instance itself, before the other one is allowed to stop.
#
# USAGE
#   scripts/rolling-update.sh                 # dev stack: build, migrate, roll both instances
#   scripts/rolling-update.sh --no-build      # roll what is already built (a config-only change)
#   SA_COMPOSE="docker compose -f a.yml -f b.yml" scripts/rolling-update.sh     # the kiosk's file set
#
# Written for bash 3.2 (the macOS default): no `mapfile`, no `declare -A`, no `timeout`.
set -euo pipefail

COMPOSE=${SA_COMPOSE:-docker compose}
INSTANCES=${SA_INSTANCES:-"api api-b"}
READY_TIMEOUT=${SA_READY_TIMEOUT:-180}
BUILD=1

for arg in "$@"; do
  case "$arg" in
    --no-build) BUILD=0 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

say() { printf '==> %s\n' "$*"; }

# Poll ONE instance's own /health/ready from inside its container. It publishes no host port — the proxy owns
# the entry point — so there is no outside address to curl, and asking through the proxy would be worthless
# anyway: the proxy would answer from the OTHER instance and the check would pass while this one was dead.
wait_ready() {
  svc=$1
  waited=0
  while [ "$waited" -lt "$READY_TIMEOUT" ]; do
    if $COMPOSE exec -T "$svc" curl -fsS http://localhost:8080/health/ready >/dev/null 2>&1; then
      say "$svc is ready (${waited}s)"
      return 0
    fi
    sleep 2
    waited=$((waited + 2))
  done
  echo "FAILED: $svc did not become ready within ${READY_TIMEOUT}s." >&2
  echo "The other instance is still serving, so this is a failed update rather than an outage." >&2
  echo "Leaving it as it is — inspect with: $COMPOSE logs --tail=100 $svc" >&2
  return 1
}

# What modules an instance actually loaded, as a stable fingerprint of its Modules directory.
#
# This exists because of a real trap: the flight-school overrides used to mount the module into the `api`
# service ONLY. Add a second instance and you get one container with the module and one without, behind a
# proxy that round-robins between them — so roughly half of all requests 404 MODULE_NOT_ACTIVE, which reads
# as a module bug rather than a topology one. The overrides now populate the shared volume instead, and this
# check is what makes a regression loud instead of intermittent.
module_fingerprint() {
  $COMPOSE exec -T "$1" sh -c \
    'find /app/Modules -type f -name "*.dll" 2>/dev/null | sort | sha256sum' 2>/dev/null | awk '{print $1}'
}

say "rolling update over: $INSTANCES"

if [ "$BUILD" -eq 1 ]; then
  say "building"
  $COMPOSE build
fi

# Migrations ONCE, before any instance restarts. Both instances have startup auto-migration off, so nothing
# else applies them — and two instances racing to migrate is what that setting exists to prevent.
say "applying migrations (one-shot)"
$COMPOSE up -d --force-recreate --no-deps db-migrate
migrate_id=$($COMPOSE ps -q db-migrate)
if [ -z "$migrate_id" ]; then
  echo "FAILED: the db-migrate container did not start; nothing has been changed." >&2
  exit 1
fi
migrate_status=$(docker wait "$migrate_id")
if [ "$migrate_status" != "0" ]; then
  echo "FAILED: migrations exited $migrate_status. No instance has been touched." >&2
  echo "$($COMPOSE logs --tail=50 db-migrate)" >&2
  exit 1
fi
say "migrations applied"

# One at a time. The instance not being replaced keeps serving throughout, which is the point.
for svc in $INSTANCES; do
  say "replacing $svc"
  $COMPOSE up -d --force-recreate --no-deps "$svc"
  wait_ready "$svc"
done

# Both are up: they must agree about what they are. A difference here is served to users at random.
first=""
first_svc=""
for svc in $INSTANCES; do
  fp=$(module_fingerprint "$svc")
  if [ -z "$first" ]; then
    first=$fp
    first_svc=$svc
  elif [ "$fp" != "$first" ]; then
    echo "FAILED: $svc and $first_svc do not have the same modules installed." >&2
    echo "Both instances are running and serving, so this is not an outage — but they are serving" >&2
    echo "DIFFERENT applications, and a client reaches one of them at random. Expect intermittent" >&2
    echo "404 MODULE_NOT_ACTIVE until it is fixed." >&2
    echo "Usual cause: an override that mounts a module into one service instead of populating the" >&2
    echo "shared modules volume. Compare: $COMPOSE exec $first_svc ls /app/Modules" >&2
    exit 1
  fi
done
say "instances agree on their module set"

say "done — updated without dropping the entry point"
