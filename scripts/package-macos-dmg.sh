#!/usr/bin/env bash
#
# Package the SimplArchive DesktopClient (Avalonia) as native macOS .app bundles + .dmg installers, one
# each for Apple Silicon (arm64) and Intel (x64). See ADR "macOS .dmg packaging for the desktop client".
#
#   - Self-contained: bundles the .NET runtime, so the target Mac needs no .NET installed.
#   - Ad-hoc code-signed (`codesign -s -`): needs NO Apple Developer account, and satisfies the Apple
#     Silicon loader's requirement that binaries carry *some* valid signature to launch.
#   - Packaged with the built-in `hdiutil` (no Homebrew dependency); each .dmg contains the .app plus an
#     /Applications symlink for drag-to-install.
#   - UNSIGNED in the Developer-ID sense / NOT notarized — Gatekeeper warns on first open (this fits the
#     project's "Not for production" showcase posture; the workaround is printed at the end).
#
# Usage:   scripts/package-macos-dmg.sh [version] [--upload]
#            version   optional (default 0.1.0) — stamped into the bundle + the .dmg file names.
#            --upload  after building, attach the two .dmgs to the existing GitHub Release v<version> on the
#                      public repo (issue #306). The macOS .dmg CI job is opt-in (needs a pricey macos runner),
#                      so in the common case you build + upload from a Mac; the release itself is created by the
#                      desktop-packages workflow on the v* tag push (with the win/linux archives). Needs `gh`
#                      authenticated with write access. The target repo defaults to HebelConsulting/SimplArchive
#                      (override with RELEASE_REPO=…). Idempotent — re-runs replace the assets (--clobber).
#
# Output:  dist/SimplArchive-<version>-<arch>.dmg   (arch = arm64 | x64)
#
# Must run on macOS (needs the .app bundle format + hdiutil + codesign).

set -euo pipefail

VERSION="0.1.0"
UPLOAD=0
for arg in "$@"; do
  case "$arg" in
    --upload) UPLOAD=1 ;;
    -*) echo "error: unknown option '$arg' (usage: package-macos-dmg.sh [version] [--upload])." >&2; exit 2 ;;
    *) VERSION="$arg" ;;
  esac
done

APP_NAME="SimplArchive"
EXE_NAME="SimplArchive.DesktopClient"          # the apphost `dotnet publish` produces (the project name)
BUNDLE_ID="ch.hebelconsulting.simplarchive"    # reverse-DNS bundle identifier
PROJECT="src/SimplArchive.DesktopClient/SimplArchive.DesktopClient.csproj"
ICNS="src/SimplArchive.DesktopClient/Assets/SimplArchive.icns"
OUT_DIR="dist"

if [[ "$(uname)" != "Darwin" ]]; then
  echo "error: this script must run on macOS (it needs the .app bundle format, hdiutil and codesign)." >&2
  exit 1
fi

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
mkdir -p "$OUT_DIR"

# Build one architecture: publish -> assemble the .app -> ad-hoc sign -> create the .dmg.
build_one() {
  local rid="$1" arch_label="$2"
  local publish_dir="$OUT_DIR/publish-$arch_label"
  local stage="$OUT_DIR/stage-$arch_label"
  local app_dir="$stage/${APP_NAME}.app"
  local dmg="$OUT_DIR/${APP_NAME}-${VERSION}-${arch_label}.dmg"

  echo "==> [$arch_label] Publishing $EXE_NAME ($rid, self-contained, Release)…"
  rm -rf "$publish_dir" "$stage" "$dmg"
  # -p:Version stamps the assembly's InformationalVersion, which is what the client reports as its own version
  # (ClientUpdate) + the update check compares against the release tag — WITHOUT it the build defaults to 1.0.0.
  # The CFBundleVersion in Info.plist below is separate macOS bundle metadata and does NOT affect this.
  dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
    -p:Version="$VERSION" -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false \
    -o "$publish_dir"

  echo "==> [$arch_label] Assembling ${APP_NAME}.app…"
  mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
  cp -R "$publish_dir/." "$app_dir/Contents/MacOS/"
  chmod +x "$app_dir/Contents/MacOS/$EXE_NAME"
  cp "$ICNS" "$app_dir/Contents/Resources/${APP_NAME}.icns"

  cat > "$app_dir/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${APP_NAME}</string>
  <key>CFBundleDisplayName</key><string>${APP_NAME}</string>
  <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>${EXE_NAME}</string>
  <key>CFBundleIconFile</key><string>${APP_NAME}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.business</string>
  <!-- The simplarchive:// deep-link scheme (#761): macOS registers handlers declaratively from the bundle. -->
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>${BUNDLE_ID}.deeplink</string>
      <key>CFBundleURLSchemes</key><array><string>simplarchive</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

  # Ad-hoc signature (no Apple account). Required for the app to launch on Apple Silicon; harmless on Intel.
  echo "==> [$arch_label] Ad-hoc signing…"
  codesign --force --deep --sign - "$app_dir" 2>/dev/null \
    || echo "   (codesign failed — the app is fully unsigned; it may be blocked on Apple Silicon.)"

  # /Applications symlink so the .dmg offers drag-to-install.
  ln -sf /Applications "$stage/Applications"

  echo "==> [$arch_label] Building ${dmg}…"
  hdiutil create -volname "${APP_NAME} ${VERSION}" -srcfolder "$stage" -ov -format UDZO "$dmg" >/dev/null

  rm -rf "$publish_dir" "$stage"
  echo "==> [$arch_label] Done: $dmg"
}

build_one osx-arm64 arm64
build_one osx-x64 x64

arm_dmg="$OUT_DIR/${APP_NAME}-${VERSION}-arm64.dmg"
x64_dmg="$OUT_DIR/${APP_NAME}-${VERSION}-x64.dmg"

echo
echo "Built:"
ls -lh "$arm_dmg" "$x64_dmg"

# --upload (issue #306): attach the two version-matching .dmgs to the existing GitHub Release v<version> on the
# public repo. Only the .dmgs named for THIS version are uploaded — a guard against attaching a stale build to
# the wrong release.
if [[ "$UPLOAD" == 1 ]]; then
  tag="v${VERSION}"
  release_repo="${RELEASE_REPO:-HebelConsulting/SimplArchive}"

  command -v gh >/dev/null 2>&1 || { echo "error: '--upload' needs the GitHub CLI ('gh'), authenticated with write access to ${release_repo}." >&2; exit 1; }

  echo
  echo "==> Uploading the macOS .dmg(s) to release ${tag} on ${release_repo}…"
  # The release must already exist (the desktop-packages workflow creates it on the v* tag push, with the
  # win/linux archives); this only ADDS the macOS artifacts the opt-in macOS CI job would otherwise build.
  if ! gh release view "$tag" --repo "$release_repo" >/dev/null 2>&1; then
    echo "error: release '$tag' not found on ${release_repo}. Push the ${tag} tag first (it creates the release), then re-run with --upload." >&2
    exit 1
  fi

  gh release upload "$tag" "$arm_dmg" "$x64_dmg" --clobber --repo "$release_repo"
  echo "==> Uploaded $(basename "$arm_dmg") + $(basename "$x64_dmg") to ${release_repo} ${tag}."
fi

cat <<'NOTE'

These .dmg installers are UNSIGNED and not notarized, so macOS Gatekeeper will warn on first open.
To run the app after dragging it to /Applications:
  - right-click SimplArchive.app -> Open (confirm once), or
  - clear the quarantine flag:  xattr -dr com.apple.quarantine /Applications/SimplArchive.app
NOTE
