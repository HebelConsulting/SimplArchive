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

# Bake the desktop-client packages into the served /download area so downloads stay in lock-step with this API
# image (ADR 0490). Only win-x64 + linux-x64 — a Linux build can't produce the macOS .dmg (clients/macos/ links to
# the GitHub Release instead). Needs bash + zip + tar for scripts/package-windows-linux.sh. The .gitkeep
# placeholders that kept the (otherwise-empty) folders in git are removed so they don't clutter the listing.
RUN apk add --no-cache bash zip tar \
 && bash scripts/package-windows-linux.sh "$VERSION" \
 && cp dist/SimplArchive-"$VERSION"-win-x64.zip /app/publish/wwwroot/download/clients/windows/ \
 && cp dist/SimplArchive-"$VERSION"-linux-x64.tar.gz /app/publish/wwwroot/download/clients/linux/ \
 && rm -f /app/publish/wwwroot/download/clients/windows/.gitkeep /app/publish/wwwroot/download/clients/linux/.gitkeep \
 && rm -rf dist


FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# icu-libs: proper globalization/culture handling for multi-tenant deployments with varying locales
# curl: used by the container HEALTHCHECK below
# krb5-libs: provides libgssapi_krb5.so.2, which Npgsql probes for at startup (GSSAPI/Kerberos auth); without it
#   the app logs a harmless "Error loading shared library libgssapi_krb5.so.2" on every boot even though we use
#   password auth. Adding it silences that noise.
# Non-root 'app' user/group already exist in this base image; no addgroup/adduser needed.
RUN apk add --no-cache icu-libs curl krb5-libs

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

USER app
EXPOSE 8080

# Kubernetes deployments use their own separate liveness/readiness/startup probes against /health/live and
# /health/ready instead of relying on this; this HEALTHCHECK covers plain `docker run` / docker-compose usage.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "SimplArchive.Api.dll"]
