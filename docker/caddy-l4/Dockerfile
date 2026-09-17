# Caddy with the layer-4 (TCP/UDP) app, so the dev/LAN stack can front IMAP as well as HTTP (#1245).
#
# WHY WE BUILD RATHER THAN PULL. `caddy-l4` is a plugin and is not in the official `caddy:` image; adding it
# needs an `xcaddy` build. That makes this a workaround dependency in the standing sense — so the trigger that
# retires it is written down: **when upstream Caddy ships layer-4 proxying in the stock image, delete this
# directory and point the compose service back at `caddy:<version>-alpine`.**
#
# LICENCE. Caddy is Apache-2.0 and `caddy-l4` is Apache-2.0 (verified 2026-09-17 by reading its LICENSE), both
# compatible with this project's constraint. Note that the CI licence gate CANNOT see this: it scans the NuGet
# transitive tree and has no visibility into Go modules baked into an image. So this check is manual, it is
# done, and it needs redoing only if the plugin set changes.
#
# THE VERSION IS PINNED IN `images.env` (`CADDY_VERSION`) and passed in as a build argument, so the pin stays
# where every other third-party image version lives rather than being buried in a Dockerfile nobody greps.
ARG CADDY_VERSION=2.11.4

FROM caddy:${CADDY_VERSION}-builder AS builder
RUN xcaddy build --with github.com/mholt/caddy-l4

FROM caddy:${CADDY_VERSION}-alpine
# Replace the stock binary with the one carrying the layer-4 app. Everything else about the image — entrypoint,
# config path, data volumes — is unchanged, so the service definition and Caddyfile stay ordinary Caddy.
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
