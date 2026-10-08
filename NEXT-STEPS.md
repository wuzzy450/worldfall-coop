# Coopfall – next steps (handoff, 2026-10-08)

Working copy: this folder (git, origin github.com/wuzzy450/worldfall-coop, branch `main`).
Everything below "Done" is committed and pushed, including `release/Coopfall.dll` (+ `.sha256`)
and the relay plugin (`server/cuberite/Plugins/WorldfallRooms`, protocol **v3**).

## Status
- Relay test: `python server/test_client.py` → **73/73**.
- Two-game sync test (`sh tools/run-meet.sh`, about 5 min): last three runs **21/21, 22/22, 21/21**
  "everything matches" (22 = with the new rain-cloud step).
- Two-game lobby test (`sh tools/run-lobby.sh`, about 4 min): every step passes (password, mods
  check, approval, guest limits, speed put back, spectator can't possess, kick, diagnostics zip).
- Tested by two players on two PCs on the **same LAN**. **Not yet tested over the internet via a
  public IP** (port forwarding). That is the most important open test.

## Next session: do first
1. **Internet test** with a friend: forward TCP 25598, connect via public IP. Check the HUD ping,
   joining a big world (snapshot transfer over a slow uplink), and that live sync keeps up
   (creatures 10 Hz, tiles about 7 Hz). If the host's upload is too slow, consider lowering
   `liveSyncHz` or compressing `wu` lines.
2. **Relay hang when started from the test scripts.** A relay started by `run-meet.sh` /
   `run-lobby.sh` sometimes stops answering (no CPU, port open, no logs) after the games close
   or mid-run. Starting it by hand (`tail -f /dev/null | ./Cuberite.exe > console.log` from
   `server/cuberite`, or `start_server.bat`) has never hung. Probably the way the script
   detaches it from its pipes. Workaround: restart the relay before `test_client.py`. Check that
   `start_server.bat` (what players use) never hangs in a long session.
3. Click through the new menus by hand once (the lobby test drives them through code and
   screenshots, not mouse clicks): password box, mods window, approval box, settings section,
   Kick / Watch buttons, Export diagnostics.

## Ideas / open issues
- **"Full lockstep" (asked 2026-10-08):** in theory a DLL could patch WorldBox to be deterministic
  (one seeded random number generator stepped per tick, no parallel jobs, fixed time step, inputs
  delayed by the ping), so no extra download is needed. In practice it means rewriting how a
  large, multithreaded game simulates, it breaks on every WorldBox update, and float math can
  still differ between CPUs. Not planned. The current host-authoritative sync plus muting guests'
  own dice gets the same result for players, with only the network delay as a difference.
- Weather: WorldBox clouds and Worldfall's wind gusts are synced ("wc"). Lightning bolts from a
  lightning cloud still strike where each game rolls them (the damage and fires follow the host).
  Sync the strikes too if players notice.
- Spectators: can't use powers/speed/possess. Their camera could follow a player by default.
- Mods check compares file fingerprints. A mod that writes its own files (logs, caches) into its
  folder changes its fingerprint; `.log/.pdb/.tmp/.cache` are already ignored. Watch for reports.
- Approval requests time out after 60 s; there's no "cancel" button for the waiting joiner.
- The sync reports (DIAG) now skip host changes under 1.5 s old ("still on their way"); a miss
  means a real desync.

## Build / run / test
- Build: `cd client; powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstall` (the install
  step fails while the game has the DLL open). Output `client/Coopfall/bin/Release/Coopfall.dll`.
- `tools/run-meet.sh` and `tools/run-lobby.sh` (Git Bash) install the built DLL, archive old logs,
  restart the relay with no stored worlds, start host + guest games and print the results.
  Run them in the foreground with a 600000 ms timeout.
- Logs: `%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\coopfall\profiles\{host,guest}\log.txt`
  (`TEST lobby:` / `DIAG #` lines), screenshots `lobby-*.png`, `diag\`.
- DIAG details: `grep -a -A12 "DIAG #<n> details" profiles/host/log.txt`.
- Release: copy the DLL to `release/`, then `sha256sum -b Coopfall.dll > Coopfall.dll.sha256`.

## Done (2026-10-07/08, protocol v3)
- Mods check (gameplay mods must match; client-only mods ignored), world settings (password stored
  hashed, lock, approval, max players, spectators, guest powers, guest speed, extra mods,
  "everyone is an admin"), kick, per-player ping, spectating, safer backups, Export diagnostics.
- Defaults are open: everyone is an admin, all powers, speed/pause for all, no approval, no
  password, connect automatically.
- Fire: on guests only the host's fires burn (`TileSync.OnlyHostFires`); copied fires no longer
  burn away the host's grass (fire applied before the tile type).
- Weather sync (`WeatherSync.cs`): clouds and Worldfall gusts.
- Menu fits its buttons; toggles show their state.
