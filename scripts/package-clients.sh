#!/usr/bin/env bash
#
# Stage the SimplArchive desktop-client packages into the served download area (ADR 0490):
#   src/SimplArchive.Api/wwwroot/download/clients/{windows,linux,macos}/
# named with the git release tag (if HEAD is exactly on a tag) or the short commit SHA — matching the naming the
# Dockerfile bakes in (SimplArchive-<version>-{win-x64.zip,linux-x64.tar.gz,arm64.dmg,x64.dmg}).
#
# The Dockerfile already bakes win-x64 + linux-x64 into the published image on every build, so this script is for:
#   - populating the folders for a LOCAL `dotnet run` of the API (the API serves its own wwwroot), and
#   - producing the macOS .dmg (which a Linux Docker build can't) — run this on a Mac to drop real .dmg files into
#     clients/macos/ (otherwise that folder just links to the GitHub Release).
#
# Runs scripts/package-windows-linux.sh always; scripts/package-macos-dmg.sh only when on macOS.
#
# Usage:  scripts/package-clients.sh
#
# The staged artifacts are gitignored (build outputs) — see .gitignore.

set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

# Version = the exact release tag (leading "v" stripped) if HEAD is tagged, else the short commit SHA.
tag="$(git describe --tags --exact-match 2>/dev/null || true)"
if [[ -n "$tag" ]]; then
  version="${tag#v}"
else
  version="$(git rev-parse --short HEAD)"
fi
echo "==> Packaging desktop clients as version: $version"

dest="src/SimplArchive.Api/wwwroot/download/clients"

# Windows + Linux (cross-buildable on any OS).
scripts/package-windows-linux.sh "$version"
mkdir -p "$dest/windows" "$dest/linux"
rm -f "$dest/windows/SimplArchive-"*.zip "$dest/linux/SimplArchive-"*.tar.gz
cp "dist/SimplArchive-${version}-win-x64.zip" "$dest/windows/"
cp "dist/SimplArchive-${version}-linux-x64.tar.gz" "$dest/linux/"

# macOS .dmg — only on a Mac (needs hdiutil/codesign).
if [[ "$(uname)" == "Darwin" ]]; then
  scripts/package-macos-dmg.sh "$version"
  mkdir -p "$dest/macos"
  rm -f "$dest/macos/SimplArchive-"*.dmg
  cp "dist/SimplArchive-${version}-arm64.dmg" "dist/SimplArchive-${version}-x64.dmg" "$dest/macos/"
else
  echo "==> Skipping macOS .dmg (not on macOS) — clients/macos/ keeps its GitHub-Release link."
fi

echo "==> Staged into $dest/"
ls -R "$dest"
