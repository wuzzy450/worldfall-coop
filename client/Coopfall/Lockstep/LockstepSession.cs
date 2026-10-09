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
        /// <summary>Ticks between checksum checks (a drift is caught within 0.2 s at x1).</summary>
        public const int HashEvery = 10;
        /// <summary>Ticks per second at x1 (LockstepClock.DefaultStep is 0.02 s).</summary>
        public const float TicksPerSecond = 1f / LockstepClock.DefaultStep;
        /// <summary>Seconds of simulation the host may be ahead of the slowest player.</summary>
        public const float MaxLeadSeconds = 1f;
        private const float ReadyTimeout = 90f, MinSecondsBetweenEpochs = 5f;

        private readonly CoopSession _s;

        /// <summary>Lockstep is running in this world (inputs, grants and checks are live).</summary>
        public bool Active => _epoch > 0 && _loaded && LockstepClock.Active;
        /// <summary>Inputs sent now reach the world (not while the host waits for everyone to load).</summary>
        public bool CanSubmit => Active && !(_s.IsHost && _waitingReady);
        /// <summary>Waiting for (or loading) the save of a new epoch.</summary>
        public bool Starting => _epoch > 0 && !_loaded;
        public int Epoch => _epoch;
        public int Desyncs, Epochs, Checked;
        public string LastDesync;

        private int _epoch, _seed;
        private string _h0;
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
            WorldCalls.Submit = SubmitCall;
            LockstepInput.Applied += i => { if (TraceTicks) Log.Info("lockstep: applied " + i + " (epoch " + _epoch + ")"); };
            if (TraceTicks) LockstepClock.BeforeTick += t => { if (Active) UnitRing.BeforeTick(t); };
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
            var wle = new JObject { ["e"] = _epoch, ["seed"] = _seed, ["sha"] = _sha, ["mods"] = ModFingerprint(), ["reason"] = reason };
            JObject wfs = WorldfallSettings.Capture();
            if (wfs != null) wle["wfs"] = wfs;
            Send("wle", wle);
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
            LockstepControl.Running = false;
            _ready.Clear();
            _playerTick.Clear();
            _myHashes.Clear();
            _theirHashes.Clear();
            _sentGrant = -1;
            _grantAcc = 0;
            _lastHashTick = -1;
            UnitRing.Clear();
            _owners.Clear();
            LockstepControl.Reset();
            // freeze the world and seed loading before anything is loaded
            if (!LockstepClock.Install()) { Log.Error("lockstep: can't hook the game loop"); _epoch = 0; return; }
            LockstepClock.Start(_seed);
            Randy.resetSeed(_seed);
            SharedWeather.Reset(_seed);
            FastLoad.On = true;
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
            FastLoad.On = false;
            LockstepClock.NormalizeAfterLoad();
            _loaded = true;
            LockstepControl.Running = true;
            // the world right after loading: a guest whose load went wrong is told to load again
            string h0 = StateHash.Compute(0, false).Line();
            Log.Info("lockstep: epoch " + _epoch + " ready at tick 0");
            if (_s.IsHost)
            {
                _h0 = h0;
                _ready.Add(_s.MyId);
                _waitingReady = true;
                _readySince = Time.unscaledTime;
            }
            else Send("wlready", new JObject { ["e"] = _epoch, ["from"] = _s.MyId, ["h0"] = h0 });
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
            LockstepControl.Running = false;
            _epochData = null;
            _waitingReady = false;
            FastLoad.On = false;
            LockstepClock.Stop();
            WorldfallSettings.Restore();
            LockstepInput.Clear();
            LockstepControl.Reset();
            _owners.Clear();
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
            ReleaseLeavers();
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

        /// <summary>Host: creatures of players who left are let go.</summary>
        private void ReleaseLeavers()
        {
            if (_owners.Count == 0) return;
            var here = new HashSet<string>();
            foreach (PlayerInfo p in RoomPlayers()) here.Add(p.id);
            here.Add(_s.MyId);
            foreach (KeyValuePair<long, string> kv in new List<KeyValuePair<long, string>>(_owners))
                if (!here.Contains(kv.Value)) { _owners.Remove(kv.Key); Assign(kv.Value, LockstepInput.Kind.Release, null, kv.Key, -1, null); }
        }

        /// <summary>Host: let the world run on, at the room's speed, not too far ahead of anyone.</summary>
        private float _lastRate = -1f;

        private void Grant()
        {
            float rate = Rate();
            if (rate != _lastRate)
            {
                Log.Info("lockstep: speed " + (rate == 0f ? "paused" : rate + " ticks/s") + " (" + Config.time_scale_asset?.id + (Config.paused ? ", paused" : "") + ")");
                _lastRate = rate;
            }
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

        /// <summary>A game call Worldfall (or anything else) made outside a tick (WorldCalls).</summary>
        private bool SubmitCall(string enc)
        {
            if (!Active) return false;
            if (_s.IsHost) { if (_waitingReady) return false; Assign(_s.MyId, LockstepInput.Kind.Call, enc, -1, -1, null); }
            else Send("wlr", new JObject { ["e"] = _epoch, ["from"] = _s.MyId, ["k"] = (int)LockstepInput.Kind.Call, ["id"] = enc, ["a"] = -1, ["b"] = -1, ["s"] = _localSeq++ });
            return true;
        }

        /// <summary>Host: who controls which creature (one player per creature).</summary>
        private readonly Dictionary<long, string> _owners = new Dictionary<long, string>();

        /// <summary>This player takes over, lets go of, or steers a creature (LockstepControl).</summary>
        public void SubmitControl(LockstepInput.Kind kind, long unit, string ctl)
        {
            if (!Active) return;
            if (_s.IsHost) { if (!_waitingReady) AcceptControl(_s.MyId, kind, unit, ctl); }
            else Send("wlr", new JObject { ["e"] = _epoch, ["from"] = _s.MyId, ["k"] = (int)kind, ["id"] = ctl, ["a"] = unit, ["b"] = -1, ["s"] = _localSeq++ });
        }

        private void AcceptControl(string from, LockstepInput.Kind kind, long unit, string ctl)
        {
            _owners.TryGetValue(unit, out string owner);
            switch (kind)
            {
                case LockstepInput.Kind.Possess:
                    if (owner != null && owner != from)
                    {
                        if (from == _s.MyId) CoopMod.Instance?.UI.ShowToast("Someone else controls that creature");
                        return;
                    }
                    // one creature per player
                    foreach (KeyValuePair<long, string> kv in new List<KeyValuePair<long, string>>(_owners))
                        if (kv.Value == from && kv.Key != unit) { _owners.Remove(kv.Key); Assign(from, LockstepInput.Kind.Release, null, kv.Key, -1, null); }
                    _owners[unit] = from;
                    break;
                case LockstepInput.Kind.Release:
                    if (owner != from) return;
                    _owners.Remove(unit);
                    break;
                case LockstepInput.Kind.Control:
                    if (owner != from) return;
                    break;
                default: return;
            }
            Assign(from, kind, ctl, unit, -1, null);
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
            UnitRing.AfterTick(tick);
            TickHash th = StateHash.Compute(tick, tick % 50 == 0);
            _trace.WriteLine(th.Line());
            if (th.detail != null)
            {
                // every creature's record at the check ticks (diff host and guest to see who drifted)
                if (_units == null || _unitsEpoch != _epoch)
                {
                    _units?.Dispose();
                    _unitsEpoch = _epoch;
                    _units = new System.IO.StreamWriter(System.IO.Path.Combine(Log.Dir ?? Application.persistentDataPath, "lockstep-units" + _epoch + ".txt"));
                }
                var ids = new List<long>(th.detail.Keys);
                ids.Sort();
                foreach (long id in ids)
                {
                    UnitRec r = th.detail[id];
                    _units.WriteLine(tick + "	" + id + "	" + r.px + "," + r.py + "	hp " + r.hp + "	tile " + r.tile + "	asset " + r.asset + "	timer " + r.timer + "	cool " + r.cool + "	task " + r.task + "	path " + r.path);
                }
                _units.Flush();
            }
            // which part of the tick rolled which dice (diff host and guest files to find a split)
            if (_sections == null)
            {
                SectionTrace.Install();
                SectionTrace.Enabled = true;
                SectionTrace.WatchSpecial = true;
                SectionTrace.WatchAllUntil = 400;   // early drifts: every creature's behaviour steps
                SectionTrace.MaxTick = long.MaxValue;
            }
            if (_sectionsEpoch != _epoch || _sections == null)
            {
                _sections?.Dispose();
                _sectionsEpoch = _epoch;
                _sections = new System.IO.StreamWriter(System.IO.Path.Combine(Log.Dir ?? Application.persistentDataPath, "lockstep-sections" + _epoch + ".txt")) { AutoFlush = false };
                SectionTrace.Current.Clear();
            }
            if (tick <= 3000)
                foreach (SectionTrace.Entry en in SectionTrace.Current) _sections.WriteLine(en.tick + "	" + en.section + "	" + en.state.ToString("x8"));
            SectionTrace.Current.Clear();
            if (tick % 100 == 0) _sections.Flush();
        }

        /// <summary>"-coopfall-lockstep-dump N": zone owners and cultures after tick N of every epoch.</summary>
        private static readonly long DumpAt = DumpTick();
        private static long DumpTick()
        {
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < a.Length; i++) if (a[i].Equals("-coopfall-lockstep-dump", StringComparison.OrdinalIgnoreCase) && long.TryParse(a[i + 1], out long t)) return t;
            return -1;
        }

        private void DumpMeta(long tick)
        {
            var lines = new List<string>();
            object calc = R.Get(World.world, "zone_calculator");
            if (R.Get(calc, "zones") is System.Collections.IEnumerable zones)
                foreach (TileZone z in zones)
                    lines.Add("zone " + R.Get(z, "id") + " city " + (z.city?.getID() ?? -1) + " tiles " + ((System.Collections.ICollection)R.Get(z, "tiles")).Count);
            foreach (City c in World.world.cities)
            {
                if (c == null) continue;
                string traits = "";
                if (c.culture != null) foreach (var t in c.culture.getTraits()) traits += " " + t.id;
                lines.Add("city " + c.getID() + " alive " + c.isAlive() + " culture " + (c.culture?.getID() ?? -1) + " roads " + (c.culture?.canUseRoads() ?? false) + " traits" + traits + " zones " + ((R.Get(c, "zones") as System.Collections.ICollection)?.Count ?? -1) + " kingdom " + ((R.Get(c, "kingdom") as Kingdom)?.getID() ?? -1));
            }
            System.IO.File.WriteAllLines(System.IO.Path.Combine(Log.Dir ?? Application.persistentDataPath, "lockstep-meta" + _epoch + "-" + tick + ".txt"), lines);
        }

        private System.IO.StreamWriter _sections, _units;
        private int _unitsEpoch;
        private int _sectionsEpoch;

        private void OnTick(long tick)
        {
            if (TraceTicks && Active) TraceTick(tick);
            if (TraceTicks && Active && tick % 100 == 0) Log.Info("lockstep: job lists at tick " + tick + ": " + LockstepClock.ListSignature());
            if (Active && DumpAt > 0 && (tick == 1 || (tick % DumpAt == 0 && tick <= 2000))) DumpMeta(tick);
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
                    if (tick < LockstepClock.Tick - 200) _theirHashes.RemoveAt(i);   // too old to compare
                    continue;
                }
                _theirHashes.RemoveAt(i);
                string theirs = (string)p["h"];
                if (theirs == mine) { Checked++; continue; }
                Desyncs++;
                LastDesync = (_s.Player(who)?.name ?? who) + " at tick " + tick;
                Log.Warn("lockstep: " + LastDesync + " differs: " + Describe(mine, theirs));
                _s.AddChat(null, "Lockstep: " + (_s.Player(who)?.name ?? "a player") + "'s world drifted at tick " + tick + " - re-syncing everyone", true);
                if (TraceTicks) UnitRing.Write(TracePath("lockstep-ring" + _epoch + ".txt"));
                StartEpoch("desync");
                return;
            }
            // keep a few seconds of our own checksums
            if (_myHashes.Count > 64)
            {
                var old = new List<long>();
                foreach (long t in _myHashes.Keys) if (t < LockstepClock.Tick - 400) old.Add(t);
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
                    if (TraceTicks && (string)p["reason"] == "desync" && _epoch > 0) UnitRing.Write(TracePath("lockstep-ring" + _epoch + ".txt"));
                    _epoch = e;
                    _seed = (int?)p["seed"] ?? 1;
                    _sha = (string)p["sha"];
                    Epochs++;
                    Log.Info("lockstep: host started epoch " + e + " (" + (string)p["reason"] + ")");
                    WorldfallSettings.Apply(p["wfs"] as JObject);
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
                    if (pl == null || pl.room != _s.RoomId || pl.spectator) return;
                    var kind = (LockstepInput.Kind)((int?)p["k"] ?? 1);
                    if (kind == LockstepInput.Kind.Possess || kind == LockstepInput.Kind.Release || kind == LockstepInput.Kind.Control)
                    {
                        if (kind == LockstepInput.Kind.Possess && !_s.PowerAllowedFor(pl, "possess")) return;
                        AcceptControl(from, kind, (long?)p["a"] ?? -1, id);
                        return;
                    }
                    if (kind == LockstepInput.Kind.Call) { if (id != null) Assign(from, kind, id, -1, -1, null); return; }
                    if (!_s.PowerAllowedFor(pl, id)) return;
                    Assign(from, kind, id, (long?)p["a"] ?? -1, (long?)p["b"] ?? -1, (string)p["brush"]);
                    break;
                case "wlready":
                    if (e != _epoch || !_s.IsHost) return;
                    if (_h0 != null && (string)p["h0"] != null && (string)p["h0"] != _h0)
                    {
                        Log.Warn("lockstep: " + (_s.Player((string)p["from"])?.name ?? "a guest") + " loaded a different world (" + Describe(_h0, (string)p["h0"]) + "); asking it to load again");
                        Send("wlreload", new JObject { ["e"] = _epoch, ["to"] = (string)p["from"] });
                        _readySince = Time.unscaledTime;   // give it time
                        return;
                    }
                    _ready.Add((string)p["from"]);
                    break;
                case "wlreload":
                    if (e != _epoch || _s.IsHost || (string)p["to"] != _s.MyId) return;
                    Log.Warn("lockstep: the host says this world didn't load right; loading it again");
                    _loaded = false;
                    FastLoad.On = true;
                    LockstepControl.Running = false;
                    LockstepClock.Start(_seed);
                    Randy.resetSeed(_seed);
                    SharedWeather.Reset(_seed);
                    RequestEpochSave();
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

        private static string TracePath(string name) => System.IO.Path.Combine(Log.Dir ?? Application.persistentDataPath, name);

        private static string Short(string sha) => sha == null ? "?" : sha.Substring(0, Math.Min(8, sha.Length));

        public string StatusLine()
        {
            if (_epoch == 0) return _s.Cfg.lockstep ? "lockstep: waiting" : "lockstep: off";
            if (!_loaded) return "lockstep: loading epoch " + _epoch;
            return "lockstep: epoch " + _epoch + ", tick " + LockstepClock.Tick + "/" + LockstepClock.Granted + (Desyncs > 0 ? ", " + Desyncs + " re-syncs" : "");
        }
    }
}
