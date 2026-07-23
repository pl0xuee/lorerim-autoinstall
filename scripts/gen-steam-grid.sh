#!/bin/sh
# Renders the Steam grid artwork for the LoreRim shortcut from packaging/icon.svg.
# Output goes to src/Lorerim.Gui/Assets/SteamGrid/ (bundled as AvaloniaResource,
# copied into Steam's userdata grid dir by SteamGridArtService at setup time).
# Also renders the AppImage icon (packaging/icon-256.png).
# Requires: rsvg-convert, ImageMagick, Liberation Mono Bold.
set -eu
HERE="$(dirname "$(readlink -f "$0")")"
ROOT="$HERE/.."
OUT="$ROOT/src/Lorerim.Gui/Assets/SteamGrid"
SVG="$ROOT/packaging/icon.svg"
FONT="Liberation-Mono-Bold"

BG="#0E1116"      # night slate
BG2="#1A2029"     # glow center
EDGE="#2A3340"    # hairline
BRASS="#C9A05A"
TEXT="#E3DFD3"

mkdir -p "$OUT"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

rsvg-convert -w 512 -h 512 "$SVG" -o "$TMP/icon512.png"
rsvg-convert -w 256 -h 256 "$SVG" -o "$OUT/icon.png"
rsvg-convert -w 256 -h 256 "$SVG" -o "$ROOT/packaging/icon-256.png"

wordmark() { # $1=pointsize $2=kerning $3=text $4=color $5=out
  magick -background none -fill "$4" -font "$FONT" \
    -pointsize "$1" -kerning "$2" label:"$3" "$5"
}

# --- logo (transparent, used standalone and composited into the others) ---
wordmark 46 12 "THE ELDER SCROLLS V" "$TEXT"  "$TMP/line1.png"
wordmark 160 8 "LORERIM"             "$BRASS" "$TMP/line2.png"
wordmark 32 16 "WABBAJACK EDITION"   "$TEXT"  "$TMP/line3.png"
magick "$TMP/line1.png" "$TMP/line2.png" "$TMP/line3.png" \
  -background none -gravity center -smush 18 "$OUT/logo.png"

# --- portrait capsule 600x900 ---
magick -size 600x900 radial-gradient:"$BG2"-"$BG" \
  \( "$TMP/icon512.png" -resize 380x380 \) -gravity north -geometry +0+120 -composite \
  \( "$OUT/logo.png" -resize 480x \) -gravity south -geometry +0+110 -composite \
  -bordercolor "$EDGE" -shave 0 -border 2 "$OUT/portrait.png"

# --- landscape capsule 920x430 ---
magick -size 920x430 radial-gradient:"$BG2"-"$BG" \
  \( "$TMP/icon512.png" -resize 300x300 \) -gravity west -geometry +70+0 -composite \
  \( "$OUT/logo.png" -resize 440x \) -gravity east -geometry +70+0 -composite \
  -bordercolor "$EDGE" -border 2 "$OUT/landscape.png"

# --- hero 1920x620 (logo is overlaid by Steam itself, keep it atmospheric) ---
magick -size 1920x620 radial-gradient:"$BG2"-"$BG" \
  \( "$TMP/icon512.png" -resize 760x760 -alpha set -channel A -evaluate multiply 0.16 +channel \) \
  -gravity east -geometry +160-40 -composite \
  \( -size 1920x2 xc:"$BRASS" -alpha set -channel A -evaluate multiply 0.5 +channel \) \
  -gravity south -geometry +0+56 -composite \
  "$OUT/hero.png"

echo "wrote:"; ls -la "$OUT"
