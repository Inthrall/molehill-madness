#!/usr/bin/env bash
#
# Build the client and export a debug APK. If a device is connected and authorised, also
# install it and read the log back; if not, print where the APK is so it can be sideloaded.
#
# The install half is optional on purpose. USB debugging is not available on every phone,
# and an APK that has to be copied across by hand is still an APK, so a missing device is
# not a failure. Pass --export-only to skip looking for one at all.
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
PACKAGE="nz.molehill.madness"
APK="$REPO_ROOT/build/android/molehill-madness.apk"
EXPORT_ONLY=0
[ "${1:-}" = "--export-only" ] && EXPORT_ONLY=1

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

step "Checking the toolchain"
[ -x "$GODOT" ] || { echo "Godot not found at $GODOT"; exit 1; }
echo "godot  $("$GODOT" --version 2>/dev/null | head -1)"

DEVICES=""

if [ "$EXPORT_ONLY" = "0" ] && [ -x "$ADB" ]; then
  step "Looking for a device"
  "$ADB" start-server >/dev/null 2>&1 || true
  DEVICES="$("$ADB" devices | awk 'NR>1 && $2=="device" {print $1}')"

  if [ -n "$DEVICES" ]; then
    echo "device $DEVICES"
    echo "model  $("$ADB" -s "$DEVICES" shell getprop ro.product.model 2>/dev/null | tr -d '\r')"
    echo "abi    $("$ADB" -s "$DEVICES" shell getprop ro.product.cpu.abi 2>/dev/null | tr -d '\r')"
  else
    echo "None connected and authorised. Exporting anyway, to be copied across by hand."
  fi
fi

step "Building the simulation and its tests"
dotnet test "$REPO_ROOT/Molehill.slnx" --configuration Release --nologo --verbosity quiet

step "Exporting the APK"
mkdir -p "$(dirname "$APK")"
rm -f "$APK"
# Godot builds the C# assemblies as part of the export.
"$GODOT" --headless --path "$REPO_ROOT/client" --export-debug "Android" "$APK"
[ -f "$APK" ] || { echo "Export produced no APK."; exit 1; }
ls -lh "$APK"

# Godot silently exports an APK with no C# in it if the classic .sln beside the client
# csproj is missing. It warns, signs it, and looks exactly like success, so this is checked
# rather than trusted.
step "Checking the C# actually made it in"
if command -v unzip >/dev/null 2>&1; then
  if unzip -l "$APK" | grep -q "MoleSim.dll"; then
    echo "MoleSim.dll is present."
  else
    echo "MoleSim.dll is MISSING. Check that client/Molehill.Client.sln exists."
    exit 1
  fi
else
  echo "unzip not available, skipped. Verify by hand if the APK looks suspiciously small."
fi

if [ -z "$DEVICES" ]; then
  step "Done"
  echo "No device, so nothing was installed. Copy this across and open it on the phone:"
  echo "  $APK"
  echo
  echo "Android will ask permission to install from this source the first time."
  exit 0
fi

step "Installing"
"$ADB" -s "$DEVICES" install -r "$APK"

step "Launching"
"$ADB" -s "$DEVICES" logcat -c
"$ADB" -s "$DEVICES" shell am start -n "$PACKAGE/com.godot.game.GodotApp" >/dev/null
echo "Started. Watch for trouble with:"
echo "  \"$ADB\" -s $DEVICES logcat -s godot"
