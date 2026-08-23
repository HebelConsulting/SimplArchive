#!/usr/bin/env bash
# Verify that every mailbox the PUBLIC README advertises actually RECEIVES mail on a running instance —
# the companion to verify-kiosk-logins.sh, for the delivery path (ADR 0628/0683): SMTP :25 → Postfix →
# LMTP → the lazily-provisioned mailbox → IMAP :993.
#
# Why this exists: the logins check proves the front door; nothing proved the mail path end-to-end until
# v0.6.0 shipped it (the #703 arc + the seeded demo TenantMailDomain). A kiosk that accepts a login but
# 550s every message — or worse, 250s it into nowhere — looks healthy by every other check we run.
#
# Same philosophy as the logins check: the README is the input, so this fails when what we publish and what
# a sender experiences disagree, whichever moved. Each advertised address gets one message with a nonce
# subject over REAL port-25 SMTP (no submission port, no auth — the path any outside MTA uses), then the
# same user's advertised IMAP credential must find that subject in INBOX. The demo's seeded DEPARTMENT
# mailbox (events@<domain>, ADR 0684) is checked the same way, read through the admin's IMAP view.
#
# THE SENDER NEEDS FORWARD-CONFIRMED rDNS. The kiosk's Postfix runs `reject_unknown_client_hostname`
# (postfix/entrypoint.sh), so a client whose IP has no PTR→A match is answered `450 4.7.25` before RCPT —
# which is the anti-spam screen working, not a delivery failure: real MTAs have FCrDNS, residential dev
# machines do not. Found live on the v0.6.0 rollout: both this Mac AND the docker bridge were refused. So
# the SMTP leg runs ON the kiosk host over ssh, hairpinning to the public address — the host's own IP has
# a matching PTR, making it the one always-available FCrDNS sender we control. The IMAP leg runs locally,
# as any visitor's client would.
#
# Usage:  scripts/verify-kiosk-mail.sh [mail-host] [ssh-host]   (defaults demo.simplarchive.dev, kiosk)
# Exit:   0 = every advertised mailbox received and served its message
#         1 = at least one message was refused at SMTP or never appeared over IMAP
#         2 = the host could not be reached — nothing was learned

set -u

HOST="${1:-demo.simplarchive.dev}"
SSH_HOST="${2:-kiosk}"
README="$(cd "$(dirname "$0")/.." && pwd)/publish/README.public.md"

if [ ! -f "$README" ]; then
  echo "FAIL: $README not found — this check reads the advertised credentials from it." >&2
  exit 1
fi

if ! nc -z -w 10 "$HOST" 25 2>/dev/null; then
  echo "UNREACHABLE: $HOST:25 did not answer — nothing was learned about delivery." >&2
  exit 2
fi

# The same tolerant backticked-span extraction the logins check uses (see its comment for why).
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
' "$README" | sort -u)

if [ -z "$CREDS" ]; then
  echo "FAIL: no advertised credentials parsed from the README — the table layout may have changed." >&2
  exit 1
fi

NONCE="kioskmail-$(date +%s)-$RANDOM"
FAILED=0

while read -r EMAIL PASSWORD; do
  SUBJECT="$NONCE-${EMAIL%%@*}"

  # Plain SMTP, exactly as an outside MTA speaks it — from the FCrDNS host (see the header).
  if ! ssh "$SSH_HOST" "python3 - '$HOST' '$EMAIL' '$SUBJECT'" <<'PY'
import smtplib, sys
host, rcpt, subject = sys.argv[1:4]
msg = (f"From: release-check@example.net\r\nTo: {rcpt}\r\nSubject: {subject}\r\n"
       f"Message-ID: <{subject}@release-check.example.net>\r\n\r\n"
       "Release verification: this mailbox receives.\r\n")
try:
    with smtplib.SMTP(host, 25, timeout=30) as s:
        s.sendmail("release-check@example.net", [rcpt], msg.encode())
except Exception as e:
    print(f"SMTP refused {rcpt}: {e}", file=sys.stderr)
    sys.exit(1)
PY
  then
    echo "FAIL $EMAIL — refused at SMTP"
    FAILED=1
    continue
  fi

  # Delivery is asynchronous (Postfix relays to LMTP); poll IMAP briefly rather than asserting instantly.
  FOUND=""
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    if curl -s --max-time 15 --user "$EMAIL:$PASSWORD" \
        "imaps://$HOST/INBOX?SUBJECT%20$SUBJECT" | grep -q '[0-9]'; then
      FOUND=yes
      break
    fi
    sleep 3
  done

  if [ -n "$FOUND" ]; then
    echo "OK   $EMAIL — sent over :25, found over IMAP"
  else
    echo "FAIL $EMAIL — accepted at SMTP but never appeared in INBOX over IMAP"
    FAILED=1
  fi
done <<< "$CREDS"

# The seeded department mailbox (ADR 0684): the showcase address, derived from the advertised domain the
# way the seeder derives it. Delivery lands in ITS lazily-created Inbox, which projects into IMAP for
# anyone with rights on the repository — read here as the first advertised (admin) user.
ADMIN_LINE=$(printf '%s\n' "$CREDS" | head -1)
ADMIN_EMAIL=${ADMIN_LINE%% *}
ADMIN_PASSWORD=${ADMIN_LINE#* }
DOMAIN=${ADMIN_EMAIL#*@}
EVENTS="events@$DOMAIN"
EV_SUBJECT="$NONCE-events"

if ssh "$SSH_HOST" "python3 - '$HOST' '$EVENTS' '$EV_SUBJECT'" <<'PY'
import smtplib, sys
host, rcpt, subject = sys.argv[1:4]
msg = (f"From: release-check@example.net\r\nTo: {rcpt}\r\nSubject: {subject}\r\n"
       f"Message-ID: <{subject}@release-check.example.net>\r\n\r\nDept mailbox verification.\r\n")
try:
    with smtplib.SMTP(host, 25, timeout=30) as s:
        s.sendmail("release-check@example.net", [rcpt], msg.encode())
except Exception as e:
    print(f"SMTP refused {rcpt}: {e}", file=sys.stderr)
    sys.exit(1)
PY
then
  FOUND=""
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    if curl -s --max-time 15 --user "$ADMIN_EMAIL:$ADMIN_PASSWORD"         "imaps://$HOST/Demo%20Repository%2FDepartments%2FEvents%2FMailbox%2FInbox?SUBJECT%20$EV_SUBJECT" | grep -q '[0-9]'; then
      FOUND=yes
      break
    fi
    sleep 3
  done
  if [ -n "$FOUND" ]; then
    echo "OK   $EVENTS — department mailbox received into its Inbox"
  else
    echo "FAIL $EVENTS — accepted at SMTP but not found in the department Inbox over IMAP"
    FAILED=1
  fi
else
  echo "FAIL $EVENTS — refused at SMTP"
  FAILED=1
fi

if [ "$FAILED" = 0 ]; then
  echo "All advertised mailboxes receive."
fi
exit $FAILED
