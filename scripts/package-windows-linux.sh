#!/usr/bin/env bash
#
# Package the SimplArchive DesktopClient (Avalonia) as self-contained, portable x64 archives for Windows and
# Linux — one Windows .zip and one Linux .tar.gz. See ADR "Windows + Linux desktop packaging".
#
#   - Self-contained + single-file: each archive holds ONE launcher that bundles the .NET runtime, so the
#     target machine needs no .NET installed (mirrors the macOS .dmg's self-contained posture, ADR 0444).
#   - Cross-buildable: `dotnet publish -r win-x64 / linux-x64` runs on any OS (macOS/Linux/Windows) — no
#     Windows box or extra tooling needed, just the .NET SDK plus `zip` and `tar`.
#   - Portable, not an installer: unzip/untar and run. UNSIGNED — Windows SmartScreen warns on first run
#     ("More info" → "Run anyway"); on Linux mark it executable (the tar preserves the bit, but a browser
#     download may drop it: `chmod +x SimplArchive.DesktopClient`). Fits the "Not for production" showcase.
#
# Usage:   scripts/package-windows-linux.sh [version]
#            version  optional (default 0.1.0) — stamped into the assembly + the archive file names.
#
# Output:  dist/SimplArchive-<version>-win-x64.zip
#          dist/SimplArchive-<version>-linux-x64.tar.gz

set -euo pipefail

VERSION="${1:-0.1.0}"

APP_NAME="SimplArchive"
EXE_NAME="SimplArchive.DesktopClient"   # the apphost `dotnet publish` produces (the project name)
PROJECT="src/SimplArchive.DesktopClient/SimplArchive.DesktopClient.csproj"
OUT_DIR="dist"

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

for tool in dotnet zip tar magick; do
  command -v "$tool" >/dev/null 2>&1 || { echo "error: '$tool' is required but not found on PATH." >&2; exit 1; }
done

mkdir -p "$OUT_DIR"

