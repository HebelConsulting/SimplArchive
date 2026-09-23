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
#   SA_SIDECARS="encryption" scripts/rolling-update.sh                          # narrow the sidecar set
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

# The project's services, resolved ONCE, and matched below with `case` rather than a pipeline.
#
# `$COMPOSE config --services | grep -qx "$svc"` looks obviously correct and is not. `grep -q` exits at its
# FIRST match, which SIGPIPEs the compose process upstream, and under `set -o pipefail` the pipeline then
# reports that signal instead of grep's success — so a service that IS in the project reads as absent, at
# random. Measured on the kiosk over 20 attempts each: encryption 5/20, ocr 16/20, postfix 6/20 wrongly
# reported absent. Every one of those skips was SILENT, so a rollout could decline to roll a sidecar, or
# skip the module install entirely, and still report success. `case` uses no pipe and cannot do this.
PROJECT_SERVICES=" $($COMPOSE config --services 2>/dev/null | tr '\n' ' ' || true) "

in_project() {
  case "$PROJECT_SERVICES" in
    *" $1 "*) return 0 ;;
    *)        return 1 ;;
  esac
}

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

# Module assemblies into the shared volume, BEFORE anything is replaced (#1310).
#
# Every `up -d` below carries --no-deps, which is what makes the cutover one-at-a-time instead of letting
# compose recreate everything at once. The cost is that compose then starts no dependencies either — so the
# `modules-local` one-shot, which copies the host's module directory into the volume both instances read,
# never ran. A module updated on the host was therefore NEVER deployed by this script: the instances
# restarted, re-read the volume, and loaded the previous assembly.
#
# That failure was silent in every direction. The file on the host was visibly new, the instances came up
# healthy, and the module-set check below passed — because both instances agreed, on the stale build. Only
# the build sha in the startup log said otherwise, and only to someone who thought to look. It is the same
# shape as #1242: a stale module still loads, seeds its masks and answers requests, so what is missing reads
# as "never implemented" rather than "not deployed".
#
# Safe to run while an instance is serving: the populator stages into a new directory and swaps the directory
# ENTRY, so a running host keeps the inode it already mapped. Overwriting a mapped assembly in place is what
# poisons not-yet-JITted IL and surfaces as "Bad IL range" hours later (ADR 0808).
#
# A stack without a given one-shot has no such service, so each is a no-op there rather than a special case.
# TWO installers since #1246: `modules-init` resolves the PINNED packages (version + digest, ADR 0799) and
# `modules-local` overlays any genuinely local build an override supplies — pinned first, so a local overlay
# deliberately wins over the pin it is testing a replacement for.
install_oneshot() {
  service="$1"
  if ! in_project "$service"; then
    say "$service is not in this project; nothing to install"
    return 0
  fi

  say "installing modules into the shared volume ($service)"
  $COMPOSE up -d --force-recreate --no-deps "$service"
  oneshot_id=$($COMPOSE ps -q "$service")
  if [ -n "$oneshot_id" ]; then
    oneshot_status=$(docker wait "$oneshot_id")
    if [ "$oneshot_status" != "0" ]; then
      # Refuse rather than roll: replacing the instances now would deploy the OLD module while reporting
      # success, which is exactly the silence this block exists to end.
      echo "FAILED: $service exited $oneshot_status. No instance has been touched." >&2
      echo "$($COMPOSE logs --tail=30 "$service")" >&2
      exit 1
    fi
  fi
  say "$service done"
}

install_oneshot modules-init
install_oneshot modules-local

