#!/usr/bin/env bash
# Load-test a running SimplArchive deployment (#705, ADR 0700) — MANUAL ONLY, never CI.
#
# WHY NEVER CI. A load test in a PR gate is cost with no verdict value: it cannot fail a change honestly (the
# runner is not the deployment) and it cannot pass one meaningfully. It is a question somebody asks on purpose,
# about a specific machine, at a time when loading it is acceptable.
#
# NIGHT-WINDOW DISCIPLINE. Run against the kiosk at roughly 01:00-03:30. Degradation then reaches few visitors,
# and everything the run creates — uploads, sessions — is erased by the 04:00 reset (`reset.sh`, `down -v`), so
# the cleanup is free and already guaranteed. Running at noon means loading a public demo in front of whoever
# happens to be looking at it.
#
# THE TARGET GUARD. This script refuses any target but the kiosk unless --i-know-what-im-doing is passed. A
# mistyped host must not load-test somebody else's server: a wrong URL here is not a failed command, it is
# traffic somebody else has to explain.
#
# Usage:
#   scripts/load-test.sh --scenario steady10                       # against the kiosk
#   scripts/load-test.sh --scenario steady10 --local               # self-host one and prove the harness
#   scripts/load-test.sh --scenario steady10 --target https://…    # needs --i-know-what-im-doing
#
# Exit: 0 PASS · 1 FAIL (the target degraded) · 2 usage · 3 INVALID (the generator saturated; nothing learned)

set -euo pipefail

KIOSK_URL="https://demo.simplarchive.dev"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
README="$ROOT/publish/README.public.md"

SCENARIO="steady10"
TARGET="$KIOSK_URL"
LOCAL=0
OVERRIDE=0
EXTRA=()

while [ $# -gt 0 ]; do
  case "$1" in
    --scenario) SCENARIO="$2"; shift 2 ;;
    --target) TARGET="$2"; shift 2 ;;
    --local) LOCAL=1; shift ;;
    --i-know-what-im-doing) OVERRIDE=1; shift ;;
    --users|--minutes|--out) EXTRA+=("$1" "$2"); shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ "$LOCAL" = 1 ]; then
  # No target at all: the harness self-hosts an instance. This is the calibration path — prove the workload and
  # the report here, where a mistake costs nothing, before ever pointing it at a live deployment.
  echo "Local calibration run (self-hosted instance, not the kiosk)."
  exec dotnet run --project "$ROOT/tests/SimplArchive.LoadTest" -c Release -- \
    --scenario "$SCENARIO" ${EXTRA+"${EXTRA[@]}"}
fi

if [ "$TARGET" != "$KIOSK_URL" ] && [ "$OVERRIDE" != 1 ]; then
  echo "REFUSED: '$TARGET' is not the kiosk ($KIOSK_URL)." >&2
  echo "Loading a host you did not mean to load is traffic somebody else has to explain. If you do mean it," >&2
  echo "pass --i-know-what-im-doing." >&2
  exit 2
fi

# The credentials come from the PUBLISHED README, exactly as verify-kiosk-logins.sh takes them: the harness must
# sign in as a real advertised account, and a copy kept here would go stale the way every other copy has.
if [ ! -f "$README" ]; then
  echo "FAIL: $README not found — this reads the target's advertised credentials from it." >&2
  exit 2
fi

CREDS=$(awk '
  /\|/ {
    line = $0
    email = ""; password = ""
    while (match(line, /`[^`]+`/)) {
      tok = substr(line, RSTART + 1, RLENGTH - 2)
      line = substr(line, RSTART + RLENGTH)
      if (tok ~ /^[^ @]+@[^ @]+\.[^ @]+$/) { email = tok }
      else if (tok !~ /^https?:/ && tok !~ / /) { password = tok }
    }
    if (email != "" && password != "") { print email " " password }
  }
' "$README" | head -1)

if [ -z "$CREDS" ]; then
  echo "FAIL: no advertised credentials parsed from the README — has the live-demo table changed shape?" >&2
  exit 2
fi

EMAIL=${CREDS%% *}
PASSWORD=${CREDS#* }

HOUR=$(date +%H)
if [ "$HOUR" -lt 1 ] || [ "$HOUR" -gt 3 ]; then
  # A warning, not a refusal: the discipline is a judgement about who is watching, and only the person running
  # it knows. Silence here would let the habit erode without anyone deciding to erode it.
  echo "NOTE: it is ${HOUR}:xx — outside the 01:00-03:30 window this run is meant for." >&2
  echo "      Visitors will feel this, and the 04:00 reset is further away than usual." >&2
fi

echo "Load-testing $TARGET as $EMAIL — scenario $SCENARIO"
exec dotnet run --project "$ROOT/tests/SimplArchive.LoadTest" -c Release -- \
  --scenario "$SCENARIO" --target "$TARGET" --email "$EMAIL" --password "$PASSWORD" ${EXTRA+"${EXTRA[@]}"}
