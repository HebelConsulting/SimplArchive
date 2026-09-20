#!/usr/bin/env bash
# Verify that a PROTOCOL read populates a module's on-demand content — over IMAP *and* over WebDAV.
#
# WHAT THIS PROVES, AND WHY NOTHING ELSE DOES IT
#
# The populate-on-open hook (core ADR 0756) is what fills a weather folder, and until ABI 0.27 (ADR 0810) it
# was invoked by the two SimplArchive clients and by nothing else. Meanwhile the ephemeral sweep kept purging
# expired content on schedule. So a user who reached the archive only over a mounted drive or a mail client
# saw an EMPTY folder — not stale, not an error, empty — and no surface on that path could say why.
#
# Four separate things have to be true for that to work, and each of them lives somewhere a release does not
# reach: the MODULE must declare its source eligible, the TENANT setting must be on, the deployed module build
# must be new enough to carry the declaration, and the host must enforce an ABI minor that accepts it. A stale
# module still loads, seeds its masks and answers requests (#1242), and a missing tenant setting is simply an
# absent row — so every one of those failures presents as "the folder is empty", which is indistinguishable
# from "this feature was never built". That is exactly the shape nobody notices.
#
# HOW IT TESTS IT — and why the obvious tests do not work
#
# It creates a NEW aerodrome folder, opens it over one protocol, and asserts content appeared. The folder must
# be new, because the two obvious probes both prove nothing:
#
#   * An EXISTING aerodrome folder is usually already full, and the hook's cooldown then correctly skips it.
#     A folder that does not change is the feature WORKING, and reads as the feature failing.
#   * The seeded folders are populated by the demo seeder over HTTP, so finding content in them after a
#     protocol read says nothing about which path filled it. (Both of these were measured on the kiosk while
#     verifying v0.29.0 — one after the other, each looking like evidence.)
#
# The name must be a REAL ICAO code, because the module fetches weather for the code in the folder's name; a
# random probe name would fetch nothing and the test would fail for the wrong reason. The defaults are real
# Swiss airfields that the demo seed does NOT use: LSZR (St. Gallen-Altenrhein) for IMAP and LSZG (Grenchen)
# for WebDAV. Two different aerodromes so the two protocols cannot mask each other — the second surface must
# populate a folder the first one has never touched, or it is proving the first surface twice.
#
# It then opens the SAME folder a second time and asserts nothing new arrived: the cooldown is what keeps a
# polling file manager or mail client from becoming the unattended fetcher ADR 0756 rejected on legal grounds,
# and a populate path without it would be worse than the defect it fixes.
#
# Both probe folders are deleted at the end, including when a leg fails, so the demo is left as it was found.
#
# Usage:  scripts/verify-kiosk-populate.sh [base-url]
# Env:    SA_ADMIN / SA_PASSWORD   (default: the kiosk demo admin)
#         SA_IMAP_ICAO             (default LSZR)   SA_DAV_ICAO (default LSZG)
#         SA_REPO                  (default "Flight School")
#         SA_PATH                  (default "Operations/Flight Planning")
# Exit:   0 = both protocols populated an empty folder, and neither re-fetched on the second read
#         1 = the site served, but a protocol read did not populate (or the cooldown did not hold)
#         2 = the site could not be reached, or the fixtures this needs are absent — nothing was learned

set -u

BASE_URL="${1:-https://demo.simplarchive.dev}"
BASE_URL="${BASE_URL%/}"
HOST="${BASE_URL#https://}"; HOST="${HOST#http://}"; HOST="${HOST%%/*}"

ADMIN="${SA_ADMIN:-admin@demo.simplarchive.dev}"
PASSWORD="${SA_PASSWORD:-SimplDemo2026!}"
IMAP_ICAO="${SA_IMAP_ICAO:-LSZR}"
DAV_ICAO="${SA_DAV_ICAO:-LSZG}"
REPO="${SA_REPO:-Flight School}"
SUBPATH="${SA_PATH:-Operations/Flight Planning}"

command -v jq >/dev/null 2>&1 || { echo "FAIL: jq is required." >&2; exit 2; }

