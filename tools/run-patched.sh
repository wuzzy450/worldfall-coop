#!/bin/sh
# Two-game test of the 2026-10-07 sync fixes ("patched" scenario). Notes are kept locally, not in the repository.
# Builds the mod, installs it, starts a private relay (your real worlds are set aside and put back
# afterwards), starts a host and a guest game, waits for the scenario to finish, then prints every
# "TEST CHECK" line from both games and the DIAG summaries from the host.
S=$(cd "$(dirname "$0")/.." && pwd)
W="${WORLDBOX_DIR:-/c/Program Files (x86)/Steam/steamapps/common/worldbox}"
C="$S/server/cuberite"
P="$USERPROFILE/AppData/LocalLow/mkarpenko/WorldBox/coopfall/profiles"
P2="$USERPROFILE\\AppData\\LocalLow\\mkarpenko\\WorldBox\\coopfall\\profiles"
T=$(date +%H%M%S)

"/c/Program Files/dotnet/dotnet.exe" build "$S/client/Coopfall/Coopfall.csproj" -c Release -nologo -v q || exit 1
taskkill //IM worldbox.exe >/dev/null 2>&1; sleep 5; taskkill //F //IM worldbox.exe >/dev/null 2>&1; sleep 1
cp "$S/client/Coopfall/bin/Release/Coopfall.dll" "$W/worldbox_Data/StreamingAssets/mods/" || exit 1
for r in host guest; do for f in log.txt diag unity.log; do [ -e "$P/$r/$f" ] && mv "$P/$r/$f" "$P/$r/$f.bak-$T"; done; done

# Private relay: set the real rooms aside, always restore them.
RELAY_STARTED=0
if tasklist | grep -qi cuberite; then
  echo "A relay is already running - stop it first (type 'stop' in its window) so the test doesn't touch your real worlds."; exit 1
fi
[ -d "$C/worldfall_rooms" ] && mv "$C/worldfall_rooms" "$C/worldfall_rooms.real-$T"
restore() {
  [ $RELAY_STARTED = 1 ] && taskkill //F //IM Cuberite.exe >/dev/null 2>&1 && sleep 2
  [ -d "$C/worldfall_rooms" ] && mv "$C/worldfall_rooms" "$C/worldfall_rooms.test-$T"
  [ -d "$C/worldfall_rooms.real-$T" ] && mv "$C/worldfall_rooms.real-$T" "$C/worldfall_rooms"
  echo "real worlds restored; this run's test rooms: server/cuberite/worldfall_rooms.test-$T"
}
trap restore EXIT INT TERM
(cd "$C" && tail -f /dev/null | ./Cuberite.exe > console.log 2>&1 &)
RELAY_STARTED=1
n=0; until grep -q "listening" "$C/console.log" 2>/dev/null; do sleep 1; n=$((n+1)); [ $n -gt 60 ] && echo "relay didn't start" && exit 1; done

cd "$W"
./worldbox.exe -coopfall-profile host -coopfall-scenario patched -coopfall-test-load 1 -logFile "$P2\\host\\unity.log" >/dev/null 2>&1 &
sleep 40
./worldbox.exe -coopfall-profile guest -coopfall-scenario patched -logFile "$P2\\guest\\unity.log" >/dev/null 2>&1 &
sleep 2
L="$P/host/log.txt"; n=0
until grep -aq "TEST meet: done" "$L" 2>/dev/null || [ $(tasklist | grep -ci worldbox) -lt 2 ]; do sleep 3; n=$((n+1)); [ $n -gt 300 ] && echo TIMEOUT && break; done
sleep 8   # last DIAG capture
echo "games running: $(tasklist | grep -ci worldbox)"
taskkill //IM worldbox.exe >/dev/null 2>&1; sleep 5; taskkill //F //IM worldbox.exe >/dev/null 2>&1

echo; echo "=== checks ==="
for r in host guest; do grep -a "TEST CHECK" "$P/$r/log.txt" | cut -c1-12,22-260 | sed "s/^/[$r] /"; done
echo; echo "=== warnings ==="
for r in host guest; do grep -a -E "\[WARN|\[ERROR|re-syncing|died \(Other\)" "$P/$r/log.txt" | cut -c1-200 | sed "s/^/[$r] /" | head -20; done
echo; echo "=== DIAG ==="
grep -a "DIAG #[0-9]* (" "$L" | cut -c22-240
echo; echo "screenshots: $P/host/diag and $P/guest/diag (paired by DIAG number), plus test-*.png in the profile folders"
