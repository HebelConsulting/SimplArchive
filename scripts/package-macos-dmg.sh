#!/usr/bin/env bash
#
# Package the SimplArchive DesktopClient (Avalonia) as native macOS .app bundles + .dmg installers, one
# each for Apple Silicon (arm64) and Intel (x64). See ADR "macOS .dmg packaging for the desktop client".
#
#   - Self-contained: bundles the .NET runtime, so the target Mac needs no .NET installed.
#   - SIGNED AND NOTARIZED when a Developer ID identity is available (ADR 0896): every Mach-O file signed with
#     the hardened runtime and a timestamp, then the app with scripts/macos/SimplArchive.entitlements, then the
#     .dmg, which is notarized and stapled so it opens without Gatekeeper's warning — offline too.
#   - AD-HOC signed otherwise (`codesign -s -`), as every build was before ADR 0896: enough for the Apple
#     Silicon loader to launch it, and Gatekeeper warns on first open. The script says so loudly.
#   - Packaged with the built-in `hdiutil` (no Homebrew dependency); each .dmg contains the .app plus an
#     /Applications symlink for drag-to-install.
#
# Signing is driven by the ENVIRONMENT, never by arguments, so no secret reaches argv or shell history:
#   MACOS_SIGN_IDENTITY   the identity in the keychain, e.g. "Developer ID Application: Name (TEAMID)"
#   NOTARY_APPLE_ID, NOTARY_APP_PASSWORD, APPLE_TEAM_ID   notarytool's Apple ID route (no App Store Connect)
#   REQUIRE_SIGNING=1     refuse to build an unsigned .dmg — the release sets this, so a release cannot ship
#                         an ad-hoc build without saying so by failing.
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

ENTITLEMENTS="scripts/macos/SimplArchive.entitlements"
SIGN_IDENTITY="${MACOS_SIGN_IDENTITY:-}"
if [[ -z "$SIGN_IDENTITY" ]]; then
  if [[ "${REQUIRE_SIGNING:-0}" == 1 ]]; then
    echo "error: REQUIRE_SIGNING=1 but MACOS_SIGN_IDENTITY is empty — refusing to build an unsigned .dmg." >&2
    exit 1
  fi
  echo "==> No MACOS_SIGN_IDENTITY: building AD-HOC signed .dmgs (Gatekeeper will warn on first open)."
elif [[ -z "${NOTARY_APPLE_ID:-}" || -z "${NOTARY_APP_PASSWORD:-}" || -z "${APPLE_TEAM_ID:-}" ]]; then
  # A signed but un-notarized .dmg is WORSE than an ad-hoc one: Gatekeeper still refuses it, and it looks finished.
  echo "error: MACOS_SIGN_IDENTITY is set but NOTARY_APPLE_ID / NOTARY_APP_PASSWORD / APPLE_TEAM_ID are not." >&2
  exit 1
fi

# Developer ID signing, inside-out (ADR 0896). `codesign --deep` is NOT used: it signs nested code with the
# outer call's options, which gives every dylib the app's entitlements, and Apple's notary rejects --deep
# signatures outright. So every Mach-O file is signed on its own first, then the bundle seals the rest.
sign_app() {
  local app_dir="$1" exe="$2"
  local file
  while IFS= read -r -d '' file; do
    # A string match, not `file … | grep -q`: under pipefail that pipe can report a MATCH as a failure, which
    # here would silently skip signing a library and get the .dmg refused by the notary — only sometimes.
    if [[ "$file" != "$app_dir/Contents/MacOS/$exe" && "$(file -b "$file")" == *Mach-O* ]]; then
      codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" "$file"
    fi
  done < <(find "$app_dir/Contents/MacOS" -type f -print0)
  codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY" \
    "$app_dir/Contents/MacOS/$exe"
  codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY" "$app_dir"
  codesign --verify --strict --deep --verbose=2 "$app_dir"
}

# Sign, notarize and staple the .dmg. The password reaches notarytool through its argv, which is unavoidable
# for the Apple ID route; it is never echoed, and on CI the runner masks secrets in its log.
notarize_dmg() {
  local dmg="$1" out submission
  codesign --force --timestamp --sign "$SIGN_IDENTITY" "$dmg"
  echo "==> Notarizing $(basename "$dmg") (this waits for Apple, typically a few minutes)…"
  if ! out="$(xcrun notarytool submit "$dmg" --apple-id "$NOTARY_APPLE_ID" --password "$NOTARY_APP_PASSWORD" \
      --team-id "$APPLE_TEAM_ID" --wait 2>&1)"; then
    echo "$out" >&2
    exit 1
  fi
  grep -E "id:|status:" <<<"$out" || true   # informational only; must never fail the script
  if [[ "$out" != *"status: Accepted"* ]]; then
    # The notary says WHY in a log the submit output only links to; fetch it, or the refusal is unreadable.
    submission="$(echo "$out" | sed -n 's/^ *id: //p' | head -1)"
    xcrun notarytool log "$submission" --apple-id "$NOTARY_APPLE_ID" --password "$NOTARY_APP_PASSWORD" \
      --team-id "$APPLE_TEAM_ID" >&2 || true
    echo "error: notarization of $(basename "$dmg") was not accepted." >&2
    exit 1
  fi
  xcrun stapler staple "$dmg"
  spctl --assess --type open --context context:primary-signature --verbose=2 "$dmg"
}

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
  # The project multi-targets since #1398; macOS is always the `net10.0` assembly (Unix CK_ULONG width).
  dotnet publish "$PROJECT" -c Release -r "$rid" -f net10.0 --self-contained true \
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

  if [[ -n "$SIGN_IDENTITY" ]]; then
    echo "==> [$arch_label] Signing with the Developer ID (hardened runtime)…"
    sign_app "$app_dir" "$EXE_NAME"
  else
    # Ad-hoc signature (no Apple account). Required for the app to launch on Apple Silicon; harmless on Intel.
    echo "==> [$arch_label] Ad-hoc signing…"
    codesign --force --deep --sign - "$app_dir" 2>/dev/null \
      || echo "   (codesign failed — the app is fully unsigned; it may be blocked on Apple Silicon.)"
  fi

  # /Applications symlink so the .dmg offers drag-to-install.
  ln -sf /Applications "$stage/Applications"

  echo "==> [$arch_label] Building ${dmg}…"
  hdiutil create -volname "${APP_NAME} ${VERSION}" -srcfolder "$stage" -ov -format UDZO "$dmg" >/dev/null
  if [[ -n "$SIGN_IDENTITY" ]]; then
    notarize_dmg "$dmg"
  fi

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

if [[ -n "$SIGN_IDENTITY" ]]; then
  echo
  echo "These .dmg installers are signed with the Developer ID, notarized and stapled."
  exit 0
fi

cat <<'NOTE'

These .dmg installers are AD-HOC signed and not notarized, so macOS Gatekeeper will warn on first open.
To run the app after dragging it to /Applications:
  - right-click SimplArchive.app -> Open (confirm once), or
  - clear the quarantine flag:  xattr -dr com.apple.quarantine /Applications/SimplArchive.app
NOTE
