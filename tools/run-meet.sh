#!/bin/sh
# Install the built DLL, archive old logs, start host + guest in the "meet" scenario (relay must be running).
taskkill //IM worldbox.exe >/dev/null 2>&1; sleep 5; taskkill //F //IM worldbox.exe >/dev/null 2>&1; sleep 1
S=/c/Users/Wuzzy450/Desktop/staging-for-github-upload
W="/c/Program Files (x86)/Steam/steamapps/common/worldbox"
cp $S/client/Coopfall/bin/Release/Coopfall.dll "$W/worldbox_Data/StreamingAssets/mods/" || exit 1
T=$(date +%H%M%S); P="$USERPROFILE/AppData/LocalLow/mkarpenko/WorldBox/coopfall/profiles"
for r in host guest; do for f in log.txt diag unity.log; do [ -e "$P/$r/$f" ] && mv "$P/$r/$f" "$P/$r/$f.bak-$T"; done; done
[ -d $S/server/cuberite/worldfall_rooms ] && mv $S/server/cuberite/worldfall_rooms $S/server/cuberite/worldfall_rooms.bak-$T
P2="$USERPROFILE\AppData\LocalLow\mkarpenko\WorldBox\coopfall\profiles"
cd "$W"
./worldbox.exe -coopfall-profile host -coopfall-scenario meet -coopfall-test-load 1 -logFile "$P2\host\unity.log" >/dev/null 2>&1 &
sleep 40
./worldbox.exe -coopfall-profile guest -coopfall-scenario meet $GUEST_ARGS -logFile "$P2\guest\unity.log" >/dev/null 2>&1 &
sleep 2
L="$P/host/log.txt"; n=0
until grep -aq "after guest used bear, 2" "$L" 2>/dev/null || [ $(tasklist | grep -ci worldbox) -lt 2 ]; do sleep 3; n=$((n+1)); [ $n -gt 250 ] && echo TIMEOUT && break; done
echo "games running: $(tasklist | grep -ci worldbox)"
grep -a "DIAG #[0-9]* (" "$L" | cut -c22-240
