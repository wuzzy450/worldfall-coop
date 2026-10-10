using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// "-coopfall-lockstep-trace": every value field of every creature (and its AI) after each of
    /// the last <see cref="Keep"/> ticks, kept in memory. On a desync both PCs write it out
    /// (lockstep-ringN.txt) as the fields that changed since the creature's previous record, so a
    /// diff of host and guest files shows the first field that split.
    ///
    /// It also compares each creature right after a tick with the same creature right before the
    /// next one: anything that changed in between was written by per-frame code (drawing,
    /// another mod) and is logged as "between ticks" once per field.
    /// </summary>
    public static class UnitRing
    {
        public const int Keep = 400;

        private struct Fld { public FieldInfo f; public string name; public int kind; }
        private const int KFloat = 0, KInt = 1, KBool = 2, KDouble = 3, KLong = 4, KEnum = 5, KV2 = 6, KV3 = 7, KSim = 8, KAsset = 9, KByte = 10;

        private static Fld[] _actorFields, _aiFields;
        private static FieldInfo _ai;

        /// <summary>Changes made by drawing on purpose (the tick copies them again before running).</summary>
        private static readonly HashSet<string> DrawingOnly = new HashSet<string>
        {
            "is_visible", "timestamp_session_ate_food", "cur_transform_position", "current_rotation", "current_scale", "m_CachedPtr",
        };

        private class Snap { public long tick; public Dictionary<long, ulong[]> units = new Dictionary<long, ulong[]>(); }
        private static readonly Queue<Snap> _ring = new Queue<Snap>();
        private static Snap _last;
        private static readonly HashSet<string> _betweenSeen = new HashSet<string>();

        private static void Init()
        {
            if (_actorFields != null) return;
            _ai = HarmonyLib.AccessTools.Field(typeof(Actor), "ai");
            _actorFields = Fields(typeof(Actor), "");
            _aiFields = _ai == null ? new Fld[0] : Fields(_ai.FieldType, "ai.");
        }

        private static Fld[] Fields(Type type, string prefix)
        {
            var l = new List<Fld>();
            for (Type t = type; t != null && t != typeof(object) && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Type ft = f.FieldType;
                    int k = ft == typeof(float) ? KFloat : ft == typeof(int) ? KInt : ft == typeof(bool) ? KBool : ft == typeof(double) ? KDouble
                        : ft == typeof(long) ? KLong : ft == typeof(byte) ? KByte : ft.IsEnum ? KEnum : ft == typeof(Vector2) ? KV2 : ft == typeof(Vector3) ? KV3
                        : typeof(BaseSimObject).IsAssignableFrom(ft) ? KSim : typeof(Asset).IsAssignableFrom(ft) ? KAsset : -1;
                    if (k < 0) continue;
                    l.Add(new Fld { f = f, name = prefix + f.Name, kind = k });
                }
            return l.ToArray();
        }

        private static ulong Bits(Fld d, object o)
        {
            object v;
            try { v = d.f.GetValue(o); } catch { return 0xDEAD; }
            switch (d.kind)
            {
                case KFloat: return (uint)BitConverter.SingleToInt32Bits((float)v);
                case KInt: return (uint)(int)v;
                case KBool: return (bool)v ? 1UL : 0UL;
                case KDouble: return (ulong)BitConverter.DoubleToInt64Bits((double)v);
                case KLong: return (ulong)(long)v;
                case KByte: return (byte)v;
                case KEnum: return (ulong)Convert.ToInt64(v);
                case KV2: { var x = (Vector2)v; return (ulong)(uint)BitConverter.SingleToInt32Bits(x.x) << 32 | (uint)BitConverter.SingleToInt32Bits(x.y); }
                case KV3: { var x = (Vector3)v; return ((ulong)(uint)BitConverter.SingleToInt32Bits(x.x) << 32 | (uint)BitConverter.SingleToInt32Bits(x.y)) ^ (ulong)(uint)BitConverter.SingleToInt32Bits(x.z) * 0x9E3779B97F4A7C15UL; }
                case KSim: return v is BaseSimObject so && so != null ? (ulong)so.getID() + 1 : 0;
                case KAsset: return v is Asset a ? (ulong)(uint)(a.id?.GetHashCode() ?? 1) : 0;
            }
            return 0;
        }

        private static string Show(Fld d, ulong b)
        {
            switch (d.kind)
            {
                case KFloat: return BitConverter.Int32BitsToSingle((int)(uint)b).ToString("R");
                case KDouble: return BitConverter.Int64BitsToDouble((long)b).ToString("R");
                case KV2: return BitConverter.Int32BitsToSingle((int)(uint)(b >> 32)).ToString("R") + "," + BitConverter.Int32BitsToSingle((int)(uint)b).ToString("R");
                case KSim: return b == 0 ? "null" : "#" + (b - 1);
                case KV3: case KAsset: return b.ToString("x16");
                default: return ((long)b).ToString();
            }
        }

        private static ulong[] Values(Actor a)
        {
            var v = new ulong[_actorFields.Length + _aiFields.Length + 11];
            int x = _actorFields.Length + _aiFields.Length;
            v[x] = (uint)StateHash.Gear(a);
            v[x + 1] = (uint)a.getHealth();
            v[x + 2] = StateHash.Cooldowns(a);
            v[x + 3] = (uint)StateHash.Path(a);
            v[x + 4] = (uint)StateHash.Task(a);
            // what decides a creature's next enemy besides the ring fields (drift at t1573, 2026-10-09)
            v[x + 5] = IdSetHash(_aggro?.GetValue(a));
            v[x + 6] = IdSetHash(_ignore?.GetValue(a));
            try { v[x + 7] = a.isInsideSomething() ? 1UL : 0UL; } catch { v[x + 7] = 0xDEAD; }
            try { v[x + 8] = (_inMagnet != null && (bool)_inMagnet.Invoke(a, null)) ? 1UL : 0UL; } catch { v[x + 8] = 0xDEAD; }
            try { v[x + 9] = R.CallN(a, "isFlying", 0) is bool fl && fl ? 1UL : 0UL; } catch { v[x + 9] = 0xDEAD; }
            try { object act = R.Get(R.Get(a, "ai"), "action"); v[x + 10] = act != null && R.Get(act, "special_prevent_can_be_attacked") is bool sp && sp ? 1UL : 0UL; } catch { v[x + 10] = 0xDEAD; }
            for (int i = 0; i < _actorFields.Length; i++) v[i] = Bits(_actorFields[i], a);
            object ai = _ai?.GetValue(a);
            if (ai != null) for (int i = 0; i < _aiFields.Length; i++) v[_actorFields.Length + i] = Bits(_aiFields[i], ai);
            return v;
        }

        private static readonly string[] Extra = { "equipment", "health", "decision cooldowns (hash)", "path (hash)", "task (hash)", "aggression targets (hash)", "ignored targets (hash)", "inside something", "in magnet", "flying", "can't be attacked (action)" };
        private static readonly FieldInfo _aggro = typeof(Actor).GetField("_aggression_targets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), _ignore = typeof(BaseSimObject).GetField("_targets_to_ignore", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo _inMagnet = typeof(Actor).GetMethod("isInMagnet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        /// <summary>Order-free hash of a set of creature IDs (0 for none).</summary>
        private static ulong IdSetHash(object set)
        {
            if (!(set is HashSet<long> h) || h.Count == 0) return 0;
            ulong sum = 0;
            foreach (long id in h) { ulong z = (ulong)id * 0x9E3779B97F4A7C15UL; z ^= z >> 29; sum += z; }
            return sum ^ (ulong)h.Count;
        }
        private static string Name(int i) => i < _actorFields.Length ? _actorFields[i].name : i - _actorFields.Length < _aiFields.Length ? _aiFields[i - _actorFields.Length].name : Extra[i - _actorFields.Length - _aiFields.Length];
        private static Fld Field(int i) => i < _actorFields.Length ? _actorFields[i] : i - _actorFields.Length < _aiFields.Length ? _aiFields[i - _actorFields.Length] : new Fld { name = Name(i), kind = KLong };

        /// <summary>Right before a tick runs: what changed since the previous tick ended.</summary>
        public static void BeforeTick(long tick)
        {
            if (_last == null || _last.tick != tick) return;
            foreach (Actor a in World.world.units)
            {
                if (a == null || !_last.units.TryGetValue(a.getID(), out ulong[] was)) continue;
                ulong[] now = Values(a);
                for (int i = 0; i < now.Length; i++)
                {
                    if (now[i] == was[i]) continue;
                    string n = Name(i);
                    if (DrawingOnly.Contains(n) || !_betweenSeen.Add(n)) continue;
                    Log.Warn("lockstep: creature " + a.getID() + " (" + a.asset?.id + ") " + n + " changed between ticks " + tick + " and " + (tick + 1) + ": " + Show(Field(i), was[i]) + " -> " + Show(Field(i), now[i])
                        + " (camera " + (Camera.main != null ? Camera.main.orthographicSize.ToString("0") : "?") + ")");
                }
            }
        }

        /// <summary>After a tick: remember every creature.</summary>
        public static void AfterTick(long tick)
        {
            Init();
            var s = new Snap { tick = tick };
            foreach (Actor a in World.world.units) if (a != null) s.units[a.getID()] = Values(a);
            _ring.Enqueue(s);
            while (_ring.Count > Keep) _ring.Dequeue();
            _last = s;
        }

        public static void Clear() { _ring.Clear(); _last = null; }

        /// <summary>Writes the ring as per-creature changes (first record of each creature in full).</summary>
        public static void Write(string path)
        {
            if (_ring.Count == 0) return;
            try
            {
                using (var w = new System.IO.StreamWriter(path))
                {
                    var prev = new Dictionary<long, ulong[]>();
                    foreach (Snap s in _ring)
                    {
                        var ids = new List<long>(s.units.Keys);
                        ids.Sort();
                        foreach (long id in ids)
                        {
                            ulong[] v = s.units[id];
                            prev.TryGetValue(id, out ulong[] p);
                            for (int i = 0; i < v.Length; i++)
                                if (p == null || p[i] != v[i]) w.WriteLine(s.tick + "\t" + id + "\t" + Name(i) + "\t" + Show(Field(i), v[i]));
                            prev[id] = v;
                        }
                        foreach (long gone in new List<long>(prev.Keys)) if (!s.units.ContainsKey(gone)) { w.WriteLine(s.tick + "\t" + gone + "\tgone"); prev.Remove(gone); }
                    }
                }
                Log.Info("lockstep: wrote the last " + _ring.Count + " ticks of every creature to " + System.IO.Path.GetFileName(path));
            }
            catch (Exception e) { Log.Warn("lockstep: couldn't write the creature ring: " + e.Message); }
        }
    }
}
