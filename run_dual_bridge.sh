#!/bin/sh
# Start the localhost bridge and both loader-backed game processes.
# Set SDTV_COMMAND and TERRARIA_COMMAND to the exact SMAPI/tModLoader launch commands.
set -eu

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
: "${SDTV_COMMAND:?Set SDTV_COMMAND to the SMAPI Stardew launch command}"
: "${TERRARIA_COMMAND:?Set TERRARIA_COMMAND to the tModLoader Terraria launch command}"

exec python3 "$ROOT_DIR/bridge/bridge.py" \
  --port "${SDTV_BRIDGE_PORT:-28741}" \
  --port-alias "${SDTV_BRIDGE_PORT_ALIAS:-28741}" \
  --state-file "${SDTV_BRIDGE_STATE:-$ROOT_DIR/bridge-state.json}" \
  --stardew-cmd "$SDTV_COMMAND" \
  --terraria-cmd "$TERRARIA_COMMAND" \
  --stardew-process "${SDTV_PROCESS_NAME:-StardewModdingAPI}" \
  --terraria-process "${TERRARIA_PROCESS_NAME:-Terraria}" \
  "$@"
