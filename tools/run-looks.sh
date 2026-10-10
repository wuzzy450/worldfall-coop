#!/bin/sh
# Two games in the "lockstep-looks" scenario: both draw the same creatures with Worldfall's character
# view at ticks 400 and 1400 of every epoch; then compare-looks.py checks host against guest.
S=$(cd "$(dirname "$0")/.." && pwd)
P="$USERPROFILE/AppData/LocalLow/mkarpenko/WorldBox/coopfall/profiles"
rm -rf "$P/host/looks" "$P/guest/looks"
TRACE=${TRACE- } SCENARIO=lockstep-looks sh "$S/tools/run-lockstep.sh"
python "$S/tools/compare-looks.py" "$P/host" "$P/guest"
