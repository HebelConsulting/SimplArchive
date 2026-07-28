# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy all project files first (preserving relative paths, needed since Api's project references must resolve
# during restore) so the restore layer is cached independently of source-code changes.
COPY ["src/SimplArchive.Localization/SimplArchive.Localization.csproj", "src/SimplArchive.Localization/"]
COPY ["src/SimplArchive.Domain/SimplArchive.Domain.csproj", "src/SimplArchive.Domain/"]
COPY ["src/SimplArchive.Application/SimplArchive.Application.csproj", "src/SimplArchive.Application/"]
COPY ["src/SimplArchive.Infrastructure/SimplArchive.Infrastructure.csproj", "src/SimplArchive.Infrastructure/"]
COPY ["src/SimplArchive.Auth/SimplArchive.Auth.csproj", "src/SimplArchive.Auth/"]
COPY ["src/SimplArchive.Client/SimplArchive.Client.csproj", "src/SimplArchive.Client/"]
COPY ["src/SimplArchive.Api/SimplArchive.Api.csproj", "src/SimplArchive.Api/"]
RUN dotnet restore "src/SimplArchive.Api/SimplArchive.Api.csproj"

COPY . .
RUN dotnet publish "src/SimplArchive.Api/SimplArchive.Api.csproj" -c Release -o /app/publish --no-restore


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
