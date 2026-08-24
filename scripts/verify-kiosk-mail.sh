#!/usr/bin/env bash
# Verify that mail actually reaches every advertised demo mailbox, after a kiosk rollout — and that an address
# that is NOT a user bounces rather than being swallowed.
#
# WHY THIS EXISTS. A release ships the IMAGE, and mail ingress depends on almost nothing that is in it: the MTA
# is a compose sidecar on the host, its TLS certificate is Caddy's and is re-resolved at every reset, the LMTP
# port is host config, and the tenant's mail DOMAIN is a row that `mail-override.sh` INSERTs directly — the
# product has no surface for registering one (#667). All of that lives outside the image, so a good release can
# roll out onto a stack where mail silently stops arriving while every other rollout check stays green: the OCI
# labels are right, the site serves, the logins work.
#
# WHAT IT CHECKS, per advertised address:
#
#   accepted   the Postfix QUEUE ID's outcome — `status=sent` for a real user (the app's LMTP answered
#              "250 delivered": accepted and filed), `status=bounced` for a non-user ("550 no such recipient
#              here", which ADR 0628 requires).
#   readable   the message is then found over IMAP as that user, which is the hop `status=sent` cannot see.
#
# Plus the seeded DEPARTMENT mailbox (`events@<domain>`, ADR 0684), read through the first advertised user's
# IMAP view: it is the showcase address, and nothing else here would notice if it stopped receiving.
#
# THREE THINGS THAT ARE NOT OBVIOUS, each of which produced a wrong answer before it was understood:
#
# 1. THE SEND MUST RUN INSIDE THE POSTFIX CONTAINER. The live policy is
#       mynetworks                = 127.0.0.0/8 [::1]/128
#       smtpd_client_restrictions = permit_mynetworks reject_unknown_client_hostname
#    so any client without reverse DNS is refused before the recipient is even considered. A developer laptop
#    is refused (`450 4.7.25 cannot find your hostname`) and so is the kiosk HOST, which reaches the published
#    port as the docker bridge gateway 172.18.0.1. Only a client speaking to 127.0.0.1 from inside the
#    container is permitted. That exclusion is not a gap in the check: the anti-spam policy is fixed config no
#    release changes, and a real sending MTA has valid rDNS and is accepted — which is why mail from the
#    outside world works while this script's first version concluded, wrongly, that ingress was broken.
#
# 2. A NON-USER IS ACCEPTED AT RCPT AND BOUNCES LATER. The domain is a Postfix VIRTUAL domain, so smtpd queues
#    the message and the refusal comes from the app's LMTP at delivery time. Asserting a 550 at RCPT therefore
#    tests nothing — it never arrives — which is why the outcome is read from the log rather than the session.
#
# 3. IMAP READ-BACK *IS* AVAILABLE, for the demo tenant. An earlier version of this file stated the opposite:
#    IMAP authenticates against `User.ImapPasswordHash`, a separately generated password shown once (ADR 0594),
#    so no automated caller has one — TRUE in general, and FALSE here, because `DemoDataSeeder` seeds that hash
#    with the demo password for exactly these users. So the advertised credential does open IMAP on the kiosk,
#    and the stronger proof is available after all. Do not carry this leg into a check against a real tenant.
#
# THE HISTORY IS THE POINT OF THIS PARAGRAPH. Two versions of this script have existed under one name: the
# queue-id/bounce check above, and a later IMAP read-back that REPLACED it wholesale and silently dropped the
# negative case, leaving the runbook describing an expected output that could no longer be printed. Both halves
# are real, they check different hops, and they are now one script. When adding a leg here, add it — the file
# already proves how easy it is to overwrite research by rewriting rather than extending.
#
# Recipients come from publish/README.public.md, filtered to the host under test, for the same reason
# verify-kiosk-logins.sh reads its credentials there: a list hard-coded here would go stale exactly the way the
# host does, and the point is to check what we PUBLISH against what a visitor EXPERIENCES.
#
# Usage:  scripts/verify-kiosk-mail.sh [base-url]      (default https://demo.simplarchive.dev)
# Exit:   0 = every advertised mailbox received AND served its message, and a non-user bounced
#         1 = at least one did not (or a non-user was accepted)
#         2 = the host or the MTA could not be reached — nothing was learned about delivery
#
# Env:    KIOSK_SSH   ssh target that runs the send (default "kiosk")

set -u

