# Third-party notices

Coopfall's own code (everything in `client/`, `server/start_server.bat`, `server/protocol.md`
and `server/cuberite/Plugins/WorldfallRooms/`) is
released under the MIT License, see [LICENSE](LICENSE).

## Included in this repository

### Cuberite (the relay's host program)

`server/cuberite/` contains a Windows x64 build of **Cuberite** (`Cuberite.exe`, `lua5.1.dll`,
`lua51.dll`, its data files `*.txt` / `*.ini` and `Prefabs/`), build "Cuberite Windows x64
Master-#328" (see `server/cuberite/buildinfo.txt`). Cuberite is a free, open-source Minecraft
server; Coopfall only uses it as a host for its Lua plugin, which opens a plain TCP relay.

- Website: https://cuberite.org
- Source: https://github.com/cuberite/cuberite
- License: Apache License 2.0, full text in `server/cuberite/LICENSE-Cuberite.txt`
- Copyright: Cuberite Contributors

`server/cuberite/settings.ini`, `webadmin.ini` and `motd.txt` were changed for Coopfall (only the
WorldfallRooms plugin is enabled, web admin is off, different message of the day).

### Libraries bundled inside Cuberite

Their license texts are in `server/cuberite/ThirdPartyLicenses/`, as shipped with Cuberite:

| Component | License file |
|---|---|
| Lua (Lua.org, PUC-Rio) | `Lua-LICENSE.txt` (MIT) |
| Libevent | `LibEvent-LICENSE.txt` (3-clause BSD) |
| LuaExpat | `LuaExpat-license.html` |
| LuaSQLite3 (lsqlite3) | `LuaSQLite3-LICENSE.txt` |
| Mersenne Twister | `MersenneTwister-LICENSE.txt` |
| SQLiteCpp | `SQLiteCpp-LICENSE.txt` (MIT) |

## Not included (needed at build time or run time)

- **WorldBox** (by Maxim Karpenko). The mod is compiled against the game's own assemblies
  (`Assembly-CSharp.dll`, `UnityEngine*.dll`, `Newtonsoft.Json.dll`) from *your* WorldBox
  installation. None of them are in this repository, and nothing from the game is
  redistributed. You need your own copy of WorldBox.
- **Worldfall** (optional, unofficial first-person mod for WorldBox by its own author):
  https://worldfall3d.com/ (source and downloads: https://github.com/s3cond2/worldfall).
  Not included; Coopfall works with or without it and only talks to it at run time.
- **.NET SDK** to build the mod (https://dotnet.microsoft.com/download).

## Trademarks

Coopfall is an unofficial fan project. It is not made by, affiliated with or endorsed by the
makers of WorldBox, Worldfall, Cuberite, Minecraft or Unity. All names belong to their owners.
