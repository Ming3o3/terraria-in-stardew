#!/bin/sh
set -eu

TML_DIR=${TMODLOADER_DIR:-${TML_GAME_DIR:-}}
if [ -z "$TML_DIR" ]; then
  TML_DIR="$HOME/Library/Application Support/Steam/steamapps/common/tModLoader"
fi

cd "$TML_DIR"
exec ./LaunchUtils/ScriptCaller.sh "$@"
