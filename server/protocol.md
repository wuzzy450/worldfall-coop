# WorldfallRooms protocol v3

TCP, port 25598, UTF-8 JSON, one object per line (`\n`). Every message has `"t"` (type).

## Client → server

| t | fields | meaning |
|---|---|---|
| `hello` | `name, version:3, color "#rrggbb", game, mods[{id, ver}]` | first message; names are made unique. `mods` = gameplay mods (client-only mods left out), `ver` a file fingerprint |
| `join` | `room, name?, seed, preferLocal, resume?, password?, spectate?` | enter a world. `seed`: willing to provide my open world if the room is new/empty. `preferLocal`: replace the stored copy with my open world (owner of a `home-` room, or `shared`). `resume`: I was this world's last host (connection dropped), so `preferLocal` is allowed for me too |
| `leave` | | leave the current world |
| `resync` | | guest asks for a fresh copy of the world from the host |
| `snap-begin` | `room, size, sha, total` | host uploads a snapshot (zlib'd save JSON, like `map.wbox`) |
| `snap-chunk` | `room, seq, data(base64)` | 60 KB raw per chunk |
| `snap-end` | `room` | |
| `preview` | `room, png(base64), stats{year,pop,w,h}` | host's map thumbnail |
| `avatar` | `on, aid, asset, x, y, flip, hp, mhp` | my possessed unit (≈15 Hz); `on:false` = released; `on:false, dead:true, aid, at, by` = it died (cause `at` = AttackType, killer `by`) |
| `cursor` | `x, y, p?` | my god cursor + selected power (≤10 Hz) |
| `act` | `a (attack/talk/swear/steal), x, y` | possessed-unit action (the host also applies a guest's attacks on buildings) |
| `hit` | `to, aid, dmg, at, by` | my possessed unit hit player `to`'s possessed unit `aid` in my game; their game applies it |
| `diag` | `cmd, ...` | sync diagnostics: `capture {n, reason}` (everyone snapshots), `state {n, s}` (to the host, which compares), and test-scenario commands `possess / face / act {for, ...}` |
| `whit` | `vid, hp, at, by` | guest: my possessed unit `by` hit creature `vid`, leaving it at `hp`; the host applies it |
| `power` | `p, x, y, brush` or `batch:[...]` | god power used on a tile |
| `speed` | `s, paused` | world speed / pause changed |
| `wu` | `room, seq, part, parts, full, ts, idu, ck[cities,kingdoms], a[asset ids], u[...], d?[...], ids?{}` | host only: creatures. `u` is flat `[idDelta, assetIndex, x*10, y*10, hp, flags, heading, vx*10, vy*10, height*10, ...]` (`st` = values per creature, now 10; flags: 1 facing right, 2 walking, 4 pushed by forces; heading 0-255 or -1; vx, vy = velocity in tiles/s), ids ascending and delta-coded. `ts` = host send time (UTC ms); guests use it to tell how long the message waited in queues. `full` lists include every creature (guests remove the rest); otherwise only moved/changed ones. `d` = deaths since the last message, flat `[id, attackType, killerId, ...]`. `ids` (full only) = the host's next id per object type (`id_unit`, `id_city`, ...) |
| `wb` | `room, seq, part, parts, full, idb, a[asset ids], b[...], gone?[ids]` | host only: buildings, `b` is `[idDelta, assetIndex, state(0 normal, 1 building, 2 ruin), ...]` |
| `wdata` | `room, u[ActorData], b[BuildingData]` | host only: full save data of new (or requested) creatures/buildings |
| `wneed` | `room, u[ids], b[ids]` | guest asks the host for `wdata` of objects it is missing |
| `wm` | `room, k, d?[data], s?[fingerprints], gone?[ids], full?, h?[id, fingerprint, ...]` | host only: meta objects of kind `k` (`subspecies, family, language, religion, item, book, culture, clan, kingdom, city, war, army, alliance, plot, diplomacy`). `d` = full save data of changed objects, `h` = every object's fingerprint (every 15 s) |
| `wa` | `room, r[[id, name, city, kingdom, clan, family, culture, religion, language, subspecies, army, plot, lover, profession, level, experience, kills, renown, "traits", "items"], ...]` | host only: creatures' details (changed ones every 2 s, all every 20 s). `kingdom` is a civ id or `w:<asset>` for wild kingdoms |
| `wt` | `room, k?[tile keys], z?[[zone, key index x64], ...], n?, zh?[fingerprints]` | host only: terrain. Zones are WorldBox's 8x8-tile zones sorted by (y, x); a tile key is `main|top|fire|burned`. `zh` = every zone's fingerprint (every 10 s) |
| `ww` | `room, time, age, slot, prog, dur, apause, amul, slots, stats{}, laws?[[name, bool, int, string], ...], pop{village: living members}, pl?{village: [creature ids]}` | host only: world time, age/era, statistics and (when changed) world laws, every 2 s. `pl` answers a guest's `pc` |
| `wask` | `room, m?{kind: [ids]}, a?[creature ids], z?[zones], pc?[village ids]` | guest asks the host for meta objects, creature details or zones that are missing or differ, and (`pc`) member lists of villages whose population is off |
| `chat` | `text` | server-wide chat |
| `rename-room` / `delete-room` | `room, name?` | owner only |
| `ping` | `ts, rtt?` | keep-alive (every 5 s); `rtt` = my last measured round trip, shown to everyone |
| `room-settings` | any of `password, locked, approval, maxPlayers, spectators, guestPowers (all/safe/none), blocked[power ids], guestSpeed, allowExtraMods, everyoneAdmin` | admin only (see below); only the given fields change; `everyoneAdmin` only by the owner |
| `approve` | `id, ok` | admin answers a `join-request` |
| `kick` | `id` | admin removes a player from their world (no rejoin for 10 min) |
| `bye` | | disconnect |

`avatar, cursor, act, power, speed, hit, whit, diag` are relayed to the other synced players in the
sender's room, with `id, name, color, room` added by the server (ids can't be spoofed, and any
`id` field the sender put in is replaced).

Live sync lines (`wu, wb, wdata, wm, wa, wt, ww, wneed, wask`) must start with `{"t":"<type>"`:
the server recognizes them by that prefix and relays them verbatim without decoding (they are
large and frequent). `wu, wb, wdata, wm, wa, wt, ww` are dropped unless the sender is the room's
host. Guests number objects their
own simulation creates from `idu`/`idb` + 50,000,000, so they never reuse one of the host's ids.

## Server → client

`welcome {yourId, name, rooms, players}`, `players {players}`, `rooms {rooms}`,
`preview {room, png}`, `joined {room, name, role: host|guest, load, host}`, `role {room, role:"host"}`
(host migration), `snap-request {room, reason}` (to the host), `snap-begin/chunk/end` (to joiners),
`snap-stored {room, version}`, `chat {id, name, color, room, text}`, `notice {text}`,
`error {msg, code?, room?, mods?}`, `pong {ts}`, `join-request {room, id, name, spectate}` (to the
admin), `waiting {room, admin}` (to the joiner), `kicked {room, name, by}`, plus the relayed
messages above. Error codes on a refused join: `password, locked, full, spectators, empty,
kicked, approval, denied, mods` (`mods` = `{missing[], different[], extra[]}`).

`players` entries have `id, name, color, room, host, synced, game, ping, spectator, mods` (count).
`rooms` entries have `settings {hasPassword, locked, approval, maxPlayers, spectators,
guestPowers, blocked, guestSpeed, allowExtraMods, everyoneAdmin}` (never the password), `spectating` and `mods`.

Defaults: no password, not locked, no approval, any number of players, spectators allowed,
`guestPowers` all, `guestSpeed` true, `allowExtraMods` false, `everyoneAdmin` true.

## Room rules

- **Owner** of a world: its owner; for the shared world (no owner), its current host. **Admin**:
  the owner, or, while `everyoneAdmin` is on (the default), every player in the world who isn't
  spectating. The owner skips all join checks; the owner can't be kicked. Everyone else is checked in this order: kicked, locked, password,
  spectators allowed / someone playing (spectators) or max players, mods (the world's mods are
  those of the player who seeded it), approval.
- Guests (not the host, not the admin) have `power` messages dropped per `guestPowers` /
  `blocked`, and `speed` unless `guestSpeed`. Spectators can only send `cursor` and `diag`.
- Passwords are stored as a salted SHA-1 hash in `worldfall_rooms/<id>/meta.json`.

- Joining a room that has a host: the joiner waits; the host gets `snap-request` and its upload is
  streamed to everyone waiting and stored (disk: `cuberite/worldfall_rooms/<id>/`).
- Joining an empty room: the joiner becomes host and loads the stored copy, or (`seed`) provides
  their own world if there is no copy yet / `preferLocal` applies.
- Host leaves: the first synced member becomes host; with nobody left the room keeps its last
  snapshot. If a host doesn't answer a snapshot request in 90 s, waiting players get the stored copy.
