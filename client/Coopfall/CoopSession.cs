using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Coopfall.Net;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    public class PlayerInfo
    {
        public string id, name, color = "#ffffff", room = "", game = "";
        public bool host, synced;
    }

    public class RoomInfo
    {
        public string id, name, kind = "world", owner = "", host = "";
        public int players, year, pop, w, h, version;
        public long size;
        public List<string> names = new List<string>();
        public bool hasWorld;
    }

    public class ChatLine
    {
        public string name, text, color;
        public bool system;
        public float time;
    }

    public enum Phase { Offline, Connecting, Lobby, Joining, Downloading, Loading, InWorld, Leaving }

    /// <summary>
    /// One server connection: rooms (worlds), hosting duties, snapshot transfer, travel and resync.
    ///
    /// Shared mode: everyone joins room "shared". Own mode: each player hosts "home-&lt;name&gt;" from
    /// the world they have open and can travel to anybody else's world from the World Map.
    /// The room's HOST runs the authoritative simulation; guests load the host's snapshot on join,
    /// replay everyone's god powers live, puppet remote players' units, and periodically resync.
    /// </summary>
    public class CoopSession
    {
        public const int ChunkBytes = 60 * 1024;
        public const int ProtocolVersion = 2;

        public readonly NetClient Net = new NetClient();
        public readonly CoopConfig Cfg;

        public Phase Phase = Phase.Offline;
        public string Status = "Not connected";
        public string MyId, MyName;
        public string RoomId, RoomName;
        public bool IsHost;
        public int PingMs = -1;

        public readonly List<PlayerInfo> Players = new List<PlayerInfo>();
        public readonly Dictionary<string, RoomInfo> Rooms = new Dictionary<string, RoomInfo>();
        public readonly List<string> RoomOrder = new List<string>();
        public readonly Dictionary<string, Texture2D> Previews = new Dictionary<string, Texture2D>();
        public readonly List<ChatLine> Chat = new List<ChatLine>();

        public event Action<string> Toast;
        public event Action<string, string> ChatFrom;   // player id, text

        // download state
        private string _dlRoom, _dlSha;
        private int _dlTotal, _dlGot;
        private long _dlSize;
        private MemoryStream _dlBuf;
        public float DownloadProgress { get { return _dlTotal <= 0 ? 0f : (float)_dlGot / _dlTotal; } }

        // hosting / travel state
        private volatile bool _uploading;
        private bool _snapRequestPending;
        private float _nextHostSave, _nextPreview, _lastSyncAt, _leavingSince, _phaseSince;
        private JObject _pendingJoin;
        private bool _restoreCamera;
        private Vector3 _camPos;
        private float _camSize;
        private bool _backedUp;
        private string _travelTarget;
        // re-sync while possessing: load after Worldfall has left first person, then possess again
        private byte[] _deferredLoad;
        private float _deferredSince;
        private long _repossessId;
        private bool _repossessView;
        private bool _liveSyncWarned;
        // connection drops: reconnect by itself, and resume the world we were hosting from our open copy
        private bool _userLeft, _reconnecting;
        private float _reconnectAt;
        private int _reconnectTries;
        private string _resumeRoom, _resumeName;
        public bool Reconnecting { get { return _reconnecting; } }

        public CoopSession(CoopConfig cfg)
        {
            Cfg = cfg;
            Net.OnConnected = OnConnected;
            Net.OnDisconnected = OnDisconnected;
            Net.OnPacket = HandlePacket;
        }

        // ================================================================ public actions

        public bool Online { get { return Net.Connected && MyId != null; } }
        public bool InWorld { get { return Phase == Phase.InWorld; } }
        public bool Busy { get { return Phase == Phase.Joining || Phase == Phase.Downloading || Phase == Phase.Loading || Phase == Phase.Leaving; } }
        public string HomeId { get { return "home-" + Slug(MyName ?? Cfg.playerName); } }
        public string TravelTarget { get { return _travelTarget; } }

        public void Connect()
        {
            if (Net.Connected || Net.Connecting) return;
            _userLeft = false;
            SetPhase(Phase.Connecting);
            Status = "Connecting to " + Cfg.serverHost + ":" + Cfg.serverPort + " ...";
            Log.Info(Status);
            Net.ConnectAsync(Cfg.serverHost.Trim(), Cfg.serverPort);
        }

        public void Disconnect()
        {
            _userLeft = true;
            _reconnecting = false;
            _resumeRoom = null;
            if (Net.Connected)
            {
                Net.Send("bye");
                Thread.Sleep(30);
            }
            Net.Disconnect("you disconnected");
        }

        /// <summary>Joins the world that matches the configured mode (shared world, or my own world).</summary>
        public void EnterModeWorld()
        {
            if (!Online) return;
            if (_resumeRoom != null)
            {
                // The connection dropped while we hosted this world: our open world is its newest state.
                string room = _resumeRoom, name = _resumeName;
                _resumeRoom = null;
                Log.Info("resuming " + room + " from the open world");
                Travel(room, name, true, true, true);
                return;
            }
            if (Cfg.mode == "own") Travel(HomeId, MyName + "'s World", true, RoomId == null);
            else Travel("shared", "Shared World", true, false);
        }

        public void GoHome() { Travel(HomeId, MyName + "'s World", true, false); }

        /// <summary>Publishes the map I have open as a brand-new world on the server.</summary>
        public void PublishAsNewWorld(string name)
        {
            string id = "world-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Travel(id, string.IsNullOrWhiteSpace(name) ? MyName + "'s new world" : name.Trim(), true, true);
        }

        /// <summary>Go to another world. If I'm the last player in my current world, its latest state is uploaded first.</summary>
        public void Travel(string roomId, string name = null, bool seed = false, bool preferLocal = false, bool resume = false)
        {
            if (!Online || roomId == RoomId || Phase == Phase.Leaving || Phase == Phase.Loading || Phase == Phase.Downloading) return;
            var join = new JObject { ["room"] = roomId, ["seed"] = seed, ["preferLocal"] = preferLocal };
            if (resume) join["resume"] = true;
            if (name != null) join["name"] = name;
            _travelTarget = roomId;
            if (IsHost && InWorld && OthersInRoom() == 0 && WorldBoxApi.WorldReady)
            {
                _pendingJoin = join;
                SetPhase(Phase.Leaving);
                _leavingSince = Time.unscaledTime;
                Status = "Saving " + RoomName + " to the server before leaving...";
                UploadSnapshot("leave");
            }
            else SendJoin(join);
        }

        public void Resync(bool auto = false)
        {
            if (!Online || !InWorld || IsHost) return;
            Status = (auto ? "Catching up with " : "Re-syncing with ") + HostName() + "...";
            Net.Send("resync");
            _lastSyncAt = Time.unscaledTime;
        }

        public void SendChat(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.StartsWith("/"))
            {
                string cmd = text.ToLowerInvariant();
                if (cmd == "/sync") { Resync(); return; }
                if (cmd == "/home") { GoHome(); return; }
                if (cmd == "/shared") { Travel("shared", "Shared World", true, false); return; }
                if (cmd.StartsWith("/report")) { CoopMod.Instance?.Diag.Request(text.Length > 7 ? "/report: " + text.Substring(7).Trim() : "/report"); AddChat(null, "Sync report captured in every game (coopfall/diag)", true); return; }
                AddChat(null, "Commands: /sync /home /shared /report", true);
                return;
            }
            if (!Online) { AddChat(null, "Not connected.", true); return; }
            Net.Send("chat", new JObject { ["text"] = text });
            AddChat(MyName, text, false, Cfg.color);
            ChatFrom?.Invoke(MyId, text);
        }

        public void DeleteRoom(string id) { if (Online) Net.Send("delete-room", new JObject { ["room"] = id }); }

        public int OthersInRoom()
        {
            int n = 0;
            foreach (var p in Players) if (p.id != MyId && p.room == RoomId && RoomId != null) n++;
            return n;
        }

        public string HostName()
        {
            foreach (var p in Players) if (p.room == RoomId && p.host) return p.name;
            return "?";
        }

        public PlayerInfo Player(string id)
        {
            foreach (var p in Players) if (p.id == id) return p;
            return null;
        }

        // ================================================================ per-frame

        public void Tick()
        {
            Net.Pump();
            float now = Time.unscaledTime;

            if (_reconnecting && !Net.Connected && !Net.Connecting && now >= _reconnectAt && Phase != Phase.Loading)
            {
                Log.Info("reconnecting (attempt " + _reconnectTries + ")");
                Connect();
            }

            if (_deferredLoad != null)
            {
                // Worldfall treats a possessed unit vanishing as death; we let go of it first and
                // wait until its first-person view has closed before replacing the world.
                bool fpBusy = WorldfallBridge.FirstPerson || WorldfallBridge.Diving;
                if ((!fpBusy && now - _deferredSince > 0.1f) || now - _deferredSince > 3f)
                {
                    byte[] data = _deferredLoad;
                    _deferredLoad = null;
                    BeginLoad(data);
                }
            }
            else if (Phase == Phase.Loading && WorldBoxApi.WorldReady && now - _phaseSince > 0.5f)
                OnWorldLoaded();

            if (Phase == Phase.Leaving && now - _leavingSince > 60f)
            {
                Log.Warn("server did not confirm the final save in time; traveling anyway");
                SendJoinPending();
            }

            if (!Online || !InWorld || !WorldBoxApi.WorldReady) return;

            if (IsHost)
            {
                if (_snapRequestPending) { _snapRequestPending = false; UploadSnapshot("requested"); }
                if (Cfg.hostSaveMinutes > 0 && now >= _nextHostSave && !_uploading)
                    UploadSnapshot("autosave");
                if (now >= _nextPreview) SendPreview();
            }
            else if (Cfg.autoResyncMinutes > 0 && now - _lastSyncAt > Cfg.autoResyncMinutes * 60f && !CoopMod.UiBlocking)
            {
                Log.Info("auto-resync (every " + Cfg.autoResyncMinutes + " min)");
                Resync(true);
            }
        }

        // ================================================================ connection

        private void OnConnected()
        {
            Status = "Connected, saying hello...";
            Net.Send("hello", new JObject
            {
                ["name"] = Cfg.playerName,
                ["version"] = ProtocolVersion,
                ["color"] = Cfg.color,
                ["game"] = Application.version,
            });
        }

        private void OnDisconnected(string reason)
        {
            Log.Info("disconnected: " + reason);
            bool wasOnline = MyId != null;
            bool wasReconnecting = _reconnecting;
            if (wasOnline && IsHost && RoomId != null && Phase == Phase.InWorld) { _resumeRoom = RoomId; _resumeName = RoomName; }
            MyId = null;
            RoomId = null;
            RoomName = null;
            IsHost = false;
            _uploading = false;
            _pendingJoin = null;
            _dlBuf = null;
            Players.Clear();
            _deferredLoad = null;
            CoopMod.Instance?.Sync.Reset();
            Status = "Offline - " + reason;
            CoopMod.Instance?.Avatars.ReleaseAll();
            if (Phase != Phase.Loading) SetPhase(Phase.Offline);
            else _phaseAfterLoadOffline = true;
            if (!_userLeft && (wasOnline || wasReconnecting))
            {
                float wait = Mathf.Min(60f, 3f * Mathf.Pow(2f, Mathf.Min(_reconnectTries, 5)));
                _reconnectTries++;
                _reconnecting = true;
                _reconnectAt = Time.unscaledTime + wait;
                Status += " - retrying in " + Mathf.RoundToInt(wait) + " s";
                if (wasOnline) Toast?.Invoke("Disconnected: " + reason + " - reconnecting...");
                return;
            }
            Toast?.Invoke(wasOnline ? "Disconnected: " + reason : "Could not connect: " + reason);
        }
        private bool _phaseAfterLoadOffline;

        // ================================================================ packets

        private void HandlePacket(JObject p)
        {
            string t = (string)p["t"];
            switch (t)
            {
                case "welcome": OnWelcome(p); break;
                case "players": ParsePlayers(p["players"] as JArray); break;
                case "rooms": ParseRooms(p["rooms"] as JArray); break;
                case "preview": OnPreview((string)p["room"], (string)p["png"]); break;
                case "notice": Toast?.Invoke((string)p["text"]); break;
                case "error": OnServerError((string)p["msg"]); break;
                case "joined": OnJoined(p); break;
                case "role":
                    if ((string)p["room"] == RoomId && (string)p["role"] == "host")
                    {
                        IsHost = true;
                        CoopMod.Instance?.Sync.Reset();
                        _nextHostSave = Time.unscaledTime + 30f;
                        _nextPreview = Time.unscaledTime + 2f;
                        Toast?.Invoke("You are now hosting " + RoomName);
                    }
                    break;
                case "snap-request":
                    if ((string)p["room"] != RoomId || !IsHost) break;
                    Log.Info("snapshot requested (" + (string)p["reason"] + ")");
                    if (InWorld && WorldBoxApi.WorldReady) UploadSnapshot((string)p["reason"]);
                    else _snapRequestPending = true;
                    break;
                case "snap-begin": OnSnapBegin(p); break;
                case "snap-chunk": OnSnapChunk(p); break;
                case "snap-end": OnSnapEnd(p); break;
                case "snap-stored":
                    Log.Info("server stored " + (string)p["room"] + " v" + (int?)p["version"]);
                    if (Phase == Phase.Leaving) SendJoinPending();
                    break;
                case "avatar": case "cursor": case "act":
                    if (InWorld && (string)p["room"] == RoomId)
                    {
                        CoopMod.Instance?.Avatars.OnPacket(t, p);
                        if (t == "act") CoopMod.Instance?.Combat.OnPacket(t, p);
                    }
                    break;
                case "diag":
                    if (InWorld && (string)p["room"] == RoomId) CoopMod.Instance?.Diag.OnPacket(p);
                    break;
                case "hit": case "whit": case "shot":
                    if (InWorld && (string)p["room"] == RoomId) CoopMod.Instance?.Combat.OnPacket(t, p);
                    break;
                case "wu": case "wb": case "wdata": case "wneed":
                    if (InWorld && (string)p["room"] == RoomId) CoopMod.Instance?.Sync.OnPacket(t, p);
                    break;
                case "wm": case "wa": case "ww": case "wask":
                    if (InWorld && (string)p["room"] == RoomId) CoopMod.Instance?.Meta.OnPacket(t, p);
                    break;
                case "wt":
                    if (InWorld && (string)p["room"] == RoomId) CoopMod.Instance?.Tiles.OnPacket(p);
                    break;
                case "power": case "speed":
                    if (InWorld && (string)p["room"] == RoomId) CoopMod.Instance?.Powers.OnPacket(t, p);
                    else Log.Warn("dropped '" + t + "' (phase " + Phase + ", room " + (string)p["room"] + " vs " + RoomId + ")");
                    break;
                case "chat":
                    AddChat((string)p["name"], (string)p["text"], false, (string)p["color"]);
                    ChatFrom?.Invoke((string)p["id"], (string)p["text"]);
                    break;
                case "pong":
                    long ts = (long?)p["ts"] ?? 0;
                    if (ts > 0) PingMs = (int)Math.Max(0, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - ts);
                    break;
            }
        }

        private void OnWelcome(JObject p)
        {
            MyId = (string)p["yourId"];
            if (_reconnecting) Toast?.Invoke("Reconnected");
            _reconnecting = false;
            _reconnectTries = 0;
            MyName = (string)p["name"] ?? Cfg.playerName;
            Status = "Online as " + MyName;
            SetPhase(Phase.Lobby);
            ParsePlayers(p["players"] as JArray);
            ParseRooms(p["rooms"] as JArray);
            Log.Info("welcome: " + MyName + " (" + MyId + "), " + Rooms.Count + " world(s) on server");
            Toast?.Invoke("Connected to " + Cfg.serverHost + " as " + MyName);
            EnterModeWorld();
        }

        private void OnServerError(string msg)
        {
            if (msg != null && msg.StartsWith("unknown message type 'w"))
            {
                // Relay from before live sync: keep playing without it (re-syncs still work).
                WorldSync sync = CoopMod.Instance?.Sync;
                if (sync != null) sync.Disabled = true;
                if (!_liveSyncWarned)
                {
                    _liveSyncWarned = true;
                    Log.Warn("server has no live sync (" + msg + ") - update its WorldfallRooms plugin");
                    Toast?.Invoke("The server is out of date: live sync is off until it is updated");
                }
                return;
            }
            Log.Warn("server: " + msg);
            Toast?.Invoke("Server: " + msg);
            if (Phase == Phase.Joining || Phase == Phase.Downloading)
            {
                SetPhase(RoomId != null ? Phase.InWorld : Phase.Lobby);
                Status = "Online as " + MyName;
                _travelTarget = null;
            }
        }

        private void SendJoin(JObject join)
        {
            CoopMod.Instance?.Avatars.ReleaseAll();
            SetPhase(Phase.Joining);
            Status = "Joining " + ((string)join["name"] ?? (string)join["room"]) + "...";
            Net.Send("join", join);
        }

        private void SendJoinPending()
        {
            JObject j = _pendingJoin;
            _pendingJoin = null;
            if (j != null) SendJoin(j);
            else SetPhase(Phase.InWorld);
        }

        private void OnJoined(JObject p)
        {
            string room = (string)p["room"];
            bool load = (bool?)p["load"] ?? false;
            RoomId = room;
            RoomName = (string)p["name"] ?? room;
            IsHost = (string)p["role"] == "host";
            _snapRequestPending = false;
            CoopMod.Instance?.Sync.Reset();
            Log.Info("joined " + room + " as " + (IsHost ? "host" : "guest") + (load ? ", waiting for world" : ""));
            if (load)
            {
                SetPhase(Phase.Downloading);
                Status = "Downloading " + RoomName + "...";
            }
            else
            {
                SetPhase(Phase.InWorld);
                _travelTarget = null;
                _lastSyncAt = Time.unscaledTime;
                _nextHostSave = Time.unscaledTime + Mathf.Max(1, Cfg.hostSaveMinutes) * 60f;
                _nextPreview = Time.unscaledTime + 1f;
                Status = (IsHost ? "Hosting " : "Playing in ") + RoomName;
                Toast?.Invoke((IsHost ? "You are hosting " : "You are in ") + RoomName);
            }
        }

        // ================================================================ snapshot download + load

        private void OnSnapBegin(JObject p)
        {
            if ((string)p["room"] != RoomId) return;
            _dlRoom = RoomId;
            _dlTotal = (int?)p["total"] ?? 0;
            _dlSize = (long?)p["size"] ?? 0;
            _dlSha = (string)p["sha"];
            _dlGot = 0;
            _dlBuf = new MemoryStream((int)Math.Min(_dlSize > 0 ? _dlSize : 1 << 20, 128L << 20));
            if (Phase != Phase.Downloading) { SetPhase(Phase.Downloading); }
            Status = "Downloading " + RoomName + " (" + (_dlSize / 1024) + " KB)...";
        }

        private void OnSnapChunk(JObject p)
        {
            if (_dlBuf == null || (string)p["room"] != _dlRoom) return;
            int seq = (int?)p["seq"] ?? -1;
            if (seq != _dlGot) { Log.Warn("snapshot chunk out of order: " + seq + " != " + _dlGot); _dlBuf = null; return; }
            byte[] bytes = Convert.FromBase64String((string)p["data"]);
            _dlBuf.Write(bytes, 0, bytes.Length);
            _dlGot++;
        }

        private void OnSnapEnd(JObject p)
        {
            if (_dlBuf == null || (string)p["room"] != _dlRoom) { Toast?.Invoke("World download failed - try again"); SetPhase(Phase.Lobby); return; }
            byte[] data = _dlBuf.ToArray();
            _dlBuf = null;
            if (!string.IsNullOrEmpty(_dlSha) && !string.Equals(_dlSha, Sha256(data), StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("snapshot checksum mismatch");
                Toast?.Invoke("World download was corrupted - re-syncing");
                SetPhase(Phase.InWorld);
                Resync();
                return;
            }
            StartLoad(data);
        }

        private void StartLoad(byte[] data)
        {
            bool sameWorld = _travelTarget == null; // resync of the world I'm already in
            try
            {
                if (!_backedUp && WorldBoxApi.WorldReady)
                {
                    string dir = WorldBoxApi.BackupCurrentWorld("before-coop");
                    _backedUp = true;
                    Log.Info("your original world was backed up to " + dir);
                    Toast?.Invoke("Your own world was backed up (coopfall/backups)");
                }
            }
            catch (Exception e) { Log.Warn("backup failed: " + e.Message); }

            Camera cam = WorldBoxApi.MapCamera;
            _restoreCamera = sameWorld && cam != null;
            if (_restoreCamera) { _camPos = cam.transform.position; _camSize = cam.orthographicSize; }

            // Re-syncing while possessing: remember the creature so we can step back into it.
            Actor mine = null;
            try { mine = ControllableUnit.isControllingUnit() ? ControllableUnit.getControllableUnit() : null; } catch { }
            _repossessId = sameWorld && mine != null && mine.isAlive() ? mine.getID() : 0;
            _repossessView = !WorldfallBridge.Present || WorldfallBridge.ViewEnabled;

            CoopMod.Instance?.Avatars.ReleaseAll();
            Status = "Loading " + RoomName + "...";
            SetPhase(Phase.Loading);
            if (mine != null)
            {
                try { ControllableUnit.clear(false); } catch { }
                if (WorldfallBridge.FirstPerson || WorldfallBridge.Diving)
                {
                    _deferredLoad = data;
                    _deferredSince = Time.unscaledTime;
                    return;
                }
            }
            BeginLoad(data);
        }

        private void BeginLoad(byte[] data)
        {
            Log.Info("loading world " + RoomId + " (" + (data.Length / 1024) + " KB)");
            SetPhase(Phase.Loading);
            try { WorldBoxApi.LoadSnapshot(data); }
            catch (Exception e)
            {
                Log.Error("world load failed: " + e);
                Toast?.Invoke("Could not load that world: " + e.Message);
                SetPhase(Phase.InWorld);
            }
        }

        private void OnWorldLoaded()
        {
            int purged = WorldBoxApi.PurgeStandins();
            if (_restoreCamera)
            {
                Camera cam = WorldBoxApi.MapCamera;
                if (cam != null) { cam.transform.position = _camPos; cam.orthographicSize = _camSize; }
                _restoreCamera = false;
            }
            bool traveled = _travelTarget != null;
            _travelTarget = null;
            _lastSyncAt = Time.unscaledTime;
            CoopMod.Instance?.Sync.Reset();
            if (_phaseAfterLoadOffline) { _phaseAfterLoadOffline = false; _repossessId = 0; SetPhase(Phase.Offline); return; }
            if (_repossessId != 0) Repossess(_repossessId);
            _repossessId = 0;
            SetPhase(Phase.InWorld);
            Status = (IsHost ? "Hosting " : "Playing in ") + RoomName;
            _nextHostSave = Time.unscaledTime + Mathf.Max(1, Cfg.hostSaveMinutes) * 60f;
            _nextPreview = Time.unscaledTime + 12f;
            Log.Info("world ready (" + purged + " stale stand-ins removed)");
            if (traveled) Toast?.Invoke("Welcome to " + RoomName + "!");
        }

        /// <summary>After a re-sync: step back into the creature you were possessing (same id in the host's world).</summary>
        private void Repossess(long id)
        {
            Actor a = WorldBoxApi.FindActor(id);
            if (a == null || !a.isAlive() || !a.canBePossessed())
            {
                Log.Warn("re-sync: your creature #" + id + " isn't in the host's world");
                Toast?.Invoke("Your creature wasn't in the host's world");
                return;
            }
            try
            {
                // Possessing resets the game speed to x1; keep the room's speed.
                string speed = Config.time_scale_asset?.id;
                bool paused = Config.paused;
                CoopMod.Instance?.Powers.SuppressSpeedEcho();
                a.finishStatusEffect("invincible");
                ControllableUnit.setControllableCreature(a);
                if (speed != null && Config.time_scale_asset?.id != speed) Config.setWorldSpeed(speed);
                Config.paused = paused;
                if (WorldfallBridge.Present) WorldfallBridge.ViewEnabled = _repossessView;
                Log.Info("re-sync: possessing #" + id + " again");
            }
            catch (Exception e) { Log.Warn("re-possess: " + e.Message); }
        }

        // ================================================================ hosting

        private void UploadSnapshot(string reason)
        {
            if (_uploading || !Online || RoomId == null) return;
            byte[] data;
            float t0 = Time.realtimeSinceStartup;
            try { data = WorldBoxApi.TakeSnapshot(); }
            catch (Exception e)
            {
                Log.Error("snapshot failed: " + e);
                if (Phase == Phase.Leaving) SendJoinPending();
                return;
            }
            Log.Info("snapshot (" + reason + "): " + (data.Length / 1024) + " KB in " + (int)((Time.realtimeSinceStartup - t0) * 1000) + " ms");
            _nextHostSave = Time.unscaledTime + Mathf.Max(1, Cfg.hostSaveMinutes) * 60f;
            _uploading = true;
            string room = RoomId;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int total = (data.Length + ChunkBytes - 1) / ChunkBytes;
                    Net.SendBulkLine("{\"t\":\"snap-begin\",\"room\":\"" + room + "\",\"size\":" + data.Length +
                                     ",\"sha\":\"" + Sha256(data) + "\",\"total\":" + total + "}");
                    var sb = new StringBuilder(ChunkBytes * 4 / 3 + 128);
                    for (int i = 0; i < total; i++)
                    {
                        int off = i * ChunkBytes, len = Math.Min(ChunkBytes, data.Length - off);
                        sb.Length = 0;
                        sb.Append("{\"t\":\"snap-chunk\",\"room\":\"").Append(room).Append("\",\"seq\":").Append(i)
                          .Append(",\"data\":\"").Append(Convert.ToBase64String(data, off, len)).Append("\"}");
                        Net.SendBulkLine(sb.ToString());
                    }
                    Net.SendBulkLine("{\"t\":\"snap-end\",\"room\":\"" + room + "\"}");
                }
                catch (Exception e) { Log.Error("upload: " + e.Message); }
                finally { _uploading = false; }
            });
            SendPreview();
        }

        /// <summary>Game is closing: if I'm the last one in my world, push its final state to the server (blocks up to ~8 s).</summary>
        public void SaveBeforeQuit()
        {
            if (!Online || !IsHost || !InWorld || OthersInRoom() > 0 || !WorldBoxApi.WorldReady) return;
            Log.Info("saving world to server before quitting...");
            UploadSnapshot("quit");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((_uploading || Net.BulkBytesQueued > 0) && Net.Connected && sw.ElapsedMilliseconds < 8000)
                Thread.Sleep(50);
            Thread.Sleep(200);
        }

        private void SendPreview()
        {
            _nextPreview = Time.unscaledTime + 90f;
            if (!Online || RoomId == null) return;
            byte[] png = WorldBoxApi.MakePreviewPng(160);
            var msg = new JObject
            {
                ["room"] = RoomId,
                ["stats"] = new JObject
                {
                    ["year"] = WorldBoxApi.Year(),
                    ["pop"] = WorldBoxApi.Population(),
                    ["w"] = MapBox.width,
                    ["h"] = MapBox.height,
                },
            };
            if (png != null)
            {
                msg["png"] = Convert.ToBase64String(png);
                SetPreviewTexture(RoomId, png);
            }
            else _nextPreview = Time.unscaledTime + 10f;   // map not rendered yet - retry soon
            Net.Send("preview", msg);
        }

        // ================================================================ lists

        private void ParsePlayers(JArray arr)
        {
            int othersBefore = OthersInRoom();
            Players.Clear();
            if (arr != null)
                foreach (JToken it in arr)
                {
                    if (!(it is JObject o)) continue;
                    Players.Add(new PlayerInfo
                    {
                        id = (string)o["id"], name = (string)o["name"] ?? "?", color = (string)o["color"] ?? "#ffffff",
                        room = (string)o["room"] ?? "", host = (bool?)o["host"] ?? false, synced = (bool?)o["synced"] ?? false,
                        game = (string)o["game"] ?? "",
                    });
                }
            foreach (var pl in Players)
                if (pl.id == MyId) { IsHost = pl.room == RoomId && RoomId != null && pl.host; }
            CoopMod.Instance?.Avatars.SyncWithPlayers();
            if (IsHost && OthersInRoom() > othersBefore) CoopMod.Instance?.Sync.ForceFull();
        }

        private void ParseRooms(JArray arr)
        {
            Rooms.Clear();
            RoomOrder.Clear();
            if (arr == null) return;
            foreach (JToken it in arr)
            {
                if (!(it is JObject o)) continue;
                var r = new RoomInfo
                {
                    id = (string)o["id"], name = (string)o["name"] ?? (string)o["id"], kind = (string)o["kind"] ?? "world",
                    owner = (string)o["owner"] ?? "", host = (string)o["host"] ?? "", players = (int?)o["players"] ?? 0,
                    size = (long?)o["size"] ?? 0, year = (int?)o["year"] ?? 0, pop = (int?)o["pop"] ?? 0,
                    w = (int?)o["w"] ?? 0, h = (int?)o["h"] ?? 0, version = (int?)o["version"] ?? 0,
                    hasWorld = (bool?)o["hasWorld"] ?? true,
                };
                if (o["names"] is JArray names) foreach (JToken n in names) r.names.Add((string)n);
                if (r.id == null) continue;
                Rooms[r.id] = r;
                RoomOrder.Add(r.id);
                if (r.id == RoomId) RoomName = r.name;
            }
        }

        private void OnPreview(string room, string b64)
        {
            if (room == null || string.IsNullOrEmpty(b64)) return;
            try { SetPreviewTexture(room, Convert.FromBase64String(b64)); }
            catch (Exception e) { Log.Warn("preview decode: " + e.Message); }
        }

        private void SetPreviewTexture(string room, byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            if (!ImageConversion.LoadImage(tex, png)) { UnityEngine.Object.Destroy(tex); return; }
            if (Previews.TryGetValue(room, out Texture2D old) && old != null) UnityEngine.Object.Destroy(old);
            Previews[room] = tex;
        }

        // ================================================================ helpers

        public void AddChat(string name, string text, bool system, string color = null)
        {
            Chat.Add(new ChatLine { name = name, text = text, system = system, color = color ?? "#ffffff", time = Time.unscaledTime });
            if (Chat.Count > 100) Chat.RemoveAt(0);
        }

        private void SetPhase(Phase p)
        {
            Phase = p;
            _phaseSince = Time.unscaledTime;
        }

        public static string Slug(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 40) break;
            }
            string r = sb.ToString().Trim('-');
            return r.Length == 0 ? "player" : r;
        }

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}
