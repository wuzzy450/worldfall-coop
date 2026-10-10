using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coopfall.Net
{
    /// <summary>
    /// The co-op relay, built into the mod: the same rooms, hosts, stored worlds and lockstep
    /// lanes as the WorldfallRooms plugin (server/cuberite/Plugins/WorldfallRooms/Main.lua, protocol
    /// v4), so any player can host a server from the co-op menu without downloading anything.
    /// One relay thread handles every event in order (like Cuberite's single thread); each
    /// connection has a reader thread and a writer thread with its own send queue.
    /// Stored worlds live in persistentDataPath/coopfall/relay.
    /// </summary>
    public sealed class EmbeddedRelay
    {
        public const int ProtocolVersion = 4;

        private const int MaxSnapshotBytes = 64 * 1024 * 1024, MaxRooms = 32, MaxLineBytes = 2 * 1024 * 1024, MaxPreviewBytes = 256 * 1024, MaxMods = 200;
        private const long TimeoutMs = 45000, SnapTimeoutMs = 90000, ApprovalMs = 60000, KickMs = 600000;

        private sealed class Client
        {
            public string Addr, Id, Name, Color, GameVersion, ApprovedFor, DropWhy;
            public List<JObject> Mods;
            public Room Room;
            public bool Synced, WaitingSnap, Spectator, Removed, PingDirty;
            public long LastSeenMs;
            public int Ping = -1;
            public PendingJoin Pending;
            public Stream Stream;
            public readonly BlockingCollection<string> Out = new BlockingCollection<string>(new ConcurrentQueue<string>());
        }

        private sealed class PendingJoin { public string Room; public JObject Msg; public long At; }

        private sealed class Snap { public List<string> Chunks; public long Size; public string Sha; public int Version; public long Time; }

        private sealed class Upload { public Client From; public List<string> Chunks = new List<string>(); public long Size; public string Sha; public int Total; public long B64; public HashSet<Client> Followers; }

        private sealed class Room
        {
            public string Id, Name, Kind, Owner, LastHost, Preview, Wle, WleSha;
            public readonly List<Client> Members = new List<Client>();
            public Client Host;
            public Snap Snap;
            public Upload Pending;
            public JObject Stats;
            public long? SnapRequestedAt;
            public JObject Settings = DefaultSettings();
            public JArray ModsList;
            public readonly Dictionary<string, long> Kicked = new Dictionary<string, long>();
        }

        private readonly int _port;
        private readonly string _dir;
        private readonly BlockingCollection<Action> _events = new BlockingCollection<Action>(new ConcurrentQueue<Action>());
        private readonly List<Client> _clients = new List<Client>();
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Random _rnd = new System.Random();
        private TcpListener _listener;
        private Thread _loop, _accept;
        private volatile bool _stop;
        private int _idCounter;
        private long _nextPingBroadcast;

        public bool Running { get; private set; }
        public int Port => _port;
        public string Error { get; private set; }
        /// <summary>Players connected right now (for the menu).</summary>
        public int Players { get; private set; }

        public EmbeddedRelay(int port, string dataDir)
        {
            _port = port;
            _dir = dataDir;
        }

        private long Now => _clock.ElapsedMilliseconds;
        private static void Info(string s) => Log.Info("relay: " + s);
        private static void Warn(string s) => Log.Warn("relay: " + s);

        // ================================================================ start / stop

        public bool Start()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                LoadRooms();
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Error = e is SocketException ? "port " + _port + " is in use (another server running?)" : e.Message;
                Warn("could not start: " + e.Message);
                try { _listener?.Stop(); } catch { }
                return false;
            }
            Running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "Coopfall relay" };
            _loop.Start();
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Coopfall relay accept" };
            _accept.Start();
            Info("listening on port " + _port + " (TCP), worlds in " + _dir);
            return true;
        }

        public void Stop()
        {
            if (!Running) return;
            Running = false;
            _stop = true;
            try { _listener.Stop(); } catch { }
            _events.Add(() =>
            {
                foreach (Client c in _clients.ToArray()) RemoveClient(c, true, "server stopping");
            });
            _events.CompleteAdding();
            Info("stopped");
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_stop) return; Thread.Sleep(100); continue; }
                tcp.NoDelay = true;
                AddStream(tcp.GetStream(), tcp.Client.RemoteEndPoint?.ToString() ?? "?");
            }
        }

        /// <summary>A new player link (TCP, or a Steam connection wrapped as a stream).</summary>
        public void AddStream(Stream s, string addr)
        {
            if (!Running) { try { s.Close(); } catch { } return; }
            var c = new Client { Stream = s, Addr = addr };
            _events.Add(() => { c.LastSeenMs = Now; _clients.Add(c); });
            new Thread(() => ReadLoop(c)) { IsBackground = true, Name = "Coopfall relay in" }.Start();
            new Thread(() => WriteLoop(c)) { IsBackground = true, Name = "Coopfall relay out" }.Start();
        }

        private void ReadLoop(Client c)
        {
            string why = "closed";
            try
            {
                Stream s = c.Stream;
                var buf = new byte[65536];
                var line = new MemoryStream();
                while (!_stop)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    int start = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        line.Write(buf, start, i - start);
                        start = i + 1;
                        string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                        line.SetLength(0);
                        _events.Add(() => { if (!c.Removed) { c.LastSeenMs = Now; ProcessLine(c, text); } });
                    }
                    line.Write(buf, start, n - start);
                    if (line.Length > MaxLineBytes)
                    {
                        _events.Add(() => { SendError(c, "message too long"); RemoveClient(c, true, "line too long"); });
                        return;
                    }
                }
            }
            catch (Exception e) { why = "link error " + e.Message; }
            if (!_stop) _events.Add(() => RemoveClient(c, false, why));
        }

        private static void WriteLoop(Client c)
        {
            try
            {
                Stream s = c.Stream;
                foreach (string line in c.Out.GetConsumingEnumerable())
                {
                    byte[] b = Encoding.UTF8.GetBytes(line + "\n");
                    s.Write(b, 0, b.Length);
                }
            }
            catch { }
            try { c.Stream.Close(); } catch { }
        }

        private void Loop()
        {
            long nextSweep = 1000;
            while (true)
            {
                Action a;
                try { if (!_events.TryTake(out a, 200)) a = null; }
                catch (InvalidOperationException) { break; }
                if (a == null && _events.IsCompleted) break;
                try { a?.Invoke(); }
                catch (Exception e) { Warn("event failed: " + e); }
                if (Now >= nextSweep)
                {
                    nextSweep = Now + 1000;
                    try { Sweep(); } catch (Exception e) { Warn("sweep failed: " + e.Message); }
                }
            }
        }

        // ================================================================ helpers

        private static string Enc(JToken t) => t.ToString(Formatting.None);

        private static string JStr(string s) => JsonConvert.ToString(s ?? "");

        private static string SanitizeRoomId(object id)
        {
            string s = id as string;
            if (s == null || s.Length < 1 || s.Length > 48 || !Regex.IsMatch(s, "^[A-Za-z0-9_-]+$")) return null;
            return s.ToLowerInvariant();
        }

        private static string SanitizeText(JToken t, int max, string def)
        {
            if (t == null || t.Type != JTokenType.String) return def;
            string s = Regex.Replace((string)t, "\\p{Cc}", "").Trim();
            if (s.Length == 0) return def;
            return s.Length > max ? s.Substring(0, max) : s;
        }

        private static string SanitizeColor(JToken t)
        {
            string s = t?.Type == JTokenType.String ? (string)t : null;
            return s != null && Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$") ? s : "#ffcc33";
        }

        private static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || (t.Type == JTokenType.Float && !double.IsNaN((double)t)));
        private static bool IsTrue(JToken t) => t != null && t.Type == JTokenType.Boolean && (bool)t;
        private static bool IsB64(string s) => s != null && s.Length > 0 && Regex.IsMatch(s, "^[A-Za-z0-9+/=]+$");

        private static JObject DefaultSettings() => new JObject
        {
            ["password"] = "", ["locked"] = false, ["approval"] = false, ["maxPlayers"] = 0, ["spectators"] = true,
            ["guestPowers"] = "all", ["blocked"] = new JArray(), ["guestSpeed"] = true, ["allowExtraMods"] = false, ["everyoneAdmin"] = true,
        };

        private void SendLine(Client c, string line)
        {
            if (c.Removed || c.Out.IsAddingCompleted) return;
            try { c.Out.Add(line); } catch { }
        }

        private void Send(Client c, JObject o) => SendLine(c, Enc(o));
        private void SendError(Client c, string msg) => Send(c, new JObject { ["t"] = "error", ["msg"] = msg });

        private IEnumerable<Client> Players_ => _clients.Where(c => c.Id != null && !c.Removed).ToList();

        // ================================================================ lists

        private JArray PlayerList()
        {
            var a = new JArray();
            foreach (Client c in Players_)
                a.Add(new JObject
                {
                    ["id"] = c.Id, ["name"] = c.Name, ["color"] = c.Color, ["room"] = c.Room?.Id ?? "", ["host"] = c.Room != null && c.Room.Host == c,
                    ["synced"] = c.Synced, ["game"] = c.GameVersion ?? "", ["ping"] = c.Ping, ["spectator"] = c.Spectator, ["mods"] = c.Mods?.Count ?? 0,
                });
            return a;
        }

        private static JObject PublicSettings(Room r)
        {
            JObject s = r.Settings;
            return new JObject
            {
                ["hasPassword"] = (string)s["password"] != "", ["locked"] = s["locked"], ["approval"] = s["approval"], ["maxPlayers"] = s["maxPlayers"],
                ["spectators"] = s["spectators"], ["guestPowers"] = s["guestPowers"], ["blocked"] = s["blocked"], ["guestSpeed"] = s["guestSpeed"],
                ["allowExtraMods"] = s["allowExtraMods"], ["everyoneAdmin"] = s["everyoneAdmin"],
            };
        }

        private static JObject RoomInfo(Room r)
        {
            var names = new JArray();
            int watching = 0;
            foreach (Client m in r.Members) { names.Add(m.Name); if (m.Spectator) watching++; }
            JObject st = r.Stats ?? new JObject();
            return new JObject
            {
                ["id"] = r.Id, ["name"] = r.Name, ["kind"] = r.Kind, ["owner"] = r.Owner ?? "", ["host"] = r.Host?.Name ?? "", ["players"] = r.Members.Count,
                ["names"] = names, ["size"] = r.Snap?.Size ?? 0, ["version"] = r.Snap?.Version ?? 0, ["updated"] = r.Snap?.Time ?? 0,
                ["hasWorld"] = r.Snap != null || r.Host != null, ["hasPreview"] = r.Preview != null,
                ["year"] = st["year"] ?? 0, ["pop"] = st["pop"] ?? 0, ["w"] = st["w"] ?? 0, ["h"] = st["h"] ?? 0,
                ["spectating"] = watching, ["settings"] = PublicSettings(r), ["mods"] = r.ModsList?.Count ?? 0,
            };
        }

        private JArray RoomList()
        {
            var l = _rooms.Values.Select(RoomInfo).ToList();
            l.Sort((a, b) =>
            {
                bool sa = (string)a["id"] == "shared", sb = (string)b["id"] == "shared";
                if (sa != sb) return sa ? -1 : 1;
                return string.CompareOrdinal((string)a["name"], (string)b["name"]);
            });
            return new JArray(l);
        }

        private void BroadcastPlayers() { string line = Enc(new JObject { ["t"] = "players", ["players"] = PlayerList() }); foreach (Client c in Players_) SendLine(c, line); Players = _clients.Count(c => c.Id != null); }
        private void BroadcastRooms() { string line = Enc(new JObject { ["t"] = "rooms", ["rooms"] = RoomList() }); foreach (Client c in Players_) SendLine(c, line); }

        private void Toast(string text, Client except = null)
        {
            string line = Enc(new JObject { ["t"] = "notice", ["text"] = text });
            foreach (Client c in Players_) if (c != except) SendLine(c, line);
        }

        // ================================================================ persistence

        private string RoomDir(string id) => Path.Combine(_dir, id);

        private void SaveRoomIndex()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, "rooms.json"), Enc(new JObject { ["rooms"] = new JArray(_rooms.Values.Where(r => r.Snap != null).Select(r => r.Id)) }));
            }
            catch (Exception e) { Warn("cannot write rooms.json: " + e.Message); }
        }

        private void SaveRoomMeta(Room r)
        {
            try
            {
                Directory.CreateDirectory(RoomDir(r.Id));
                File.WriteAllText(Path.Combine(RoomDir(r.Id), "meta.json"), Enc(new JObject
                {
                    ["id"] = r.Id, ["name"] = r.Name, ["kind"] = r.Kind, ["owner"] = r.Owner ?? "", ["size"] = r.Snap?.Size ?? 0, ["sha"] = r.Snap?.Sha ?? "",
                    ["total"] = r.Snap?.Chunks.Count ?? 0, ["version"] = r.Snap?.Version ?? 0, ["time"] = r.Snap?.Time ?? 0, ["stats"] = r.Stats ?? new JObject(),
                    ["settings"] = r.Settings, ["mods"] = r.ModsList,
                }));
            }
            catch (Exception e) { Warn("cannot write meta of " + r.Id + ": " + e.Message); }
        }

        private void SaveRoomSnapshot(Room r)
        {
            if (r.Snap == null) return;
            try
            {
                Directory.CreateDirectory(RoomDir(r.Id));
                string tmp = Path.Combine(RoomDir(r.Id), "snapshot.b64.tmp"), final = Path.Combine(RoomDir(r.Id), "snapshot.b64");
                File.WriteAllLines(tmp, r.Snap.Chunks);
                if (File.Exists(final)) File.Delete(final);
                File.Move(tmp, final);
            }
            catch (Exception e) { Warn("cannot write the snapshot of " + r.Id + ": " + e.Message); }
            SaveRoomMeta(r);
            SaveRoomIndex();
        }

        private void SaveRoomPreview(Room r)
        {
            if (r.Preview == null) return;
            try { Directory.CreateDirectory(RoomDir(r.Id)); File.WriteAllText(Path.Combine(RoomDir(r.Id), "preview.b64"), r.Preview); }
            catch { }
            SaveRoomMeta(r);
        }

        private static Room NewRoom(string id, string name, string kind, string owner) => new Room { Id = id, Name = name, Kind = kind, Owner = owner };

        private void LoadRooms()
        {
            string idx = Path.Combine(_dir, "rooms.json");
            if (!File.Exists(idx)) return;
            try
            {
                foreach (JToken t in (JArray)JObject.Parse(File.ReadAllText(idx))["rooms"])
                {
                    string id = SanitizeRoomId((string)t);
                    string metaPath = id == null ? null : Path.Combine(RoomDir(id), "meta.json");
                    if (metaPath == null || !File.Exists(metaPath)) continue;
                    JObject meta = JObject.Parse(File.ReadAllText(metaPath));
                    Room r = NewRoom(id, SanitizeText(meta["name"], 64, id), (string)meta["kind"] ?? "world", (string)meta["owner"] ?? "");
                    r.Stats = meta["stats"] as JObject;
                    if (meta["settings"] is JObject ms)
                        foreach (KeyValuePair<string, JToken> kv in ms)
                            if (r.Settings[kv.Key] != null && r.Settings[kv.Key].Type == kv.Value.Type) r.Settings[kv.Key] = kv.Value;
                    r.ModsList = meta["mods"] as JArray;
                    string snapPath = Path.Combine(RoomDir(id), "snapshot.b64");
                    var chunks = File.Exists(snapPath) ? File.ReadAllLines(snapPath).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList() : new List<string>();
                    if (chunks.Count > 0 && chunks.Count == ((int?)meta["total"] ?? -1))
                    {
                        r.Snap = new Snap { Chunks = chunks, Size = (long?)meta["size"] ?? 0, Sha = (string)meta["sha"] ?? "", Version = (int?)meta["version"] ?? 1, Time = (long?)meta["time"] ?? 0 };
                        string prev = Path.Combine(RoomDir(id), "preview.b64");
                        r.Preview = File.Exists(prev) ? File.ReadAllText(prev) : null;
                        if (r.Preview != null && r.Preview.Length == 0) r.Preview = null;
                        _rooms[id] = r;
                    }
                    else Warn("room " + id + ": snapshot missing or incomplete on disk, skipped");
                }
            }
            catch (Exception e) { Warn("couldn't read the stored worlds: " + e.Message); }
            Info("loaded " + _rooms.Count + " stored world(s)");
        }

        private void DeleteRoomFiles(string id)
        {
            try { if (Directory.Exists(RoomDir(id))) Directory.Delete(RoomDir(id), true); } catch { }
        }

        // ================================================================ snapshots

        private bool SendSnapshot(Client c, Room r)
        {
            Snap s = r.Snap;
            if (s == null) return false;
            string rid = JStr(r.Id);
            if (r.Wle != null && r.WleSha == s.Sha) SendLine(c, r.Wle);
            SendLine(c, "{\"t\":\"snap-begin\",\"room\":" + rid + ",\"size\":" + s.Size + ",\"sha\":" + JStr(s.Sha) + ",\"total\":" + s.Chunks.Count + ",\"version\":" + s.Version + "}");
            for (int i = 0; i < s.Chunks.Count; i++) SendLine(c, "{\"t\":\"snap-chunk\",\"room\":" + rid + ",\"seq\":" + i + ",\"data\":\"" + s.Chunks[i] + "\"}");
            SendLine(c, "{\"t\":\"snap-end\",\"room\":" + rid + "}");
            c.WaitingSnap = false;
            c.Synced = true;
            return true;
        }

        private void RequestSnapshot(Room r, string reason)
        {
            if (r.Host == null || r.Pending != null) return;
            if (r.SnapRequestedAt.HasValue && Now - r.SnapRequestedAt.Value < SnapTimeoutMs) return;
            r.SnapRequestedAt = Now;
            Send(r.Host, new JObject { ["t"] = "snap-request", ["room"] = r.Id, ["reason"] = reason ?? "join" });
        }

        private static bool AnyoneWaiting(Room r) => r.Members.Any(m => m.WaitingSnap);

        private void ServeWaiting(Room r)
        {
            foreach (Client m in r.Members.ToArray()) if (m.WaitingSnap) SendSnapshot(m, r);
        }

        // ================================================================ membership

        private void SetHost(Room r, Client c)
        {
            r.Host = c;
            r.SnapRequestedAt = null;
            if (c != null)
            {
                Send(c, new JObject { ["t"] = "role", ["room"] = r.Id, ["role"] = "host" });
                Info(c.Name + " now hosts room " + r.Id);
            }
        }

        private void LeaveRoom(Client c)
        {
            Room r = c.Room;
            if (r == null) return;
            r.Members.Remove(c);
            c.Room = null;
            c.Synced = false;
            c.WaitingSnap = false;
            if (r.Pending != null && r.Pending.From == c) { r.Pending = null; r.SnapRequestedAt = null; }
            if (r.Host == c)
            {
                r.Host = null;
                r.LastHost = c.Name;
                for (int pass = 1; pass <= 2; pass++)
                    foreach (Client m in r.Members.ToArray())
                        if (r.Host == null && m.Synced && !m.WaitingSnap && (pass == 2 || !m.Spectator)) SetHost(r, m);
                if (r.Host == null && r.Members.Count > 0)
                {
                    if (r.Snap != null) { ServeWaiting(r); SetHost(r, r.Members[0]); }
                    else
                    {
                        foreach (Client m in r.Members.ToArray()) { SendError(m, "the host left before sending the world; please travel again"); m.Room = null; m.WaitingSnap = false; }
                        r.Members.Clear();
                    }
                }
                if (r.Host != null && AnyoneWaiting(r)) RequestSnapshot(r, "join");
            }
            if (r.Members.Count == 0 && r.Snap == null) _rooms.Remove(r.Id);
        }

        private static string HashPassword(string roomId, string pw)
        {
            using (SHA1 sha = SHA1.Create())
                return "sha1:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("wfrooms|" + roomId + "|" + pw))).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsRealAdmin(Client c, Room r)
        {
            if (!string.IsNullOrEmpty(r.Owner)) return string.Equals(r.Owner, c.Name ?? "", StringComparison.OrdinalIgnoreCase);
            return r.Host == c;
        }

        private static bool IsAdmin(Client c, Room r) => IsRealAdmin(c, r) || ((bool)r.Settings["everyoneAdmin"] && c.Room == r && !c.Spectator);

        private Client FindAdmin(Room r)
        {
            if (string.IsNullOrEmpty(r.Owner)) return r.Host;
            return Players_.LastOrDefault(c => string.Equals(c.Name, r.Owner, StringComparison.OrdinalIgnoreCase));
        }

        private Client FindPlayer(string id) => Players_.LastOrDefault(c => c.Id == id);

        private static JObject ModDiff(JArray need, List<JObject> have, bool allowExtra)
        {
            if (need == null) return null;
            var n = new Dictionary<string, JObject>();
            var h = new Dictionary<string, JObject>();
            foreach (JObject m in need.OfType<JObject>()) n[((string)m["id"] ?? "").ToLowerInvariant()] = m;
            foreach (JObject m in have ?? new List<JObject>()) h[((string)m["id"] ?? "").ToLowerInvariant()] = m;
            var missing = new JArray(); var extra = new JArray(); var different = new JArray();
            foreach (KeyValuePair<string, JObject> kv in n)
            {
                if (!h.TryGetValue(kv.Key, out JObject hm)) missing.Add((string)kv.Value["id"] + " " + (string)kv.Value["ver"]);
                else if ((string)hm["ver"] != (string)kv.Value["ver"]) different.Add((string)kv.Value["id"] + " (needs " + (string)kv.Value["ver"] + ", you have " + (string)hm["ver"] + ")");
            }
            if (!allowExtra) foreach (KeyValuePair<string, JObject> kv in h) if (!n.ContainsKey(kv.Key)) extra.Add((string)kv.Value["id"] + " " + (string)kv.Value["ver"]);
            if (missing.Count + extra.Count + different.Count == 0) return null;
            return new JObject { ["missing"] = missing, ["extra"] = extra, ["different"] = different };
        }

        private static List<JObject> SanitizeMods(JToken t)
        {
            var o = new List<JObject>();
            if (!(t is JArray a)) return o;
            foreach (JToken m in a)
            {
                if (!(m is JObject mo) || o.Count >= MaxMods) continue;
                string id = SanitizeText(mo["id"], 64, null);
                if (id != null) o.Add(new JObject { ["id"] = id, ["ver"] = SanitizeText(mo["ver"], 64, "?") });
            }
            return o;
        }

        // ================================================================ handlers

        private void Hello(Client c, JObject msg)
        {
            if (c.Id != null) return;
            if ((int?)msg["version"] != ProtocolVersion)
            {
                SendError(c, "protocol version mismatch: server speaks v" + ProtocolVersion + ", your Coopfall speaks v" + msg["version"] + " - update the mod or the server plugin");
                return;
            }
            string name = SanitizeText(msg["name"], 24, "Player"), baseName = name;
            for (int n = 2; Players_.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = baseName + n;
            _idCounter++;
            c.Id = "p" + _idCounter + "-" + _rnd.Next(0, 65536).ToString("x4");
            c.Name = name;
            c.Color = SanitizeColor(msg["color"]);
            c.GameVersion = SanitizeText(msg["game"], 32, "");
            c.Mods = SanitizeMods(msg["mods"]);
            Send(c, new JObject { ["t"] = "welcome", ["yourId"] = c.Id, ["name"] = name, ["rooms"] = RoomList(), ["players"] = PlayerList(), ["server"] = "Coopfall relay v" + ProtocolVersion });
            foreach (Room r in _rooms.Values) if (r.Preview != null) SendLine(c, "{\"t\":\"preview\",\"room\":" + JStr(r.Id) + ",\"png\":\"" + r.Preview + "\"}");
            Info("hello: " + name + " (" + c.Id + ") from " + c.Addr + ", game " + c.GameVersion);
            BroadcastPlayers();
            Toast(name + " connected", c);
        }

        private void Join(Client c, JObject msg)
        {
            if (c.Id == null) { SendError(c, "join before hello"); return; }
            string id = SanitizeRoomId((string)msg["room"]);
            if (id == null) { SendError(c, "invalid room id"); return; }
            bool seed = IsTrue(msg["seed"]), preferLocal = IsTrue(msg["preferLocal"]), spectate = IsTrue(msg["spectate"]);
            if (c.Room != null && c.Room.Id == id)
            {
                Send(c, new JObject { ["t"] = "joined", ["room"] = id, ["name"] = c.Room.Name, ["role"] = c.Room.Host == c ? "host" : "guest", ["load"] = false });
                return;
            }
            _rooms.TryGetValue(id, out Room room);
            if (room == null && !seed) { SendError(c, "that world no longer exists"); return; }
            if (room == null && _rooms.Count >= MaxRooms) { SendError(c, "the server has too many worlds (" + MaxRooms + "); delete one first"); return; }
            if (room != null && !IsAdmin(c, room))
            {
                JObject st = room.Settings;
                void Refuse(string code, string text, JObject extra = null)
                {
                    var m = new JObject { ["t"] = "error", ["msg"] = text, ["code"] = code, ["room"] = id };
                    if (extra != null) foreach (KeyValuePair<string, JToken> kv in extra) m[kv.Key] = kv.Value;
                    Send(c, m);
                }
                if (room.Kicked.TryGetValue(c.Name.ToLowerInvariant(), out long until) && until > Now) { Refuse("kicked", "you were removed from " + room.Name + "; try again in a few minutes"); return; }
                if ((bool)st["locked"]) { Refuse("locked", room.Name + " is locked"); return; }
                string pw = (string)st["password"];
                if (pw != "" && (msg["password"]?.Type != JTokenType.String || HashPassword(id, (string)msg["password"]) != pw))
                {
                    Refuse("password", msg["password"] != null ? "wrong password for " + room.Name : room.Name + " needs a password");
                    return;
                }
                if (spectate)
                {
                    if (!(bool)st["spectators"]) { Refuse("spectators", room.Name + " doesn't allow spectators"); return; }
                    if (room.Host == null) { Refuse("empty", "nobody is playing in " + room.Name + " right now"); return; }
                }
                else if ((int)st["maxPlayers"] > 0 && room.Members.Count(m => !m.Spectator) >= (int)st["maxPlayers"]) { Refuse("full", room.Name + " is full (" + (int)st["maxPlayers"] + " players)"); return; }
                JObject diff = ModDiff(room.ModsList, c.Mods, (bool)st["allowExtraMods"]);
                if (diff != null) { Refuse("mods", "your mods don't match " + room.Name + "'s", new JObject { ["mods"] = diff }); return; }
                if ((bool)st["approval"] && c.ApprovedFor != id)
                {
                    Client admin = FindAdmin(room);
                    if (admin == null || admin == c) { Refuse("approval", room.Name + " needs its owner's approval, and they aren't online"); return; }
                    c.Pending = new PendingJoin { Room = id, Msg = msg, At = Now };
                    Send(admin, new JObject { ["t"] = "join-request", ["room"] = id, ["id"] = c.Id, ["name"] = c.Name, ["spectate"] = spectate });
                    Send(c, new JObject { ["t"] = "waiting", ["room"] = id, ["admin"] = admin.Name });
                    return;
                }
            }
            c.ApprovedFor = null;
            c.Pending = null;
            LeaveRoom(c);
            c.Spectator = spectate;
            if (room == null)
            {
                string kind = "world", owner = c.Name, defName = c.Name + "'s world";
                if (id == "shared") { kind = "shared"; owner = ""; defName = "Shared World"; }
                else if (id.StartsWith("home-", StringComparison.Ordinal)) kind = "home";
                room = NewRoom(id, SanitizeText(msg["name"], 64, defName), kind, owner);
                _rooms[id] = room;
            }
            room.Members.Add(c);
            c.Room = room;
            c.Synced = false;
            c.WaitingSnap = false;
            bool isOwner = !string.IsNullOrEmpty(room.Owner) && string.Equals(room.Owner, c.Name, StringComparison.OrdinalIgnoreCase);
            if (room.Host != null && room.Host != c)
            {
                c.WaitingSnap = true;
                Send(c, new JObject { ["t"] = "joined", ["room"] = id, ["name"] = room.Name, ["role"] = "guest", ["load"] = true, ["host"] = room.Host.Name });
                RequestSnapshot(room, "join");
            }
            else if (seed && (room.Snap == null || (preferLocal && (isOwner || room.Kind == "shared" || (IsTrue(msg["resume"]) && room.LastHost != null && string.Equals(room.LastHost, c.Name, StringComparison.OrdinalIgnoreCase))))))
            {
                room.Host = c;
                room.ModsList = new JArray(c.Mods);
                c.Synced = true;
                Send(c, new JObject { ["t"] = "joined", ["room"] = id, ["name"] = room.Name, ["role"] = "host", ["load"] = false });
                RequestSnapshot(room, "seed");
            }
            else if (room.Snap != null)
            {
                room.Host = c;
                if (room.ModsList == null) room.ModsList = new JArray(c.Mods);
                Send(c, new JObject { ["t"] = "joined", ["room"] = id, ["name"] = room.Name, ["role"] = "host", ["load"] = true });
                SendSnapshot(c, room);
            }
            else
            {
                LeaveRoom(c);
                SendError(c, "that world has no saved copy yet");
                return;
            }
            Info(c.Name + " joined room " + id + " as " + (room.Host == c ? "host" : "guest") + (spectate ? " (spectating)" : ""));
            BroadcastPlayers();
            BroadcastRooms();
        }

        private void Resync(Client c, JObject msg)
        {
            Room r = c.Room;
            if (r == null || r.Host == c) return;
            c.WaitingSnap = true;
            string sha = msg["sha"]?.Type == JTokenType.String ? (string)msg["sha"] : null;
            if (sha != null && r.Snap != null && r.Snap.Sha == sha) { SendSnapshot(c, r); return; }
            if (sha != null && r.Pending != null && r.Pending.Sha == sha)
            {
                Upload p = r.Pending;
                string rid = JStr(r.Id);
                if (r.Wle != null && r.WleSha == p.Sha) SendLine(c, r.Wle);
                SendLine(c, "{\"t\":\"snap-begin\",\"room\":" + rid + ",\"size\":" + p.Size + ",\"sha\":" + JStr(p.Sha) + ",\"total\":" + p.Total + "}");
                for (int i = 0; i < p.Chunks.Count; i++) SendLine(c, "{\"t\":\"snap-chunk\",\"room\":" + rid + ",\"seq\":" + i + ",\"data\":\"" + p.Chunks[i] + "\"}");
                (p.Followers = p.Followers ?? new HashSet<Client>()).Add(c);
                c.WaitingSnap = false;
                return;
            }
            if (sha != null && r.Pending != null) return;
            if (r.Host != null) RequestSnapshot(r, "resync");
            else if (r.Snap != null) ServeWaiting(r);
        }

        private void SnapBegin(Client c, JObject msg)
        {
            Room r = c.Room;
            if (r == null || r.Host != c || SanitizeRoomId((string)msg["room"]) != r.Id) { SendError(c, "only the host of a world can upload it"); return; }
            if (!IsNumber(msg["size"]) || (double)msg["size"] <= 0 || (double)msg["size"] > MaxSnapshotBytes) { r.Pending = null; SendError(c, "world snapshot too large (max " + MaxSnapshotBytes / 1048576 + " MB)"); return; }
            if (!IsNumber(msg["total"]) || (double)msg["total"] < 1 || (double)msg["total"] > 4096) { r.Pending = null; SendError(c, "invalid chunk count"); return; }
            r.Pending = new Upload { From = c, Size = (long)(double)msg["size"], Sha = SanitizeText(msg["sha"], 64, ""), Total = (int)(double)msg["total"] };
        }

        private void SnapChunk(Client c, JObject msg)
        {
            Room r = c.Room;
            Upload p = r?.Pending;
            if (p == null || p.From != c) return;
            string data = msg["data"]?.Type == JTokenType.String ? (string)msg["data"] : null;
            if (!IsNumber(msg["seq"]) || (int)(double)msg["seq"] != p.Chunks.Count || !IsB64(data)) { r.Pending = null; SendError(c, "bad snapshot chunk " + msg["seq"]); return; }
            p.B64 += data.Length;
            if (p.B64 * 3 / 4 > MaxSnapshotBytes + 4) { r.Pending = null; SendError(c, "snapshot exceeds the size limit"); return; }
            p.Chunks.Add(data);
            if (p.Followers != null)
            {
                string line = "{\"t\":\"snap-chunk\",\"room\":" + JStr(r.Id) + ",\"seq\":" + (p.Chunks.Count - 1) + ",\"data\":\"" + data + "\"}";
                foreach (Client f in p.Followers) SendLine(f, line);
            }
        }

        private void SnapEnd(Client c, JObject msg)
        {
            Room r = c.Room;
            Upload p = r?.Pending;
            if (p == null || p.From != c) return;
            r.Pending = null;
            r.SnapRequestedAt = null;
            if (p.Followers != null)
                foreach (Client f in p.Followers)
                {
                    if (p.Chunks.Count == p.Total) { SendLine(f, "{\"t\":\"snap-end\",\"room\":" + JStr(r.Id) + "}"); f.Synced = true; }
                    else f.WaitingSnap = true;
                }
            if (p.Chunks.Count != p.Total) { SendError(c, "snapshot incomplete (" + p.Chunks.Count + "/" + p.Total + " chunks)"); return; }
            int version = (r.Snap?.Version ?? 0) + 1;
            r.Snap = new Snap { Chunks = p.Chunks, Size = p.Size, Sha = p.Sha, Version = version, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            Info("room " + r.Id + ": snapshot v" + version + " from " + c.Name + " (" + p.Size / 1024 + " KB)");
            ServeWaiting(r);
            SaveRoomSnapshot(r);
            Send(c, new JObject { ["t"] = "snap-stored", ["room"] = r.Id, ["version"] = version });
            BroadcastPlayers();
            BroadcastRooms();
        }

        private void Preview(Client c, JObject msg)
        {
            Room r = c.Room;
            if (r == null || r.Host != c) return;
            if (msg["stats"] is JObject s)
                r.Stats = new JObject { ["year"] = IsNumber(s["year"]) ? s["year"] : 0, ["pop"] = IsNumber(s["pop"]) ? s["pop"] : 0, ["w"] = IsNumber(s["w"]) ? s["w"] : 0, ["h"] = IsNumber(s["h"]) ? s["h"] : 0 };
            string png = msg["png"]?.Type == JTokenType.String ? (string)msg["png"] : null;
            if (png != null && png.Length <= MaxPreviewBytes && IsB64(png))
            {
                r.Preview = png;
                string line = "{\"t\":\"preview\",\"room\":" + JStr(r.Id) + ",\"png\":\"" + png + "\"}";
                foreach (Client x in Players_) if (x != c) SendLine(x, line);
                SaveRoomPreview(r);
            }
            BroadcastRooms();
        }

        private void RenameRoom(Client c, JObject msg)
        {
            _rooms.TryGetValue(SanitizeRoomId((string)msg["room"]) ?? "", out Room r);
            if (r == null || (!string.IsNullOrEmpty(r.Owner) && !string.Equals(r.Owner, c.Name ?? "", StringComparison.OrdinalIgnoreCase))) { SendError(c, "only the owner can rename that world"); return; }
            r.Name = SanitizeText(msg["name"], 64, r.Name);
            SaveRoomMeta(r);
            BroadcastRooms();
        }

        private void DeleteRoom(Client c, JObject msg)
        {
            string id = SanitizeRoomId((string)msg["room"]) ?? "";
            if (!_rooms.TryGetValue(id, out Room r)) return;
            if (string.IsNullOrEmpty(r.Owner) || !string.Equals(r.Owner, c.Name ?? "", StringComparison.OrdinalIgnoreCase)) { SendError(c, "only the owner can delete that world"); return; }
            if (r.Members.Count > 1 || (r.Members.Count == 1 && r.Members[0] != c)) { SendError(c, "someone is still in that world"); return; }
            if (c.Room == r) LeaveRoom(c);
            _rooms.Remove(id);
            DeleteRoomFiles(id);
            SaveRoomIndex();
            Info(c.Name + " deleted room " + id);
            BroadcastPlayers();
            BroadcastRooms();
        }

        private void RoomSettings(Client c, JObject msg)
        {
            Room r = c.Room;
            if (r == null || !IsAdmin(c, r)) { SendError(c, "only the world's owner (or the shared world's host) can change its settings"); return; }
            JObject st = r.Settings;
            if (msg["password"]?.Type == JTokenType.String)
            {
                string pw = SanitizeText(msg["password"], 32, "");
                st["password"] = pw == "" ? "" : HashPassword(r.Id, pw);
            }
            foreach (string k in new[] { "locked", "approval", "spectators", "guestSpeed", "allowExtraMods" })
                if (msg[k]?.Type == JTokenType.Boolean) st[k] = msg[k];
            if (msg["everyoneAdmin"]?.Type == JTokenType.Boolean && IsRealAdmin(c, r)) st["everyoneAdmin"] = msg["everyoneAdmin"];
            if (IsNumber(msg["maxPlayers"])) st["maxPlayers"] = Math.Max(0, Math.Min(64, (int)Math.Floor((double)msg["maxPlayers"])));
            string gp = (string)msg["guestPowers"];
            if (gp == "all" || gp == "safe" || gp == "none") st["guestPowers"] = gp;
            if (msg["blocked"] is JArray bl)
            {
                var list = new JArray();
                foreach (JToken p in bl) { string pid = SanitizeText(p, 48, null); if (pid != null && list.Count < 200) list.Add(pid); }
                st["blocked"] = list;
            }
            SaveRoomMeta(r);
            Info(c.Name + " changed the settings of " + r.Id);
            BroadcastRooms();
        }

        private void Approve(Client c, JObject msg)
        {
            Client who = FindPlayer((string)msg["id"]);
            PendingJoin pj = who?.Pending;
            Room r = null;
            if (pj != null) _rooms.TryGetValue(pj.Room, out r);
            if (r == null || !IsAdmin(c, r)) return;
            who.Pending = null;
            if (IsTrue(msg["ok"])) { who.ApprovedFor = pj.Room; Join(who, pj.Msg); }
            else Send(who, new JObject { ["t"] = "error", ["msg"] = c.Name + " didn't let you into " + r.Name, ["code"] = "denied", ["room"] = r.Id });
        }

        private void Kick(Client c, JObject msg)
        {
            Room r = c.Room;
            if (r == null || !IsAdmin(c, r)) { SendError(c, "only the world's owner (or the shared world's host) can remove players"); return; }
            Client who = FindPlayer((string)msg["id"]);
            if (who == null || who.Room != r || who == c) return;
            if (IsRealAdmin(who, r)) { SendError(c, "the world's owner can't be removed"); return; }
            r.Kicked[who.Name.ToLowerInvariant()] = Now + KickMs;
            LeaveRoom(who);
            Send(who, new JObject { ["t"] = "kicked", ["room"] = r.Id, ["name"] = r.Name, ["by"] = c.Name });
            Info(c.Name + " removed " + who.Name + " from " + r.Id);
            Toast(who.Name + " was removed from " + r.Name);
            BroadcastPlayers();
            BroadcastRooms();
        }

        private static readonly HashSet<string> Relayed = new HashSet<string> { "avatar", "cursor", "power", "speed", "act", "emote", "hit", "whit", "shot", "diag" };

        private void RelayToRoom(Client c, JObject msg)
        {
            Room r = c.Room;
            if (r == null || !c.Synced) return;
            string t = (string)msg["t"];
            if (c.Spectator && t != "cursor" && t != "diag" && t != "emote") return;
            if (r.Host != c && !IsAdmin(c, r))
            {
                JObject st = r.Settings;
                if (t == "speed" && !(bool)st["guestSpeed"]) return;
                if (t == "power")
                {
                    string gp = (string)st["guestPowers"];
                    if (gp == "none") return;
                    if (gp == "safe")
                    {
                        var blocked = new HashSet<string>(((JArray)st["blocked"]).Select(x => (string)x));
                        if (blocked.Contains((string)msg["p"])) return;
                        if (msg["batch"] is JArray batch)
                        {
                            var keep = new JArray(batch.OfType<JObject>().Where(ev => !blocked.Contains((string)ev["p"])));
                            if (keep.Count == 0) return;
                            msg["batch"] = keep;
                        }
                    }
                }
            }
            msg["id"] = c.Id;
            msg["name"] = c.Name;
            msg["color"] = c.Color;
            msg["room"] = r.Id;
            string line = Enc(msg);
            foreach (Client m in r.Members) if (m != c && m.Synced && !m.WaitingSnap) SendLine(m, line);
        }

        /// <summary>true: only the room's host may send it (as in the Lua relay).</summary>
        private static readonly Dictionary<string, bool> LiveSync = new Dictionary<string, bool>
        {
            { "wu", true }, { "wb", true }, { "wdata", true }, { "wm", true }, { "wa", true }, { "ww", true }, { "wt", true }, { "wc", true }, { "wneed", false }, { "wask", false },
            { "wle", true }, { "wli", true }, { "wlg", true }, { "wlreload", true }, { "wlr", false }, { "wlh", false }, { "wlready", false },
        };

        private void RelayRawToRoom(Client c, string type, string line)
        {
            Room r = c.Room;
            if (r == null || !c.Synced || c.WaitingSnap)
            {
                string why = r == null ? "no room" : !c.Synced ? "not synced" : "waiting for a snapshot";
                if (c.DropWhy != why) { c.DropWhy = why; Warn("dropping " + type + " from " + c.Name + ": " + why); }
                return;
            }
            c.DropWhy = null;
            if (LiveSync[type] && r.Host != c) return;
            if (type == "wle")
            {
                r.Wle = line;
                try { r.WleSha = (string)JObject.Parse(line)["sha"]; } catch { r.WleSha = null; }
            }
            foreach (Client m in r.Members) if (m != c && m.Synced && !m.WaitingSnap) SendLine(m, line);
        }

        private void Chat(Client c, JObject msg)
        {
            if (c.Id == null) return;
            string text = SanitizeText(msg["text"], 300, null);
            if (text == null) return;
            string line = Enc(new JObject { ["t"] = "chat", ["id"] = c.Id, ["name"] = c.Name, ["color"] = c.Color, ["text"] = text, ["room"] = c.Room?.Name ?? "" });
            foreach (Client x in Players_) if (x != c) SendLine(x, line);
        }

        private void Ping(Client c, JObject msg)
        {
            Send(c, new JObject { ["t"] = "pong", ["ts"] = msg["ts"] });
            if (IsNumber(msg["rtt"]) && (double)msg["rtt"] >= 0)
            {
                int rtt = (int)Math.Floor(Math.Min((double)msg["rtt"], 99999));
                if (Math.Abs(rtt - (c.Ping == -1 ? -1000 : c.Ping)) >= 15) c.PingDirty = true;
                c.Ping = rtt;
            }
        }

        // ================================================================ lifecycle

        private void RemoveClient(Client c, bool close, string reason)
        {
            if (c.Removed) return;
            LeaveRoom(c);
            c.Removed = true;
            c.Out.CompleteAdding();
            if (!close) try { c.Stream.Close(); } catch { }
            _clients.Remove(c);
            if (c.Id != null)
            {
                Info(c.Name + " disconnected (" + reason + ")");
                BroadcastPlayers();
                BroadcastRooms();
                Toast(c.Name + " left the server");
            }
            Players = _clients.Count(x => x.Id != null);
        }

        private static readonly Regex LiveType = new Regex("^\\{\"t\":\"(w[a-z]+)\"", RegexOptions.Compiled);

        private void ProcessLine(Client c, string line)
        {
            if (line.Length == 0) return;
            Match lm = LiveType.Match(line);
            if (lm.Success && LiveSync.ContainsKey(lm.Groups[1].Value))
            {
                if (c.Id == null) { SendError(c, "say hello first"); return; }
                try { RelayRawToRoom(c, lm.Groups[1].Value, line); }
                catch (Exception e) { Warn("relay '" + lm.Groups[1].Value + "' failed: " + e.Message); }
                return;
            }
            JObject msg;
            try { msg = JObject.Parse(line); }
            catch { SendError(c, "invalid message"); return; }
            string t = msg["t"]?.Type == JTokenType.String ? (string)msg["t"] : null;
            if (t == null) { SendError(c, "invalid message"); return; }
            if (c.Id == null && t != "hello" && t != "ping" && t != "bye") { SendError(c, "say hello first"); return; }
            try
            {
                switch (t)
                {
                    case "hello": Hello(c, msg); break;
                    case "join": Join(c, msg); break;
                    case "leave": if (c.Room != null) { LeaveRoom(c); BroadcastPlayers(); BroadcastRooms(); } break;
                    case "resync": Resync(c, msg); break;
                    case "snap-begin": SnapBegin(c, msg); break;
                    case "snap-chunk": SnapChunk(c, msg); break;
                    case "snap-end": SnapEnd(c, msg); break;
                    case "preview": Preview(c, msg); break;
                    case "rename-room": RenameRoom(c, msg); break;
                    case "delete-room": DeleteRoom(c, msg); break;
                    case "room-settings": RoomSettings(c, msg); break;
                    case "approve": Approve(c, msg); break;
                    case "kick": Kick(c, msg); break;
                    case "chat": Chat(c, msg); break;
                    case "ping": Ping(c, msg); break;
                    case "bye": RemoveClient(c, true, "bye"); break;
                    default:
                        if (Relayed.Contains(t)) RelayToRoom(c, msg);
                        else SendError(c, "unknown message type '" + t + "'");
                        break;
                }
            }
            catch (Exception e)
            {
                Warn("handler '" + t + "' failed: " + e);
                SendError(c, "server error while handling '" + t + "'");
            }
        }

        private void Sweep()
        {
            foreach (Client c in _clients.ToArray()) if (Now - c.LastSeenMs > TimeoutMs) RemoveClient(c, true, "timed out");
            bool pingChanged = false;
            foreach (Client c in Players_)
            {
                if (c.Pending != null && Now - c.Pending.At > ApprovalMs)
                {
                    c.Pending = null;
                    Send(c, new JObject { ["t"] = "error", ["msg"] = "nobody answered your request to join", ["code"] = "denied" });
                }
                if (c.PingDirty) { c.PingDirty = false; pingChanged = true; }
            }
            if (pingChanged && Now >= _nextPingBroadcast) { _nextPingBroadcast = Now + 10000; BroadcastPlayers(); }
            foreach (Room r in _rooms.Values.ToArray())
            {
                if (!r.SnapRequestedAt.HasValue || r.Pending != null || Now - r.SnapRequestedAt.Value <= SnapTimeoutMs) continue;
                r.SnapRequestedAt = null;
                if (!AnyoneWaiting(r)) continue;
                if (r.Snap != null) { Warn("room " + r.Id + ": host did not send a snapshot in time, serving the stored copy"); ServeWaiting(r); }
                else RequestSnapshot(r, "retry");
            }
        }
    }
}
