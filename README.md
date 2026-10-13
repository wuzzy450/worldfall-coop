# Coopfall: online co-op for WorldBox

Play **WorldBox** together with your friends in one shared world. Works with
**[Worldfall](https://worldfall3d.com/)** (optional), so you can walk around together in first person.

- Everyone sees the same world, kept identical on every PC (lockstep).
- See your friends' god cursors, or the creatures they possess with their name over them.
- God powers, speed, pause and chat are shared.

> **Status:** work in progress, tested by two players. Help and bug reports:
> **[Discord](https://discord.gg/nuvpXycag8)**

## Install (every player, 2 minutes)

1. Download **[Coopfall.dll](https://github.com/wuzzy450/worldfall-coop/raw/main/release/Coopfall.dll)**.
2. In Steam, right-click **WorldBox**, then **Manage**, then **Browse local files**.
3. Open `worldbox_Data\StreamingAssets\mods` (make the `mods` folder if it isn't there) and put
   `Coopfall.dll` in it. Using Worldfall? Its `Worldfall.dll` goes in the same folder.
4. Start WorldBox, open **Settings**, turn on **Experimental Mode**, and restart WorldBox.

That's it. WorldBox turns Experimental Mode off after game updates, so turn it back on if
Coopfall stops loading.

**Using NeoModLoader instead?** Download
**[Coopfall_NML.zip](https://github.com/wuzzy450/worldfall-coop/raw/main/release/Coopfall_NML.zip)**
and drop it in the `Mods` folder NeoModLoader made next to the game. Use one install, not both.

> Everyone must use the **same** Coopfall file, the same WorldBox version and the same mods
> (Worldfall included). Coopfall checks this when you join.

## Play

**The host** (one person):

1. Press **F8** in WorldBox.
2. Tick **Host the server on this PC** and press **Connect**.
3. The menu shows an address. Send it to your friends:
   - same house or Wi-Fi: your local address
   - over the internet: the `steam:...` address (press **Copy**). Nothing else to set up.

**Friends:**

1. Press **F8**, type your name and pick a color.
2. Paste the host's address into **Server IP** and press **Connect**.

Coopfall reconnects by itself next time you start WorldBox.

## Keys

| Key | Does |
|---|---|
| **F8** | Co-op menu |
| **F7** | World Map: everyone's worlds, travel between them |
| **Enter** | Chat |
| **F9** | Something out of sync? Press this, then **Export diagnostics** in the F8 menu, and post the zip on Discord |

Keys can be changed in
`%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\coopfall\config.json`.

## Good to know

- **Your own world is safe.** The first time you join, your world is backed up to
  `...\WorldBox\coopfall\backups\`.
- **"Loading Shared World..."** for a few seconds means the games noticed a difference and the
  host re-synced everyone. If it keeps happening, please send a bug report.
- **World settings** (F8 menu, owner only): password, lock the world, ask before people join,
  max players, which god powers guests may use.
- **Worldfall's own multiplayer is switched off** while Coopfall is installed. Use Coopfall's
  F8 menu instead; its F2 tab turns into a Coopfall page.
- Dragons can't be possessed (WorldBox's own rule).

## Problems

| Problem | Fix |
|---|---|
| Coopfall doesn't show up | Turn on **Experimental Mode** again and restart WorldBox. |
| Friends can't connect with your local address | Use the `steam:...` address instead (Steam must be running). |
| "Mods differ" when joining | Everyone needs exactly the same mod files and versions. |
| Anything else | Press **F9**, then **Export diagnostics** in the F8 menu, and post the zip on [Discord](https://discord.gg/nuvpXycag8). Names and addresses are removed from it. |

<details>
<summary><b>Advanced: standalone server, port forwarding, building from source</b></summary>

### Standalone server (always on)

Only needed if you want a server that keeps running when nobody is playing. Download the
**[whole project (ZIP)](https://github.com/wuzzy450/worldfall-coop/archive/refs/heads/main.zip)**,
unzip it, and double-click `server\start_server.bat` (keep the whole `server` folder). If
Windows warns you, click **More info** and then **Run anyway**, and allow it through the
firewall. Players connect to that PC's address on port **25598**. The server is a small relay
plugin running inside the bundled [Cuberite](https://cuberite.org); no Minecraft is involved.

### Playing by IP over the internet

Instead of `steam:...` you can forward **TCP port 25598** on the host's router to the host PC
and give friends your public IP. Coopfall tries to open the port automatically (UPnP).
A virtual LAN such as Tailscale or ZeroTier also works.

### Building from source

Needs Windows and the [.NET SDK](https://dotnet.microsoft.com/download) (8.0 works). From the
project folder:

```powershell
cd client
powershell -ExecutionPolicy Bypass -File build.ps1
```

This builds against your own WorldBox and installs `Coopfall.dll` into the mods folder
(`-GameDir "D:\...\worldbox"` if WorldBox is elsewhere, `-NoInstall` to only build).
The NeoModLoader zip is made with `sh client/build-nml.sh`.

### Project layout

| Path | What |
|---|---|
| `client/Coopfall/` | the mod (C#) |
| `client/CoopfallNml/` | NeoModLoader entry and `mod.json` |
| `release/` | prebuilt `Coopfall.dll`, `Coopfall_NML.zip` and their SHA-256 |
| `server/` | standalone relay (Cuberite + `Plugins/WorldfallRooms`), protocol in `server/protocol.md` |

### For AI agents

- `client/Coopfall/Coopfall.csproj` (netstandard2.1) references WorldBox's managed DLLs via
  `GameDir`/`ManagedDir`; they are not in the repo. WorldBox loads `Coopfall.dll` from
  `StreamingAssets/mods` and adds `Coopfall.WorldBoxMod`.
- Lockstep lives in `client/Coopfall/Lockstep/` (`LockstepClock`, `LockstepSession`,
  `StateHash`); Worldfall is reached only by reflection (`WorldfallBridge.cs`,
  `Lockstep/*InTick.cs`).
- Two games on one PC: start `worldbox.exe` with `-coopfall-profile host` and
  `-coopfall-profile guest` (logs in `coopfall\profiles\<name>\`).

</details>

## Credits

- **wuzzy450**: creator
- **Defect**: tester. Thanks for all the playtesting!

Coopfall is free. If you enjoy it, you can [support it on Ko-fi](https://ko-fi.com/wuzzy450).

## License

MIT, see [LICENSE](LICENSE). The bundled Cuberite runtime and its libraries have their own
licenses, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Coopfall is an unofficial fan
project, not affiliated with the makers of WorldBox, Worldfall, Cuberite, Minecraft or Unity.
