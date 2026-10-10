# Coopfall: online co-op for WorldBox (with Worldfall's 3D first person)

> **Status: work in progress.** Coopfall has been tested by two players on **two different
> PCs**, but both PCs were on the **same network** (LAN). It has **not yet been
> confirmed to work over the internet via a public IP** (port forwarding). If you try that,
> please report how it went. An official GitHub **release** will follow once it has been
> confirmed to work.
>
> **Want to try it now?** See [Download](#download) below. You can also point your own AI
> coding agent at this repository (see [For AI agents](#for-ai-agents)) and let it build,
> install and test it for you. Bug reports are welcome.
>
> **Join the Discord** for help, bug reports and people to play with: https://discord.gg/nuvpXycag8

Coopfall lets several people play WorldBox together over the internet or a LAN:

- **Shared world**: everybody plays in one world together.
- **Own worlds**: everybody hosts their own world. The in-game **World Map** shows everyone's
  worlds (live map preview, who is there, year, population) and lets you travel to any of them.

In a world you see the other players. If they possess a creature, it walks around in your game
with their name over its head; if they don't, you see their god cursor and selected power.
God powers, game speed/pause and chat are synced, and **live sync** keeps the whole world in
step with the host's simulation: creatures, buildings and trees, kingdoms, cities and their
borders, wars, alliances, diplomacy, cultures, religions, languages, clans, families, armies,
plots, books, items, every creature's name, traits, job and memberships, terrain and fire, the
world's time, age and laws, and the weather (clouds with their rain, snow and storms, and
Worldfall's wind gusts). Kills count everywhere: hit someone in your game and they die in
everyone's game.

Works with **[Worldfall](https://worldfall3d.com/)** (optional), an unofficial first-person mod
for WorldBox: other players appear in Worldfall's 3D view with name tags, health bars and chat
bubbles, and the co-op HUD and chat stay usable in first person.

## Download

| You are | Download | Then |
|---|---|---|
| **Every player** | **[Coopfall.dll](https://github.com/wuzzy450/worldfall-coop/raw/main/release/Coopfall.dll)** (the mod, one file) | [1. Install the mod](#1-install-the-mod-every-player) |
| **The one running a standalone server** (optional: any player can host from the game) | **[Everything (ZIP)](https://github.com/wuzzy450/worldfall-coop/archive/refs/heads/main.zip)** (server, mod and source, about 4 MB) | [2. Run the server](#2-run-the-server-one-person) |

The ZIP also contains the mod (`release\Coopfall.dll`), so the person running the server
only needs the ZIP. Unzip it anywhere (for example to your Desktop); it unpacks to a folder
called `worldfall-coop-main`. Keep the `server` folder in it **complete**: the relay needs
everything in it (`start_server.bat`, `Cuberite.exe`, its DLLs, settings and the
`Plugins\WorldfallRooms` plugin).

## Requirements

- **WorldBox** on PC (Steam). Every player needs the **same WorldBox version**.
- WorldBox **Experimental Mode** turned on (needed for mods to load).
- Optional: **Worldfall**, from its official site https://worldfall3d.com/
  (source and downloads: https://github.com/s3cond2/worldfall).
- To build the mod: Windows and the **.NET SDK** (8.0 is known to work):
  https://dotnet.microsoft.com/download

## 1. Install the mod (every player)

**Prebuilt:** download **[Coopfall.dll](https://github.com/wuzzy450/worldfall-coop/raw/main/release/Coopfall.dll)** and copy it into
`<WorldBox>\worldbox_Data\StreamingAssets\mods\` (see "Installing by hand" below). It was
built for WorldBox 0.51.2; if your WorldBox is a different version and the mod doesn't load,
build it yourself. Its SHA-256 is in `release/Coopfall.dll.sha256`
(check with `certutil -hashfile Coopfall.dll SHA256`).

**From source:** download **[everything (ZIP)](https://github.com/wuzzy450/worldfall-coop/archive/refs/heads/main.zip)**, unzip it, then in the
`worldfall-coop-main` folder:

```powershell
cd client
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` compiles against **your own** WorldBox installation and copies `Coopfall.dll` to
`worldbox_Data\StreamingAssets\mods\`. Options:

- `-GameDir "D:\SteamLibrary\steamapps\common\worldbox"` if WorldBox is not in the default
  `C:\Program Files (x86)\Steam\steamapps\common\worldbox`
- `-NoInstall` to only build (output: `client\Coopfall\bin\Release\Coopfall.dll`)

Installing by hand: copy `Coopfall.dll` into
`<WorldBox>\worldbox_Data\StreamingAssets\mods\` (Steam: right-click WorldBox, **Manage**,
**Browse local files**). If you use Worldfall, `Worldfall.dll` goes in the same folder.

Then in WorldBox: **Settings**, turn on **Experimental Mode**, restart WorldBox. WorldBox
turns this off after game updates; turn it on again if mods stop loading.

## 2. Run the server (one person)

> **Port forwarding is recommended** for playing over the internet: forward **TCP 25598** on the
> host's router to the host PC (see "Playing over the internet"). UPnP (below) tries to do this
> automatically, but many routers have it switched off or don't support it.

**Easiest: host it from the game.** In the co-op menu tick **Host the server on this PC** and
press **Connect**. The relay runs inside your game (nothing to download), and Coopfall asks your
router to open port 25598 with **UPnP**. The menu shows the address friends type in: your LAN
address for the same network, your public address for the internet. If it says no UPnP router
answered, turn UPnP on in your router's settings or forward TCP 25598 by hand (see "Playing over
the internet"). Worlds are saved in `%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\coopfall\relay`.
The server stops when you close the game; for an always-on server use the standalone relay below.

### Standalone relay

The server is a small relay. It does not simulate anything: one player's game (the **host** of
each world) runs the world, and the relay passes messages and stores world saves.
It is a Lua plugin (`WorldfallRooms`) running inside **[Cuberite](https://cuberite.org)**, a
free, open-source server program that is bundled in `server/cuberite/` (Windows x64).
No Minecraft is involved; Cuberite is only used as the plugin host.

### Windows

1. Download **[everything (ZIP)](https://github.com/wuzzy450/worldfall-coop/archive/refs/heads/main.zip)** and unzip it (right-click, **Extract All...**).
2. In the unzipped `worldfall-coop-main\server` folder, double-click **`start_server.bat`**
   and leave the window open while you play.
   If Windows shows "Windows protected your PC" (it does this for programs downloaded from
   the internet), click **More info**, then **Run anyway**.
3. Windows Firewall will ask about `Cuberite.exe` the first time: allow it (Private networks
   for LAN; tick Public too if your network is marked public).
4. In the server window, type `wf` to list players and worlds, and `stop` to shut it down.

Worlds are saved in `server\cuberite\worldfall_rooms\` and survive restarts.

Tip: start the server by double-clicking it (or from a normal terminal). If it is started as a
child of another app, closing or updating that app also closes the server.

### Linux / macOS (not tested)

Get Cuberite for your platform from https://cuberite.org or https://github.com/cuberite/cuberite,
then copy `server/cuberite/Plugins/WorldfallRooms/` into its `Plugins` folder and use the
`settings.ini` from this repo (it enables only that plugin). Start Cuberite from its folder.

### Ports

- The relay listens on **TCP 25598**. That is the only port players need.
- Cuberite also opens TCP 25565 (Minecraft, unused). You don't need to forward it.
- Web admin is disabled (`webadmin.ini`).

### Playing over the internet

- **Same house / same Wi-Fi**: nothing to set up.
- **Over the internet**: forward **TCP port 25598** on the server owner's router to the server
  PC's LAN IP, and give friends the server owner's **public IP**.
- No router access (or CGNAT)? Use a virtual LAN such as Tailscale, ZeroTier or Radmin VPN and
  connect to the server PC's address on that network; no forwarding needed.

## 3. Join

In WorldBox press **F8** (or click **Co-op** at the top of the screen):

- enter **your name** and pick a **color**
- **Server IP**: `127.0.0.1` on the server PC itself, the server PC's LAN IP on the same network,
  or the public IP / VPN address over the internet
- **Port**: `25598`
- choose **Shared world** or **Own worlds**, then **Connect**. "Connect automatically" is on by
  default, so after the first time Coopfall connects by itself when WorldBox starts (untick it
  in the Co-op menu to stop that).

If the connection drops, Coopfall reconnects by itself. If you were hosting, your open world
is kept and uploaded, not replaced by an older server copy.

The first time a world from the server replaces the one you had open, your own world is backed
up to `%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\coopfall\backups\` (normal WorldBox
save folders: copy one into `...\WorldBox\saves\save<N>` to load it). If that backup can't be
written, the server's world is **not** loaded and your world stays untouched. The Co-op menu
also has a **Back up this map now** button.

### Mods

Everyone in a world needs the **same gameplay mods** (same files): a missing or different mod
makes the worlds drift apart without anyone noticing. Coopfall sends a list of your mods (DLLs
and folders in `StreamingAssets\mods` and the game's `Mods` folder, each with a fingerprint of
its files) when you connect, and the server keeps you out of a world whose mods don't match.
A window lists what is missing, different or extra; chat keeps working.

Mods that only change your own screen don't count. Worldfall is one by default; add others to
`clientOnlyMods` in `config.json`, or (mod authors) ship a `coopfall.json` with
`{"clientOnly": true}` in the mod's folder (or `<name>.coopfall.json` next to a DLL). The world's
owner can also allow guests to have extra mods.

### World settings (owner)

Out of the box everybody can do everything: every player in a world is an **admin** (may change
the settings below and kick), guests can use **all god powers** and change **speed / pause**
(synced for everyone), there is no password, and nobody has to be let in.

The owner of a world (for the shared world: whoever hosts it) can tighten that in the
**This world's settings** section of the Co-op menu:

- **Everyone here is an admin**: switch off so only the owner changes settings and kicks, and
  the guest limits below apply. Only the owner can flip this, and nobody can kick the owner.
- **Password**: players are asked for it when they travel there (stored hashed on the server).
- **Locked**: nobody new can join.
- **Ask me before people join**: a box at the top of your screen with **Let in** / **No**.
- **Max players** (spectators don't count) and **Spectators** on/off.
- **Guest powers**: all, no destructive powers (bombs, meteorites, plague, lava, ...), or none.
- **Guests may change speed / pause**.
- **Allow guests' extra mods**.

In the player list an admin (by default: anyone in the world) can **Kick** a player from that
world (they can't come back for 10 minutes). The owner can't be kicked. The list also shows
everyone's **ping**.

**Spectating**: the **Watch** button on a World Map card lets you join a world without playing:
no god powers, no speed changes, no possessing creatures.

### Bug reports

Co-op menu, **Export diagnostics** (or `/export` in chat) writes one zip to
`coopfall\reports\` with the logs, settings, mod list and recent sync reports. Your Windows user
name, home folder and the server address are removed, so it can be posted publicly. If
something looks out of sync, press **F9** first: it captures a sync report in every game of
your world, which then goes into the zip. The mod's own log is
`%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\coopfall\log.txt`.

### Keys

| Key | |
|---|---|
| **F7** | World Map |
| **F8** | Co-op menu |
| **F9** | capture a sync report in every game of your world (for bug reports; also `/report`) |
| **Enter** | chat (`/sync` full re-sync, `/home` your world, `/shared` the shared world, `/report` sync report, `/export` diagnostics zip) |

Keys can be changed in `%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\coopfall\config.json`.
They avoid Worldfall's keys (F1, F2, F5, G, V, E, X).

With Worldfall's first person: the co-op bar sits on the right side, just below Worldfall's
minimap (clear of its unit panel, age panel and the screen edge), with messages under it; the
chat stays at the bottom-left. While the chat is open your keys only go to the chat; opening the World Map or Co-op menu switches to Worldfall's
top-down view so you can use the mouse, and returns to first person when you close it.

## How it works

- **Rooms**: each world is a room on the relay. A room's **host** runs the authoritative
  simulation; if the host leaves, another player becomes host automatically. The host saves
  the world to the server every 3 minutes and on quit.
- **Joining**: the server asks the host for a fresh save, streams it to you, and your game
  loads it.
- **Live sync**: the host streams every creature (position, health, 10 times a second) and every
  building/tree to the guests. Guests steer their creatures onto the host's positions (looking
  ahead by half the ping), copy newborns and new buildings with the same ids, and remove what
  the host doesn't have. A creature a guest spawns appears instantly and is swapped for the
  host's copy within about a second.
- **Everything else live**: the host fingerprints every kingdom, city (with its borders), war,
  alliance, diplomatic relation, culture, religion, language, clan, family, subspecies, army,
  plot, book and item, and sends the full data of whatever changed; every 15 s guests compare
  the complete list with their own world and ask for anything missing or different. The same
  is done for each creature's name, traits, job, level, kills, equipment and memberships, for
  the terrain (8x8-tile zones: ground, roads, fields, lava, ice, fire, burn marks) and for the
  world's time, age and laws. Changes are applied in place through WorldBox's own functions,
  so there is no loading screen.
- **Guests don't roll their own dice**: WorldBox's random world events (disasters, migrants,
  plants spreading, fire spreading, biomes, erosion, clouds, ...) are switched off on guests,
  and their earthquakes and tornadoes don't change tiles. The host's results arrive through the
  sync instead, so a guest's world can't drift off by itself.
- **Weather**: the host sends its clouds (type, place, speed) and Worldfall's wind gust state
  about once a second; guests show the same clouds and the same gusts.
- **Kills**: the host announces every death with its cause and killer, and guests kill the same
  creature the same way. When a guest's possessed creature hits something, the hit is applied
  in the host's world (so the kill counts for everybody). A possessed creature belongs to its
  player: hits from other players' possessed creatures are sent to that player's game, which
  decides if it dies, and when it dies it dies in everybody's game.
- **Full re-sync** is only a safety net: when cities/kingdoms stay different for 90 s, every
  30 minutes, or on `/sync`. It also works while possessing (you are put back into your
  creature).
- Wire protocol: [`server/protocol.md`](server/protocol.md).

## Lockstep (experimental)

Turn on **Lockstep** in the co-op menu (the host's setting decides) and every game simulates
the same world tick by tick; only inputs travel (god powers, possession and controls,
Worldfall's actions, menus and conversations). Nothing is copied and nothing drifts apart over
time, and Worldfall's first-person features (law, guards, family, army service, the king's
council, war flags and fleets, quests and trials, towns and shops, bag, gear and crafting,
things on the ground, the day clock) work for every player in the shared world.

- Everyone needs exactly the same mods, Worldfall build and WorldBox version (checked at
  join; otherwise that player stays on live sync).
- Every 10 ticks the games compare a checksum. If a game ever computes something different, the
  host saves the world and everyone reloads it (a **re-sync**, about 2-3 s); what each player
  carries, wears and has explored goes on.
- Each player's explored map and people's memories of them stay their own.
- The host's gameplay Worldfall settings apply to everyone while playing together.

## Known limits

- Tested on two PCs on the same network (LAN), not yet over the internet via a public IP (see
  the top of this page).
- Everyone needs the same Coopfall version (the server refuses older ones: protocol v4). Update the
  server too: an older `Plugins\WorldfallRooms` relay refuses this mod.
- The mods check compares files. It can't tell whether a mod is really safe to mix; that is
  up to the `clientOnly` marks.
- Every change reaches the guests after the network delay (on a LAN well under a second,
  over the internet about your ping more). Guests still run the parts of the simulation that
  aren't random (creatures walking, buildings, lava flowing), so a guest's world can differ
  for a moment until the host's state arrives; guests check their terrain against the host's
  every half second and the object lists every 15 s, and fix what differs. **Lockstep** (see
  below) avoids this: every game computes exactly the same world.
- Dragons (and a few other special creatures) can't be possessed. That's WorldBox's own rule,
  not something Coopfall or Worldfall changes.

## Repository layout

| Path | What |
|---|---|
| `client/Coopfall/` | the WorldBox mod (C#, netstandard2.1) |
| `client/build.ps1` | builds and installs the mod |
| `release/Coopfall.dll` | prebuilt mod, plus its SHA-256 |
| `server/start_server.bat` | starts the relay (Windows) |
| `server/cuberite/Plugins/WorldfallRooms/` | the relay plugin (Lua) |
| `server/cuberite/` | bundled Cuberite runtime and its licenses |
| `server/protocol.md` | wire protocol |

## For AI agents

If you are an AI coding agent asked to build and test this project:

- The mod is a .NET `netstandard2.1` class library. `client/Coopfall/Coopfall.csproj`
  references WorldBox's managed assemblies through the `GameDir` property (default
  `C:\Program Files (x86)\Steam\steamapps\common\worldbox`); they are not in the repo.
- WorldBox's loader contract: in Experimental Mode the game loads `Coopfall.dll` from
  `worldbox_Data/StreamingAssets/mods/` and adds the MonoBehaviour `Coopfall.WorldBoxMod`.
  Start reading at `WorldBoxMod.cs`, then `CoopSession.cs` (connection, rooms, snapshots),
  `WorldSync.cs` (live sync of creatures and buildings), `MetaSync.cs` (everything else),
  `TileSync.cs` (terrain and fire), `WeatherSync.cs` (clouds and wind gusts), `CombatSync.cs`
  (hits and kills), `AvatarManager.cs`, `PowerSync.cs` (god powers, guest limits), `CoopUI.cs`,
  `ModScan.cs` (mods check), `DiagExport.cs` (diagnostics zip), `DiagSync.cs` (sync reports),
  `TestDriver.cs` / `TestLobby.cs` (scripted two-game test scenarios), and `WorldfallBridge.cs`
  (optional Worldfall integration via reflection).
- Relay: `server/cuberite/Plugins/WorldfallRooms/Main.lua`; protocol in `server/protocol.md`.
  Start it with `server/start_server.bat`.
- To test on one PC, start `worldbox.exe` twice with `-coopfall-profile host` and
  `-coopfall-profile guest` (each gets its own config and log in `coopfall\profiles\<name>\`;
  add `-logFile <path>` for separate Unity logs). `-coopfall-scenario meet` runs a scripted
  session in which both games compare sync reports after each step (`DIAG` lines in the host's
  log); `-coopfall-test-load 1` loads save slot 1 first.
- Note: Cuberite exits when its standard input closes; keep stdin open if you launch it
  from a script.

## Credits

- **Wuzzy450**: creator of Coopfall
- **Defect**: tester. Thanks for all the co-op playtesting and bug reports!

## Support Coopfall

Coopfall is free and always will be. If you enjoy it and would like to help out, a small
tip is very much appreciated but never expected. Thank you!

[Support Coopfall on Ko-fi](https://ko-fi.com/wuzzy450)

## License

Coopfall is free and open-source software under the **MIT License**, see [LICENSE](LICENSE).
The bundled Cuberite runtime is under the Apache License 2.0, and its bundled libraries are
under their own licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Coopfall is an unofficial fan project, not affiliated with or endorsed by the makers of
WorldBox, Worldfall, Cuberite, Minecraft or Unity.
