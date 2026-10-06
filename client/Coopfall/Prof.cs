using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Coopfall
{
    /// <summary>Per-part timing of Coopfall's own frame work, logged every 10 s (finds what slows a game).</summary>
    public static class Prof
    {
        private static readonly Dictionary<string, double> _ms = new Dictionary<string, double>();
        private static readonly List<string> _order = new List<string>();
        private static readonly Stopwatch _sw = new Stopwatch();
        private static float _since = -1f;
        private static int _frames;

        private static HashSet<string> _off;

        /// <summary>Test switch: "-coopfall-off a,b" skips those parts (to find what slows a game).</summary>
        public static bool Off(string part)
        {
            if (_off == null)
            {
                _off = new HashSet<string>();
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "-coopfall-off") foreach (string x in args[i + 1].Split(',')) _off.Add(x.Trim());
                if (_off.Count > 0) Log.Info("test: switched off " + string.Join(",", new List<string>(_off).ToArray()));
            }
            return _off.Contains(part);
        }

        public static void Run(string part, Action a)
        {
            if (Off(part)) return;
            int d0 = Dirty();
            long t0 = Stopwatch.GetTimestamp();
            try { a(); }
            finally
            {
                int dd = Dirty() - d0;
                if (dd > 0) { _dirtyBy.TryGetValue(part, out int c); _dirtyBy[part] = c + dd; }
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (!_ms.ContainsKey(part)) { _ms[part] = 0; _order.Add(part); }
                _ms[part] += ms;
            }
        }

        private static long _tUpd, _tLate;
        private static double _updToLate, _lateToUpd;

        /// <summary>Called at the start of our LateUpdate: time since our Update = the rest of the update phase.</summary>
        public static void Late()
        {
            long now = Stopwatch.GetTimestamp();
            if (_tUpd != 0) _updToLate += (now - _tUpd) * 1000.0 / Stopwatch.Frequency;
            _tLate = now;
        }

        public static void Frame()
        {
            long nowTs = Stopwatch.GetTimestamp();
            if (_tLate != 0) _lateToUpd += (nowTs - _tLate) * 1000.0 / Stopwatch.Frequency;
            _tUpd = nowTs;
            if (!_benchOn && ProfileActive) { _benchOn = true; Bench.bench_enabled = true; }
            float now = Time.realtimeSinceStartup;
            if (_since < 0f) { _since = now; return; }
            _frames++;
            if (Dirty() > 0) _dirtyFrames++;
            if (now - _since < 10f || _frames == 0) return;
            if (!ProfileActive) { foreach (string p in _order) _ms[p] = 0; _frames = 0; _since = now; return; }
            var sb = new StringBuilder("perf: ").Append(_frames).Append(" frames, ")
                .Append((1000f * (now - _since) / _frames).ToString("0")).Append(" ms each; Coopfall per frame:");
            double total = 0;
            foreach (string p in _order) { double v = _ms[p] / _frames; total += v; sb.Append(' ').Append(p).Append(' ').Append(v.ToString("0.0")); }
            sb.Append(" = ").Append(total.ToString("0.0")).Append(" ms; creatures ").Append(World.world?.units?.Count ?? 0)
              .Append(", buildings ").Append(World.world?.buildings?.Count ?? 0).Append(", items ").Append(World.world?.items?.Count ?? 0);
            sb.Append("; Update->LateUpdate ").Append((_updToLate / _frames).ToString("0")).Append(" ms, LateUpdate->next Update (drawing, GUI) ").Append((_lateToUpd / _frames).ToString("0")).Append(" ms");
            _updToLate = _lateToUpd = 0;
            var tsync = CoopMod.Instance?.Tiles;
            if (tsync != null) { sb.Append("; tiles applied ").Append(tsync.TilesChanged - _tilesLast).Append(" in ").Append(tsync.ZonesApplied - _zonesLast).Append(" zones"); _tilesLast = tsync.TilesChanged; _zonesLast = tsync.ZonesApplied; }
            sb.Append(", border redraws ").Append(MetaSync.BorderRedraws - _bordersLast); _bordersLast = MetaSync.BorderRedraws;
            int gc = GC.CollectionCount(0);
            sb.Append("; GCs ").Append(gc - _gcLast).Append(", heap ").Append(GC.GetTotalMemory(false) / (1024 * 1024)).Append(" MB");
            _gcLast = gc;
            sb.Append("; chunks dirtied by:");
            foreach (var kv in _dirtyBy) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value);
            sb.Append(", frames starting dirty ").Append(_dirtyFrames);
            _dirtyBy.Clear(); _dirtyFrames = 0;
            Log.Info(sb.ToString());
            LogGameBench();
            LogScene();
            foreach (string p in _order) _ms[p] = 0;
            _frames = 0; _since = now;
        }
            private static readonly Dictionary<string, int> _dirtyBy = new Dictionary<string, int>();
        private static int _dirtyFrames, _gcLast, _tilesLast, _zonesLast, _bordersLast;
        private static System.Reflection.FieldInfo _dirtyField, _mgrField;

        /// <summary>Map chunks waiting for the (expensive) region/path rebuild.</summary>
        private static int Dirty()
        {
            try
            {
                if (World.world == null) return 0;
                if (_mgrField == null) _mgrField = typeof(MapBox).GetField("map_chunk_manager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                object m = _mgrField?.GetValue(World.world);
                if (m == null) return 0;
                if (_dirtyField == null) _dirtyField = typeof(MapChunkManager).GetField("_dirty_chunks_regions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return (_dirtyField?.GetValue(m) as System.Collections.ICollection)?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>What the scene holds (objects piling up slow Unity's drawing): the most common names.</summary>
        private static void LogScene()
        {
            if (!ProfileActive) return;
            try
            {
                var objs = UnityEngine.Object.FindObjectsOfType<Transform>();
                var count = new Dictionary<string, int>();
                int active = 0;
                foreach (Transform t in objs)
                {
                    if (t.gameObject.activeInHierarchy) active++;
                    string n = t.name;
                    int cut = n.IndexOfAny(new[] { '(', ' ', '_' });
                    if (cut > 3) n = n.Substring(0, cut);
                    count.TryGetValue(n, out int c); count[n] = c + 1;
                }
                var list = new List<KeyValuePair<string, int>>(count);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new StringBuilder("scene: ").Append(objs.Length).Append(" objects (").Append(active).Append(" active):");
                for (int i = 0; i < list.Count && i < 10; i++) sb.Append(' ').Append(list[i].Key).Append(' ').Append(list[i].Value);
                Log.Info(sb.ToString());
            }
            catch (Exception e) { Log.Warn("scene count: " + e.Message); }
        }

        private static bool _benchOn;
        private static int _profileActive = -1;
        /// <summary>Only test profiles (-coopfall-profile) log performance.</summary>
        private static bool ProfileActive
        {
            get
            {
                if (_profileActive < 0 && Log.Dir != null) _profileActive = Log.Dir.Replace(System.IO.Path.DirectorySeparatorChar, '/').Contains("/profiles/") ? 1 : 0;
                return _profileActive == 1;
            }
        }

        /// <summary>The game's own benchmark (on in test profiles): its slowest entries right now.</summary>
        private static void LogGameBench()
        {
            if (!_benchOn) return;
            try
            {
                var dict = typeof(Bench).GetField("dict", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as Dictionary<string, BenchmarkGroup>;
                if (dict == null) return;
                var all = new List<KeyValuePair<string, double>>();
                foreach (var g in dict)
                    if (g.Key != "loading")
                    foreach (var d in g.Value.dict_data)
                        all.Add(new KeyValuePair<string, double>(g.Key + "/" + d.Key, d.Value.latest_result));
                all.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new StringBuilder("game bench (ms):");
                for (int i = 0; i < all.Count && i < 14; i++) sb.Append(' ').Append(all[i].Key).Append(' ').Append((all[i].Value * 1000).ToString("0.0"));
                Log.Info(sb.ToString());
            }
            catch (Exception e) { Log.Warn("game bench: " + e.Message); _benchOn = false; }
        }
    }
}
