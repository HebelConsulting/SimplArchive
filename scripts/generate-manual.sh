#!/usr/bin/env bash
#
# Regenerate the SimplArchive user manual (ADR 0502): capture fresh screenshots from the real app, then compile the
# Typst sources to the served PDF.
#
#   scripts/generate-manual.sh [--desktop-only]
#
#     (default)        capture BOTH the desktop (Avalonia, headless — cheap) and web (Blazor, via Testcontainers +
#                      Chrome — needs Docker + a system Google Chrome) screens, then compile.
#     --desktop-only   capture only the desktop screens, then compile against the committed web screenshots. This
#                      is the cheap PR-gate path (no Docker/Chrome).
#
# Outputs:
#   manual/screenshots/{desktop,web}-*.png          (regenerated screenshots)
#   src/SimplArchive.Api/wwwroot/download/manual/SimplArchive-Manual.pdf   (the served manual)
#
# Requires: the .NET SDK, `typst` on PATH, and (for the web capture) Docker + Google Chrome.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

mode="--desktop --web"
if [[ "${1:-}" == "--desktop-only" ]]; then
  mode="--desktop"
fi

if ! command -v typst >/dev/null 2>&1; then
  echo "error: 'typst' not found on PATH — install it (e.g. 'brew install typst' or see typst.app)." >&2
  exit 1
fi

pdf_out="src/SimplArchive.Api/wwwroot/download/manual/SimplArchive-Manual.pdf"
mkdir -p manual/screenshots "$(dirname "$pdf_out")"

echo "==> Building the capture harness (and the app projects it drives)…"
dotnet build tests/SimplArchive.ManualCapture/SimplArchive.ManualCapture.csproj -clp:ErrorsOnly

echo "==> Capturing screenshots ($mode)…"
# shellcheck disable=SC2086
dotnet run --project tests/SimplArchive.ManualCapture --no-build -- $mode --out manual/screenshots

# A regenerated PNG whose every pixel is within ±1 of the committed one is raster noise, not a change (#832).
# Runs BEFORE the compile below, so neither the PNG nor the PDF that embeds it churns over nothing.
python3 scripts/keep-unchanged-screenshots.py manual/screenshots

echo "==> Compiling the Typst manual → $pdf_out"
# Reproducible build (ADR 0510): pin the compile timestamp so the PDF's internal CreationDate/ModDate and the
# `datetime.today()` copyright year are byte-stable, not the wall clock. Matches the fixed demo/screenshot clock
# (2026-06-01T09:00:00Z) the screenshots were captured under.
SOURCE_DATE_EPOCH=1780304400 typst compile manual/manual.typ "$pdf_out" --root .

echo "==> Done. Served at /download/manual/ (browse /download or open $pdf_out)."