KIOSK_SSH="${KIOSK_SSH:-kiosk}"
BASE_URL="${1:-https://demo.simplarchive.dev}"
BASE_URL="${BASE_URL%/}"
HOST="${BASE_URL#*://}"
HOST="${HOST%%/*}"
MTA="simplarchive-postfix-1"
README="$(cd "$(dirname "$0")/.." && pwd)/publish/README.public.md"

if [ ! -f "$README" ]; then
  echo "FAIL: $README not found — this check reads the advertised users from it." >&2
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Every advertised address ON THIS HOST's domain, with its password. Same tolerant backtick parse as
# verify-kiosk-logins.sh — the table has been reshaped twice, and a parser that must be re-taught its layout is
# one that gets deleted.
awk '
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
' "$README" | awk -v host="$HOST" '$1 ~ ("@" host "$")' | sort -u > "$WORK/users.txt"

COUNT=$(wc -l < "$WORK/users.txt" | tr -d ' ')
if [ "$COUNT" = "0" ]; then
  # Never pass on an empty list: "no users found" is indistinguishable from a README whose table moved.
  echo "FAIL: no users at @$HOST parsed from publish/README.public.md — has the live-demo table changed shape?" >&2
  exit 1
fi

# PREFLIGHT, once, before asking anything about delivery — otherwise "the MTA is down" arrives as N delivery
# failures, which is the wrong question answered confidently.
if ! ssh -n -o ConnectTimeout=15 "$KIOSK_SSH" "docker ps --filter name=$MTA --format '{{.Names}}'" 2>/dev/null \
      | grep -q "$MTA"; then
  echo "UNREACHABLE: $MTA is not running on $KIOSK_SSH (or the host is unreachable)." >&2
  echo "Nothing was learned about delivery — this is not a mail-routing problem:" >&2
  echo "  ssh $KIOSK_SSH 'docker ps -a --filter name=postfix --format \"{{.Status}}\"'" >&2
  exit 2
fi

TOKEN="rollout-$(date -u +%Y%m%d%H%M%S)-$$"
echo "Verifying mail for $COUNT advertised mailbox(es) at $HOST  [token $TOKEN]"

# Sends one message from inside the container and echoes the whole SMTP transcript. `-i 1` paces nc: without
# it the socket can close before Postfix replies, leaving a transcript with no queue id to correlate on.
# `ssh -n` throughout, because ssh otherwise consumes this script's own stdin — which silently swallowed the
# rest of the recipient list and made a three-user check test exactly one user.
send_to() {
  ssh -n -o ConnectTimeout=15 "$KIOSK_SSH" "docker exec -i $MTA sh -c '
      {
        echo \"EHLO rollout.check\"
        echo \"MAIL FROM:<rollout@example.invalid>\"
        echo \"RCPT TO:<$1>\"
        echo \"DATA\"
        echo \"From: SimplArchive rollout check <rollout@example.invalid>\"
        echo \"To: $1\"
        echo \"Subject: $2\"
        echo \"\"
        echo \"Automated rollout verification. Safe to delete.\"
        echo \".\"
        echo \"QUIT\"
      } | nc -i 1 127.0.0.1 25
    '" 2>&1
}

# The queue id Postfix assigns on accepting DATA ("250 2.0.0 Ok: queued as 3F2A17008B5"). Correlating on it is
# what keeps two runs — or two recipients — from reading each other's delivery lines.
queue_id() {
  grep -oE 'queued as [0-9A-F]+' "$1" | tail -1 | awk '{print $3}'
}

# The queue id's delivery outcome: "sent" / "bounced" / "" while still queued.
outcome_of() {
  ssh -n -o ConnectTimeout=15 "$KIOSK_SSH" \
    "docker logs --tail 400 $MTA 2>&1 | grep '$1:' | grep -oE 'status=[a-z]+' | tail -1" 2>/dev/null \
    | sed 's/status=//'
}

# Finds the subject in a mailbox over IMAP. Polls: filing is asynchronous, and one immediate read is a race
# that passes on an idle host and fails on a loaded one.
imap_finds() {
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    if curl -s --max-time 15 --user "$1:$2" "imaps://$HOST/$3?SUBJECT%20$4" | grep -q '[0-9]'; then
      return 0
    fi
    sleep 3
  done
  return 1
}

