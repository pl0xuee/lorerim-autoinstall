#!/usr/bin/env bash
# Builds LorerimAutoinstall-x86_64.AppImage:
# self-contained dotnet publish -> fetch jackify-engine -> AppDir -> appimagetool
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APPDIR="$ROOT/packaging/LorerimAutoinstall.AppDir"
PUBLISH="$ROOT/publish"
OUT="$ROOT/LorerimAutoinstall-x86_64.AppImage"

VERSION="${VERSION:-0.1.0}"

rm -rf "$PUBLISH" "$APPDIR/usr"
dotnet publish "$ROOT/src/Lorerim.Gui/Lorerim.Gui.csproj" \
    -c Release -r linux-x64 --self-contained -o "$PUBLISH" -p:Version="$VERSION"
rm -f "$PUBLISH"/*.pdb

"$ROOT/scripts/setup-deps.sh" "$PUBLISH"

mkdir -p "$APPDIR/usr/bin"
cp -r "$PUBLISH"/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/AppRun"

# appimagetool only publishes a rolling "continuous" release, so we pin its SHA-256. If this
# check fails, upstream pushed a new continuous build: review the change and update the hash
# deliberately (a failed build is the point — it stops a silently-swapped tool from shipping).
APPIMAGETOOL="$ROOT/appimagetool-x86_64.AppImage"
APPIMAGETOOL_SHA256="95cbe7cce9717fce90c484e34052ee7c7f1d7635b33c12525b4776826a7d29b6"
if [[ ! -x "$APPIMAGETOOL" ]]; then
    curl -fLo "$APPIMAGETOOL" \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    actual="$(sha256sum "$APPIMAGETOOL" | cut -d' ' -f1)"
    if [[ "$actual" != "$APPIMAGETOOL_SHA256" ]]; then
        echo "ERROR: appimagetool checksum mismatch (expected $APPIMAGETOOL_SHA256, got $actual)" >&2
        rm -f "$APPIMAGETOOL"
        exit 1
    fi
    chmod +x "$APPIMAGETOOL"
fi

# APPIMAGE_EXTRACT_AND_RUN lets appimagetool run without FUSE (CI runners, containers).
APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" "$APPDIR" "$OUT"
chmod +x "$OUT"
echo "Built: $OUT"
