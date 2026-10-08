# Co-op sync issues from the 2026-10-07 session (Wuzzy450 hosting, Defect as guest)

Status key: **patched** = code changed and builds, not yet play-tested with two players.

| # | Issue | Cause | Status |
|---|-------|-------|--------|
| 1 | Guest stuck on "joining shared world", then disconnects (`error 10054`) | Network: guest came in through the router's NAT loopback (`192.168.1.254`), which dropped the world download | Not a code bug. Same house: connect to the host's LAN IP. Different house: fix router double NAT, or use Tailscale/ZeroTier |
| 2 | Guest's possessed creature at 0 hearts but alive | Guest blocks deaths of host-world creatures (host announces them), and that included the guest's own possessed creature. The host only copies the guest's HP, so neither side ever killed it | **patched** (`CombatSync.OnHit`: own creature excluded) |
| 3 | No nameplate for the host when the guest is in "2D" and the host is possessing in 3D | When zoomed in, Worldfall swaps the 2D map for its 3D god view. Tags were placed with 2D map coordinates, which don't match that picture | **patched** (`CoopUI` + `WorldfallBridge.GodView` / `ProjectHead(god)` use Worldfall's renderer projection) |
| 4 | Kingdom colors not synced | WorldBox caches the drawn color (`MetaObject._cached_color`) and only refreshes it in its own setter. Live sync replaced the data and kept the stale cache | **patched** (`MetaSync`: clear cache when `color_id` changes, repaint kingdom buildings and borders). Covers clans, cultures, etc. too |
| 5 | Kingdom/village member counts differ | Counts come from each creature's memberships, synced every 2 s plus a full check every 20 s | Not changed. Short drift is expected. If counts stay wrong, send both logs |
| 6 | Jumping in Worldfall 3D not shown to the other player | Jumps weren't relayed | **patched** (`act: jump` → game's `jump` status on the puppet) |
| 7 | Species ability (X) not shown | Not relayed | **patched** (`act: ability` with ability id and aim. The other side runs that ability's own effect on the puppet). Summons may briefly show extra creatures on guests |
| 8 | Creatures that enter buildings stay standing outside on guests (and can be hit there) | Creature sync had no "inside building" state | **patched** (extra column in the `wu` creature rows: building id. Guest calls `stayInBuilding`/`exitBuilding`. Older clients ignore the column) |
| 9 | Players entering a building don't go in on the other side | Avatar packets had no building | **patched** (`bld` in the avatar packet, from vanilla or Worldfall's house interior). The other player's creature goes inside too. Inside Worldfall's room view, the other player is added to the room's walkers and pinned to their position in their own room (`rx`/`ry` in the avatar packet; the layout is seeded by building id, so both rooms match). **patched** |
| 10 | Tree the guest chops shakes but never falls | Worldfall's chopping finishes with `Building.extractResources` locally. The host never heard about it, and live sync kept the host's tree | **patched** (`act: work` with building id, detected from Worldfall's `Work._yieldAt`. Receiver runs `extractResources`, so trees fall, rocks get mined, plants get gathered) |
| 11 | Guest's sword not shown in their hand | Equipment only flowed host → guest, never from a player's own creature | **patched** (`wpn` in the avatar packet. The puppet gets that weapon. Host's equipment sync no longer overwrites a player's own creature or a puppet) |
| 12 | Guest has a bounty but nobody comes to kill them | The host undid every hit on a remote player's creature and only relayed hits from the host player, so guards and other NPCs could never hurt a player | **patched** (`CombatSync`: on the host, NPC hits on a puppet are sent to its owner as real damage). Whether guards *choose* to hunt the guest depends on Worldfall's bounty logic. Re-test |
| 13 | Laser guns / shots (players and NPC aliens) not seen by the other player | Worldfall fires everything through vanilla `World.world.projectiles.spawn`. Projectiles aren't synced (damage is decided at attack time, and player-vs-player hits already go through `hit`) | **patched** (`shot` packets: the host sends every new projectile, a guest only its own creature's; the other side spawns the same projectile from the same shooter) |
| 14 | Host wasn't alerted when the guest killed a king | The host's game logs a king's death itself when its copy dies, so the alert can only be missing if the host's world didn't have that creature as king (kingdom data drift) | **diagnostic**: host log now says `Defect killed human #N - the king of X`, and notes when the two games disagree on whether it was the king |
| 15 | A forced re-sync (world reload) killed the guest's possessed creature (log: `Defect's alien #604 died (Other)` right after `snapshot requested (resync)`) | After the reload the guest steps back into the same creature, but as a new copy from the reloaded world. Coopfall saw the old copy gone and reported the creature dead, so the host killed it | **patched** (`AvatarManager.ForgetMine` before the reload: send "off", not "dead") |
| 16 | Village populations still way off after `/sync` | A village's count is the living creatures listed for it, and the game only re-lists them when the village is marked dirty | **diagnostic + fix** (host sends each village's count every 2 s. The guest recounts stale villages and logs `population: <village> has N here (listed M), host K` for real differences) |
| 17 | Flooding god powers (hundreds of humans/skeletons) pushed the worlds apart and caused a reload | On a guest, every spawn power ran locally too: a second set of creatures, plus villages/kingdoms the host never had, all to be thrown away. The drift check (counts differ for 90 s) also fired while the host's counts were still climbing | **patched** (creature-spawning powers run only in the host's game. The guest's click is still relayed and the creatures arrive by live sync. The drift re-sync waits until the host's counts stop changing) |

## Testing checklist (both players need the new Coopfall.dll)
- Guest takes damage to 0: dies on both screens.
- Host possessing in 3D, guest zoomed in (god view) and zoomed out (2D): tag over the host in both.
- Change a kingdom's color on the host: it updates on the guest within ~2 s.
- Jump, use X, chop a tree, hold a sword: the other player sees each one.
- Walk into a house: the other player sees you disappear inside, not standing at the door.
- Guest murders in a town: guards attack and do damage.
- Shoot a laser/bow and watch an alien NPC shoot: the other player sees the shots.
- Flood: both players spam humans/skeletons for a few minutes. No freeze or reload, and the counts settle afterwards.
- Village populations: check the guest's log for `population:` lines.
- Log lines to look for: `Worldfall abilities:`, `Worldfall work:`, `inside sync`, `weapon sync`, `ability ...` warnings.
