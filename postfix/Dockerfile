# The MTA (ADR 0628): it terminates SMTP from the open internet, applies the protections mature mail software
# has spent thirty years developing, and hands us LMTP on a private network. We never see a byte that has not
# already been through software designed for that job.
#
# Built from the distribution's own package rather than pulled from a community image (ADR 0611): this is the
# most exposed service in the stack, so its provenance is Alpine's package database — queryable, upgradable and
# auditable by the means an administrator already has — rather than one maintainer's account.
FROM alpine:3.21

# postfix-pgsql is the map type that lets Postfix read the accepted domains straight out of the application's
# own table. That is not a convenience: it removes the SECOND list, and with it the drift ADR 0628 warns about
# in both directions — accepting a domain we cannot resolve (mail taken from the internet, then rejected) or
# missing one we can (mail that never arrives, with nothing to show for it).
RUN apk add --no-cache postfix postfix-pgsql ca-certificates

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# 25 is not published to the host by the compose stack. Reaching this from the internet is a deliberate,
# documented act by an operator who controls a public IP, correct reverse DNS and an MX record.
EXPOSE 25

ENTRYPOINT ["/entrypoint.sh"]
