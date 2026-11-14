#!/usr/bin/env bash
# Build a mod and install it into the game's Dev mods folder.
#   ./build.sh                          DivergenceProbe, build + install
#   ./build.sh MultiViewExperiment      another mod under src/
#   ./build.sh DivergenceProbe --no-install
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

MOD="DivergenceProbe"
INSTALL=1
for a in "$@"; do
  case "$a" in
    --no-install) INSTALL=0 ;;
    *) MOD="$a" ;;
  esac
done

GAME_MANAGED="${GAME_MANAGED:-/mnt/c/Program Files (x86)/Steam/steamapps/common/OxygenNotIncluded/OxygenNotIncluded_Data/Managed}"
MODS_ROOT="${MODS_ROOT:-/mnt/c/Users/DELL/Documents/Klei/OxygenNotIncluded/mods/Dev}"
MOD_DIR="$MODS_ROOT/$MOD"
PROJ="$ROOT/src/$MOD/$MOD.csproj"
OUT="$ROOT/src/$MOD/bin/Release"

[[ -f "$PROJ" ]] || { echo "ERROR: no project at $PROJ" >&2; exit 1; }
dotnet build "$PROJ" -c Release -nologo -v minimal "-p:GameManaged=$GAME_MANAGED"

if [[ $INSTALL -eq 0 ]]; then
  echo "built: $OUT/$MOD.dll"
  exit 0
fi

# A running game keeps the loaded DLL locked (grep without -q so pipefail doesn't trip on SIGPIPE).
if [[ -f "$MOD_DIR/$MOD.dll" ]] && command -v tasklist.exe >/dev/null 2>&1 \
   && tasklist.exe 2>/dev/null | grep -i "OxygenNotIncluded.exe" >/dev/null; then
  echo "ERROR: the game is running and keeps the old $MOD.dll locked. Quit ONI, then run ./build.sh again." >&2
  exit 1
fi

mkdir -p "$MOD_DIR"
cp "$OUT/$MOD.dll" "$OUT/mod.yaml" "$OUT/mod_info.yaml" "$MOD_DIR/"
echo "installed to: $MOD_DIR"
ls -la "$MOD_DIR"
