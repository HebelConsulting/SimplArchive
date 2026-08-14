#!/usr/bin/env bash
# Regenerates every desktop launcher icon from the design tokens (ADR 0578).
#
# Run this after changing the brand accent: the art (a filing cabinet on a rounded tile) takes its colours from
# src/SimplArchive.Theming/tokens.json, so an icon left ungenerated contradicts the application it launches.
#
# The results are COMMITTED. Packaging reads them from the repository and needs no image tools installed —
# adding ImageMagick to the packaging preflight once broke the Docker image build and with it the Trivy scan,
# which is why the .ico and .icns containers are written in .NET rather than shelled out to.
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project src/SimplArchive.DesktopClient -- --gen-icons "src/SimplArchive.DesktopClient/Assets" "$@"
