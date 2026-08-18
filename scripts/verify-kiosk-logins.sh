#!/usr/bin/env bash
# Verify that every credential the PUBLIC README advertises actually logs in to a running instance.
#
# Why this exists: the demo credentials are NOT in the image — they are env vars in the compose file ON the
# kiosk host, which a release does not touch (`reset.sh` pulls a new image and starts it with whatever config
# is already there). So the repo and the host drift silently, and the only symptom is a visitor being turned
# away. That is precisely what happened: the public README advertised `SimplDemo2026!` while the host had been
# left on the breach-flagged `demo1234` since before that password was changed. Verifying the API image's OCI
# version/revision labels — the old rollout check — said nothing, because the labels were correct the whole time.
#
# The README is the input on purpose. A check with the passwords hard-coded would go stale exactly the way the
# host did; this one fails when what we PUBLISH and what a visitor EXPERIENCES disagree, whichever moved.
#
# It also fails when the README is ahead of the deployment — a credential change merged but not yet released.
# That is a real finding, not noise: until the release lands, the front door lists a login that does not work.
#
# A REACHABILITY failure is reported as itself, never as a bad password. When the API was crash-looping after a
# rollout, every request answered 502 and this script printed three "FAIL <email>" lines and advised changing a
# password on the host — a confident, specific, wrong cause, for a site that was simply not serving. The two
# have completely different remedies, so they get different messages and different exit codes.
#
# Usage:  scripts/verify-kiosk-logins.sh [base-url]      (default https://demo.simplarchive.dev)
# Exit:   0 = every advertised credential logged in
#         1 = the site served, but at least one advertised credential was rejected (or none were parsed)
#         2 = the site could not be reached — nothing was learned about the credentials

set -u

BASE_URL="${1:-https://demo.simplarchive.dev}"
BASE_URL="${BASE_URL%/}"
README="$(cd "$(dirname "$0")/.." && pwd)/publish/README.public.md"

