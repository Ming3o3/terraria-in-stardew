#!/bin/sh
set -eu

GAME_DIR=${STARDEW_GAME_DIR:-${STARDEW_GAME_PATH:-}}
if [ -z "$GAME_DIR" ]; then
  GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS"
fi

cd "$GAME_DIR"
exec ./StardewModdingAPI "$@"