# Waits for a queue id to reach a terminal state, then reports it.
settled_outcome() {
  OUTCOME=""
  ATTEMPT=0
  while [ "$ATTEMPT" -lt 15 ] && [ -z "$OUTCOME" ]; do
    ATTEMPT=$((ATTEMPT + 1))
    OUTCOME="$(outcome_of "$1")"
    [ -z "$OUTCOME" ] && sleep 2
  done
  echo "$OUTCOME"
}

FAILED=0

while read -r RCPT PASSWORD; do
  SUBJECT="$TOKEN-${RCPT%%@*}"
  send_to "$RCPT" "$SUBJECT" > "$WORK/send.txt"
  QID="$(queue_id "$WORK/send.txt")"

  if [ -z "$QID" ]; then
    echo "  FAIL  $RCPT — the MTA never queued the message" >&2
    grep -E '^[0-9]{3} ' "$WORK/send.txt" | tail -3 | sed 's/^/          /' >&2
    FAILED=1
    continue
  fi

  OUTCOME="$(settled_outcome "$QID")"
  if [ "$OUTCOME" != "sent" ]; then
    if [ -z "$OUTCOME" ]; then
      echo "  FAIL  $RCPT — queued as $QID and still undelivered after 30s" >&2
      echo "          The MTA accepted it, so this is LMTP or filing, not SMTP:" >&2
      echo "          ssh $KIOSK_SSH 'docker logs --tail 40 $MTA | grep $QID'" >&2
    else
      echo "  FAIL  $RCPT — expected sent, got $OUTCOME ($QID)" >&2
      echo "          A real user's mail is being refused; the tenant mail domain row is the usual cause (#667)." >&2
    fi
    FAILED=1
    continue
  fi

  # Delivered AND servable. status=sent is the app accepting it; only IMAP says the user can read it.
  if imap_finds "$RCPT" "$PASSWORD" "INBOX" "$SUBJECT"; then
    echo "  ok    $RCPT — delivered and readable over IMAP ($QID)"
  else
    echo "  FAIL  $RCPT — LMTP accepted it ($QID) but IMAP does not serve it" >&2
    FAILED=1
  fi
done < "$WORK/users.txt"

# The seeded department mailbox (ADR 0684): the showcase address, derived from the advertised domain the way the
# seeder derives it. Delivery lands in ITS lazily-created Inbox, which projects into IMAP for anyone with rights
# on the repository — read here as the first advertised (admin) user.
ADMIN_LINE=$(head -1 "$WORK/users.txt")
ADMIN_EMAIL=${ADMIN_LINE%% *}
ADMIN_PASSWORD=${ADMIN_LINE#* }
EVENTS="events@${ADMIN_EMAIL#*@}"
EV_SUBJECT="$TOKEN-events"

send_to "$EVENTS" "$EV_SUBJECT" > "$WORK/send.txt"
EV_QID="$(queue_id "$WORK/send.txt")"
if [ -n "$EV_QID" ] && [ "$(settled_outcome "$EV_QID")" = "sent" ] \
   && imap_finds "$ADMIN_EMAIL" "$ADMIN_PASSWORD" "Demo%20Repository%2FDepartments%2FEvents%2FMailbox%2FInbox" "$EV_SUBJECT"; then
  echo "  ok    $EVENTS — department mailbox received into its Inbox"
else
  echo "  FAIL  $EVENTS — the department mailbox did not receive (or does not serve) the message" >&2
  FAILED=1
fi

# The NEGATIVE case, last: an address that is not a user must BOUNCE. Swallowing it looks identical to
# delivering it from the sender's side, and is the failure ADR 0628 names explicitly.
NOBODY="definitely-not-a-user-$TOKEN@${ADMIN_EMAIL#*@}"
send_to "$NOBODY" "$TOKEN-nobody" > "$WORK/send.txt"
NO_QID="$(queue_id "$WORK/send.txt")"
NO_OUTCOME="$(settled_outcome "$NO_QID")"

if [ "$NO_OUTCOME" = "bounced" ]; then
  echo "  ok    a non-existent recipient bounced ($NO_QID)"
else
  echo "  FAIL  a non-existent recipient was not bounced — got '${NO_OUTCOME:-still queued}' ($NO_QID)" >&2
  echo "          The MTA is swallowing mail for unknown recipients rather than refusing it (ADR 0628)." >&2
  FAILED=1
fi

if [ "$FAILED" = "1" ]; then
  echo "Mail ingress is not healthy." >&2
  exit 1
fi

echo "All advertised mailboxes receive, are readable over IMAP, and unknown recipients bounce."
