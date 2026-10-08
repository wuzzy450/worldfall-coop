#!/bin/sh
# Lobby test: world settings, password, mods check, approval, spectator, kick, diagnostics (restarts the relay).
taskkill //IM worldbox.exe >/dev/null 2>&1; sleep 5; taskkill //F //IM worldbox.exe >/dev/null 2>&1; sleep 1
S=$(cd "$(dirname "$0")/.." && pwd)
W="/c/Program Files (x86)/Steam/steamapps/common/worldbox"
cp $S/client/Coopfall/bin/Release/Coopfall.dll "$W/worldbox_Data/StreamingAssets/mods/" || exit 1
T=$(date +%H%M%S); P="$USERPROFILE/AppData/LocalLow/mkarpenko/WorldBox/coopfall/profiles"
for r in host guest; do for f in log.txt diag unity.log; do [ -e "$P/$r/$f" ] && mv "$P/$r/$f" "$P/$r/$f.bak-$T"; done; done
# a fresh relay: worlds (and their settings) left over from an earlier run would change the result
taskkill //F //IM Cuberite.exe >/dev/null 2>&1; sleep 1
[ -d $S/server/cuberite/worldfall_rooms ] && mv $S/server/cuberite/worldfall_rooms $S/server/cuberite/worldfall_rooms.bak-$T
(cd $S/server/cuberite && (tail -f /dev/null | ./Cuberite.exe > console.log 2>&1 &)) > /dev/null 2>&1 < /dev/null
for i in $(seq 1 30); do grep -q "listening on port" $S/server/cuberite/console.log 2>/dev/null && break; sleep 1; done
P2="$USERPROFILE\AppData\LocalLow\mkarpenko\WorldBox\coopfall\profiles"
cd "$W"
./worldbox.exe -coopfall-profile host -coopfall-scenario lobby -coopfall-test-load 1 -logFile "$P2\host\unity.log" >/dev/null 2>&1 &
sleep 40
./worldbox.exe -coopfall-profile guest -coopfall-scenario lobby -coopfall-fake-mod FakeTestMod $GUEST_ARGS -logFile "$P2\guest\unity.log" >/dev/null 2>&1 &
sleep 2
L="$P/guest/log.txt"; n=0
until grep -aq "TEST lobby: done" "$L" 2>/dev/null || [ $(tasklist | grep -ci worldbox) -lt 2 ]; do sleep 3; n=$((n+1)); [ $n -gt 250 ] && echo TIMEOUT && break; done
echo "games running: $(tasklist | grep -ci worldbox)"
grep -ah "TEST lobby" "$P/host/log.txt" "$P/guest/log.txt" | cut -c1-400
taskkill //IM worldbox.exe >/dev/null 2>&1
