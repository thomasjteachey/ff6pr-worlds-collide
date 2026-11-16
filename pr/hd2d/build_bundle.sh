#!/usr/bin/env bash
# Build pr/hd2d/out/hd2d.bundle (the HD-2D shaders) with Unity 2019.4.26f1, the game's
# own Unity version: shader bundles only load in a player of the same minor version.
#   pr/hd2d/build_bundle.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity/Hub/Editor/2019.4.26f1/Editor/Unity.exe}"
LOG="$HERE/out/unity_build.log"
mkdir -p "$HERE/out"
rm -f "$HERE/out/hd2d.bundle"
set +e
"$UNITY" -batchmode -nographics -quit -projectPath "$(cygpath -w "$HERE/unity")" \
  -executeMethod BuildHd2dBundle.Build -logFile "$(cygpath -w "$LOG")"
rc=$?
set -e
grep -E "\[hd2d\]|error CS|Shader error|Compilation failed" "$LOG" | grep -v "^Debug" || true
if [ $rc -ne 0 ] || [ ! -f "$HERE/out/hd2d.bundle" ]; then
  echo "[hd2d] Unity build failed (exit $rc), see $LOG"; exit 1
fi
echo "[hd2d] ok: $(ls -l "$HERE/out/hd2d.bundle" | awk '{print $5}') bytes"
