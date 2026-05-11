#!/usr/bin/env bash
# bin/suru-compile.sh <source.suru>
#
# Compiles a Suru source file to a native binary in the current directory.
# The binary is named after the source file stem (e.g. main.suru → ./main).
#
# Requires:
#   - clang-15 on PATH
#   - SURU_BUILD:   path to the suru-build binary      (default: bin/suru-build next to this script)
#   - SURU_RUNTIME: directory containing the five runtime .ll files
#                   (default: bin/ — same directory as this script)

set -euo pipefail

# ─── Arguments ────────────────────────────────────────────────────────────────

if [[ $# -ne 1 ]]; then
    echo "usage: suru-compile.sh <source.suru>" >&2
    exit 1
fi

SOURCE="$1"

if [[ ! -f "$SOURCE" ]]; then
    echo "error: file not found: $SOURCE" >&2
    exit 1
fi

# ─── Locate tools ─────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

SURU_BUILD="${SURU_BUILD:-$SCRIPT_DIR/suru-build}"
if [[ ! -x "$SURU_BUILD" ]]; then
    echo "error: suru-build not found at $SURU_BUILD" >&2
    echo "  set SURU_BUILD=/path/to/suru-build binary" >&2
    exit 1
fi

SURU_RUNTIME="${SURU_RUNTIME:-$SCRIPT_DIR}"
for rt in suru_box suru_string suru_array suru_struct suru_variant; do
    if [[ ! -f "$SURU_RUNTIME/$rt.ll" ]]; then
        echo "error: runtime file not found: $SURU_RUNTIME/$rt.ll" >&2
        echo "  set SURU_RUNTIME=/path/to/runtime/dir" >&2
        exit 1
    fi
done

# ─── Derive output name ────────────────────────────────────────────────────────

SOURCE_ABS="$(cd "$(dirname "$SOURCE")" && pwd)/$(basename "$SOURCE")"
STEM="$(basename "$SOURCE" .suru)"
OUT_DIR="$(pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

# ─── Step 1: generate .ll files ───────────────────────────────────────────────

"$SURU_BUILD" "$SOURCE_ABS" "$WORK_DIR/$STEM.ll"

# ─── Step 2: compile all generated .ll files ──────────────────────────────────

for ll in "$WORK_DIR"/*.ll; do
    clang-15 -c "$ll" -o "${ll%.ll}.o" 2>/dev/null
done

# ─── Step 3: compile runtime modules ──────────────────────────────────────────

for rt in suru_box suru_string suru_array suru_struct suru_variant; do
    clang-15 -c "$SURU_RUNTIME/$rt.ll" -o "$WORK_DIR/$rt.o" 2>/dev/null
done

# ─── Step 4: link ─────────────────────────────────────────────────────────────

clang-15 "$WORK_DIR"/*.o -o "$OUT_DIR/$STEM"

echo "→ $OUT_DIR/$STEM"
