using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// A lockstep co-op session: every PC runs the same simulation from the same save, and only
    /// player actions travel over the network.
    ///
    /// Epochs. The host starts one when lockstep is switched on, when someone joins, and when the
    /// worlds have drifted apart: it saves the world, announces (epoch, seed, save checksum) with
    /// "wle", uploads the save, and loads that same save itself. Every PC loads it with the dice
    /// seeded, normalizes it (LockstepClock.NormalizeAfterLoad) and waits at tick 0.
    ///
    /// Ticks. Only the host decides how far the world may run ("wlg": run up to tick g), at the
    /// room's speed, and never more than about a second ahead of the slowest player.
    ///
    /// Inputs. A player's god power becomes a request ("wlr"); the host gives it the next tick
    /// nobody has been allowed to run yet and sends it to everyone ("wli") before the grant that
    /// covers that tick, so every PC applies it at the same point. The relay keeps the order of
    /// one sender's packets.
    ///
    /// Checks. Every HashEvery ticks each player sends a checksum of its world ("wlh"); the host
    /// compares them with its own and starts a new epoch when they differ.
    ///
    /// All packets start with {"t":"wl so the relay passes them through without parsing
    /// (WorldfallRooms: wle/wli/wlg host only; wlr/wlh/wlready from anyone).
    /// </summary>
    public class LockstepSession
    {
        public const int HashEvery = 50;
        /// <summary>Ticks per second at x1 (LockstepClock.DefaultStep is 0.02 s).</summary>
        public const float TicksPerSecond = 1f / LockstepClock.DefaultStep;
        /// <summary>Seconds of simulation the host may be ahead of the slowest player.</summary>
        public const float MaxLeadSeconds = 1f;
        private const float ReadyTimeout = 90f, MinSecondsBetweenEpochs = 20f;

        private readonly CoopSession _s;

        /// <summary>Lockstep is running in this world (inputs, grants and checks are live).</summary>
        public bool Active => _epoch > 0 && _loaded && LockstepClock.Active;
        /// <summary>Waiting for (or loading) the save of a new epoch.</summary>
        public bool Starting => _epoch > 0 && !_loaded;
        public int Epoch => _epoch;
        public int Desyncs, Epochs, Checked;
        public string LastDesync;

        private int _epoch, _seed;
        private string _sha;
        private bool _loaded;
        private float _epochAt = -999f;

        // host
        private byte[] _epochData;
        private readonly HashSet<string> _ready = new HashSet<string>();
        private readonly Dictionary<string, long> _playerTick = new Dictionary<string, long>();
        private bool _waitingReady;
        private float _readySince;
        private double _grantAcc;
        private long _sentGrant = -1;
        private float _lastGrantSend;
        private int _seq;
        private readonly Dictionary<long, string> _myHashes = new Dictionary<long, string>();
        private readonly List<KeyValuePair<string, JObject>> _theirHashes = new List<KeyValuePair<string, JObject>>();

        // everyone
        private int _localSeq;
        private long _lastHashTick = -1;

        public LockstepSession(CoopSession s)
        {
            _s = s;
            LockstepClock.AfterTick += OnTick;
        }

        private bool Wanted => _s.Cfg.lockstep && _s.Online && _s.RoomId != null;

        // ============================================================== host: epochs

        /// <summary>Host: save the world and make everyone (this PC too) restart from that save.</summary>
        public void StartEpoch(string reason)
        {
            if (!_s.IsHost || !Wanted || !WorldBoxApi.WorldReady) return;
            if (Time.unscaledTime - _epochAt < MinSecondsBetweenEpochs && reason == "desync")
            {
                Log.Warn("lockstep: another desync within " + MinSecondsBetweenEpochs + " s; waiting before re-syncing");
                return;
            }
            if (!LockstepClock.Install()) { Log.Error("lockstep: can't hook the game loop; staying on live sync"); return; }
            if (_s.Uploading) return;   // an autosave is still going up; try again next frame
            byte[] data;
            try { data = WorldBoxApi.TakeSnapshot(); }
            catch (Exception e) { Log.Error("lockstep: snapshot failed: " + e); return; }
            _epoch = Math.Max(_epoch + 1, 1);
            _seed = LockstepClock.TickSeed(Environment.TickCount, _epoch);
            _sha = CoopSession.Sha256(data);
            _epochData = data;
            _epochAt = Time.unscaledTime;
            Epochs++;
            Log.Info("lockstep: epoch " + _epoch + " (" + reason + "), seed " + _seed + ", " + (data.Length / 1024) + " KB");
            Send("wle", new JObject { ["e"] = _epoch, ["seed"] = _seed, ["sha"] = _sha, ["mods"] = ModFingerprint(), ["reason"] = reason });
            _s.UploadSnapshotData(data, "lockstep");
            BeginEpochLoad(data);
        }

        /// <summary>Host: someone asked for the world (joining, re-sync). In lockstep that's a new epoch.</summary>
        public bool OnSnapshotRequested(string reason)
        {
            if (!Wanted || !_s.IsHost) return false;
            StartEpoch(reason ?? "join");
            return true;
        }

        private void BeginEpochLoad(byte[] data)
        {
            _loaded = false;
            _ready.Clear();
            _playerTick.Clear();
            _myHashes.Clear();
            _theirHashes.Clear();
            _sentGrant = -1;
            _grantAcc = 0;
            _lastHashTick = -1;
            // freeze the world and seed loading before anything is loaded
            if (!LockstepClock.Install()) { Log.Error("lockstep: can't hook the game loop"); _epoch = 0; return; }
            LockstepClock.Start(_seed);
            Randy.resetSeed(_seed);
            if (data != null) _s.LoadOwnSnapshot(data);
        }

        // ============================================================== everyone: world loaded

        /// <summary>CoopSession calls this when a world has finished loading.</summary>
        public void OnWorldLoaded(string loadedSha)
        {
            if (_epoch <= 0 || _loaded) return;
            if (!_s.IsHost && loadedSha != null && _sha != null && !string.Equals(loadedSha, _sha, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("lockstep: loaded save " + Short(loadedSha) + " but epoch " + _epoch + " uses " + Short(_sha) + "; asking for it again");
                RequestEpochSave();
                return;
            }
            LockstepClock.NormalizeAfterLoad();
            _loaded = true;
            Log.Info("lockstep: epoch " + _epoch + " ready at tick 0");
            if (_s.IsHost)
            {
                _ready.Add(_s.MyId);
                _waitingReady = true;
                _readySince = Time.unscaledTime;
            }
            else Send("wlready", new JObject { ["e"] = _epoch, ["from"] = _s.MyId });
        }

        private void RequestEpochSave()
        {
            // the relay serves its stored save if it is this epoch's, and otherwise waits for the upload
            _s.Net.Send("resync", new JObject { ["sha"] = _sha });
        }

        /// <summary>Leave lockstep (switched off, left the world, disconnected).</summary>
        public void Stop(string why)
        {
            if (_epoch == 0 && !LockstepClock.Active) return;
            Log.Info("lockstep: stopped (" + why + ")");
            _epoch = 0;
            _loaded = false;
            _epochData = null;
            _waitingReady = false;
            LockstepClock.Stop();
            LockstepInput.Clear();
        }

        // ============================================================== frame

        public void Tick()
        {
            if (_epoch > 0 && (!_s.Online || _s.RoomId == null || !_s.Cfg.lockstep)) { Stop(!_s.Cfg.lockstep ? "switched off" : "left the world"); return; }
            if (!_s.IsHost)
            {
                // a guest whose host doesn't run lockstep simply plays on live sync
                if (Active) KeepSpeedUp();
                return;
            }
            if (!Wanted || !_s.InWorld || !WorldBoxApi.WorldReady) return;
            if (_epoch == 0) { StartEpoch("lockstep on"); return; }
            if (!Active) return;
            if (_waitingReady)
            {
                bool all = true;
                foreach (PlayerInfo p in RoomPlayers()) if (!_ready.Contains(p.id)) all = false;
                if (!all && Time.unscaledTime - _readySince < ReadyTimeout) return;
                if (!all) Log.Warn("lockstep: starting without everyone ready (" + _ready.Count + " of " + RoomPlayers().Count + ")");
                _waitingReady = false;
                _sentGrant = 0;
            }
            Grant();
            CompareHashes();
        }

        private List<PlayerInfo> RoomPlayers()
        {
            var l = new List<PlayerInfo>();
            foreach (PlayerInfo p in _s.Players) if (p.room == _s.RoomId) l.Add(p);
            return l;
        }

        private float Rate()
        {
            if (Config.paused || Config.time_scale_asset == null) return 0f;
            return TicksPerSecond * Config.time_scale_asset.multiplier * Math.Max(1, Config.time_scale_asset.ticks);
        }

        /// <summary>Host: let the world run on, at the room's speed, not too far ahead of anyone.</summary>
        private void Grant()
        {
            float rate = Rate();
            _grantAcc += Time.unscaledDeltaTime * rate;
            long add = (long)_grantAcc;
            _grantAcc -= add;
            long g = _sentGrant + add;
            long slowest = long.MaxValue;
            foreach (PlayerInfo p in RoomPlayers())
                if (p.id != _s.MyId && _ready.Contains(p.id)) slowest = Math.Min(slowest, _playerTick.TryGetValue(p.id, out long t) ? t : 0);
            if (slowest != long.MaxValue) g = Math.Min(g, slowest + (long)Math.Max(TicksPerSecond * MaxLeadSeconds, rate * MaxLeadSeconds));
            g = Math.Max(g, _sentGrant);
            KeepSpeedUp();
            if (g == _sentGrant && Time.unscaledTime - _lastGrantSend < 0.5f) return;
            _sentGrant = g;
            _lastGrantSend = Time.unscaledTime;
            LockstepClock.Granted = g;
            Send("wlg", new JObject { ["e"] = _epoch, ["g"] = g });
        }

        /// <summary>Enough ticks per frame to keep up with the speed (and catch up after a hitch).</summary>
        private static void KeepSpeedUp()
        {
            long behind = LockstepClock.Granted - LockstepClock.Tick;
            LockstepClock.MaxPerFrame = (int)Math.Max(5, Math.Min(200, behind / 4 + 5));
        }

        // ============================================================== inputs

        /// <summary>
        /// A local god power use. Host: give it a tick and send it out; guest: ask the host.
        /// Returns false when lockstep isn't running (the power then runs the normal way).
        /// </summary>
        public bool SubmitPower(string powerId, WorldTile tile, string brush)
        {
            if (!Active || tile == null) return false;
            if (_s.IsHost && _waitingReady) { CoopMod.Instance?.UI.ShowToast("Everyone is still loading the world"); return true; }
            if (_s.IsHost) Assign(_s.MyId, LockstepInput.Kind.Power, powerId, tile.x, tile.y, brush);
            else Send("wlr", new JObject { ["e"] = _epoch, ["from"] = _s.MyId, ["k"] = (int)LockstepInput.Kind.Power, ["id"] = powerId, ["a"] = tile.x, ["b"] = tile.y, ["brush"] = brush, ["s"] = _localSeq++ });
            return true;
        }

        /// <summary>Host: the next tick nobody may have run yet, sent to everyone before its grant.</summary>
        private void Assign(string from, LockstepInput.Kind kind, string id, long a, long b, string brush)
        {
            var i = new LockstepInput.Input
            {
                tick = Math.Max(LockstepClock.Tick + 1, _sentGrant + 1),
                player = 0,
                seq = _seq++,
                kind = kind,
                id = id,
                a = a,
                b = b,
                brush = brush,
            };
            LockstepInput.Queue(i);
            Send("wli", new JObject { ["e"] = _epoch, ["i"] = i.Encode(), ["from"] = from });
        }

        // ============================================================== checks

        /// <summary>"-coopfall-lockstep-trace": every tick's checksum to lockstep-epochN.tsv (compare with tools/compare-determinism.py).</summary>
        private static readonly bool TraceTicks = Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals("-coopfall-lockstep-trace", StringComparison.OrdinalIgnoreCase));
        private System.IO.StreamWriter _trace;
        private int _traceEpoch;

        private void TraceTick(long tick)
        {
            if (_traceEpoch != _epoch)
            {
                _trace?.Dispose();
                _traceEpoch = _epoch;
                string f = System.IO.Path.Combine(Log.Dir ?? Application.persistentDataPath, "lockstep-epoch" + _epoch + ".tsv");
                _trace = new System.IO.StreamWriter(f) { AutoFlush = true };
                _trace.WriteLine("# epoch " + _epoch + " seed " + _seed + " " + (_s.IsHost ? "host" : "guest"));
                _trace.WriteLine(TickHash.Header);
            }
            _trace.WriteLine(StateHash.Compute(tick, false).Line());
        }

        private void OnTick(long tick)
        {
            if (TraceTicks && Active) TraceTick(tick);
            if (!Active || tick % HashEvery != 0 || tick == _lastHashTick) return;
            _lastHashTick = tick;
            string h = StateHash.Compute(tick, false).Line();
            if (_s.IsHost) _myHashes[tick] = h;
            else Send("wlh", new JObject { ["e"] = _epoch, ["from"] = _s.MyId, ["tick"] = tick, ["h"] = h });
        }

        private void CompareHashes()
        {
            for (int i = _theirHashes.Count - 1; i >= 0; i--)
            {
                string who = _theirHashes[i].Key;
                JObject p = _theirHashes[i].Value;
                long tick = (long?)p["tick"] ?? -1;
                if (!_myHashes.TryGetValue(tick, out string mine))
                {
                    if (tick < LockstepClock.Tick - HashEvery * 4) _theirHashes.RemoveAt(i);   // too old to compare
                    continue;
                }
                _theirHashes.RemoveAt(i);
                string theirs = (string)p["h"];
                if (theirs == mine) { Checked++; continue; }
                Desyncs++;
                LastDesync = (_s.Player(who)?.name ?? who) + " at tick " + tick;
                Log.Warn("lockstep: " + LastDesync + " differs: " + Describe(mine, theirs));
                _s.AddChat(null, "Lockstep: " + (_s.Player(who)?.name ?? "a player") + "'s world drifted at tick " + tick + " - re-syncing everyone", true);
                StartEpoch("desync");
                return;
            }
            // keep a few seconds of our own checksums
            if (_myHashes.Count > 64)
            {
                var old = new List<long>();
                foreach (long t in _myHashes.Keys) if (t < LockstepClock.Tick - HashEvery * 40) old.Add(t);
                foreach (long t in old) _myHashes.Remove(t);
            }
        }

        private static string Describe(string a, string b)
        {
            string[] x = a.Split('\t'), y = b.Split('\t');
            string[] names = TickHash.Header.Split('\t');
            var diff = new List<string>();
            for (int i = 1; i < x.Length && i < y.Length && i < names.Length; i++) if (x[i] != y[i]) diff.Add(names[i]);
            return string.Join(", ", diff.ToArray());
        }

        // ============================================================== packets

        public void OnPacket(string t, JObject p)
        {
            int e = (int?)p["e"] ?? 0;
            switch (t)
            {
                case "wle":
                    if (_s.IsHost) return;
                    if (!_s.Cfg.lockstep)
                    {
                        _s.AddChat(null, "The host runs lockstep co-op, which is off here (co-op menu). Playing on live sync.", true);
                        return;
                    }
                    if ((string)p["mods"] != ModFingerprint())
                    {
                        Log.Warn("lockstep: mods differ from the host's; not joining lockstep");
                        _s.AddChat(null, "Lockstep needs the same mods (and the same Worldfall build) as the host. Playing on live sync.", true);
                        return;
                    }
                    if (e == _epoch && (string)p["sha"] == _sha) return;   // the relay repeats it ahead of the save it serves
                    _epoch = e;
                    _seed = (int?)p["seed"] ?? 1;
                    _sha = (string)p["sha"];
                    Epochs++;
                    Log.Info("lockstep: host started epoch " + e + " (" + (string)p["reason"] + ")");
                    BeginEpochLoad(null);
                    // a new arrival gets this before the save itself; everyone else fetches the save
                    if (!_s.Downloading) RequestEpochSave();
                    break;
                case "wli":
                    if (e != _epoch || _s.IsHost) return;
                    if (LockstepInput.Input.TryDecode((string)p["i"], out LockstepInput.Input i)) LockstepInput.Queue(i);
                    break;
                case "wlg":
                    if (e != _epoch || _s.IsHost) return;
                    LockstepClock.Granted = Math.Max(LockstepClock.Granted, (long?)p["g"] ?? 0);
                    break;
                case "wlr":
                    if (e != _epoch || !_s.IsHost || !Active || _waitingReady) return;   // players still loading would miss it
                    string from = (string)p["from"];
                    string id = (string)p["id"];
                    PlayerInfo pl = _s.Player(from);
                    if (pl == null || pl.room != _s.RoomId || pl.spectator || !_s.PowerAllowedFor(pl, id)) return;
                    Assign(from, (LockstepInput.Kind)((int?)p["k"] ?? 1), id, (long?)p["a"] ?? -1, (long?)p["b"] ?? -1, (string)p["brush"]);
                    break;
                case "wlready":
                    if (e != _epoch || !_s.IsHost) return;
                    _ready.Add((string)p["from"]);
                    break;
                case "wlh":
                    if (e != _epoch || !_s.IsHost) return;
                    string who = (string)p["from"];
                    _playerTick[who] = Math.Max(_playerTick.TryGetValue(who, out long had) ? had : 0, (long?)p["tick"] ?? 0);
                    _ready.Add(who);
                    _theirHashes.Add(new KeyValuePair<string, JObject>(who, p));
                    break;
            }
        }

        private void Send(string t, JObject body)
        {
            // raw lane: the relay recognizes {"t":"wl... without parsing it
            body.AddFirst(new JProperty("t", t));
            if (!_s.Online) return;
            _s.Net.SendRaw(body.ToString(Newtonsoft.Json.Formatting.None), false);
        }

        private static string _modPrint;

        /// <summary>The mods that change the world, with their file fingerprints (Worldfall build included).</summary>
        public static string ModFingerprint()
        {
            if (_modPrint == null) _modPrint = CoopSession.Sha256(System.Text.Encoding.UTF8.GetBytes(ModScan.ToJson().ToString(Newtonsoft.Json.Formatting.None)));
            return _modPrint;
        }

        private static string Short(string sha) => sha == null ? "?" : sha.Substring(0, Math.Min(8, sha.Length));

        public string StatusLine()
        {
            if (_epoch == 0) return _s.Cfg.lockstep ? "lockstep: waiting" : "lockstep: off";
            if (!_loaded) return "lockstep: loading epoch " + _epoch;
            return "lockstep: epoch " + _epoch + ", tick " + LockstepClock.Tick + "/" + LockstepClock.Granted + (Desyncs > 0 ? ", " + Desyncs + " re-syncs" : "");
        }
    }
}
