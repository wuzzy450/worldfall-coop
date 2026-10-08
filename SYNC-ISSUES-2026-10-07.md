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
| 9 | Players entering a building don't go in on the other side | Avatar packets had no building | **patched** (`bld` in the avatar packet, from vanilla or Worldfall's house interior). The other player's creature goes inside too. **Still open:** standing at the same spot inside Worldfall's room view (needs adding them to Worldfall's room walkers) |
| 10 | Tree the guest chops shakes but never falls | Worldfall's chopping finishes with `Building.extractResources` locally. The host never heard about it, and live sync kept the host's tree | **patched** (`act: work` with building id, detected from Worldfall's `Work._yieldAt`. Receiver runs `extractResources`, so trees fall, rocks get mined, plants get gathered) |
| 11 | Guest's sword not shown in their hand | Equipment only flowed host → guest, never from a player's own creature | **patched** (`wpn` in the avatar packet. The puppet gets that weapon. Host's equipment sync no longer overwrites a player's own creature or a puppet) |
| 12 | Guest has a bounty but nobody comes to kill them | The host undid every hit on a remote player's creature and only relayed hits from the host player, so guards and other NPCs could never hurt a player | **patched** (`CombatSync`: on the host, NPC hits on a puppet are sent to its owner as real damage). Whether guards *choose* to hunt the guest depends on Worldfall's bounty logic. Re-test |
| 13 | Laser guns / shots (players and NPC aliens) not seen by the other player | Worldfall fires everything through vanilla `World.world.projectiles.spawn`. Projectiles aren't synced (damage is decided at attack time, and player-vs-player hits already go through `hit`) | **patched** (`shot` packets: the host sends every new projectile, a guest only its own creature's; the other side spawns the same projectile from the same shooter) |
| 14 | Host wasn't alerted when the guest killed a king | Not checked yet. The guest's kill reaches the host as `whit` → `getHit`, so the host's own death news should fire. Need to re-test with the new DLL first | open |
| 15 | A forced re-sync (world reload) killed the guest's possessed creature (log: `Defect's alien #604 died (Other)` right after `snapshot requested (resync)`) | The re-possess-after-reload step didn't catch it. A flood of god powers (400 replayed in ~5 min) likely caused the drift that triggered the reload | open, needs the guest's log from 21:28 |
| 16 | Village populations still way off after `/sync` | A village's count is the living creatures listed for it, and the game only re-lists them when the village is marked dirty | **diagnostic + fix** (host sends each village's count every 2 s. The guest recounts stale villages and logs `population: <village> has N here (listed M), host K` for real differences) |

## Testing checklist (both players need the new Coopfall.dll)
- Guest takes damage to 0: dies on both screens.
- Host possessing in 3D, guest zoomed in (god view) and zoomed out (2D): tag over the host in both.
- Change a kingdom's color on the host: it updates on the guest within ~2 s.
- Jump, use X, chop a tree, hold a sword: the other player sees each one.
- Walk into a house: the other player sees you disappear inside, not standing at the door.
- Guest murders in a town: guards attack and do damage.
- Shoot a laser/bow and watch an alien NPC shoot: the other player sees the shots.
- Village populations: check the guest's log for `population:` lines.
- Log lines to look for: `Worldfall abilities:`, `Worldfall work:`, `inside sync`, `weapon sync`, `ability ...` warnings.
