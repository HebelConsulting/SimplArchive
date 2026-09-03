# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Version stamped into the desktop-client download artifacts + their file names (ADR 0490) and into the API
# assembly, which surfaces it as GET /api's serverVersion (ADR 0512). CI passes the release tag
# (docker/metadata-action's {{version}}). The Dockerfile can't derive it from git — .git is excluded from the
# build context (.dockerignore).
#
# The default is the non-release sentinel, matching the Api project's own default. It used to be 0.1.0 — a REAL
# past release — so an image built without --build-arg was indistinguishable from a genuine v0.1.0 deployment,
# which is a plausible-looking lie rather than an admission of not knowing (issue #425).
ARG VERSION=0.0.0-dev

# Copy all project files first (preserving relative paths, needed since Api's project references must resolve
# during restore) so the restore layer is cached independently of source-code changes.
COPY ["src/SimplArchive.Localization/SimplArchive.Localization.csproj", "src/SimplArchive.Localization/"]
COPY ["src/SimplArchive.ModuleAbi/SimplArchive.ModuleAbi.csproj", "src/SimplArchive.ModuleAbi/"]
COPY ["src/SimplArchive.Presentation/SimplArchive.Presentation.csproj", "src/SimplArchive.Presentation/"]
COPY ["src/SimplArchive.Theming/SimplArchive.Theming.csproj", "src/SimplArchive.Theming/"]
COPY ["src/SimplArchive.Domain/SimplArchive.Domain.csproj", "src/SimplArchive.Domain/"]
COPY ["src/SimplArchive.Application/SimplArchive.Application.csproj", "src/SimplArchive.Application/"]
COPY ["src/SimplArchive.Infrastructure/SimplArchive.Infrastructure.csproj", "src/SimplArchive.Infrastructure/"]
COPY ["src/SimplArchive.Auth/SimplArchive.Auth.csproj", "src/SimplArchive.Auth/"]
COPY ["src/SimplArchive.Client/SimplArchive.Client.csproj", "src/SimplArchive.Client/"]
COPY ["src/SimplArchive.Api/SimplArchive.Api.csproj", "src/SimplArchive.Api/"]
RUN dotnet restore "src/SimplArchive.Api/SimplArchive.Api.csproj"

COPY . .
# -p:Version=$VERSION stamps the API assembly's InformationalVersion with the release tag so the /api discovery
# document reports the real server version (ADR 0512, for the desktop self-update check); defaults to 0.1.0 locally.
RUN dotnet publish "src/SimplArchive.Api/SimplArchive.Api.csproj" -c Release -o /app/publish --no-restore -p:Version=$VERSION

# (The desktop-client archives are built in their own stage below and copied into the final image — they used to
#  be produced here, which made the ARM leg build them under emulation. See the `clients` stage for why.)


# ── Desktop-client archives, built ONCE on the BUILD platform (#701) ─────────────────────────────────────────
#
# `--platform=$BUILDPLATFORM` is the whole point: this stage runs on the machine doing the building, never on
# the image's target architecture, so it is never emulated. What it produces — win-x64 and linux-x64 archives —
# is x64 content either way, so there was never a reason to produce it twice, let alone once under QEMU.
#
# Measured on v0.5.1, when this ran inside the per-architecture build stage:
#
#     linux/amd64    99 s
#     linux/arm64  1350 s      — 13.6x, emulated
#
# Twenty-two minutes of emulated ARM for artefacts identical to the ones the amd64 leg had just built. That one
# step was most of the margin against the publish job's `timeout-minutes: 60`: the v0.5.1 run was cancelled at
# exactly sixty minutes, having pushed the image three minutes earlier and been killed during the cache export.
#
# Both final images still carry the archives, so /download/clients/{windows,linux}/ keeps working and downloads
# stay in lock-step with the API image (ADR 0490). macOS is unaffected — a Linux build cannot produce a .dmg, so
# clients/macos/ links to the GitHub Release.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS clients
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY . .
RUN apk add --no-cache bash zip tar \
 && bash scripts/package-windows-linux.sh "$VERSION"


FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
ARG VERSION=0.0.0-dev
WORKDIR /app

# icu-libs: proper globalization/culture handling for multi-tenant deployments with varying locales
# curl: used by the container HEALTHCHECK below
# krb5-libs: provides libgssapi_krb5.so.2, which Npgsql probes for at startup (GSSAPI/Kerberos auth); without it
#   the app logs a harmless "Error loading shared library libgssapi_krb5.so.2" on every boot even though we use
#   password auth. Adding it silences that noise.
# Non-root 'app' user/group already exist in this base image; no addgroup/adduser needed.
#
# `apk upgrade` FIRST, and it is not belt-and-braces: the base image is rebuilt on its own schedule, so between
# rebuilds it carries whatever its packages were at that moment. When an advisory lands against one of them —
# CVE-2026-14456 in libcrypto3/libssl3, HIGH, fixed in 3.5.8-r0 while the base still had 3.5.7-r0 — the image
# scan fails for a package we never chose and cannot bump, and it blocks EVERY pull request until the base
# happens to be refreshed. Upgrading at build time takes the patched package as soon as Alpine publishes it,
# which is both the real fix and one that keeps working for the next advisory of this kind rather than needing
# a fresh .trivyignore entry each time.
#
# The build and clients stages are deliberately left alone: they are SDK images that never ship, and only this
# final stage is published and scanned.
RUN apk upgrade --no-cache && apk add --no-cache icu-libs curl krb5-libs

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

# The archives from the build-platform stage. The .gitkeep placeholders that kept the (otherwise-empty) folders
# in git are removed so they do not clutter the served listing.
COPY --from=clients /src/dist/SimplArchive-${VERSION}-win-x64.zip wwwroot/download/clients/windows/
COPY --from=clients /src/dist/SimplArchive-${VERSION}-linux-x64.tar.gz wwwroot/download/clients/linux/
RUN rm -f wwwroot/download/clients/windows/.gitkeep wwwroot/download/clients/linux/.gitkeep

USER app
EXPOSE 8080

# Kubernetes deployments use their own separate liveness/readiness/startup probes against /health/live and
# /health/ready instead of relying on this; this HEALTHCHECK covers plain `docker run` / docker-compose usage.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "SimplArchive.Api.dll"]
