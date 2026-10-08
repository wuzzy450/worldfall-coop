-- Info.lua
-- WorldfallRooms: plugin description for the Worldfall Co-op relay.
-- The "wf" console command is bound in Main.lua (Initialize).

g_PluginInfo =
{
	Name = "WorldfallRooms",
	Version = "3",
	Date = "2026-10-07",
	Description = [[Room relay for Worldfall Co-op (WorldBox + Coopfall mod).
Listens on TCP port 25598 and speaks newline-delimited JSON (not the Minecraft protocol).
Each room is one WorldBox world. A room has a host (the authoritative player); joiners get a fresh
world snapshot from the host, and avatars, cursors, god powers, game speed and chat are relayed
between the players in a room. Latest snapshots are stored under worldfall_rooms/ so worlds
survive restarts.]],
}