# Publish one runtime as a self-contained single file, then wrap it in an archive with a small README.
#   $1 = RID (win-x64 | linux-x64)   $2 = archive kind (zip | tar)
build_one() {
  local rid="$1" kind="$2"
  local stage_name="${APP_NAME}-${VERSION}-${rid}"
  local publish_dir="$OUT_DIR/publish-$rid"
  local stage="$OUT_DIR/$stage_name"

  echo "==> [$rid] Publishing $EXE_NAME (self-contained single-file, Release)…"
  rm -rf "$publish_dir" "$stage"
  dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
    -p:Version="$VERSION" -o "$publish_dir"

  mkdir -p "$stage"
  cp -R "$publish_dir/." "$stage/"

  cat > "$stage/README.txt" <<README
SimplArchive desktop client ${VERSION} (${rid})

Self-contained — no .NET installation required.

Windows:  run  ${EXE_NAME}.exe
          (unsigned: if SmartScreen warns, choose "More info" -> "Run anyway".)
Linux:    run  ./${EXE_NAME}
          (if it is not executable after download:  chmod +x ${EXE_NAME})

          For a menu entry and dock icon, run  ./install.sh
          (installs a .desktop entry + icons under ~/.local/share; ./uninstall.sh removes them.
           The binary stays where you unpacked it, so do not move it afterwards.)

The client connects to a SimplArchive API; pick/enter the server on the logon window.
Not for production — a showcase build.
README

  # Linux desktop integration (issue #429): a binary in a tarball has no menu entry, no dock icon and nothing
  # to pin — the window icon Avalonia sets is not a LAUNCHER icon. Ship a .desktop entry plus hicolor PNGs and a
  # tiny installer, because asking someone to place four files by hand is most of the reason nobody would.
  if [[ "$kind" == "tar" ]]; then
    echo "==> [$rid] Adding desktop entry + icons…"
    local icon_src="$repo_root/src/SimplArchive.DesktopClient/Assets/cabinet-1024.png"
    for size in 48 64 128 256; do
      mkdir -p "$stage/share/icons/hicolor/${size}x${size}/apps"
      magick "$icon_src" -resize "${size}x${size}" "$stage/share/icons/hicolor/${size}x${size}/apps/simplarchive.png"
    done

    mkdir -p "$stage/share/applications"
    # Exec is a placeholder: the tarball can be unpacked anywhere, so install.sh rewrites it to the absolute
    # path of the binary it just installed. A .desktop with a relative Exec silently does nothing when launched
    # from a menu, which is the failure this avoids.
    cat > "$stage/share/applications/simplarchive.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=SimplArchive
GenericName=Document Management
Comment=Browse, file and search documents in a SimplArchive archive
Exec=__EXEC__
Icon=simplarchive
Terminal=false
Categories=Office;
StartupWMClass=$EXE_NAME
DESKTOP

    cat > "$stage/install.sh" <<'INSTALL'
#!/usr/bin/env sh
# Installs the menu entry + icons for the current user (no root, no package manager).
# Undo with uninstall.sh. The binary itself stays where you unpacked it.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
apps="$HOME/.local/share/applications"
icons="$HOME/.local/share/icons/hicolor"

mkdir -p "$apps"
sed "s|__EXEC__|$here/SimplArchive.DesktopClient|" "$here/share/applications/simplarchive.desktop" \
    > "$apps/simplarchive.desktop"
chmod +x "$here/SimplArchive.DesktopClient" 2>/dev/null || true

for size in 48 64 128 256; do
    mkdir -p "$icons/${size}x${size}/apps"
    cp "$here/share/icons/hicolor/${size}x${size}/apps/simplarchive.png" "$icons/${size}x${size}/apps/"
done

# Best effort: desktops that cache the menu need a nudge; those that do not have these commands are fine.
update-desktop-database "$apps" 2>/dev/null || true
gtk-update-icon-cache -f -t "$icons" 2>/dev/null || true

echo "Installed. SimplArchive should appear in your applications menu."
echo "If it does not, log out and back in — some desktops only rescan then."
INSTALL

    cat > "$stage/uninstall.sh" <<'UNINSTALL'
#!/usr/bin/env sh
set -eu
rm -f "$HOME/.local/share/applications/simplarchive.desktop"
for size in 48 64 128 256; do
    rm -f "$HOME/.local/share/icons/hicolor/${size}x${size}/apps/simplarchive.png"
done
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
echo "Removed the menu entry and icons. The unpacked files are untouched."
UNINSTALL

    chmod +x "$stage/install.sh" "$stage/uninstall.sh"
  fi

  if [[ "$kind" == "zip" ]]; then
    local out="$OUT_DIR/${stage_name}.zip"
    rm -f "$out"
    echo "==> [$rid] Zipping…"
    ( cd "$OUT_DIR" && zip -qr "${stage_name}.zip" "$stage_name" )
  else
    local out="$OUT_DIR/${stage_name}.tar.gz"
    rm -f "$out"
    chmod +x "$stage/$EXE_NAME" 2>/dev/null || true   # keep the launcher executable inside the tarball
    echo "==> [$rid] Creating tarball…"
    tar -czf "$out" -C "$OUT_DIR" "$stage_name"
  fi

  rm -rf "$publish_dir" "$stage"
  echo "==> [$rid] Done."
}

build_one win-x64 zip
build_one linux-x64 tar

echo
echo "Built:"
ls -lh "$OUT_DIR"/${APP_NAME}-${VERSION}-win-x64.zip "$OUT_DIR"/${APP_NAME}-${VERSION}-linux-x64.tar.gz

cat <<'NOTE'

These archives are self-contained and UNSIGNED (a showcase build):
  - Windows: SmartScreen warns on first run — "More info" -> "Run anyway".
  - Linux:   if the launcher lost its executable bit on download, run: chmod +x SimplArchive.DesktopClient
NOTE