if [ ! -f "$README" ]; then
  echo "FAIL: $README not found — this check reads the advertised credentials from it." >&2
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Pull (email, password) out of the live-demo table. A row carries the address in one backticked cell and the
# password in another; anything without an "@" cell is a heading or prose, not a credential.
#
# Deliberately tolerant of column ORDER and count: the table has been reshaped twice already (a two-column
# key/value block, then a four-column per-user table), and a check that has to be re-taught the layout every
# time is one that gets deleted instead.
awk '
  # Only BACKTICKED spans are candidates. In both layouts an address and a password are code spans while the
  # surrounding cells are prose or links, so this classifies without knowing which column anything is in.
  /\|/ {
    line = $0
    email = ""; password = ""
    while (match(line, /`[^`]+`/)) {
      tok = substr(line, RSTART + 1, RLENGTH - 2)
      line = substr(line, RSTART + RLENGTH)
      if (tok ~ /^[^ @]+@[^ @]+\.[^ @]+$/) { email = tok }
      else if (tok !~ /^https?:/ && tok !~ / /) { password = tok }
    }

    # Two layouts have to work. The per-user table puts an address and its password on ONE row; the older
    # key/value block puts them on separate rows ("| **Email** | `…` |" then "| **Password** | `…` |"). So a
    # lone password pairs with the address last seen above it, and a shared password applies to each address
    # that preceded it.
    if (email != "" && password != "") { print email "\t" password; pending = "" }
    else if (email != "") { pending = email; queue[++n] = email }
    else if (password != "" && n > 0) {
      for (i = 1; i <= n; i++) { print queue[i] "\t" password }
      n = 0; pending = ""
    }
  }
' "$README" | sort -u > "$WORK/creds.tsv"

COUNT=$(wc -l < "$WORK/creds.tsv" | tr -d ' ')
if [ "$COUNT" = "0" ]; then
  # Silence here would read as success, and "no credentials found" is the one result that must never pass:
  # it is indistinguishable from a README whose table was reformatted out from under this parser.
  echo "FAIL: no credentials parsed from publish/README.public.md — has the live-demo table changed shape?" >&2
  exit 1
fi

# PREFLIGHT — ask whether the site is serving at all, once, before asking anything about credentials. Without
# this the answer arrives as N credential failures, which is the wrong question answered confidently.
PRE_STATUS=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$BASE_URL/Account/Login" 2>/dev/null)
case "$PRE_STATUS" in
  2*|3*) ;;
  000)
    echo "UNREACHABLE: no response from $BASE_URL (DNS, TLS, or nothing listening)." >&2
    echo "Nothing was learned about the advertised credentials — this is not a password problem." >&2
    exit 2
    ;;
  *)
    echo "UNREACHABLE: $BASE_URL/Account/Login answered HTTP $PRE_STATUS, so the login form never rendered." >&2
    echo "A 502/503 here usually means the API container is down or crash-looping, NOT that a password is wrong:" >&2
    echo "  ssh kiosk 'docker ps -a --filter name=api --format \"{{.Status}}\"; docker logs --tail 30 \$(docker ps -aqf name=api)'" >&2
    exit 2
    ;;
esac

echo "Verifying $COUNT advertised login(s) against $BASE_URL"

FAILED=0
UNREACHABLE=0
while IFS="$(printf '\t')" read -r EMAIL PASSWORD; do
  [ -z "$EMAIL" ] && continue

  JAR="$WORK/jar-$$.txt"
  rm -f "$JAR"

  # The app issues no password grant (client-credentials + auth-code/PKCE only), so the only honest test of a
  # human's credential is the human's form: GET it for an antiforgery token + cookie, then POST.
  # A fetch failure mid-run means the site stopped serving (it passed the preflight moments ago), so it is
  # counted as UNREACHABLE rather than as this credential being wrong.
  if ! curl -fsS -c "$JAR" "$BASE_URL/Account/Login" -o "$WORK/login.html"; then
    echo "  ????  $EMAIL — could not fetch the login form; the site stopped responding mid-run" >&2
    UNREACHABLE=1
    continue
  fi

  # The trailing quote matters: leaving it on corrupts the token and the server answers 400, which looks like a
  # rejected password but is a malformed request. Strip it.
  TOKEN=$(grep -o '<input name="__RequestVerificationToken"[^>]*value="[^"]*"' "$WORK/login.html" \
    | head -1 | sed 's/.*value="//; s/"$//')
  if [ -z "$TOKEN" ]; then
    echo "  FAIL  $EMAIL — no antiforgery token on the login page" >&2
    FAILED=1
    continue
  fi

  curl -sS -b "$JAR" -D "$WORK/headers.txt" -o /dev/null -X POST "$BASE_URL/Account/Login" \
    -H "Referer: $BASE_URL/Account/Login" \
    --data-urlencode "Input.Email=$EMAIL" \
    --data-urlencode "Input.Password=$PASSWORD" \
    --data-urlencode "__RequestVerificationToken=$TOKEN" >/dev/null 2>&1

  # Success is a 302 that SETS THE AUTH COOKIE. Checking only for the redirect would pass on a re-rendered
  # form, and checking only for absence of an error string would pass on a localised error message.
  if grep -qi '^HTTP/[0-9.]* 302' "$WORK/headers.txt" && grep -qi 'set-cookie: *\.AspNetCore\.Cookies=' "$WORK/headers.txt"; then
    echo "  ok    $EMAIL"
  else
    STATUS=$(head -1 "$WORK/headers.txt" | tr -d '\r')
    echo "  FAIL  $EMAIL — advertised password did not log in ($STATUS)" >&2
    FAILED=1
  fi
done < "$WORK/creds.tsv"

if [ "$UNREACHABLE" != "0" ]; then
  echo
  echo "$BASE_URL stopped responding partway through — the credentials it did not reach are UNVERIFIED." >&2
  echo "Check the API container before reading anything into this run." >&2
  exit 2
fi

if [ "$FAILED" != "0" ]; then
  echo
  echo "The site served the login form and REJECTED at least one credential the public README advertises." >&2
  echo "Usual cause: the host's /opt/simplarchive/docker-compose.yml still carries the OLD Demo__Administrator__" >&2
  echo "Password — a release ships the IMAGE only. Copy deploy/kiosk/docker-compose.yml to the host, then reset." >&2
  exit 1
fi

echo "All advertised credentials log in."