WORK="$(mktemp -d)"
CREATED=""
cleanup() {
  # Remove the probe folders however we got here. A leftover probe is not harmless: it is a real aerodrome
  # folder the next run would find already present, and its content would make that run's cooldown skip —
  # turning the next verification into a false PASS.
  for id in $CREATED; do
    tag=$(curl -s -o /dev/null -D - --max-time 20 -H "Authorization: Bearer $TOKEN" \
            -X HEAD "$BASE_URL/api/documents/$id" 2>/dev/null | tr -d '\r' | sed -n 's/^[Ee][Tt]ag: //p')
    [ -n "${tag:-}" ] && curl -s -o /dev/null --max-time 20 -X DELETE \
      -H "Authorization: Bearer $TOKEN" -H "If-Match: $tag" "$BASE_URL/api/documents/$id" 2>/dev/null
  done
  rm -rf "$WORK"
}
trap cleanup EXIT

# PREFLIGHT — ask whether the site serves at all, once, before asking anything else. Without it a down site
# answers as "the populate hook is broken", which is a confident, specific, wrong cause.
PRE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$BASE_URL/health/ready" 2>/dev/null)
case "$PRE" in
  2*) ;;
  000) echo "UNREACHABLE: no response from $BASE_URL." >&2; exit 2 ;;
  *)   echo "UNREACHABLE: $BASE_URL/health/ready answered HTTP $PRE." >&2; exit 2 ;;
esac

# ---------------------------------------------------------------------------------------------------------
# Sign in the way the SPA does (authorization code + PKCE). There is no password grant, so this is the only
# honest way to hold a user's token — the same flow verify-kiosk-logins.sh drives, carried one step further
# to the token exchange because this check has to CALL the API, not merely reach the form.
# ---------------------------------------------------------------------------------------------------------
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
VERIFIER=$(openssl rand 32 | b64url)
CHALLENGE=$(printf '%s' "$VERIFIER" | openssl dgst -binary -sha256 | b64url)
REDIRECT="$BASE_URL/authentication/login-callback"
JAR="$WORK/jar"

AUTHORIZE="$BASE_URL/connect/authorize?client_id=blazor-client&response_type=code&redirect_uri=$(
  printf '%s' "$REDIRECT" | jq -sRr @uri)&scope=openid&code_challenge=$CHALLENGE&code_challenge_method=S256&state=x"

LOGIN_PATH=$(curl -s -o /dev/null -D - -c "$JAR" -b "$JAR" --max-time 20 "$AUTHORIZE" | tr -d '\r' | sed -n 's/^[Ll]ocation: //p')
[ -n "$LOGIN_PATH" ] || { echo "UNREACHABLE: /connect/authorize did not redirect to the login form." >&2; exit 2; }
case "$LOGIN_PATH" in http*) LOGIN_URL="$LOGIN_PATH" ;; *) LOGIN_URL="$BASE_URL$LOGIN_PATH" ;; esac

