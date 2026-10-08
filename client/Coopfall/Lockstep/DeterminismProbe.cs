using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Feasibility test for lockstep: load a save, run N ticks under LockstepClock, checksum the
    /// world after every tick, reload and run again, then report the first tick (and creature)
    /// that differs. Each run is also written to a file so runs from two game processes (or two
    /// PCs) can be compared with tools/compare-determinism.py.
    ///
    ///   worldbox.exe -coopfall-determinism &lt;slot&gt; &lt;ticks&gt; [-coopfall-determinism-runs 2]
    ///                [-coopfall-determinism-seed 12345] [-coopfall-determinism-quit]
    /// </summary>
    public class DeterminismProbe
    {
        private enum Phase { WaitWorld, Loading, Settle, Running, Done }

        private readonly int _slot, _ticks, _runs, _seed;
        private readonly bool _quit;
        private long _dumpTick = -1, _dumpTo = -1;
        private bool _dumpAll, _dumpBuildings, _dumpTiles;
        private readonly HashSet<long> _dumpIds = new HashSet<long>();
        /// <summary>Per run, per dumped tick.</summary>
        private readonly List<List<Dictionary<string, string>>> _dumps = new List<List<Dictionary<string, string>>>();
        private readonly List<Dictionary<string, double[]>> _tileDumps = new List<Dictionary<string, double[]>>();
        private Phase _phase = Phase.WaitWorld;
        private int _run;
        private float _phaseAt;
        private bool _sawLoading;
        private readonly List<List<TickHash>> _results = new List<List<TickHash>>();
        private readonly List<List<SectionTrace.Entry>> _traces = new List<List<SectionTrace.Entry>>();
        private List<TickHash> _cur;
        private long _detailBudget;   // creature records kept per run for pinpointing
        private const long DetailPerRun = 4000000;
        private string _dir;

        public bool Finished => _phase == Phase.Done;
        private static string _traceExtra;
        /// <summary>-coopfall-determinism-chaos N: wars, magic users and disasters as inputs, every N ticks.</summary>
        private static int _chaos;
        /// <summary>
        /// -coopfall-determinism-camera: from run 2 on, the camera flies over the map (run 2 zoomed
        /// out, run 3 zoomed in, ...), like another player's view. Anything the camera changes in
        /// the world shows up as a difference.
        /// </summary>
        private static bool _moveCamera;

        public static DeterminismProbe FromCommandLine()
        {
            string[] a = Environment.GetCommandLineArgs();
            int slot = -1, ticks = 600, runs = 2, seed = 12345;
            bool quit = false;
            for (int i = 0; i < a.Length; i++)
            {
                string s = a[i].ToLowerInvariant();
                if (s == "-coopfall-determinism" && i + 2 < a.Length) { int.TryParse(a[i + 1], out slot); int.TryParse(a[i + 2], out ticks); }
                else if (s == "-coopfall-determinism-runs" && i + 1 < a.Length) int.TryParse(a[i + 1], out runs);
                else if (s == "-coopfall-determinism-seed" && i + 1 < a.Length) int.TryParse(a[i + 1], out seed);
                else if (s == "-coopfall-determinism-quit") quit = true;
                else if (s == "-coopfall-determinism-trace" && i + 1 < a.Length) _traceExtra = a[i + 1];
                else if (s == "-coopfall-determinism-stack" && i + 2 < a.Length) { int.TryParse(a[i + 1], out SectionTrace.StackTile); int.TryParse(a[i + 2], out SectionTrace.StackTile2); }
                else if (s == "-coopfall-determinism-tiles" && i + 1 < a.Length) int.TryParse(a[i + 1], out StateHash.TileEvery);
                else if (s == "-coopfall-determinism-chaos" && i + 1 < a.Length) int.TryParse(a[i + 1], out _chaos);
                else if (s == "-coopfall-determinism-camera") _moveCamera = true;
            }
            if (slot < 0) return null;
            var p = new DeterminismProbe(slot, Math.Max(1, ticks), Math.Max(1, runs), seed, quit);
            // -coopfall-determinism-dump <tick> <id,id,...>: every field of those creatures after that tick
            for (int i = 0; i + 2 < a.Length; i++)
                if (a[i].ToLowerInvariant() == "-coopfall-determinism-dump")
                {
                    // a tick or a range "from-to" (reports the first tick in it that differs)
                    string[] range = a[i + 1].Split('-');
                    long.TryParse(range[0], out p._dumpTick);
                    p._dumpTo = p._dumpTick;
                    if (range.Length > 1) long.TryParse(range[1], out p._dumpTo);
                    if (a[i + 2] == "all") p._dumpAll = true;
                    else if (a[i + 2] == "buildings") p._dumpBuildings = true;
                    else if (a[i + 2] == "tiles") p._dumpTiles = true;
                    else foreach (string id in a[i + 2].Split(',')) if (long.TryParse(id, out long v)) { p._dumpIds.Add(v); SectionTrace.Watch.Add(v); }
                }
            return p;
        }

        private DeterminismProbe(int slot, int ticks, int runs, int seed, bool quit)
        {
            _slot = slot; _ticks = ticks; _runs = runs; _seed = seed; _quit = quit;
            Application.runInBackground = true;
            _dir = Path.Combine(Log.Dir ?? Application.persistentDataPath, "determinism");
            Directory.CreateDirectory(_dir);
            Log.Info("DETERMINISM: slot " + slot + ", " + ticks + " ticks, " + runs + " runs, seed " + seed);
            LockstepClock.AfterTick += OnTick;
            if (!LockstepClock.Install()) Log.Error("lockstep install failed");
            SectionTrace.Install();
            SectionTrace.Enabled = true;
            SectionTrace.MaxTick = ticks + 1;
            if (!string.IsNullOrEmpty(_traceExtra)) SectionTrace.TraceMethods(_traceExtra);
            LockstepClock.BeforeTick += BetweenTicks;
        }

        public void Tick()
        {
            switch (_phase)
            {
                case Phase.WaitWorld:
                    if (!WorldBoxApi.WorldReady) return;
                    if (!LockstepClock.Install()) { Fail("could not hook the game loop"); return; }
                    StartLoad();
                    break;

                case Phase.Loading:
                    if (SmoothLoader.isLoading()) { _sawLoading = true; _phaseAt = Time.unscaledTime; return; }
                    // wait for loading to begin and end, then a short quiet period
                    if ((_sawLoading || Time.unscaledTime - _phaseAt > 10f) && Time.unscaledTime - _phaseAt > 2f && WorldBoxApi.WorldReady)
                    {
                        _phase = Phase.Settle;
                        _phaseAt = Time.unscaledTime;
                    }
                    break;

                case Phase.Settle:
                    if (Time.unscaledTime - _phaseAt < 1f) return;
                    Log.Info("DETERMINISM: run " + (_run + 1) + " loaded: " + World.world.units.Count + " creatures, " + World.world.buildings.Count + " buildings");
                    _cur = new List<TickHash>(_ticks + 1);
                    LockstepClock.NormalizeAfterLoad();
                    _tileDumps.Add(DumpTiles());
                    SectionTrace.ResetWatch();
                    _detailBudget = DetailPerRun;
                    _cur.Add(StateHash.Compute(0, TakeDetail()));
                    if (_chaos > 0) QueueChaos();
                    _phase = Phase.Running;
                    _phaseAt = Time.unscaledTime;
                    LockstepClock.Granted = _ticks;
                    break;

                case Phase.Running:
                    if (_moveCamera && _run > 0) MoveCamera();
                    if (LockstepClock.Tick < _ticks) return;
                    FinishRun();
                    break;
            }
        }

        private void MoveCamera()
        {
            Camera cam = WorldBoxApi.MapCamera;
            if (cam == null) return;
            float t = Time.unscaledTime * (0.05f + 0.03f * _run);
            float x = MapBox.width * (0.5f + 0.4f * Mathf.Sin(t * 1.3f + _run));
            float y = MapBox.height * (0.5f + 0.4f * Mathf.Cos(t * 0.9f + 2 * _run));
            cam.transform.position = new Vector3(x, y, cam.transform.position.z);
            cam.orthographicSize = _run % 2 == 1 ? 150f : 12f;
        }

        private void StartLoad()
        {
            // freeze the world before anything simulates, and make loading use the same dice
            LockstepClock.Start(_seed);
            Randy.resetSeed(_seed);
            Log.Info("DETERMINISM: run " + (_run + 1) + " loading save slot " + _slot);
            SaveManager.setCurrentSlot(_slot);
            R.Call0(R.Get(World.world, "save_manager"), "startLoadSlot");
            _sawLoading = false;
            _phase = Phase.Loading;
            _phaseAt = Time.unscaledTime;
        }

        /// <summary>
        /// Powers used by the chaos script, in order. Magic users come first so they have time
        /// to cast; the rest cycle through disasters, curses and bombs.
        /// </summary>
        private static readonly string[] ChaosPowers =
        {
            "evil_mage", "white_mage", "necromancer", "druid", "plague_doctor", "demon", "dragon",
            "meteorite", "lightning", "tornado", "earthquake", "lava", "plague", "fire", "acid",
            "zombie_infection", "curse", "madness", "bomb", "cloud_lightning", "grenade", "rain",
            "atomic_bomb", "volcano", "blessing", "spite", "napalm_bomb", "cloud_fire", "acid_blob",
            "fire_skull", "skeleton", "ufo", "heatray", "bowling_ball", "tnt", "geyser", "ghost",
        };

        /// <summary>
        /// The scenario for wars, magic and disasters, as lockstep inputs (so it also tests the
        /// input path): every civ kingdom declares war on the next one at tick 1, then a power
        /// every _chaos ticks on a creature picked by ID. Picks depend only on the loaded world.
        /// </summary>
        private void QueueChaos()
        {
            var kingdoms = new List<Kingdom>();
            foreach (Kingdom k in World.world.kingdoms) if (k != null && k.isAlive() && k.isCiv()) kingdoms.Add(k);
            kingdoms.Sort((x, y) => x.getID().CompareTo(y.getID()));
            int seq = 0;
            for (int k = 0; k + 1 < kingdoms.Count; k += 2)
                LockstepInput.Queue(new LockstepInput.Input { tick = 1, player = 0, seq = seq++, kind = LockstepInput.Kind.War, id = "normal", a = kingdoms[k].getID(), b = kingdoms[k + 1].getID() });
            var units = new List<Actor>();
            foreach (Actor a in World.world.units) if (a != null && a.current_tile != null) units.Add(a);
            units.Sort((x, y) => x.getID().CompareTo(y.getID()));
            int n = 0, missing = 0;
            for (long t = 2; t < _ticks && units.Count > 0; t += _chaos, n++)
            {
                string id = ChaosPowers[n % ChaosPowers.Length];
                if (AssetManager.powers.get(id) == null) { missing++; continue; }
                WorldTile tile = units[(int)((n * 7919L) % units.Count)].current_tile;
                LockstepInput.Queue(new LockstepInput.Input { tick = t, player = 1 + n % 2, seq = 0, kind = LockstepInput.Kind.Power, id = id, a = tile.x, b = tile.y, brush = n % 3 == 0 ? "circ_5" : "circ_2" });
            }
            Log.Info("DETERMINISM: chaos: " + (seq) + " wars, " + LockstepInput.PendingCount + " inputs" + (missing > 0 ? ", " + missing + " skipped (unknown powers)" : ""));
        }

        private bool TakeDetail()
        {
            int n = World.world.units.Count;
            if (_detailBudget < n) return false;
            _detailBudget -= n;
            return true;
        }

        /// <summary>Every field of the chosen creatures (-coopfall-determinism-dump).</summary>
        private Dictionary<string, string> DumpFields()
        {
            var d = new Dictionary<string, string>();
            if (_dumpTiles)
            {
                var flash = HarmonyLib.AccessTools.Field(typeof(WorldTile), "flash_state");
                foreach (WorldTile t in (WorldTile[])HarmonyLib.AccessTools.Field(typeof(MapBox), "tiles_list").GetValue(World.world))
                    d["tile " + t.tile_id] = t.Type?.id + "/" + t.main_type?.id + " hp " + t.health + " burned " + t.burned_stages + " fire " + World.world.tile_manager.fires[t.tile_id]
                        + " flash " + flash.GetValue(t) + " changed " + t.timestamp_type_changed.ToString("R") + " frozen " + t.data.frozen;
            }
            if (_dumpBuildings)
                foreach (Building b in World.world.buildings)
                    if (b != null)
                        d["building " + b.getID()] = (HarmonyLib.AccessTools.Field(typeof(Building), "asset")?.GetValue(b) as Asset)?.id + " tile " + b.current_tile?.tile_id + " hp " + b.getHealth() + " alive " + b.isAlive()
                            + " city " + (b.city?.getID() ?? -1) + " built " + !b.isUnderConstruction() + " slots " + b.hasResidentSlots() + " residents " + string.Join(",", b.residents);
            int pi = 0;
            foreach (Projectile pr in World.world.projectiles) DumpObject(d, "projectile" + (pi++) + ".", pr);
            foreach (Actor a in World.world.units)
                if (a != null && (_dumpAll || _dumpIds.Contains(a.getID())))
                {
                    DumpObject(d, a.getID() + ".", a);
                    object ai = HarmonyLib.AccessTools.Field(typeof(Actor), "ai")?.GetValue(a);
                    if (ai != null) DumpObject(d, a.getID() + ".ai.", ai);
                    object data = HarmonyLib.AccessTools.Field(typeof(Actor), "data")?.GetValue(a);
                    if (data != null) DumpObject(d, a.getID() + ".data.", data);
                    if (!_dumpAll && a.city != null) DumpObject(d, a.getID() + ".city.", a.city);
                    if (!_dumpAll && a.current_tile?.chunk != null)
                    {
                        // the chunk's creatures per kingdom, in the order enemy searches see them
                        ChunkObjectContainer co = a.current_tile.chunk.objects;
                        var sbk = new System.Text.StringBuilder();
                        foreach (long k in co.kingdoms) { sbk.Append(" k").Append(k).Append(':'); foreach (Actor u in co.getUnits(k)) sbk.Append(u.getID()).Append(','); }
                        d[a.getID() + ".chunk"] = sbk.ToString();
                    }
                    if (!_dumpAll && a.subspecies != null)
                    {
                        DumpObject(d, a.getID() + ".subspecies.", a.subspecies);
                        object sd = HarmonyLib.AccessTools.Field(a.subspecies.GetType(), "data")?.GetValue(a.subspecies);
                        if (sd != null) DumpObject(d, a.getID() + ".subspecies.data.", sd);
                    }
                    if (HarmonyLib.AccessTools.Field(typeof(Actor), "sprite_animation")?.GetValue(a) is SpriteAnimation an && an != null)
                        d[a.getID() + ".anim"] = "frame " + an.currentFrameIndex + "/" + (an.frames?.Length ?? -1) + " next " + an.nextFrameTime.ToString("R") + " on " + an.isOn + " looped " + an.looped + " dirty " + an.dirty + " visible " + HarmonyLib.AccessTools.Field(typeof(Actor), "is_visible").GetValue(a);
                    if (!_dumpAll && HarmonyLib.AccessTools.Field(typeof(Actor), "children_special")?.GetValue(a) is System.Collections.IEnumerable kids)
                        foreach (object k in kids) DumpObject(d, a.getID() + "." + k.GetType().Name + ".", k);
                    if (!_dumpAll && a.current_tile?.region != null) DumpObject(d, a.getID() + ".region.", a.current_tile.region);
                }
            return d;
        }

        private static void DumpObject(Dictionary<string, string> d, string prefix, object o)
        {
            for (Type t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    // real-time stamps and native pointers differ by nature
                    if (f.Name == "m_CachedPtr" || f.Name.Contains("unscaled")) continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    d[prefix + t.Name + "." + f.Name] = Show(v);
                }
        }

        private static string Show(object v)
        {
            if (v == null) return "null";
            if (v is float fl) return fl.ToString("R");
            if (v is double db) return db.ToString("R");
            if (v is Vector2 v2) return v2.x.ToString("R") + "," + v2.y.ToString("R");
            if (v is Vector3 v3) return v3.x.ToString("R") + "," + v3.y.ToString("R") + "," + v3.z.ToString("R");
            if (v is WorldTile wt) return "tile " + wt.tile_id;
            if (v is TileZone tz) return "zone " + tz.id;
            if (v is MapRegion mr) return "region@" + (mr.tiles.Count > 0 ? mr.tiles[0].tile_id : -1) + "x" + mr.tiles.Count;
            if (v is BaseSimObject so) return so.GetType().Name + " " + so.getID();
            if (v is Asset asset) return "asset " + asset.id;
            if (v is BaseStats st)
            {
                var sb = new System.Text.StringBuilder("stats");
                if (HarmonyLib.AccessTools.Field(typeof(BaseStats), "_stats_list")?.GetValue(st) is List<BaseStatsContainer> l)
                    foreach (BaseStatsContainer x in l) sb.Append(' ').Append(x.id).Append('=').Append(x.value.ToString("R"));
                return sb.ToString();
            }
            if (v is System.Collections.IDictionary dict)
            {
                // contents, in iteration order (order matters too)
                var sb = new System.Text.StringBuilder(v.GetType().Name + "[" + dict.Count + "]");
                int k = 0;
                foreach (System.Collections.DictionaryEntry e in dict) { if (k++ >= 40) break; sb.Append(' ').Append(ShowShallow(e.Key)).Append('=').Append(ShowShallow(e.Value)); }
                return sb.ToString();
            }
            if (v is System.Collections.ICollection c)
            {
                var sb = new System.Text.StringBuilder(v.GetType().Name + "[" + c.Count + "]");
                int k = 0;
                foreach (object e in c) { if (k++ >= 400) break; sb.Append(' ').Append(ShowShallow(e)); }
                return sb.ToString();
            }
            if (v.GetType().IsPrimitive || v is string || v.GetType().IsEnum) return v.ToString();
            if (v is System.Collections.IEnumerable en)
            {
                // hash sets and other non-list collections, in iteration order
                var sb = new System.Text.StringBuilder(v.GetType().Name + "{");
                int k = 0;
                foreach (object e in en) { if (k++ >= 400) break; sb.Append(' ').Append(ShowShallow(e)); }
                return sb.Append(" }").ToString();
            }
            return v.GetType().Name;
        }

        private static string ShowShallow(object v)
        {
            if (v is Status stt) return "status " + (HarmonyLib.AccessTools.Field(typeof(Status), "_asset")?.GetValue(stt) as Asset)?.id + "@" + HarmonyLib.AccessTools.Field(typeof(Status), "_end_time")?.GetValue(stt);
            if (v is System.Collections.ICollection) return v.GetType().Name;
            return Show(v);
        }

        /// <summary>Every simple field of every tile, right after load (to find leftovers on reused tiles).</summary>
        private static Dictionary<string, double[]> DumpTiles()
        {
            var tiles = (WorldTile[])HarmonyLib.AccessTools.Field(typeof(MapBox), "tiles_list").GetValue(World.world);
            var d = new Dictionary<string, double[]>();
            foreach (var f in typeof(WorldTile).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                Type ft = f.FieldType;
                bool simple = ft == typeof(int) || ft == typeof(float) || ft == typeof(bool) || ft == typeof(double) || ft == typeof(long) || ft == typeof(short) || ft == typeof(byte) || ft.IsEnum;
                bool asset = typeof(Asset).IsAssignableFrom(ft);
                bool obj = typeof(BaseSimObject).IsAssignableFrom(ft);
                if (!simple && !asset && !obj) continue;
                var v = new double[tiles.Length];
                for (int i = 0; i < tiles.Length; i++)
                {
                    object o = f.GetValue(tiles[i]);
                    v[i] = o == null ? -1e18 : asset ? ((Asset)o).id.GetHashCode() : obj ? ((BaseSimObject)o).getID() : Convert.ToDouble(o);
                }
                d[f.Name] = v;
            }
            return d;
        }

        private int _betweenReports;

        /// <summary>Anything that changes the world between ticks (per-frame code) breaks lockstep.</summary>
        private void BetweenTicks(long tick)
        {
            if (_phase != Phase.Running || _cur == null || _cur.Count == 0 || _betweenReports >= 5) return;
            TickHash last = _cur[_cur.Count - 1];
            TickHash now = StateHash.Compute(tick, last.detail != null);
            if (now.buildings != last.buildings || now.buildingCount != last.buildingCount)
            {
                _betweenReports++;
                Log.Info("DETERMINISM: run " + (_run + 1) + ": buildings changed BETWEEN ticks " + tick + " and " + (tick + 1) + " (count " + last.buildingCount + " -> " + now.buildingCount + ")");
            }
            if (last.detail == null) return;
            if (now.units == last.units && now.unitCount == last.unitCount) return;
            _betweenReports++;
            Log.Info("DETERMINISM: run " + (_run + 1) + ": creatures changed BETWEEN ticks " + tick + " and " + (tick + 1) + " (frame " + Time.frameCount + ")");
            foreach (string l in CompareDetail(last.detail, now.detail)) Log.Info("DETERMINISM: " + l);
        }

        private void OnTick(long tick)
        {
            if (_phase != Phase.Running || _cur == null) return;
            if (tick >= _dumpTick && tick <= _dumpTo)
            {
                while (_dumps.Count <= _run) _dumps.Add(new List<Dictionary<string, string>>());
                _dumps[_run].Add(DumpFields());
            }
            _cur.Add(StateHash.Compute(tick, TakeDetail()));
        }

        private void FinishRun()
        {
            float secs = Time.unscaledTime - _phaseAt;
            string file = Path.Combine(_dir, "run" + (_run + 1) + "-pid" + System.Diagnostics.Process.GetCurrentProcess().Id + ".tsv");
            using (var w = new StreamWriter(file))
            {
                w.WriteLine("# slot " + _slot + " seed " + _seed + " worldfall " + (WorldfallBridge.Present ? "yes" : "no") + " cpu " + SystemInfo.processorType);
                w.WriteLine(TickHash.Header);
                foreach (TickHash t in _cur) w.WriteLine(t.Line());
            }
            Log.Info("DETERMINISM: run " + (_run + 1) + " done, " + _ticks + " ticks in " + secs.ToString("0.0") + " s -> " + file);
            _results.Add(_cur);
            _traces.Add(SectionTrace.Current);
            SectionTrace.Current = new List<SectionTrace.Entry>();
            _cur = null;
            _run++;
            if (_run < _runs) { StartLoad(); return; }
            Report();
        }

        private void Report()
        {
            List<TickHash> a = _results[0];
            var lines = new List<string>();
            for (int r = 1; r < _results.Count; r++)
            {
                List<TickHash> b = _results[r];
                int n = Math.Min(a.Count, b.Count);
                int bad = -1;
                string what = null;
                for (int i = 0; i < n; i++)
                {
                    what = a[i].FirstDiff(b[i]);
                    if (what != null) { bad = i; break; }
                }
                if (bad < 0) { lines.Add("run " + (r + 1) + " matches run 1 for all " + (n - 1) + " ticks"); continue; }
                lines.Add("run " + (r + 1) + " differs from run 1 at tick " + a[bad].tick + ": " + what);
                if (bad == 0) lines.Add("  (tick 0 = right after loading: loading the save is not repeatable)");
                if (a[bad].detail != null && b[bad].detail != null) lines.AddRange(CompareDetail(a[bad].detail, b[bad].detail));
                else lines.Add("  (no per-creature detail kept this far in; rerun with fewer ticks to see which creatures)");
                string split = SectionTrace.FirstSplit(_traces[0], _traces[r], a[bad].tick);
                if (split != null) lines.Add("  dice first split " + split);
                if (what == "random number state")
                    for (int i = bad + 1; i < n; i++)
                    {
                        string w2 = a[i].FirstDiff(b[i]);
                        if (w2 == null || w2 == "random number state") continue;
                        lines.Add("first difference in the world itself: tick " + a[i].tick + ": " + w2);
                        if (a[i].detail != null && b[i].detail != null) lines.AddRange(CompareDetail(a[i].detail, b[i].detail));
                        break;
                    }
            }
            if (_tileDumps.Count >= 2)
            {
                lines.Add("tile fields that differ right after loading (run 1 vs run 2):");
                foreach (var kv in _tileDumps[0])
                {
                    if (!_tileDumps[1].TryGetValue(kv.Key, out double[] o)) continue;
                    int n = 0, first = -1;
                    for (int i = 0; i < kv.Value.Length && i < o.Length; i++) if (kv.Value[i] != o[i]) { n++; if (first < 0) first = i; }
                    if (n > 0) lines.Add("  " + kv.Key + ": " + n + " tiles (e.g. tile " + first + ": " + kv.Value[first] + " vs " + o[first] + ")");
                }
            }
            for (int r = 0; r < _dumps.Count; r++)
            {
                // every dumped tick, for following a field over time
                var all = new List<string>();
                for (int i = 0; i < _dumps[r].Count; i++)
                    foreach (var kv in _dumps[r][i]) all.Add((_dumpTick + i) + " " + kv.Key + " = " + kv.Value);
                File.WriteAllLines(Path.Combine(_dir, "dump-run" + (r + 1) + "-all.txt"), all);
            }
            if (_dumps.Count >= 2)
            {
                int at = -1;
                for (int i = 0; i < _dumps[0].Count && i < _dumps[1].Count && at < 0; i++)
                    foreach (var kv in _dumps[0][i])
                        if (!_dumps[1][i].TryGetValue(kv.Key, out string o) || o != kv.Value) { at = i; break; }
                if (at < 0)
                {
                    lines.Add("dumped fields match for ticks " + _dumpTick + "-" + _dumpTo);
                    var last = new List<string>();
                    foreach (var kv in _dumps[0][_dumps[0].Count - 1]) last.Add(kv.Key + " = " + kv.Value);
                    File.WriteAllLines(Path.Combine(_dir, "dump-run1.txt"), last);
                }
                else
                {
                    lines.Add("fields that differ after tick " + (_dumpTick + at) + " (count per field, one example):");
                    for (int r = 0; r < 2; r++)
                    {
                        var all = new List<string>();
                        if (at > 0) foreach (var kv in _dumps[r][at - 1]) all.Add("before " + kv.Key + " = " + kv.Value);
                        foreach (var kv in _dumps[r][at]) all.Add(kv.Key + " = " + kv.Value);
                        File.WriteAllLines(Path.Combine(_dir, "dump-run" + (r + 1) + ".txt"), all);
                    }
                    var perField = new Dictionary<string, int>();
                    var example = new Dictionary<string, string>();
                    foreach (var kv in _dumps[0][at])
                    {
                        if (!_dumps[1][at].TryGetValue(kv.Key, out string o) || o == kv.Value) continue;
                        string field = kv.Key.Substring(kv.Key.IndexOf('.') + 1);
                        perField[field] = (perField.TryGetValue(field, out int c) ? c : 0) + 1;
                        if (!example.ContainsKey(field)) example[field] = kv.Key + ": " + kv.Value + " vs " + o;
                    }
                    foreach (var kv in perField) lines.Add("  " + kv.Value + "x " + example[kv.Key]);
                    string split = SectionTrace.FirstSplit(_traces[0], _traces[1], _dumpTick + at);
                    if (split != null) lines.Add("  watched creatures/dice first split " + split);
                }
            }
            if (!string.IsNullOrEmpty(_traceExtra))
                for (int r = 0; r < _traces.Count; r++)
                {
                    // the extra traced calls (they carry their object/arguments), for diffing whole runs
                    var tl = new List<string>();
                    foreach (SectionTrace.Entry e in _traces[r]) if (e.section.Contains("[") || e.section.Contains("(")) tl.Add(e.tick + "	" + e.section + "	" + e.state.ToString("x8"));
                    File.WriteAllLines(Path.Combine(_dir, "trace-run" + (r + 1) + ".txt"), tl);
                }
            foreach (string l in lines) Log.Info("DETERMINISM: " + l);
            File.WriteAllLines(Path.Combine(_dir, "report.txt"), lines);
            Log.Info("DETERMINISM done");
            LockstepClock.Stop();
            _phase = Phase.Done;
            if (_quit) Application.Quit();
        }

        private static IEnumerable<string> CompareDetail(Dictionary<long, UnitRec> a, Dictionary<long, UnitRec> b)
        {
            int shown = 0, total = 0;
            var outp = new List<string>();
            foreach (var kv in a)
            {
                string d;
                if (!b.TryGetValue(kv.Key, out UnitRec o)) d = "only in run 1";
                else if (kv.Value.Hash() == o.Hash()) continue;
                else d = kv.Value.Diff(o);
                total++;
                if (shown++ < 12) outp.Add("  creature " + kv.Key + ": " + d);
            }
            foreach (var kv in b)
                if (!a.ContainsKey(kv.Key)) { total++; if (shown++ < 12) outp.Add("  creature " + kv.Key + ": only in the later run"); }
            outp.Insert(0, "  " + total + " creatures differ");
            return outp;
        }

        private void Fail(string why)
        {
            Log.Error("DETERMINISM failed: " + why);
            _phase = Phase.Done;
            if (_quit) Application.Quit();
        }
    }
}
