-- Main.lua
-- WorldfallRooms v2: room relay for Worldfall Co-op (WorldBox + the Coopfall mod).
--
-- Listens on 0.0.0.0:25598 (cNetwork:Listen) and speaks newline-delimited JSON.
-- Protocol: see ../../../protocol.md (worldfall-coop/server/protocol.md).
--
-- Model
--   * A ROOM is one WorldBox world. Ids: "shared" (the server's shared world),
--     "home-<name>" (a player's own world), "world-<random>" (extra published worlds).
--   * Each room with players has exactly one HOST: the player whose simulation is
--     authoritative. Joiners receive a fresh snapshot (zlib'd save, base64 chunks) taken
--     from the host on demand. If the host leaves, hosting migrates to another synced
--     member. A room without players keeps its last snapshot (also on disk).
--   * Avatars, cursors, god-power clicks, actions, hits and game speed are relayed to the other
--     members of the sender's room. Chat is server-wide.
--   * Live world sync (wu/wb/wdata/wm/wa/wt/ww from the host, wneed/wask from guests) is relayed
--     to the room WITHOUT decoding: these lines can be large and arrive several times per second.
--   * The server never decodes snapshots: chunks are relayed and stored as opaque base64.

---------------------------------------------------------------------
-- Configuration / state
---------------------------------------------------------------------

local PLUGIN_NAME = "WorldfallRooms"
local PROTOCOL_VERSION = 3

local CFG =
{
	PORT               = 25598,
	MAX_SNAPSHOT_BYTES = 64 * 1024 * 1024,  -- decoded size limit per world snapshot
	MAX_ROOMS          = 32,
	MAX_LINE_BYTES     = 2 * 1024 * 1024,   -- a single JSON line (snapshot chunks are ~80 KB)
	MAX_PREVIEW_BYTES  = 256 * 1024,        -- base64 PNG thumbnail
	TIMEOUT_MS         = 45 * 1000,         -- drop links silent for longer than this
	SNAP_TIMEOUT_MS    = 90 * 1000,         -- host did not answer a snapshot request in time
	APPROVAL_MS        = 60 * 1000,         -- a join request nobody answered is turned down
	KICK_MS            = 10 * 60 * 1000,    -- a kicked player can't rejoin that world for this long
	MAX_MODS           = 200,
	DATA_DIR           = "worldfall_rooms", -- relative to the Cuberite folder
}

local g_Plugin = nil
local g_Server = nil   -- cServerHandle; must stay referenced or the socket closes
local g_Clients = {}   -- array of client records
local g_Rooms = {}     -- id -> room record
local g_ClockMs = 0
local g_NextSweepMs = 1000
local g_IdCounter = 0
local g_NextPingBroadcastMs = 0

local function Log(a_Msg)  LOG("[" .. PLUGIN_NAME .. "] " .. a_Msg) end
local function Warn(a_Msg) LOGWARNING("[" .. PLUGIN_NAME .. "] " .. a_Msg) end

---------------------------------------------------------------------
-- JSON (Cuberite's built-in cJson)
---------------------------------------------------------------------

local JSON_COMPACT = { indentation = "" }

--- Serializes to ONE line. Some Cuberite builds pretty-print even with empty indentation;
--- raw newlines/tabs are only ever formatting (inside strings they are escaped), so strip them.
local function JEncode(a_Value)
	local ok, s = pcall(cJson.Serialize, cJson, a_Value, JSON_COMPACT)
	if not (ok and (type(s) == "string")) then
		ok, s = pcall(cJson.Serialize, cJson, a_Value)
	end
	if ok and (type(s) == "string") then
		s = string.gsub(s, "[\r\n\t]", "")
		return s
	end
	return nil
end

local function JDecode(a_String)
	local ok, v = pcall(cJson.Parse, cJson, a_String)
	if ok and (type(v) == "table") then
		return v
	end
	return nil
end

--- Escapes a string for direct inclusion in a hand-built JSON line.
local function JStr(a_String)
	local s = tostring(a_String or "")
	s = string.gsub(s, '[%c"\\]', function(c)
		if c == '"' then return '\\"' end
		if c == "\\" then return "\\\\" end
		return string.format("\\u%04x", string.byte(c))
	end)
	return '"' .. s .. '"'
end

---------------------------------------------------------------------
-- Validation helpers
---------------------------------------------------------------------

local function SanitizeRoomId(a_Id)
	if (type(a_Id) ~= "string") or (#a_Id < 1) or (#a_Id > 48) then
		return nil
	end
	if not string.find(a_Id, "^[%w%-_]+$") then
		return nil
	end
	return string.lower(a_Id)
end

local function SanitizeText(a_Text, a_MaxLen, a_Default)
	if type(a_Text) ~= "string" then
		return a_Default
	end
	local s = string.gsub(a_Text, "%c", "")
	s = string.gsub(s, "^%s+", "")
	s = string.gsub(s, "%s+$", "")
	if #s == 0 then
		return a_Default
	end
	return string.sub(s, 1, a_MaxLen)
end

local function SanitizeColor(a_Color)
	if (type(a_Color) == "string") and string.find(a_Color, "^#%x%x%x%x%x%x$") then
		return a_Color
	end
	return "#ffcc33"
end

local function IsNumber(a_Value)
	return (type(a_Value) == "number") and (a_Value == a_Value)  -- not NaN
end

---------------------------------------------------------------------
-- Sending
---------------------------------------------------------------------

local function SendLine(a_Client, a_Line)
	if a_Client.Removed or (a_Client.Link == nil) then
		return false
	end
	local ok = pcall(a_Client.Link.Send, a_Client.Link, a_Line .. "\n")
	if not ok then
		Warn("send failed to " .. tostring(a_Client.Name or a_Client.Addr))
	end
	return ok
end

local function Send(a_Client, a_Table)
	local line = JEncode(a_Table)
	if line == nil then
		Warn("failed to encode a '" .. tostring(a_Table.t) .. "' message")
		return false
	end
	return SendLine(a_Client, line)
end

local function SendError(a_Client, a_Msg)
	Send(a_Client, { t = "error", msg = tostring(a_Msg) })
end

local function ForEachPlayer(a_Fn)
	for _, c in ipairs(g_Clients) do
		if c.Id and (not c.Removed) then
			a_Fn(c)
		end
	end
end

---------------------------------------------------------------------
-- Lists (players / rooms)
---------------------------------------------------------------------

local function PlayerList()
	local out = {}
	ForEachPlayer(function(c)
		table.insert(out,
		{
			id = c.Id,
			name = c.Name,
			color = c.Color,
			room = c.Room and c.Room.id or "",
			host = (c.Room ~= nil) and (c.Room.host == c),
			synced = c.Synced and true or false,
			game = c.GameVersion or "",
			ping = c.Ping or -1,
			spectator = c.Spectator and true or false,
			mods = c.Mods and #c.Mods or 0,
		})
	end)
	return out
end

local function DefaultSettings()
	return
	{
		password = "",           -- empty = none
		locked = false,          -- nobody new may join
		approval = false,        -- the world's admin lets each joiner in
		maxPlayers = 0,          -- 0 = no limit (spectators don't count)
		spectators = true,       -- spectators may join
		guestPowers = "all",     -- all | safe | none
		blocked = {},            -- power ids guests may not use ("safe")
		guestSpeed = true,       -- guests may change speed / pause
		allowExtraMods = false,  -- guests may run gameplay mods the host doesn't have
		everyoneAdmin = true,    -- every player in the world may change these settings and kick
	}
end

--- Settings as other players see them (never the password itself).
local function PublicSettings(a_Room)
	local s = a_Room.settings
	return
	{
		hasPassword = (s.password ~= ""),
		locked = s.locked,
		approval = s.approval,
		maxPlayers = s.maxPlayers,
		spectators = s.spectators,
		guestPowers = s.guestPowers,
		blocked = s.blocked,
		guestSpeed = s.guestSpeed,
		allowExtraMods = s.allowExtraMods,
		everyoneAdmin = s.everyoneAdmin,
	}
end

local function RoomInfo(a_Room)
	local names = {}
	local watching = 0
	for _, m in ipairs(a_Room.members) do
		table.insert(names, m.Name)
		if m.Spectator then
			watching = watching + 1
		end
	end
	local snap = a_Room.snap
	local stats = a_Room.stats or {}
	return
	{
		id = a_Room.id,
		name = a_Room.name,
		kind = a_Room.kind,
		owner = a_Room.owner or "",
		host = a_Room.host and a_Room.host.Name or "",
		players = #a_Room.members,
		names = names,
		size = snap and snap.size or 0,
		version = snap and snap.version or 0,
		updated = snap and snap.time or 0,
		hasWorld = (snap ~= nil) or (a_Room.host ~= nil),
		hasPreview = (a_Room.preview ~= nil),
		year = stats.year or 0,
		pop = stats.pop or 0,
		w = stats.w or 0,
		h = stats.h or 0,
		spectating = watching,
		settings = PublicSettings(a_Room),
		mods = a_Room.mods and #a_Room.mods or 0,
	}
end

local function RoomList()
	local out = {}
	for _, r in pairs(g_Rooms) do
		table.insert(out, RoomInfo(r))
	end
	table.sort(out, function(a, b)
		if (a.id == "shared") ~= (b.id == "shared") then
			return a.id == "shared"
		end
		return a.name < b.name
	end)
	return out
end

local function BroadcastPlayers()
	local line = JEncode({ t = "players", players = PlayerList() })
	if line then
		ForEachPlayer(function(c) SendLine(c, line) end)
	end
end

local function BroadcastRooms()
	local line = JEncode({ t = "rooms", rooms = RoomList() })
	if line then
		ForEachPlayer(function(c) SendLine(c, line) end)
	end
end

local function Toast(a_Text, a_Except)
	local line = JEncode({ t = "notice", text = a_Text })
	if line then
		ForEachPlayer(function(c)
			if c ~= a_Except then
				SendLine(c, line)
			end
		end)
	end
end

---------------------------------------------------------------------
-- Persistence: worldfall_rooms/rooms.json + <id>/{meta.json,snapshot.b64,preview.b64}
---------------------------------------------------------------------

local function EnsureFolder(a_Path)
	if not cFile:IsFolder(a_Path) then
		pcall(cFile.CreateFolderRecursive, cFile, a_Path)
	end
end

local function RoomDir(a_Id)
	return CFG.DATA_DIR .. "/" .. a_Id
end

local function WriteFile(a_Path, a_Content)
	local f = io.open(a_Path, "wb")
	if not f then
		Warn("cannot write " .. a_Path)
		return false
	end
	f:write(a_Content)
	f:close()
	return true
end

local function ReadFile(a_Path)
	local f = io.open(a_Path, "rb")
	if not f then
		return nil
	end
	local s = f:read("*a")
	f:close()
	return s
end

local function SaveRoomIndex()
	EnsureFolder(CFG.DATA_DIR)
	local ids = {}
	for id, r in pairs(g_Rooms) do
		if r.snap then
			table.insert(ids, id)
		end
	end
	WriteFile(CFG.DATA_DIR .. "/rooms.json", JEncode({ rooms = ids }) or "{}")
end

local function SaveRoomMeta(a_Room)
	EnsureFolder(RoomDir(a_Room.id))
	local snap = a_Room.snap
	WriteFile(RoomDir(a_Room.id) .. "/meta.json", JEncode(
	{
		id = a_Room.id,
		name = a_Room.name,
		kind = a_Room.kind,
		owner = a_Room.owner or "",
		size = snap and snap.size or 0,
		sha = snap and snap.sha or "",
		total = snap and #snap.chunks or 0,
		version = snap and snap.version or 0,
		time = snap and snap.time or 0,
		stats = a_Room.stats or {},
		settings = a_Room.settings,
		mods = a_Room.mods,
	}) or "{}")
end

local function SaveRoomSnapshot(a_Room)
	if not a_Room.snap then
		return
	end
	EnsureFolder(RoomDir(a_Room.id))
	local tmp = RoomDir(a_Room.id) .. "/snapshot.b64.tmp"
	local f = io.open(tmp, "wb")
	if not f then
		Warn("cannot write snapshot for room " .. a_Room.id)
		return
	end
	for _, chunk in ipairs(a_Room.snap.chunks) do
		f:write(chunk, "\n")
	end
	f:close()
	local final = RoomDir(a_Room.id) .. "/snapshot.b64"
	os.remove(final)
	os.rename(tmp, final)
	SaveRoomMeta(a_Room)
	SaveRoomIndex()
end

local function SaveRoomPreview(a_Room)
	if a_Room.preview then
		EnsureFolder(RoomDir(a_Room.id))
		WriteFile(RoomDir(a_Room.id) .. "/preview.b64", a_Room.preview)
		SaveRoomMeta(a_Room)
	end
end

local function NewRoom(a_Id, a_Name, a_Kind, a_Owner)
	return
	{
		id = a_Id,
		name = a_Name,
		kind = a_Kind,
		owner = a_Owner,
		members = {},
		host = nil,
		snap = nil,        -- { chunks = {b64...}, size, sha, version, time }
		pending = nil,     -- snapshot upload in progress from the host
		preview = nil,     -- base64 PNG
		stats = nil,
		snapRequestedAt = nil,
		settings = DefaultSettings(),
		mods = nil,        -- the gameplay mods of the host whose world this is: { {id, ver}, ... }
		kicked = {},       -- lower-case name -> clock ms until which they can't come back
	}
end

local function LoadRooms()
	local index = JDecode(ReadFile(CFG.DATA_DIR .. "/rooms.json") or "")
	if (index == nil) or (type(index.rooms) ~= "table") then
		return
	end
	for _, id in ipairs(index.rooms) do
		local sid = SanitizeRoomId(id)
		local meta = sid and JDecode(ReadFile(RoomDir(sid) .. "/meta.json") or "")
		if meta then
			local r = NewRoom(sid, SanitizeText(meta.name, 64, sid), meta.kind or "world", meta.owner or "")
			r.stats = (type(meta.stats) == "table") and meta.stats or nil
			if type(meta.settings) == "table" then
				for k, v in pairs(meta.settings) do
					if type(v) == type(r.settings[k]) then
						r.settings[k] = v
					end
				end
			end
			if type(meta.mods) == "table" then
				r.mods = meta.mods
			end
			local chunks = {}
			local f = io.open(RoomDir(sid) .. "/snapshot.b64", "rb")
			if f then
				for line in f:lines() do
					line = string.gsub(line, "%s+$", "")
					if #line > 0 then
						table.insert(chunks, line)
					end
				end
				f:close()
			end
			if (#chunks > 0) and (#chunks == meta.total) then
				r.snap = { chunks = chunks, size = meta.size or 0, sha = meta.sha or "", version = meta.version or 1, time = meta.time or 0 }
				r.preview = ReadFile(RoomDir(sid) .. "/preview.b64")
				if r.preview and (#r.preview == 0) then
					r.preview = nil
				end
				g_Rooms[sid] = r
			else
				Warn("room " .. sid .. ": snapshot missing or incomplete on disk, skipped")
			end
		end
	end
	local n = 0
	for _ in pairs(g_Rooms) do n = n + 1 end
	Log("loaded " .. n .. " stored world(s) from " .. CFG.DATA_DIR)
end

local function DeleteRoomFiles(a_Id)
	local dir = RoomDir(a_Id)
	for _, name in ipairs({ "snapshot.b64", "snapshot.b64.tmp", "preview.b64", "meta.json" }) do
		os.remove(dir .. "/" .. name)
	end
	pcall(cFile.DeleteFolder, cFile, dir)
end

---------------------------------------------------------------------
-- Snapshots
---------------------------------------------------------------------

local function SendSnapshot(a_Client, a_Room)
	local snap = a_Room.snap
	if not snap then
		return false
	end
	local rid = JStr(a_Room.id)
	-- lockstep: a newcomer learns which epoch this save starts before it gets the save
	if a_Room.wle and (a_Room.wleSha == snap.sha) then
		SendLine(a_Client, a_Room.wle)
	end
	SendLine(a_Client, '{"t":"snap-begin","room":' .. rid .. ',"size":' .. string.format("%d", snap.size) ..
		',"sha":' .. JStr(snap.sha) .. ',"total":' .. #snap.chunks .. ',"version":' .. (snap.version or 1) .. '}')
	for i, chunk in ipairs(snap.chunks) do
		SendLine(a_Client, '{"t":"snap-chunk","room":' .. rid .. ',"seq":' .. (i - 1) .. ',"data":"' .. chunk .. '"}')
	end
	SendLine(a_Client, '{"t":"snap-end","room":' .. rid .. '}')
	a_Client.WaitingSnap = false
	a_Client.Synced = true
	return true
end

local function RequestSnapshot(a_Room, a_Reason)
	local host = a_Room.host
	if (host == nil) or a_Room.pending then
		return
	end
	if a_Room.snapRequestedAt and ((g_ClockMs - a_Room.snapRequestedAt) < CFG.SNAP_TIMEOUT_MS) then
		return  -- already asked; the answer will serve everyone waiting
	end
	a_Room.snapRequestedAt = g_ClockMs
	Send(host, { t = "snap-request", room = a_Room.id, reason = a_Reason or "join" })
end

local function AnyoneWaiting(a_Room)
	for _, m in ipairs(a_Room.members) do
		if m.WaitingSnap then
			return true
		end
	end
	return false
end

--- Sends the stored snapshot to every member still waiting for one.
local function ServeWaiting(a_Room)
	for _, m in ipairs(a_Room.members) do
		if m.WaitingSnap then
			SendSnapshot(m, a_Room)
		end
	end
end

---------------------------------------------------------------------
-- Room membership
---------------------------------------------------------------------

local function SetHost(a_Room, a_Client)
	a_Room.host = a_Client
	a_Room.snapRequestedAt = nil
	if a_Client then
		Send(a_Client, { t = "role", room = a_Room.id, role = "host" })
		Log(a_Client.Name .. " now hosts room " .. a_Room.id)
	end
end

local function LeaveRoom(a_Client)
	local room = a_Client.Room
	if room == nil then
		return
	end
	for i, m in ipairs(room.members) do
		if m == a_Client then
			table.remove(room.members, i)
			break
		end
	end
	a_Client.Room = nil
	a_Client.Synced = false
	a_Client.WaitingSnap = false
	if room.pending and (room.pending.from == a_Client) then
		room.pending = nil
		room.snapRequestedAt = nil
	end

	if room.host == a_Client then
		room.host = nil
		room.lastHost = a_Client.Name   -- may resume it from their open world (connection drop)
		-- Prefer a player (not a spectator) that already runs this world.
		for pass = 1, 2 do
			for _, m in ipairs(room.members) do
				if (room.host == nil) and m.Synced and (not m.WaitingSnap) and ((pass == 2) or (not m.Spectator)) then
					SetHost(room, m)
				end
			end
		end
		if room.host == nil and (#room.members > 0) then
			if room.snap then
				ServeWaiting(room)
				SetHost(room, room.members[1])
			else
				for _, m in ipairs(room.members) do
					SendError(m, "the host left before sending the world; please travel again")
					m.Room = nil
					m.WaitingSnap = false
				end
				room.members = {}
			end
		end
		if room.host and AnyoneWaiting(room) then
			RequestSnapshot(room, "join")
		end
	end

	if (#room.members == 0) and (room.snap == nil) then
		g_Rooms[room.id] = nil
	end
end

--- Room passwords are stored hashed (salted with the room id), never as typed.
local function HashPassword(a_RoomId, a_Password)
	local ok, h = pcall(function() return cCryptoHash.sha1HexString("wfrooms|" .. a_RoomId .. "|" .. a_Password) end)
	if ok and (type(h) == "string") and (#h > 0) then
		return "sha1:" .. string.lower(h)
	end
	return "plain:" .. a_Password
end

--- The world's real admin: its owner, or (the shared world) its current host.
local function IsRealAdmin(a_Client, a_Room)
	if a_Room.owner ~= "" then
		return string.lower(a_Room.owner) == string.lower(a_Client.Name or "")
	end
	return a_Room.host == a_Client
end

--- May change settings and kick: the real admin, or (everyoneAdmin, the default) any player in
--- the world who isn't just watching. Joiners are not in the world yet, so they are still checked.
local function IsAdmin(a_Client, a_Room)
	if IsRealAdmin(a_Client, a_Room) then
		return true
	end
	return a_Room.settings.everyoneAdmin and (a_Client.Room == a_Room) and (not a_Client.Spectator)
end

local function FindAdmin(a_Room)
	if a_Room.owner == "" then
		return a_Room.host
	end
	local found = nil
	ForEachPlayer(function(c)
		if string.lower(c.Name) == string.lower(a_Room.owner) then
			found = c
		end
	end)
	return found
end

local function FindPlayer(a_Id)
	local found = nil
	ForEachPlayer(function(c)
		if c.Id == a_Id then
			found = c
		end
	end)
	return found
end

--- Differences between the mods a world needs and a player's mods; nil if they match.
local function ModDiff(a_Need, a_Have, a_AllowExtra)
	if type(a_Need) ~= "table" then
		return nil
	end
	local need, have = {}, {}
	for _, m in ipairs(a_Need) do need[string.lower(m.id)] = m end
	for _, m in ipairs(a_Have or {}) do have[string.lower(m.id)] = m end
	local diff = { missing = {}, extra = {}, different = {} }
	local any = false
	for k, m in pairs(need) do
		local h = have[k]
		if h == nil then
			table.insert(diff.missing, m.id .. " " .. m.ver)
			any = true
		elseif h.ver ~= m.ver then
			table.insert(diff.different, m.id .. " (needs " .. m.ver .. ", you have " .. h.ver .. ")")
			any = true
		end
	end
	if not a_AllowExtra then
		for k, h in pairs(have) do
			if need[k] == nil then
				table.insert(diff.extra, h.id .. " " .. h.ver)
				any = true
			end
		end
	end
	return any and diff or nil
end

local function SanitizeMods(a_List)
	if type(a_List) ~= "table" then
		return {}
	end
	local out = {}
	for _, m in ipairs(a_List) do
		if (type(m) == "table") and (#out < CFG.MAX_MODS) then
			local id = SanitizeText(m.id, 64, nil)
			if id then
				table.insert(out, { id = id, ver = SanitizeText(m.ver, 64, "?") })
			end
		end
	end
	return out
end

local function CountRooms()
	local n = 0
	for _ in pairs(g_Rooms) do n = n + 1 end
	return n
end

---------------------------------------------------------------------
-- Message handlers
---------------------------------------------------------------------

local Handlers = {}

function Handlers.hello(a_Client, a_Msg)
	if a_Client.Id then
		return
	end
	if a_Msg.version ~= PROTOCOL_VERSION then
		SendError(a_Client, "protocol version mismatch: server speaks v" .. PROTOCOL_VERSION ..
			", your Coopfall speaks v" .. tostring(a_Msg.version) .. " - update the mod or the server plugin")
		return
	end
	local name = SanitizeText(a_Msg.name, 24, "Player")
	-- Unique display names (rooms are keyed by name for "home-" worlds).
	local base, n, taken = name, 1, true
	while taken do
		taken = false
		ForEachPlayer(function(c)
			if string.lower(c.Name) == string.lower(name) then
				taken = true
			end
		end)
		if taken then
			n = n + 1
			name = base .. n
		end
	end
	g_IdCounter = g_IdCounter + 1
	a_Client.Id = string.format("p%d-%04x", g_IdCounter, math.random(0, 65535))
	a_Client.Name = name
	a_Client.Color = SanitizeColor(a_Msg.color)
	a_Client.GameVersion = SanitizeText(a_Msg.game, 32, "")
	a_Client.Mods = SanitizeMods(a_Msg.mods)
	Send(a_Client, { t = "welcome", yourId = a_Client.Id, name = name, rooms = RoomList(), players = PlayerList(), server = "WorldfallRooms v3" })
	for _, r in pairs(g_Rooms) do
		if r.preview then
			SendLine(a_Client, '{"t":"preview","room":' .. JStr(r.id) .. ',"png":"' .. r.preview .. '"}')
		end
	end
	Log("hello: " .. name .. " (" .. a_Client.Id .. ") from " .. tostring(a_Client.Addr) .. ", game " .. tostring(a_Client.GameVersion))
	BroadcastPlayers()
	Toast(name .. " connected", a_Client)
end

function Handlers.join(a_Client, a_Msg)
	if not a_Client.Id then
		return SendError(a_Client, "join before hello")
	end
	local id = SanitizeRoomId(a_Msg.room)
	if not id then
		return SendError(a_Client, "invalid room id")
	end
	local seed = (a_Msg.seed == true)
	local preferLocal = (a_Msg.preferLocal == true)

	if a_Client.Room and (a_Client.Room.id == id) then
		Send(a_Client, { t = "joined", room = id, name = a_Client.Room.name, role = (a_Client.Room.host == a_Client) and "host" or "guest", load = false })
		return
	end

	local room = g_Rooms[id]
	if (room == nil) and (not seed) then
		return SendError(a_Client, "that world no longer exists")
	end
	if (room == nil) and (CountRooms() >= CFG.MAX_ROOMS) then
		return SendError(a_Client, "the server has too many worlds (" .. CFG.MAX_ROOMS .. "); delete one first")
	end
	local spectate = (a_Msg.spectate == true)

	if room and not IsAdmin(a_Client, room) then
		local st = room.settings
		local function Refuse(a_Code, a_Text, a_Extra)
			local m = { t = "error", msg = a_Text, code = a_Code, room = id }
			for k, v in pairs(a_Extra or {}) do m[k] = v end
			Send(a_Client, m)
		end
		local kickedUntil = room.kicked[string.lower(a_Client.Name)]
		if kickedUntil and (kickedUntil > g_ClockMs) then
			return Refuse("kicked", "you were removed from " .. room.name .. "; try again in a few minutes")
		end
		if st.locked then
			return Refuse("locked", room.name .. " is locked")
		end
		if (st.password ~= "") and ((type(a_Msg.password) ~= "string") or (HashPassword(id, a_Msg.password) ~= st.password)) then
			return Refuse("password", a_Msg.password and ("wrong password for " .. room.name) or (room.name .. " needs a password"))
		end
		if spectate then
			if not st.spectators then
				return Refuse("spectators", room.name .. " doesn't allow spectators")
			end
			if room.host == nil then
				return Refuse("empty", "nobody is playing in " .. room.name .. " right now")
			end
		elseif st.maxPlayers > 0 then
			local players = 0
			for _, m in ipairs(room.members) do
				if not m.Spectator then players = players + 1 end
			end
			if players >= st.maxPlayers then
				return Refuse("full", room.name .. " is full (" .. st.maxPlayers .. " players)")
			end
		end
		local diff = ModDiff(room.mods, a_Client.Mods, st.allowExtraMods)
		if diff then
			return Refuse("mods", "your mods don't match " .. room.name .. "'s", { mods = diff })
		end
		if st.approval and (a_Client.ApprovedFor ~= id) then
			local admin = FindAdmin(room)
			if (admin == nil) or (admin == a_Client) then
				return Refuse("approval", room.name .. " needs its owner's approval, and they aren't online")
			end
			a_Client.PendingJoin = { room = id, msg = a_Msg, at = g_ClockMs }
			Send(admin, { t = "join-request", room = id, id = a_Client.Id, name = a_Client.Name, spectate = spectate })
			Send(a_Client, { t = "waiting", room = id, admin = admin.Name })
			return
		end
	end
	a_Client.ApprovedFor = nil
	a_Client.PendingJoin = nil

	LeaveRoom(a_Client)
	a_Client.Spectator = spectate

	if room == nil then
		local kind, owner, defName = "world", a_Client.Name, a_Client.Name .. "'s world"
		if id == "shared" then
			kind, owner, defName = "shared", "", "Shared World"
		elseif string.sub(id, 1, 5) == "home-" then
			kind = "home"
		end
		room = NewRoom(id, SanitizeText(a_Msg.name, 64, defName), kind, owner)
		g_Rooms[id] = room
	end

	table.insert(room.members, a_Client)
	a_Client.Room = room
	a_Client.Synced = false
	a_Client.WaitingSnap = false

	local isOwner = (room.owner ~= "") and (string.lower(room.owner) == string.lower(a_Client.Name))
	if room.host and (room.host ~= a_Client) then
		-- Someone is running this world right now: get a fresh copy from them.
		a_Client.WaitingSnap = true
		Send(a_Client, { t = "joined", room = id, name = room.name, role = "guest", load = true, host = room.host.Name })
		RequestSnapshot(room, "join")
	elseif seed and ((room.snap == nil) or (preferLocal and (isOwner or room.kind == "shared" or
			((a_Msg.resume == true) and (room.lastHost ~= nil) and (string.lower(room.lastHost) == string.lower(a_Client.Name)))))) then
		-- The joiner's currently open world becomes this world.
		room.host = a_Client
		room.mods = a_Client.Mods
		a_Client.Synced = true
		Send(a_Client, { t = "joined", room = id, name = room.name, role = "host", load = false })
		RequestSnapshot(room, "seed")
	elseif room.snap then
		-- Dormant world: load the stored copy and run it.
		room.host = a_Client
		if room.mods == nil then
			room.mods = a_Client.Mods
		end
		Send(a_Client, { t = "joined", room = id, name = room.name, role = "host", load = true })
		SendSnapshot(a_Client, room)
	else
		LeaveRoom(a_Client)
		return SendError(a_Client, "that world has no saved copy yet")
	end
	Log(a_Client.Name .. " joined room " .. id .. " as " .. ((room.host == a_Client) and "host" or "guest") .. (spectate and " (spectating)" or ""))
	BroadcastPlayers()
	BroadcastRooms()
end

function Handlers.leave(a_Client, a_Msg)
	if a_Client.Room then
		LeaveRoom(a_Client)
		BroadcastPlayers()
		BroadcastRooms()
	end
end

function Handlers.resync(a_Client, a_Msg)
	local room = a_Client.Room
	if (room == nil) or (room.host == a_Client) then
		return
	end
	a_Client.WaitingSnap = true
	-- lockstep: a guest fetching the current epoch's save gets the stored copy (or waits for its upload)
	if (type(a_Msg.sha) == "string") and (room.snap and room.snap.sha == a_Msg.sha) then
		SendSnapshot(a_Client, room)
		return
	end
	if (type(a_Msg.sha) == "string") and room.pending and (room.pending.sha == a_Msg.sha) then
		-- the epoch's save is still coming in: stream it (chunks so far now, the rest as they arrive)
		local p = room.pending
		local rid = JStr(room.id)
		if room.wle and (room.wleSha == p.sha) then
			SendLine(a_Client, room.wle)
		end
		SendLine(a_Client, '{"t":"snap-begin","room":' .. rid .. ',"size":' .. string.format("%d", p.size) ..
			',"sha":' .. JStr(p.sha) .. ',"total":' .. p.total .. '}')
		for i, chunk in ipairs(p.chunks) do
			SendLine(a_Client, '{"t":"snap-chunk","room":' .. rid .. ',"seq":' .. (i - 1) .. ',"data":"' .. chunk .. '"}')
		end
		p.followers = p.followers or {}
		p.followers[a_Client] = true
		a_Client.WaitingSnap = false
		return
	end
	if (type(a_Msg.sha) == "string") and room.pending then
		return  -- served when the upload ends
	end
	if room.host then
		RequestSnapshot(room, "resync")
	elseif room.snap then
		ServeWaiting(room)
	end
end

Handlers["snap-begin"] = function(a_Client, a_Msg)
	local room = a_Client.Room
	if (room == nil) or (room.host ~= a_Client) or (SanitizeRoomId(a_Msg.room) ~= room.id) then
		return SendError(a_Client, "only the host of a world can upload it")
	end
	if not IsNumber(a_Msg.size) or (a_Msg.size <= 0) or (a_Msg.size > CFG.MAX_SNAPSHOT_BYTES) then
		room.pending = nil
		return SendError(a_Client, "world snapshot too large (max " .. math.floor(CFG.MAX_SNAPSHOT_BYTES / 1048576) .. " MB)")
	end
	if not IsNumber(a_Msg.total) or (a_Msg.total < 1) or (a_Msg.total > 4096) then
		room.pending = nil
		return SendError(a_Client, "invalid chunk count")
	end
	room.pending =
	{
		from = a_Client,
		chunks = {},
		size = a_Msg.size,
		sha = SanitizeText(a_Msg.sha, 64, ""),
		total = a_Msg.total,
		b64len = 0,
	}
end

Handlers["snap-chunk"] = function(a_Client, a_Msg)
	local room = a_Client.Room
	local p = room and room.pending
	if (p == nil) or (p.from ~= a_Client) then
		return
	end
	if (a_Msg.seq ~= #p.chunks) or (type(a_Msg.data) ~= "string") or (not string.find(a_Msg.data, "^[%w%+/=]+$")) then
		room.pending = nil
		return SendError(a_Client, "bad snapshot chunk " .. tostring(a_Msg.seq))
	end
	p.b64len = p.b64len + #a_Msg.data
	if (p.b64len * 3 / 4) > (CFG.MAX_SNAPSHOT_BYTES + 4) then
		room.pending = nil
		return SendError(a_Client, "snapshot exceeds the size limit")
	end
	table.insert(p.chunks, a_Msg.data)
	if p.followers then
		local line = '{"t":"snap-chunk","room":' .. JStr(room.id) .. ',"seq":' .. (#p.chunks - 1) .. ',"data":"' .. a_Msg.data .. '"}'
		for c in pairs(p.followers) do
			SendLine(c, line)
		end
	end
end

Handlers["snap-end"] = function(a_Client, a_Msg)
	local room = a_Client.Room
	local p = room and room.pending
	if (p == nil) or (p.from ~= a_Client) then
		return
	end
	room.pending = nil
	room.snapRequestedAt = nil
	-- guests who got this save streamed: finish it (an incomplete one makes them ask again)
	if p.followers then
		for c in pairs(p.followers) do
			if #p.chunks == p.total then
				SendLine(c, '{"t":"snap-end","room":' .. JStr(room.id) .. '}')
				c.Synced = true
			else
				c.WaitingSnap = true
			end
		end
	end
	if #p.chunks ~= p.total then
		return SendError(a_Client, "snapshot incomplete (" .. #p.chunks .. "/" .. p.total .. " chunks)")
	end
	local version = (room.snap and room.snap.version or 0) + 1
	room.snap = { chunks = p.chunks, size = p.size, sha = p.sha, version = version, time = os.time() }
	Log("room " .. room.id .. ": snapshot v" .. version .. " from " .. a_Client.Name .. " (" .. math.floor(p.size / 1024) .. " KB)")
	ServeWaiting(room)
	SaveRoomSnapshot(room)
	Send(a_Client, { t = "snap-stored", room = room.id, version = version })
	BroadcastPlayers()
	BroadcastRooms()
end

function Handlers.preview(a_Client, a_Msg)
	local room = a_Client.Room
	if (room == nil) or (room.host ~= a_Client) then
		return
	end
	if type(a_Msg.stats) == "table" then
		local s = a_Msg.stats
		room.stats =
		{
			year = IsNumber(s.year) and s.year or 0,
			pop = IsNumber(s.pop) and s.pop or 0,
			w = IsNumber(s.w) and s.w or 0,
			h = IsNumber(s.h) and s.h or 0,
		}
	end
	if (type(a_Msg.png) == "string") and (#a_Msg.png > 0) and (#a_Msg.png <= CFG.MAX_PREVIEW_BYTES)
		and string.find(a_Msg.png, "^[%w%+/=]+$") then
		room.preview = a_Msg.png
		local line = '{"t":"preview","room":' .. JStr(room.id) .. ',"png":"' .. room.preview .. '"}'
		ForEachPlayer(function(c)
			if c ~= a_Client then
				SendLine(c, line)
			end
		end)
		SaveRoomPreview(room)
	end
	BroadcastRooms()
end

Handlers["rename-room"] = function(a_Client, a_Msg)
	local room = g_Rooms[SanitizeRoomId(a_Msg.room) or ""]
	if (room == nil) or ((room.owner ~= "") and (string.lower(room.owner) ~= string.lower(a_Client.Name or ""))) then
		return SendError(a_Client, "only the owner can rename that world")
	end
	room.name = SanitizeText(a_Msg.name, 64, room.name)
	SaveRoomMeta(room)
	BroadcastRooms()
end

Handlers["delete-room"] = function(a_Client, a_Msg)
	local id = SanitizeRoomId(a_Msg.room) or ""
	local room = g_Rooms[id]
	if room == nil then
		return
	end
	local isOwner = (room.owner ~= "") and (string.lower(room.owner) == string.lower(a_Client.Name or ""))
	if not isOwner then
		return SendError(a_Client, "only the owner can delete that world")
	end
	if (#room.members > 1) or ((#room.members == 1) and (room.members[1] ~= a_Client)) then
		return SendError(a_Client, "someone is still in that world")
	end
	if a_Client.Room == room then
		LeaveRoom(a_Client)
	end
	g_Rooms[id] = nil
	DeleteRoomFiles(id)
	SaveRoomIndex()
	Log(a_Client.Name .. " deleted room " .. id)
	BroadcastPlayers()
	BroadcastRooms()
end

Handlers["room-settings"] = function(a_Client, a_Msg)
	local room = a_Client.Room
	if (room == nil) or not IsAdmin(a_Client, room) then
		return SendError(a_Client, "only the world's owner (or the shared world's host) can change its settings")
	end
	local st = room.settings
	if type(a_Msg.password) == "string" then
		local pw = SanitizeText(a_Msg.password, 32, "")
		st.password = (pw == "") and "" or HashPassword(room.id, pw)
	end
	for _, k in ipairs({ "locked", "approval", "spectators", "guestSpeed", "allowExtraMods" }) do
		if type(a_Msg[k]) == "boolean" then
			st[k] = a_Msg[k]
		end
	end
	if (type(a_Msg.everyoneAdmin) == "boolean") and IsRealAdmin(a_Client, room) then
		st.everyoneAdmin = a_Msg.everyoneAdmin
	end
	if IsNumber(a_Msg.maxPlayers) then
		st.maxPlayers = math.max(0, math.min(64, math.floor(a_Msg.maxPlayers)))
	end
	if (a_Msg.guestPowers == "all") or (a_Msg.guestPowers == "safe") or (a_Msg.guestPowers == "none") then
		st.guestPowers = a_Msg.guestPowers
	end
	if type(a_Msg.blocked) == "table" then
		local list = {}
		for _, p in ipairs(a_Msg.blocked) do
			local pid = SanitizeText(p, 48, nil)
			if pid and (#list < 200) then
				table.insert(list, pid)
			end
		end
		st.blocked = list
	end
	SaveRoomMeta(room)
	Log(a_Client.Name .. " changed the settings of " .. room.id)
	BroadcastRooms()
end

function Handlers.approve(a_Client, a_Msg)
	local who = FindPlayer(a_Msg.id)
	local pj = who and who.PendingJoin
	local room = pj and g_Rooms[pj.room]
	if (room == nil) or not IsAdmin(a_Client, room) then
		return
	end
	who.PendingJoin = nil
	if a_Msg.ok == true then
		who.ApprovedFor = pj.room
		Handlers.join(who, pj.msg)
	else
		Send(who, { t = "error", msg = a_Client.Name .. " didn't let you into " .. room.name, code = "denied", room = room.id })
	end
end

function Handlers.kick(a_Client, a_Msg)
	local room = a_Client.Room
	if (room == nil) or not IsAdmin(a_Client, room) then
		return SendError(a_Client, "only the world's owner (or the shared world's host) can remove players")
	end
	local who = FindPlayer(a_Msg.id)
	if (who == nil) or (who.Room ~= room) or (who == a_Client) then
		return
	end
	if IsRealAdmin(who, room) then
		return SendError(a_Client, "the world's owner can't be removed")
	end
	room.kicked[string.lower(who.Name)] = g_ClockMs + CFG.KICK_MS
	LeaveRoom(who)
	Send(who, { t = "kicked", room = room.id, name = room.name, by = a_Client.Name })
	Log(a_Client.Name .. " removed " .. who.Name .. " from " .. room.id)
	Toast(who.Name .. " was removed from " .. room.name)
	BroadcastPlayers()
	BroadcastRooms()
end

--- Messages relayed verbatim (plus sender id/name) to the other members of the sender's room.
local RELAYED = { avatar = true, cursor = true, power = true, speed = true, act = true, emote = true, hit = true, whit = true, shot = true, diag = true }

local function RelayToRoom(a_Client, a_Msg)
	local room = a_Client.Room
	if (room == nil) or (not a_Client.Synced) then
		return
	end
	local t = a_Msg.t
	if a_Client.Spectator and (t ~= "cursor") and (t ~= "diag") and (t ~= "emote") then
		return  -- spectators watch: no powers, speed changes, creatures or fights
	end
	if (room.host ~= a_Client) and not IsAdmin(a_Client, room) then
		local st = room.settings
		if (t == "speed") and not st.guestSpeed then
			return
		end
		if t == "power" then
			if st.guestPowers == "none" then
				return
			end
			if st.guestPowers == "safe" then
				local blocked = {}
				for _, p in ipairs(st.blocked) do blocked[p] = true end
				if blocked[a_Msg.p] then
					return
				end
				if type(a_Msg.batch) == "table" then
					local keep = {}
					for _, ev in ipairs(a_Msg.batch) do
						if (type(ev) == "table") and not blocked[ev.p] then
							table.insert(keep, ev)
						end
					end
					if #keep == 0 then
						return
					end
					a_Msg.batch = keep
				end
			end
		end
	end
	a_Msg.id = a_Client.Id
	a_Msg.name = a_Client.Name
	a_Msg.color = a_Client.Color
	a_Msg.room = room.id
	local line = JEncode(a_Msg)
	if line == nil then
		return
	end
	for _, m in ipairs(room.members) do
		if (m ~= a_Client) and m.Synced and (not m.WaitingSnap) then
			SendLine(m, line)
		end
	end
end

--- Live world sync. Lines start with {"t":"<type>" so they are recognized without parsing.
--- true = only the room's host may send it.
local LIVE_SYNC = { wu = true, wb = true, wdata = true, wm = true, wa = true, ww = true, wt = true, wc = true, wneed = false, wask = false,
	-- lockstep: epoch, inputs and tick grants come from the host; requests, checksums and "ready" from anyone
	wle = true, wli = true, wlg = true, wlreload = true, wlr = false, wlh = false, wlready = false }

local function RelayRawToRoom(a_Client, a_Type, a_Line)
	local room = a_Client.Room
	if (room == nil) or (not a_Client.Synced) or (a_Client.WaitingSnap) then
		return
	end
	if LIVE_SYNC[a_Type] and (room.host ~= a_Client) then
		return
	end
	if a_Type == "wle" then
		local msg = JDecode(a_Line)
		room.wle = a_Line
		room.wleSha = msg and msg.sha
	end
	for _, m in ipairs(room.members) do
		if (m ~= a_Client) and m.Synced and (not m.WaitingSnap) then
			SendLine(m, a_Line)
		end
	end
end

function Handlers.chat(a_Client, a_Msg)
	if not a_Client.Id then
		return
	end
	local text = SanitizeText(a_Msg.text, 300, nil)
	if text == nil then
		return
	end
	local line = JEncode(
	{
		t = "chat", id = a_Client.Id, name = a_Client.Name, color = a_Client.Color, text = text,
		room = a_Client.Room and a_Client.Room.name or "",
	})
	ForEachPlayer(function(c)
		if c ~= a_Client then
			SendLine(c, line)
		end
	end)
	Log("<" .. a_Client.Name .. "> " .. text)
end

function Handlers.ping(a_Client, a_Msg)
	Send(a_Client, { t = "pong", ts = a_Msg.ts })
	if IsNumber(a_Msg.rtt) and (a_Msg.rtt >= 0) then
		local rtt = math.floor(math.min(a_Msg.rtt, 99999))
		if math.abs(rtt - (a_Client.Ping or -1000)) >= 15 then
			a_Client.PingDirty = true
		end
		a_Client.Ping = rtt
	end
end

---------------------------------------------------------------------
-- Connection lifecycle
---------------------------------------------------------------------

local function RemoveClient(a_Client, a_Close, a_Reason)
	if a_Client.Removed then
		return
	end
	LeaveRoom(a_Client)
	a_Client.Removed = true
	if a_Close and a_Client.Link then
		pcall(a_Client.Link.Close, a_Client.Link)
	end
	for i, c in ipairs(g_Clients) do
		if c == a_Client then
			table.remove(g_Clients, i)
			break
		end
	end
	if a_Client.Id then
		Log(a_Client.Name .. " disconnected (" .. tostring(a_Reason) .. ")")
		BroadcastPlayers()
		BroadcastRooms()
		Toast(a_Client.Name .. " left the server")
	end
end

function Handlers.bye(a_Client, a_Msg)
	RemoveClient(a_Client, true, "bye")
end

local function ProcessLine(a_Client, a_Line)
	if #a_Line == 0 then
		return
	end
	local live = string.match(a_Line, '^{"t":"(w%l+)"')
	if live and (LIVE_SYNC[live] ~= nil) then
		if a_Client.Id == nil then
			return SendError(a_Client, "say hello first")
		end
		local ok, err = pcall(RelayRawToRoom, a_Client, live, a_Line)
		if not ok then
			Warn("relay '" .. live .. "' failed: " .. tostring(err))
		end
		return
	end
	local msg = JDecode(a_Line)
	if (msg == nil) or (type(msg.t) ~= "string") then
		return SendError(a_Client, "invalid message")
	end
	if (a_Client.Id == nil) and (msg.t ~= "hello") and (msg.t ~= "ping") and (msg.t ~= "bye") then
		return SendError(a_Client, "say hello first")
	end
	local handler = Handlers[msg.t]
	local ok, err
	if handler then
		ok, err = pcall(handler, a_Client, msg)
	elseif RELAYED[msg.t] then
		ok, err = pcall(RelayToRoom, a_Client, msg)
	else
		return SendError(a_Client, "unknown message type '" .. msg.t .. "'")
	end
	if not ok then
		Warn("handler '" .. msg.t .. "' failed: " .. tostring(err))
		SendError(a_Client, "server error while handling '" .. msg.t .. "'")
	end
end

local function MakeLinkCallbacks(a_Client)
	return
	{
		OnConnected = function(a_Link)
		end,
		OnReceivedData = function(a_Link, a_Data)
			if a_Client.Removed then
				return
			end
			a_Client.Link = a_Client.Link or a_Link
			a_Client.LastSeenMs = g_ClockMs
			a_Client.Buffer = a_Client.Buffer .. a_Data
			local start = 1
			while true do
				local nl = string.find(a_Client.Buffer, "\n", start, true)
				if nl == nil then
					break
				end
				local line = string.sub(a_Client.Buffer, start, nl - 1)
				start = nl + 1
				line = string.gsub(line, "\r$", "")
				ProcessLine(a_Client, line)
				if a_Client.Removed then
					return
				end
			end
			if start > 1 then
				a_Client.Buffer = string.sub(a_Client.Buffer, start)
			end
			if #a_Client.Buffer > CFG.MAX_LINE_BYTES then
				SendError(a_Client, "message too long")
				RemoveClient(a_Client, true, "line too long")
			end
		end,
		OnRemoteClosed = function(a_Link)
			RemoveClient(a_Client, false, "closed")
		end,
		OnError = function(a_Link, a_ErrorCode, a_ErrorMsg)
			RemoveClient(a_Client, false, "link error " .. tostring(a_ErrorCode) .. " " .. tostring(a_ErrorMsg))
		end,
	}
end

local function OnTick(a_DeltaMs)
	g_ClockMs = g_ClockMs + (a_DeltaMs or 50)
	if g_ClockMs < g_NextSweepMs then
		return
	end
	g_NextSweepMs = g_ClockMs + 1000

	local dead = {}
	for _, c in ipairs(g_Clients) do
		if (g_ClockMs - c.LastSeenMs) > CFG.TIMEOUT_MS then
			table.insert(dead, c)
		end
	end
	for _, c in ipairs(dead) do
		RemoveClient(c, true, "timed out")
	end

	local pingChanged = false
	ForEachPlayer(function(c)
		if c.PendingJoin and ((g_ClockMs - c.PendingJoin.at) > CFG.APPROVAL_MS) then
			c.PendingJoin = nil
			Send(c, { t = "error", msg = "nobody answered your request to join", code = "denied" })
		end
		if c.PingDirty then
			c.PingDirty = false
			pingChanged = true
		end
	end)
	if pingChanged and (g_ClockMs >= g_NextPingBroadcastMs) then
		g_NextPingBroadcastMs = g_ClockMs + 10000
		BroadcastPlayers()
	end

	-- Hosts that never answered a snapshot request: fall back to the stored copy.
	for _, room in pairs(g_Rooms) do
		if room.snapRequestedAt and (room.pending == nil) and ((g_ClockMs - room.snapRequestedAt) > CFG.SNAP_TIMEOUT_MS) then
			room.snapRequestedAt = nil
			if AnyoneWaiting(room) then
				if room.snap then
					Warn("room " .. room.id .. ": host did not send a snapshot in time, serving the stored copy")
					ServeWaiting(room)
				else
					RequestSnapshot(room, "retry")
				end
			end
		end
	end
end

local function HandleConsoleWf(a_Split)
	local out = { "WorldfallRooms: listening on port " .. CFG.PORT .. ": " .. ((g_Server and g_Server:IsListening()) and "yes" or "NO") }
	ForEachPlayer(function(c)
		table.insert(out, string.format("  player %-16s %-10s room=%s%s  %s", c.Name, c.Id,
			c.Room and c.Room.id or "-", (c.Room and c.Room.host == c) and " (host)" or "", tostring(c.Addr)))
	end)
	for _, r in pairs(g_Rooms) do
		table.insert(out, string.format("  world %-20s \"%s\" players=%d host=%s snapshot=%s", r.id, r.name, #r.members,
			r.host and r.host.Name or "-", r.snap and (math.floor(r.snap.size / 1024) .. " KB v" .. r.snap.version) or "none"))
	end
	return true, table.concat(out, "\n")
end

function Initialize(a_Plugin)
	g_Plugin = a_Plugin
	a_Plugin:SetName(PLUGIN_NAME)
	a_Plugin:SetVersion(PROTOCOL_VERSION)
	math.randomseed(os.time())
	math.random(); math.random()

	EnsureFolder(CFG.DATA_DIR)
	LoadRooms()

	cPluginManager:AddHook(cPluginManager.HOOK_TICK, OnTick)
	cPluginManager:BindConsoleCommand("wf", HandleConsoleWf, " - Worldfall Co-op: players and worlds")

	g_Server = cNetwork:Listen(CFG.PORT,
	{
		OnIncomingConnection = function(a_RemoteIP, a_RemotePort, a_LocalPort)
			local client =
			{
				Addr = tostring(a_RemoteIP) .. ":" .. tostring(a_RemotePort),
				LastSeenMs = g_ClockMs,
				Buffer = "",
				Synced = false,
				WaitingSnap = false,
			}
			table.insert(g_Clients, client)
			return MakeLinkCallbacks(client)
		end,
		OnAccepted = function(a_Link)
		end,
		OnError = function(a_ErrorCode, a_ErrorMsg)
			Warn("listener error " .. tostring(a_ErrorCode) .. ": " .. tostring(a_ErrorMsg))
		end,
	})
	if (g_Server == nil) or (not g_Server:IsListening()) then
		LOGERROR("[" .. PLUGIN_NAME .. "] could not listen on port " .. CFG.PORT .. " - is another server already running?")
		return false
	end
	Log("Worldfall Co-op relay listening on port " .. CFG.PORT .. " (TCP). Type 'wf' for status.")
	return true
end

function OnDisable()
	Log("shutting down relay")
	local all = {}
	for _, c in ipairs(g_Clients) do
		table.insert(all, c)
	end
	for _, c in ipairs(all) do
		RemoveClient(c, true, "server stopping")
	end
	if g_Server then
		pcall(g_Server.Close, g_Server)
		g_Server = nil
	end
end
