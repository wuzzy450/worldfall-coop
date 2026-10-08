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
        private long _dumpTick = -1;
        private bool _dumpAll;
        private readonly HashSet<long> _dumpIds = new HashSet<long>();
        private readonly List<Dictionary<string, string>> _dumps = new List<Dictionary<string, string>>();
        private readonly List<Dictionary<string, double[]>> _tileDumps = new List<Dictionary<string, double[]>>();
        private Phase _phase = Phase.WaitWorld;
        private int _run;
        private float _phaseAt;
        private bool _sawLoading;
        private readonly List<List<TickHash>> _results = new List<List<TickHash>>();
        private readonly List<List<SectionTrace.Entry>> _traces = new List<List<SectionTrace.Entry>>();
        private List<TickHash> _cur;
        private long _detailBudget;   // creature records kept per run for pinpointing
        private const long DetailPerRun = 1000000;
        private string _dir;

        public bool Finished => _phase == Phase.Done;
        private static string _traceExtra;

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
                else if (s == "-coopfall-determinism-tiles" && i + 1 < a.Length) int.TryParse(a[i + 1], out StateHash.TileEvery);
            }
            if (slot < 0) return null;
            var p = new DeterminismProbe(slot, Math.Max(1, ticks), Math.Max(1, runs), seed, quit);
            // -coopfall-determinism-dump <tick> <id,id,...>: every field of those creatures after that tick
            for (int i = 0; i + 2 < a.Length; i++)
                if (a[i].ToLowerInvariant() == "-coopfall-determinism-dump")
                {
                    long.TryParse(a[i + 1], out p._dumpTick);
                    if (a[i + 2] == "all") p._dumpAll = true;
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
                    _phase = Phase.Running;
                    _phaseAt = Time.unscaledTime;
                    LockstepClock.Granted = _ticks;
                    break;

                case Phase.Running:
                    if (LockstepClock.Tick < _ticks) return;
                    FinishRun();
                    break;
            }
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
            foreach (Actor a in World.world.units)
                if (a != null && (_dumpAll || _dumpIds.Contains(a.getID())))
                    for (Type t = a.GetType(); t != null && t != typeof(object); t = t.BaseType)
                        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                        {
                            object v;
                            try { v = f.GetValue(a); } catch { continue; }
                            d[a.getID() + "." + t.Name + "." + f.Name] = Show(v);
                        }
            return d;
        }

        private static string Show(object v)
        {
            if (v == null) return "null";
            if (v is float fl) return fl.ToString("R");
            if (v is double db) return db.ToString("R");
            if (v is Vector2 v2) return v2.x.ToString("R") + "," + v2.y.ToString("R");
            if (v is Vector3 v3) return v3.x.ToString("R") + "," + v3.y.ToString("R") + "," + v3.z.ToString("R");
            if (v is WorldTile wt) return "tile " + wt.tile_id;
            if (v is BaseSimObject so) return so.GetType().Name + " " + so.getID();
            if (v is Asset asset) return "asset " + asset.id;
            if (v is System.Collections.ICollection c) return v.GetType().Name + "[" + c.Count + "]";
            if (v.GetType().IsPrimitive || v is string || v.GetType().IsEnum) return v.ToString();
            return v.GetType().Name;
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
            if (last.detail == null) return;
            TickHash now = StateHash.Compute(tick, true);
            if (now.units == last.units && now.unitCount == last.unitCount) return;
            _betweenReports++;
            Log.Info("DETERMINISM: run " + (_run + 1) + ": creatures changed BETWEEN ticks " + tick + " and " + (tick + 1) + " (frame " + Time.frameCount + ")");
            foreach (string l in CompareDetail(last.detail, now.detail)) Log.Info("DETERMINISM: " + l);
        }

        private void OnTick(long tick)
        {
            if (_phase != Phase.Running || _cur == null) return;
            if (tick == _dumpTick) _dumps.Add(DumpFields());
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
            if (_dumps.Count >= 2)
            {
                lines.Add("fields that differ after tick " + _dumpTick + " (count per field, one example):");
                var perField = new Dictionary<string, int>();
                var example = new Dictionary<string, string>();
                foreach (var kv in _dumps[0])
                {
                    if (!_dumps[1].TryGetValue(kv.Key, out string o) || o == kv.Value) continue;
                    string field = kv.Key.Substring(kv.Key.IndexOf('.') + 1);
                    perField[field] = (perField.TryGetValue(field, out int c) ? c : 0) + 1;
                    if (!example.ContainsKey(field)) example[field] = kv.Key + ": " + kv.Value + " vs " + o;
                }
                foreach (var kv in perField) lines.Add("  " + kv.Value + "x " + example[kv.Key]);
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
