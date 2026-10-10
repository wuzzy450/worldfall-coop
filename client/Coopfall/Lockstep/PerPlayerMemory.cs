using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// What each PC remembers on its own: Worldfall's explored maps (fp_seen; only the HUD and minimap
    /// read them). People's memories, kin and regards are read by the simulation, so they are shared
    /// (the chronicle sweep runs in ticks, TownsInTick). Explored maps stay out of the shared world (DataCalls.LocalKeys),
    /// but an epoch loads the host's save, which carries the host's copy. So every PC keeps its own
    /// copy across the load: written out of Worldfall's caches and taken from the old world just before
    /// the load, put back (by creature ID) into the new one right after. Creatures this PC had nothing
    /// for lose the host's values. A PC joining for the first time starts with nothing.
    /// </summary>
    public static class PerPlayerMemory
    {
        private sealed class Kept { public readonly Dictionary<string, string> s = new Dictionary<string, string>(); public long t; public bool hasT; }

        private static readonly AccessTools.FieldRef<Actor, ActorData> _data = AccessTools.FieldRefAccess<Actor, ActorData>("data");
        private static Dictionary<long, Kept> _kept;
        private static bool _taken;
        private static MethodInfo _writeAll, _exploredReset, _chronReset, _flush, _cached;

        private static void Find()
        {
            if (_writeAll != null || WorldfallBridge.Assembly == null) return;
            Type ex = WorldfallBridge.Assembly.GetType("FirstPerson.Explored", false), ch = WorldfallBridge.Assembly.GetType("FirstPerson.Chronicle", false);
            _writeAll = ex == null ? null : AccessTools.Method(ex, "WriteAll");
            _exploredReset = ex == null ? null : AccessTools.Method(ex, "Reset");
            _chronReset = ch == null ? null : AccessTools.Method(ch, "Reset");
            _flush = ch == null ? null : AccessTools.Method(ch, "Flush", new[] { typeof(Actor) });
            _cached = ch == null ? null : AccessTools.Method(ch, "Cached", new[] { typeof(long) });
        }

        /// <summary>Before this PC loads an epoch: take this PC's copy from the world it is leaving.</summary>
        public static void Take(bool hadWorld)
        {
            if (_taken) return;   // a reload of the same epoch: keep what the old world had
            _taken = true;
            _kept = null;
            if (!hadWorld || World.world?.units == null) { _kept = new Dictionary<long, Kept>(); return; }
            Find();
            var keys = DataCalls.LocalKeys;
            DataCalls.EnterLocal();
            try
            {
                try { _writeAll?.Invoke(null, null); } catch (Exception e) { Log.Warn("lockstep: Worldfall's explored maps not written: " + (e.InnerException ?? e).Message); }
            }
            finally { DataCalls.LeaveLocal(); }
            _kept = new Dictionary<long, Kept>();
            foreach (Actor a in World.world.units)
            {
                if ((a == null ? null : _data(a)) == null) continue;
                Kept k = null;
                foreach (string key in keys)
                {
                    string v = null;
                    if (key != "fp_memt" && _data(a).custom_data_string != null && _data(a).custom_data_string.TryGetValue(key, out v)) (k = k ?? new Kept()).s[key] = v;
                }
                if (k != null) _kept[_data(a).id] = k;
            }
            Log.Info("lockstep: kept this PC's explored maps of " + _kept.Count + " creatures across the load");
        }

        /// <summary>Right after an epoch loaded (every PC, before tick 0): this PC's own copy goes back in.</summary>
        public static void Put()
        {
            if (!_taken || World.world?.units == null) return;
            _taken = false;
            Dictionary<long, Kept> kept = _kept ?? new Dictionary<long, Kept>();
            _kept = null;
            var keys = DataCalls.LocalKeys;
            int n = 0;
            DataCalls.EnterLocal();
            try {
            foreach (Actor a in World.world.units)
            {
                if ((a == null ? null : _data(a)) == null) continue;
                kept.TryGetValue(_data(a).id, out Kept k);
                ActorData d = _data(a);
                foreach (string key in keys)
                {
                    if (key == "fp_memt") continue;
                    if (k != null && k.s.TryGetValue(key, out string v)) d.set(key, v);
                    else if (d.custom_data_string != null && d.custom_data_string.TryGetValue(key, out _)) d.removeString(key);
                }
                if (k != null) n++;
            }
            } finally { DataCalls.LeaveLocal(); }
            // Worldfall's caches read the data again
            try { _exploredReset?.Invoke(null, null); } catch { }
            Log.Info("lockstep: this PC's explored maps put back for " + n + " creatures");
        }

        /// <summary>For the test's state line: a digest of this PC's local data of one creature.</summary>
        public static string Digest(Actor a)
        {
            if ((a == null ? null : _data(a)) == null) return "-";
            var l = new List<string>();
            foreach (string key in DataCalls.LocalKeys)
                if (_data(a).custom_data_string != null && _data(a).custom_data_string.TryGetValue(key, out string v)) l.Add(key + ":" + (v ?? "").Length);
            l.Sort(StringComparer.Ordinal);
            return l.Count == 0 ? "-" : string.Join(",", l.ToArray());
        }
    }
}
