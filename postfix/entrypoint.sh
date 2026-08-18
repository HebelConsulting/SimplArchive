#!/bin/sh
# Postfix configuration for SimplArchive (ADR 0628). Generated at start from the environment so the image
# carries no deployment's settings, and so the one thing that must not be duplicated — the list of accepted
# domains — is read from the application's own database rather than restated here.
set -eu

: "${LMTP_TARGET:?LMTP_TARGET is required, e.g. api:2525}"
: "${MYHOSTNAME:=mail.localhost}"
: "${DB_HOST:?DB_HOST is required}"
: "${DB_NAME:?DB_NAME is required}"
: "${DB_USER:?DB_USER is required}"
: "${DB_PASSWORD:?DB_PASSWORD is required}"

# The accepted-domain map. Postfix asks this question for every inbound recipient, and it is answered by the
# SAME table the application resolves against — so the two configurations ADR 0628 warns about drifting are
# one configuration, and there is nothing to keep in step.
#
# UPPER('%s') matches how NormalizedDomain is stored: a mail domain is case-insensitive, so the raw column
# cannot be the key. Get this wrong and every recipient is rejected while the table looks correct.
cat > /etc/postfix/pgsql-virtual-domains.cf <<EOF
hosts = ${DB_HOST}
dbname = ${DB_NAME}
user = ${DB_USER}
password = ${DB_PASSWORD}
query = SELECT 1 FROM "TenantMailDomains" WHERE "NormalizedDomain" = UPPER('%s')
EOF
chmod 640 /etc/postfix/pgsql-virtual-domains.cf

postconf -e "myhostname = ${MYHOSTNAME}"
postconf -e "mydestination ="
postconf -e "virtual_mailbox_domains = pgsql:/etc/postfix/pgsql-virtual-domains.cf"

# Everything for an accepted domain goes to our listener. LMTP rather than SMTP because it has no queue of its
# own and replies PER RECIPIENT — which is what makes the contract between the two components explicit.
postconf -e "virtual_transport = lmtp:inet:${LMTP_TARGET}"

# Our 550 for an unknown recipient must reach the SENDER as a real bounce, which is the whole reason we answer
# 550 rather than accepting silently. Postfix generates it from our reply.
postconf -e "smtpd_reject_unlisted_recipient = yes"

# Relay nothing. We RECEIVE; we do not send (ADR 0628) — no outbound SMTP, no reputation to defend, and above
# all no open relay, which is what an internet-facing MTA becomes if this is left permissive.
postconf -e "mynetworks = 127.0.0.0/8 [::1]/128"
postconf -e "smtpd_relay_restrictions = permit_mynetworks reject_unauth_destination"
postconf -e "relayhost ="

# Standard hostile-input hygiene. These are the protections that justify having an MTA at all rather than
# writing our own receiver.
postconf -e "smtpd_helo_required = yes"
postconf -e "disable_vrfy_command = yes"
postconf -e "smtpd_client_restrictions = permit_mynetworks reject_unknown_client_hostname"
postconf -e "message_size_limit = ${MAX_MESSAGE_BYTES:-36700160}"

# Opportunistic TLS for inbound mail when a certificate is provided. Without one it stays plaintext, which is
# what a LAN/demo deployment gets; a public deployment supplies a real certificate.
if [ -n "${TLS_CERT_FILE:-}" ] && [ -n "${TLS_KEY_FILE:-}" ]; then
  postconf -e "smtpd_tls_cert_file = ${TLS_CERT_FILE}"
  postconf -e "smtpd_tls_key_file = ${TLS_KEY_FILE}"
  postconf -e "smtpd_tls_security_level = may"
else
  echo "postfix: no TLS certificate configured — inbound mail is accepted in plaintext." >&2
  echo "postfix: set TLS_CERT_FILE/TLS_KEY_FILE for a deployment reachable from the internet." >&2
fi

# Log to stdout so the container's logs are the mail logs (12-factor, as the rest of the stack does).
postconf -e "maillog_file = /dev/stdout"

exec postfix start-fg
