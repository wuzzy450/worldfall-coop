# WorldfallRooms protocol v2

TCP, port 25598, UTF-8 JSON, one object per line (`\n`). Every message has `"t"` (type).

## Client → server

| t | fields | meaning |
|---|---|---|
| `hello` | `name, version:2, color "#rrggbb", game` | first message; names are made unique |
| `join` | `room, name?, seed, preferLocal, resume?` | enter a world. `seed`: willing to provide my open world if the room is new/empty. `preferLocal`: replace the stored copy with my open world (owner of a `home-` room, or `shared`). `resume`: I was this world's last host (connection dropped), so `preferLocal` is allowed for me too |
| `leave` | | leave the current world |
| `resync` | | guest asks for a fresh copy of the world from the host |
| `snap-begin` | `room, size, sha, total` | host uploads a snapshot (zlib'd save JSON, like `map.wbox`) |
| `snap-chunk` | `room, seq, data(base64)` | 60 KB raw per chunk |
| `snap-end` | `room` | |
| `preview` | `room, png(base64), stats{year,pop,w,h}` | host's map thumbnail |
| `avatar` | `on, aid, asset, x, y, flip, hp, mhp` | my possessed unit (≈15 Hz); `on:false` = released |
| `cursor` | `x, y, p?` | my god cursor + selected power (≤10 Hz) |
| `act` | `a (attack/talk/swear/steal), x, y` | possessed-unit action |
| `power` | `p, x, y, brush` or `batch:[...]` | god power used on a tile |
| `speed` | `s, paused` | world speed / pause changed |
| `wu` | `room, seq, part, parts, full, idu, ck[cities,kingdoms], a[asset ids], u[...]` | host only: creatures. `u` is flat `[idDelta, assetIndex, x*10, y*10, hp, ...]`, ids ascending and delta-coded. `full` lists include every creature (guests remove the rest); otherwise only moved/changed ones |
| `wb` | `room, seq, part, parts, full, idb, a[asset ids], b[...], gone?[ids]` | host only: buildings, `b` is `[idDelta, assetIndex, state(0 normal, 1 building, 2 ruin), ...]` |
| `wdata` | `room, u[ActorData], b[BuildingData]` | host only: full save data of new (or requested) creatures/buildings |
| `wneed` | `room, u[ids], b[ids]` | guest asks the host for `wdata` of objects it is missing |
| `chat` | `text` | server-wide chat |
| `rename-room` / `delete-room` | `room, name?` | owner only |
| `ping` | `ts` | keep-alive (every 5 s when idle) |
| `bye` | | disconnect |

`avatar, cursor, act, power, speed` are relayed to the other synced players in the sender's room,
with `id, name, color, room` added by the server (ids can't be spoofed).

Live sync lines (`wu, wb, wdata, wneed`) must start with `{"t":"<type>"`: the server recognizes
them by that prefix and relays them verbatim without decoding (they are large and frequent).
`wu, wb, wdata` are dropped unless the sender is the room's host. Guests number objects their
own simulation creates from `idu`/`idb` + 50,000,000, so they never reuse one of the host's ids.

## Server → client

`welcome {yourId, name, rooms, players}`, `players {players}`, `rooms {rooms}`,
`preview {room, png}`, `joined {room, name, role: host|guest, load, host}`, `role {room, role:"host"}`
(host migration), `snap-request {room, reason}` (to the host), `snap-begin/chunk/end` (to joiners),
`snap-stored {room, version}`, `chat {id, name, color, room, text}`, `notice {text}`,
`error {msg}`, `pong {ts}`, plus the relayed messages above.

## Room rules

- Joining a room that has a host: the joiner waits; the host gets `snap-request` and its upload is
  streamed to everyone waiting and stored (disk: `cuberite/worldfall_rooms/<id>/`).
- Joining an empty room: the joiner becomes host and loads the stored copy, or (`seed`) provides
  their own world if there is no copy yet / `preferLocal` applies.
- Host leaves: the first synced member becomes host; with nobody left the room keeps its last
  snapshot. If a host doesn't answer a snapshot request in 90 s, waiting players get the stored copy.
