#!/bin/bash
# Headless load check: boots each scene and fails on any Godot error or C# exception.
GODOT="/c/Users/Dylan/Documents/godot/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
PROJ="C:/Dev/riichi-mahjong"
FAIL=0
for scene in "$@"; do
  out=$("$GODOT" --headless --path "$PROJ" --quit-after 120 "res://Scenes/$scene" 2>&1)
  errs=$(printf '%s' "$out" | sed 's/\x1b\[[0-9;]*m//g' \
        | grep -E "SCRIPT ERROR|Unhandled exception|System\.[A-Za-z]+Exception|ERROR:|Cannot open file|Failed to load" \
        | grep -vE "resources still in use at exit|Cannot open file 'res://\.godot" | head -6)
  if [ -n "$errs" ]; then
    echo "FAIL $scene"; printf '%s\n' "$errs" | sed 's/^/    /'; FAIL=1
  else
    echo "OK   $scene"
  fi
done
exit $FAIL
