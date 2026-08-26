#!/usr/bin/env bash
#
# Build the client, export a debug APK, install it on a connected Android device and
# read the determinism fingerprint back out of the device log.
#
# The point of this script is that the Phase 0 answer should be one command, repeatable
# by anybody, rather than a sequence somebody remembers. If a phone and a desktop print
# the same COMBINED hash, cross-play is possible; if they do not, nothing above the gate
# is worth building.
#
# The Android toolchain is not installed by this project. It is borrowed from a Unity
# editor installation, which ships a complete SDK, NDK and JDK 17. Override ANDROID_HOME
# and JAVA_HOME to point somewhere else.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GODOT="${GODOT:-/c/Personal/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
UNITY_ANDROID="${UNITY_ANDROID:-/c/Program Files/Unity/Hub/Editor/6000.5.8f1/Editor/Data/PlaybackEngines/AndroidPlayer}"
ANDROID_HOME="${ANDROID_HOME:-$UNITY_ANDROID/SDK}"
ADB="$ANDROID_HOME/platform-tools/adb.exe"
PACKAGE="nz.molehill.phase0"
APK="$REPO_ROOT/build/android/molehill-phase0.apk"

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

step "Checking the toolchain"
[ -x "$GODOT" ] || { echo "Godot not found at $GODOT"; exit 1; }
[ -x "$ADB" ] || { echo "adb not found at $ADB"; exit 1; }
echo "godot  $("$GODOT" --version 2>/dev/null | head -1)"
echo "adb    $("$ADB" version 2>/dev/null | head -1)"

step "Checking for a device"
"$ADB" start-server >/dev/null 2>&1 || true
DEVICES="$("$ADB" devices | awk 'NR>1 && $2=="device" {print $1}')"
if [ -z "$DEVICES" ]; then
  echo "No device is connected and authorised."
  echo
  echo "On the phone:"
  echo "  1. Settings, About phone, tap Build number seven times."
  echo "  2. Settings, System, Developer options, turn on USB debugging."
  echo "  3. Plug it in and accept the 'Allow USB debugging' prompt."
  echo
  "$ADB" devices -l
  exit 1
fi
echo "device $DEVICES"
echo "model  $("$ADB" -s "$DEVICES" shell getprop ro.product.model 2>/dev/null | tr -d '\r')"
echo "abi    $("$ADB" -s "$DEVICES" shell getprop ro.product.cpu.abi 2>/dev/null | tr -d '\r')"

step "Building the simulation and its tests"
dotnet test "$REPO_ROOT/Molehill.slnx" --configuration Release --nologo --verbosity quiet

step "Exporting the APK"
mkdir -p "$(dirname "$APK")"
rm -f "$APK"
# Godot builds the C# assemblies as part of the export.
"$GODOT" --headless --path "$REPO_ROOT/client" --export-debug "Android" "$APK"
[ -f "$APK" ] || { echo "Export produced no APK."; exit 1; }
ls -lh "$APK"

step "Installing"
"$ADB" -s "$DEVICES" install -r "$APK"

step "Running, and reading the fingerprint back"
"$ADB" -s "$DEVICES" logcat -c
"$ADB" -s "$DEVICES" shell am start -n "$PACKAGE/com.godot.game.GodotApp" >/dev/null

# The probe runs at startup and prints through Godot, which lands in logcat.
for _ in $(seq 1 30); do
  sleep 1
  if "$ADB" -s "$DEVICES" logcat -d 2>/dev/null | grep -q "Molehill Phase 0 probe"; then
    break
  fi
done

echo
"$ADB" -s "$DEVICES" logcat -d 2>/dev/null \
  | grep -A 6 "Molehill Phase 0 probe" \
  | sed 's/^.*godot *: *//' \
  | head -12

step "Desktop, for comparison"
dotnet run --project "$REPO_ROOT/tools/Molehill.Cli" --configuration Release -- selftest \
  | grep -E "COMBINED"
