# Test scenario "patched": two games on one PC

Checks every sync fix from the 2026-10-07 session (see [SYNC-ISSUES-2026-10-07.md](SYNC-ISSUES-2026-10-07.md))
without a second player. One script builds the mod and starts a private relay. Then it opens two WorldBox
windows: a **host** and a **guest**, each with its own Coopfall profile. They play a scripted
scenario. Each game logs a pass/fail line for what it should see, and both take screenshots at the same moment.

## Run it

1. Close WorldBox, and stop your own relay if it's running (type `stop` in its window). The script refuses
   to run while another relay is up, so your real worlds are never touched.
2. From the repo folder:
   ```bash
   sh tools/run-patched.sh
   ```
   It takes about 10–15 minutes. Don't click into the two game windows while it runs.
3. When it finishes it prints:
   - `=== checks ===`: every `TEST CHECK <name>: PASS|FAIL - <detail>` line from both games
   - `=== warnings ===`: warnings/errors, any automatic re-sync, any "died (Other)"
   - `=== DIAG ===`: the paired diagnostic snapshots (host log), one after every step

What the script does to your machine:
- builds `client/Coopfall` and copies the DLL into the game's mods folder
- moves `server/cuberite/worldfall_rooms` (your real worlds) to `worldfall_rooms.real-<time>` for the run, and
  **always moves it back** at the end, even after Ctrl+C. The test's own rooms are kept as `worldfall_rooms.test-<time>`
- archives the test profiles' old logs as `*.bak-<time>`
- the host loads **save slot 1** first (a world with kingdoms to test on). Keep a world with villages in slot 1