HTML=$(curl -s -c "$JAR" -b "$JAR" --max-time 20 "$LOGIN_URL")
ANTIFORGERY=$(printf '%s' "$HTML" | sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' | head -1)
RETURN_URL=$(printf '%s' "$LOGIN_URL" | sed -n 's/.*[?&]ReturnUrl=\([^&]*\).*/\1/p' | sed 's/%3A/:/g;s/%2F/\//g;s/%3F/?/g;s/%3D/=/g;s/%26/\&/g;s/%25/%/g')
[ -n "$ANTIFORGERY" ] || { echo "UNREACHABLE: no antiforgery token on the login form." >&2; exit 2; }

NEXT=$(curl -s -o /dev/null -D - -c "$JAR" -b "$JAR" --max-time 20 -X POST "$LOGIN_URL" \
  --data-urlencode "Email=$ADMIN" --data-urlencode "Password=$PASSWORD" \
  --data-urlencode "ReturnUrl=$RETURN_URL" --data-urlencode "__RequestVerificationToken=$ANTIFORGERY" \
  | tr -d '\r' | sed -n 's/^[Ll]ocation: //p')
[ -n "$NEXT" ] || { echo "FAIL: login did not redirect — check $ADMIN's password." >&2; exit 1; }

CODE=""
for _ in 1 2 3 4 5 6 7 8; do
  case "$NEXT" in http*) URL="$NEXT" ;; *) URL="$BASE_URL$NEXT" ;; esac
  LOC=$(curl -s -o /dev/null -D - -c "$JAR" -b "$JAR" --max-time 20 "$URL" | tr -d '\r' | sed -n 's/^[Ll]ocation: //p')
  [ -n "$LOC" ] || break
  NEXT="$LOC"
  # Match the separator as "?" OR "&" OR the start of the query — a pattern anchored on [?&] alone silently
  # never fires once the URL has been split, which cost a debugging round when this was first written.
  case "$NEXT" in *"?code="*|*"&code="*) CODE=$(printf '%s' "$NEXT" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p'); break ;; esac
done
[ -n "$CODE" ] || { echo "FAIL: no authorization code — is $ADMIN a tenant admin?" >&2; exit 1; }

TOKEN=$(curl -s --max-time 20 -X POST "$BASE_URL/connect/token" \
  --data-urlencode "grant_type=authorization_code" --data-urlencode "code=$CODE" \
  --data-urlencode "redirect_uri=$REDIRECT" --data-urlencode "client_id=blazor-client" \
  --data-urlencode "code_verifier=$VERIFIER" | jq -r '.access_token // empty')
[ -n "$TOKEN" ] || { echo "FAIL: the token exchange returned no access_token." >&2; exit 1; }

api() { curl -s --max-time 30 -H "Authorization: Bearer $TOKEN" -H 'Accept: application/json' "$@"; }

# ---------------------------------------------------------------------------------------------------------
# Find the folder the probes go under, and the mask an aerodrome wears — by FOLLOWING RELS (ADR 0543), never
# by composing a URL or hard-coding an id. A hard-coded id would be a fixture that silently stops matching
# the demo the first time the seed is reshaped.
# ---------------------------------------------------------------------------------------------------------
follow() { printf '%s' "$1" | jq -r --arg r "$2" '.links[]? | select(.rel==$r) | .href' | head -1; }

REPO_ROW=$(api "$BASE_URL/api/repositories" | jq -c --arg n "$REPO" '.repositories[]? | select(.name==$n)')
[ -n "$REPO_ROW" ] || { echo "UNREACHABLE: no repository named '$REPO' — this check needs the module's demo data." >&2; exit 2; }

CHILDREN_HREF=$(follow "$REPO_ROW" children)
OLDIFS=$IFS; IFS='/'
for SEG in $SUBPATH; do
  IFS=$OLDIFS
  ROW=$(api "$BASE_URL$CHILDREN_HREF" | jq -c --arg n "$SEG" '(.children // .items)[]? | select(.name==$n)')
  [ -n "$ROW" ] || { echo "UNREACHABLE: '$SEG' not found under '$REPO/$SUBPATH' — demo data missing." >&2; exit 2; }
  CHILDREN_HREF=$(follow "$ROW" children)
  IFS='/'
done
IFS=$OLDIFS
PARENT_CHILDREN="$CHILDREN_HREF"

# The mask to give a probe: read it off an existing sibling, so the probe is "one like the ones already
# there" rather than a constant this script would have to be re-taught.
SIBLINGS=$(api "$BASE_URL$PARENT_CHILDREN")
MASK_ID=""
for SID in $(printf '%s' "$SIBLINGS" | jq -r '(.children // .items)[]? | .id'); do
  M=$(api "$BASE_URL/api/documents/$SID/mask" | jq -r '.maskId // .id // empty')
  HAS_HOOK=$(api "$BASE_URL/api/documents/$SID" | jq -r '[.links[]?.rel | select(startswith("machine-auto-refresh:"))] | length')
  if [ "${HAS_HOOK:-0}" -gt 0 ] && [ -n "$M" ]; then MASK_ID="$M"; break; fi
done
[ -n "$MASK_ID" ] || {
  echo "UNREACHABLE: no sibling under '$REPO/$SUBPATH' advertises a machine-auto-refresh rel." >&2
  echo "Either the module is not active here, or the deployed build predates its populate hooks." >&2
  exit 2
}

echo "Verifying protocol-read populate against $BASE_URL"

FAILED=0

# $1 = protocol label, $2 = ICAO probe name, $3 = a function that opens the folder over that protocol
probe() {
  LABEL="$1"; ICAO="$2"; OPEN="$3"

  # A leftover probe from a crashed run would already hold content, and the cooldown would then skip — the
  # next run's PASS would be measuring nothing. Remove it first rather than trusting it is absent.
  EXISTING=$(api "$BASE_URL$PARENT_CHILDREN" | jq -r --arg n "$ICAO" '(.children // .items)[]? | select(.name==$n) | .id' | head -1)
  if [ -n "$EXISTING" ]; then
    TAG=$(curl -s -o /dev/null -D - --max-time 20 -H "Authorization: Bearer $TOKEN" -X HEAD "$BASE_URL/api/documents/$EXISTING" | tr -d '\r' | sed -n 's/^[Ee][Tt]ag: //p')
    curl -s -o /dev/null --max-time 20 -X DELETE -H "Authorization: Bearer $TOKEN" -H "If-Match: $TAG" "$BASE_URL/api/documents/$EXISTING"
  fi

  ID=$(api -X POST -H 'Content-Type: application/json' \
        -d "$(jq -n --arg n "$ICAO" --arg m "$MASK_ID" '{name:$n, maskId:$m}')" \
        "$BASE_URL$PARENT_CHILDREN" | jq -r '.id // empty')
  if [ -z "$ID" ]; then
    echo "  FAIL  $LABEL — could not create the probe folder $ICAO."
    FAILED=1; return
  fi
  CREATED="$CREATED $ID"

  BEFORE=$(api "$BASE_URL/api/documents/$ID/children" | jq -r '(.children // .items) | length')
  if [ "${BEFORE:-0}" != "0" ]; then
    echo "  FAIL  $LABEL — $ICAO was not empty at the start ($BEFORE children); the probe proves nothing."
    FAILED=1; return
  fi

  "$OPEN" "$ICAO"

  # Poll rather than sleep a fixed amount: the hook fetches from a live provider, so the honest wait is
  # "until content appears or we give up", and a fixed sleep is either flaky or slow.
  AFTER=0
  for _ in $(seq 1 20); do
    AFTER=$(api "$BASE_URL/api/documents/$ID/children" | jq -r '(.children // .items) | length')
    [ "${AFTER:-0}" -gt 0 ] && break
    sleep 3
  done

  if [ "${AFTER:-0}" -eq 0 ]; then
    echo "  FAIL  $LABEL — $ICAO is still empty after the read; the populate hook did not run."
    echo "        Check: the tenant setting core.protocolReadRefresh, the module build, and the host's ABI minor."
    FAILED=1; return
  fi

  # The cooldown. Opening again must NOT fetch again — otherwise a polling client becomes the unattended
  # fetcher ADR 0756 ruled out, which is a worse defect than the empty folder this feature fixes.
  "$OPEN" "$ICAO"
  sleep 5
  AGAIN=$(api "$BASE_URL/api/documents/$ID/children" | jq -r '(.children // .items) | length')
  if [ "${AGAIN:-0}" -ne "${AFTER:-0}" ]; then
    echo "  FAIL  $LABEL — a second read changed the content ($AFTER → $AGAIN); the cooldown did not hold."
    FAILED=1; return
  fi

  echo "  ok    $LABEL — $ICAO populated $AFTER item(s) on first open, unchanged on the second"
}

open_imap() {
  # A SEARCH has to SELECT the mailbox first, which is the command the hook hangs off — so this is a real
  # protocol open, not a side door.
  curl -s --max-time 60 --user "$ADMIN:$PASSWORD" \
    "imaps://$HOST/$(printf '%s' "$REPO/$SUBPATH/$1" | jq -sRr @uri)?ALL" >/dev/null 2>&1 || true
}

open_webdav() {
  # Depth 1: a depth-0 PROPFIND is a property read, not an open, and deliberately does NOT trigger the hook.
  curl -s --max-time 60 -u "$ADMIN:$PASSWORD" -X PROPFIND -H 'Depth: 1' \
    "$BASE_URL/SimplArchive/$(printf '%s' "$REPO/$SUBPATH/$1" | sed 's/ /%20/g')/" >/dev/null 2>&1 || true
}

probe "IMAP   SELECT " "$IMAP_ICAO" open_imap
probe "WebDAV PROPFIND" "$DAV_ICAO" open_webdav

if [ "$FAILED" -eq 0 ]; then
  echo "Both protocol surfaces populate on-demand content, and neither re-fetches on a second read."
  exit 0
fi
echo "At least one protocol surface did not populate — see above." >&2
exit 1
