#!/usr/bin/env bash
# Fetches the pinned jackify-engine release and places it where the app expects it
# (relative to the app executable): <target>/engine/jackify-engine
#
# Usage: scripts/setup-deps.sh <target-dir>
# Idempotent: skips if the engine is already present.
set -euo pipefail

TARGET="${1:?usage: setup-deps.sh <target-dir>}"

ENGINE_VERSION="0.5.7"
# Pinned SHA-256 (from the release's SHA256SUMS asset). If a download fails verification,
# the upstream asset changed under a fixed version tag (tampering or re-release) —
# investigate, don't just bump the hash blindly.
ENGINE_SHA256="f25f639eab680978b3af602fb6a7a1c5c96916a2ecf77b9c3b53f85d760eadff"
ENGINE_URL="https://github.com/Omni-guides/dev-jackify-engine/releases/download/v${ENGINE_VERSION}/jackify-engine-${ENGINE_VERSION}-linux-x64.tar.gz"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

verify_sha256() {
    local file="$1" expected="$2"
    local actual
    actual="$(sha256sum "$file" | cut -d' ' -f1)"
    if [[ "$actual" != "$expected" ]]; then
        echo "ERROR: checksum mismatch for $file" >&2
        echo "  expected $expected" >&2
        echo "  got      $actual" >&2
        exit 1
    fi
}

if [[ ! -x "$TARGET/engine/jackify-engine" ]]; then
    echo "==> jackify-engine ${ENGINE_VERSION}"
    curl -fLo "$WORK/engine.tar.gz" "$ENGINE_URL"
    verify_sha256 "$WORK/engine.tar.gz" "$ENGINE_SHA256"
    mkdir -p "$TARGET/engine"
    tar -xzf "$WORK/engine.tar.gz" -C "$TARGET/engine"
    # The tarball may nest a directory; flatten so the binary sits at engine/jackify-engine.
    if [[ ! -f "$TARGET/engine/jackify-engine" ]]; then
        inner="$(find "$TARGET/engine" -maxdepth 2 -name jackify-engine -type f | head -1)"
        if [[ -n "$inner" && "$inner" != "$TARGET/engine/jackify-engine" ]]; then
            mv "$(dirname "$inner")"/* "$TARGET/engine/"
        fi
    fi
    chmod +x "$TARGET/engine/jackify-engine"
else
    echo "==> engine/jackify-engine present, skipping"
fi

echo "Done: dependencies in $TARGET"