Where the results are (`%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\coopfall\profiles\`):
- `host\log.txt`, `guest\log.txt`: full logs (search `TEST`)
- `host\diag\`, `guest\diag\`: paired screenshots + state dumps, by DIAG number
- `guest\test-room-guest.png`, `guest\test-tag-guest.png`: extra screenshots for the room view and nameplate

## The steps

Setup (same as the older `meet` scenario): the host spawns two humans 6 tiles apart on open ground. The host
possesses one ("Host Hero"), the guest possesses the other ("Guest Hero"), both in Worldfall's first person,
facing each other. Then, one step every ~10 s (each followed by a DIAG with screenshots):

| # | Step | What happens | Checked by | PASS means |
|---|------|--------------|------------|------------|
| 0 | `jump` | guest jumps and relays it | host | host replayed the guest's jump |
| 1 | `ability` | guest presses the ability key (Worldfall's own test hook) | host | host replayed an ability use. **FAIL is possible** if the guest's species has no ability or it's on cooldown; check the guest log for `ability` |
| 2 | `weapon` | guest equips an iron sword | host | guest's creature holds `sword_iron` on the host |
| 3 | `shots` | a host NPC shoots an arrow at the guest; the guest shoots one at the host | both | `shots-guest-to-host` and `shots-host-to-guest`: each side spawned the other's projectile |
| 4 | `npc-hit` | a host NPC (walker) hits the guest's creature for 10 | guest | guest's hp went down through a relayed hit |
| 5 | `chop` | guest chops the nearest tree (same path Worldfall uses when it finishes a chop) | host | the tree fell / was chopped on the host |
| 6 | `inside-building` | host puts a walker into a house | guest | the walker is in the same place (inside that house, or back outside if its AI left) on both |
| 7 | `kingdom-color` | host changes a kingdom's color | guest | guest's data **and drawn color** match the host's |
| 8 | `room-view` | both enter the same house (Worldfall's room view); the guest then walks a step into the room (both start on the room's spawn spot) | both | `room-view-host` / `room-view-guest`: the other player is one of the people in your room. `room-nameplate-host` / `room-nameplate-guest`: their name tag is drawn in the room. Screenshots `test-room-host.png`, `test-room-guest.png` |
| 9 | `king-kill` | guest kills a king with its creature | host | the king died on the host. Host log has `... - the king of <kingdom>` |
| 10 | `resync-keeps-creature` | guest does a full re-sync while possessing | host | after the reload, the guest is back in the same creature and it's alive on the host as the **real** creature, not a stand-in |
| 11 | `guest-death` | guest's creature takes lethal damage | both | `guest-death-local`: it dies on the guest; `guest-death`: it dies on the host too |
| 12 | `flood` | both spawn humans/skeletons 4× a second for 12 s, then 50 s to settle | guest | guest's creature, village and kingdom counts match the host's. Warnings must not show `re-syncing` |
| 13 | `population` | guest compares every village's population with the host's | guest | `all villages match`. Only villages that stay different for over 6 s count, since births and deaths between two 2-second updates are normal |
| 14 | `nameplate-godview` | guest stops possessing and zooms into Worldfall's god view on the host | guest | god view is on and the host's name tag was drawn in the last second. Screenshot `test-tag-guest.png` |

The code is `client/Coopfall/TestPatched.cs` (steps, checks and guest commands). `TestDriver.cs` has the shared
setup and `DiagSync.cs` the paired DIAG capture.

## Things the first run found (2026-10-07)

- **Shots never arrived:** the relay only forwards message types it knows, and `shot` was missing
  (`server: unknown message type 'shot'` in the logs). Fixed in `server/cuberite/Plugins/WorldfallRooms/Main.lua`.
  **Restart your own relay after updating** so real games get it too.
- **Population was off by one** in two villages: timing between 2-second updates, not a sync bug. The check now ignores short differences.
- **No name tag inside the room:** both players had just come in on the same spawn spot, so the other one was
  inside your own camera. The test now steps apart and checks the tag.

## Screen recorder

`%USERPROFILE%\WorldBoxRecorder\recorder.ps1` skips any WorldBox started with `-coopfall-scenario`,
`-coopfall-test` or `-coopfall-profile` (its log says `Not recording: Coopfall test run`), so test runs aren't recorded.

## Latest results (run 6, 2026-10-07)

**19 of 20 checks pass**, no warnings, and every DIAG matches except a single differing tile twice. Over runs 1–6 the scenario also found and fixed:

- the relay dropping `shot` messages (above)
- a **stand-in loop after a re-sync**: the host released and re-spawned a stand-in for the guest every second, because the
  "is this their real creature" check in `AvatarManager.Resolve` was stricter (distance < 12) than the swap check in `Drive`.
  So the guest's death only killed the stand-in. The real creature is now taken whenever the id and species match
  (it walks to the player, and is brought out of a house it was left in)
- population counts compared the host's *listed* count with the guest's *actual* count. Both now count actual living members

Still open: **population** is occasionally off by 1–2 in one village for longer than 6 s after a flood (`Zames 94/93`).
It's a small creature-count drift, not a counting mistake. Look at which creature is extra on the guest
(the DIAG after step 13 lists creatures).

## Reading a failure

- Find the step's `TEST step N:` line in the host log and read both logs from there. The guest acts on commands
  shown as `TEST guest ...`.
- Compare that step's DIAG screenshots (`host\diag` vs `guest\diag`, same number).
- Some steps fake the input instead of using real keys or mouse. Jump, chop and weapon call the same code
  Coopfall runs for the real action, and the ability uses Worldfall's own `PressAbilityForTest`. A PASS proves
  the sync path, so still try the real keys in a real game once.

## Not covered (test by hand with a second player)

- Real Worldfall chopping (swinging an axe until it falls) and real X ability keys: the relay is covered, the detection isn't.
- Village counts drifting over a long session, and whether guards actually *hunt* a guest with a bounty
  (the test only proves NPC hits on a guest now count).

## Handoff notes for a fresh chat

- Repo: this folder. Public names only: **Wuzzy450** (owner), **Defect** (tester). Never put a real name or
  `C:\Users\<name>` path in anything committed.
- Build: `"C:\Program Files\dotnet\dotnet.exe" build client\Coopfall\Coopfall.csproj -c Release` (.NET 8 SDK installed).
  `client\build.ps1` builds and installs (fails while WorldBox has the DLL open).
- Release: copy `client\Coopfall\bin\Release\Coopfall.dll` to `release\` and update `release\Coopfall.dll.sha256`.
- Decompiling the game/Worldfall: `ilspycmd` is installed as a global .NET tool and needs `DOTNET_ROLL_FORWARD=Major`.
  `ilspycmd -p -o <outdir> -r "<worldbox>\worldbox_Data\Managed" <dll>` decompiles a whole assembly
  (Assembly-CSharp.dll, or Worldfall.dll in StreamingAssets\mods).
- Issue list and status: [SYNC-ISSUES-2026-10-07.md](SYNC-ISSUES-2026-10-07.md).