# --- the sidecars we publish ourselves ----------------------------------------------------------------
# api and api-b are not the whole stack. The sidecars built from OUR OWN sibling repositories carry
# `:latest` and therefore drift, and until this existed a release updated the app and left them running
# whatever they already had. That produces a version claim which is true of the app and false of the
# stack: after one rollout the encryption sidecar was still on a pre-rotation image, so endpoints the core
# had just learned to call did not exist — while `docker compose ps` looked entirely healthy. The image tag
# was the only place the drift showed, which is the worst possible place for it to be.
#
# Third-party images (Postgres, OpenSearch, Tika, Gotenberg, Valkey, SeaweedFS, OpenBao, Caddy, Mailpit)
# are deliberately NOT rolled here. They are version-pinned, so they move only when somebody bumps a tag on
# purpose, and recreating the stateful ones would interrupt the demo — the one thing this script promises
# not to do. Bump those deliberately; a release is not the moment to restart a database.
#
# This runs BEFORE the migrations and before any instance is replaced, so a sidecar that comes back broken
# aborts while the stack is still serving exactly what it was serving.
SIDECARS=${SA_SIDECARS:-"encryption ocr postfix"}

# Gate on the container's own HEALTHCHECK where the image declares one. Where none does, the honest
# substitute is "still running a few seconds later" — that catches the crash-loop, which is the failure
# worth catching, without inventing a probe the image never offered and reporting its silence as success.
wait_sidecar() {
  svc=$1
  cid=$2
  waited=0
  probed=$(docker inspect -f '{{if .State.Health}}health{{end}}' "$cid" 2>/dev/null || true)
  while [ "$waited" -lt "$READY_TIMEOUT" ]; do
    if [ -n "$probed" ]; then
      state=$(docker inspect -f '{{.State.Health.Status}}' "$cid" 2>/dev/null || echo unknown)
      case "$state" in
        healthy)   say "$svc is healthy (${waited}s)"; return 0 ;;
        unhealthy) echo "FAILED: $svc came back UNHEALTHY." >&2; return 1 ;;
      esac
    else
      if [ "$(docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)" != "true" ]; then
        echo "FAILED: $svc is not running after being replaced." >&2
        return 1
      fi
      if [ "$waited" -ge 10 ]; then
        say "$svc declares no healthcheck; running and stable after ${waited}s"
        return 0
      fi
    fi
    sleep 2
    waited=$((waited + 2))
  done
  echo "FAILED: $svc did not become healthy within ${READY_TIMEOUT}s." >&2
  return 1
}

for svc in $SIDECARS; do
  # An add-on that is not installed on this host simply is not in the project — said out loud, because a
  # silent skip here is indistinguishable from "rolled it, nothing to do", which is the whole failure this
  # phase exists to end.
  if ! in_project "$svc"; then
    say "$svc is not installed on this host; skipping"
    continue
  fi

  cid=$($COMPOSE ps -q "$svc" 2>/dev/null | head -1 || true)
  if [ -z "$cid" ]; then
    say "$svc is in the project but not running; starting it"
    $COMPOSE up -d --no-deps "$svc"
    cid=$($COMPOSE ps -q "$svc" | head -1)
    wait_sidecar "$svc" "$cid" || { $COMPOSE logs --tail=30 "$svc" >&2; exit 1; }
    continue
  fi

  # Recreate only on actual drift: compare the image the container is RUNNING with the image its tag now
  # resolves to after the pull. Restarting a sidecar that did not change buys nothing and costs a blip.
  running_image=$(docker inspect -f '{{.Image}}' "$cid" 2>/dev/null || true)
  tag=$(docker inspect -f '{{.Config.Image}}' "$cid" 2>/dev/null || true)
  desired_image=$(docker image inspect -f '{{.Id}}' "$tag" 2>/dev/null || true)

  if [ -z "$desired_image" ] || [ "$running_image" = "$desired_image" ]; then
    say "$svc unchanged ($tag)"
    continue
  fi

  say "replacing $svc — its image moved ($tag)"
  $COMPOSE up -d --force-recreate --no-deps "$svc"
  cid=$($COMPOSE ps -q "$svc" | head -1)
  wait_sidecar "$svc" "$cid" || { $COMPOSE logs --tail=30 "$svc" >&2; exit 1; }
done

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
