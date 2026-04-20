#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MOD_NAME="${MOD_NAME:-$(basename "$SCRIPT_DIR")}"
RIMWORLD_DIR="${RIMWORLD_DIR:-/home/christoph/snap/steam/common/.local/share/Steam/steamapps/common/RimWorld}"
MODS_DIR="${MODS_DIR:-$RIMWORLD_DIR/Mods}"
DEST_DIR="${DEST_DIR:-$MODS_DIR/$MOD_NAME}"

if [[ ! -d "$MODS_DIR" ]]; then
  echo "Mods directory not found: $MODS_DIR" >&2
  exit 1
fi

mkdir -p "$DEST_DIR"

rsync -av --delete \
  --exclude='.git/' \
  --exclude='.codex' \
  --exclude='Source/' \
  --exclude='**/obj/' \
  --exclude='**/bin/' \
  "$SCRIPT_DIR/About/" "$DEST_DIR/About/"

if [[ -d "$SCRIPT_DIR/1.6" ]]; then
  rsync -av --delete \
    --exclude='**/obj/' \
    --exclude='**/bin/' \
    "$SCRIPT_DIR/1.6/" "$DEST_DIR/1.6/"
fi

for optional_path in LoadFolders.xml Preview.png PublishedFileId.txt; do
  if [[ -e "$SCRIPT_DIR/$optional_path" ]]; then
    rsync -av "$SCRIPT_DIR/$optional_path" "$DEST_DIR/$optional_path"
  else
    rm -rf "$DEST_DIR/$optional_path"
  fi
done

echo "Deployed to $DEST_DIR"
