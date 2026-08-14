#!/usr/bin/env bash
# Regenerates the clients' theme files from src/SimplArchive.Theming/tokens.json (ADR 0578).
#
# Run this after changing a token. The generated files are checked in — ThemeGenerationTests regenerates them
# in memory and fails the build when what is committed does not match, so a hand edit is caught rather than
# silently shipped.
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project tests/SimplArchive.ThemeGen "$@"
