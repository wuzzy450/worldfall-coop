using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall keeps most of its own state in the world objects' custom data (a creature's gear,
    /// bag, reputation, house plot, quest kills; a building's owner and shop; a kingdom's war chest).
    /// Its menus, conversations and frame code write that data with BaseSystemData.set/remove/...
    /// outside the simulation step, which in lockstep changes one PC's world only.
    ///
    /// Here such a write travels as an input (the object by its manager and ID, the key, the value)
    /// and is made in a tick on every PC. Until then this PC's own reads outside ticks see the value
    /// it wrote (an overlay), so the menu that wrote it reads its own write back; ticks only see
    /// applied writes, the same on every PC.
    /// </summary>
    public static class DataCalls
    {
        private static readonly AccessTools.FieldRef<Actor, ActorData> _actorData = AccessTools.FieldRefAccess<Actor, ActorData>("data");
        private static readonly AccessTools.FieldRef<Building, BuildingData> _buildingData = AccessTools.FieldRefAccess<Building, BuildingData>("data");
        private static readonly Dictionary<Type, Func<long, BaseSystemData>> _owners = new Dictionary<Type, Func<long, BaseSystemData>>();
        private static readonly Dictionary<Type, string> _ownerName = new Dictionary<Type, string>();
        private static readonly Dictionary<string, Func<long, BaseSystemData>> _byName = new Dictionary<string, Func<long, BaseSystemData>>();

        /// <summary>Pending writes this PC sent (what its own reads outside ticks see).</summary>
        private sealed class Pending { public object value; public bool removed; public float at; }
        private static readonly Dictionary<BaseSystemData, Dictionary<string, Pending>> _pending = new Dictionary<BaseSystemData, Dictionary<string, Pending>>();

        /// <summary>Frame code whose writes stay on this PC (cosmetic memories, ...): depth of such scopes.</summary>
        [ThreadStatic] private static int _local;
        private static bool _replaying;
        public static int Sent, Replayed, Local;
        private static readonly Dictionary<string, int> _sentByKey = new Dictionary<string, int>();
        private static float _reportAt;
        private static readonly HashSet<string> _warned = new HashSet<string>();

        public static void Install(Harmony h)
        {
            MapOwners();
            var prefix = new HarmonyMethod(typeof(DataCalls), nameof(WritePrefix));
            int n = 0;
            foreach (MethodInfo m in typeof(BaseSystemData).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "set" && m.Name != "change" && !m.Name.StartsWith("remove", StringComparison.Ordinal) && m.Name != "addFlag" && m.Name != "removeFlag") continue;
                if (m.GetParameters().Length == 0 || m.GetParameters()[0].ParameterType != typeof(string)) continue;
                try { h.Patch(m, prefix: prefix); n++; }
                catch (Exception e) { Log.Warn("lockstep: couldn't relay data " + m.Name + ": " + e.Message); }
            }
            foreach (KeyValuePair<Type, string> g in new Dictionary<Type, string> { { typeof(int), nameof(GetInt) }, { typeof(long), nameof(GetLong) }, { typeof(float), nameof(GetFloat) }, { typeof(string), nameof(GetString) }, { typeof(bool), nameof(GetBool) } })
            {
                MethodInfo m = AccessTools.Method(typeof(BaseSystemData), "get", new[] { typeof(string), g.Key.MakeByRefType(), g.Key });
                if (m == null) { Log.Warn("lockstep: data get(" + g.Key.Name + ") not found"); continue; }
                try { h.Patch(m, postfix: new HarmonyMethod(typeof(DataCalls), g.Value)); }
                catch (Exception e) { Log.Warn("lockstep: couldn't overlay data get: " + e.Message); }
            }
            MethodInfo has = AccessTools.Method(typeof(BaseSystemData), "hasFlag");
            if (has != null) h.Patch(has, postfix: new HarmonyMethod(typeof(DataCalls), nameof(HasFlagPostfix)));
            if (!WorldCalls.Register(h, AccessTools.Method(typeof(DataCalls), nameof(Apply)))) Log.Warn("lockstep: data writes can't travel");
            Log.Info("lockstep: " + n + " data writers travel as inputs outside ticks (" + _owners.Count + " kinds of world objects)");
        }

        /// <summary>Which manager holds the objects whose data is of a given type.</summary>
        private static void MapOwners()
        {
            foreach (FieldInfo mf in typeof(MapBox).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                MethodInfo get = mf.FieldType.GetMethod("get", new[] { typeof(long) });
                if (get == null) continue;
                Type t = get.ReturnType;
                FieldInfo df = null;
                for (Type x = t; x != null && df == null; x = x.BaseType) df = x.GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (df == null || !typeof(BaseSystemData).IsAssignableFrom(df.FieldType) || _owners.ContainsKey(df.FieldType)) continue;
                FieldInfo mgrField = mf;
                FieldInfo dataField = df;
                Func<long, BaseSystemData> f = id =>
                {
                    object mgr = mgrField.GetValue(World.world);
                    object o = mgr == null ? null : get.Invoke(mgr, new object[] { id });
                    return o == null ? null : dataField.GetValue(o) as BaseSystemData;
                };
                _owners[df.FieldType] = f;
                _ownerName[df.FieldType] = df.FieldType.Name;
                _byName[df.FieldType.Name] = f;
            }
        }

        /// <summary>Runs body with its data writes kept on this PC (cosmetic frame code).</summary>
        public static void LocalOnly(Action body)
        {
            _local++;
            try { body(); }
            finally { _local--; }
        }

        public static void EnterLocal() => _local++;
        public static void LeaveLocal() { if (_local > 0) _local--; }

        private static bool Relaying => !_replaying && _local == 0 && LockstepControl.Running && LockstepClock.Active && !LockstepClock.InTick;

        private static string Owner(BaseSystemData d, out long id)
        {
            id = d.id;
            if (id < 0 || !_ownerName.TryGetValue(d.GetType(), out string name)) return null;
            // the same object: not a copy, not some other system's data with a clashing id
            return ReferenceEquals(_owners[d.GetType()](id), d) ? name : null;
        }

        private static readonly bool TraceYou = Array.Exists(Environment.GetCommandLineArgs(), x => x == "-coopfall-lockstep-watchall");

        private static string Short(string s) => s.Length > 700 ? s.Substring(0, 700) : s;

        private static bool WritePrefix(BaseSystemData __instance, MethodBase __originalMethod, object[] __args)
        {
            if (TraceYou && LockstepClock.InTick && !_replaying && __args.Length > 1 && __args[0] as string == "fp_you")
                Log.Info("lockstep: in-tick fp_you write t" + LockstepClock.Tick + " #" + __instance.id + " = " + __args[1] + " from " + Short(new System.Diagnostics.StackTrace(2, false).ToString().Replace(Environment.NewLine, " <")));
            if (!Relaying) return true;
            string owner = Owner(__instance, out long id);
            if (owner == null) return true;   // not a world object's data (or a stale one): as before
            string op = __originalMethod.Name;
            string key = __args[0] as string;
            if (key == null || LocalKeys.Contains(key) || TooOften(key)) return true;
            object value = __args.Length > 1 ? __args[1] : null;
            string kind;
            switch (op)
            {
                case "set": kind = Kind(value); break;
                case "change":
                {
                    // done here as the set it ends in (the caller's own reads see the result)
                    __instance.get(key, out int cur, 0);
                    int v = cur + (int)__args[1];
                    v = Math.Max((int)__args[2], Math.Min((int)__args[3], v));
                    op = "set"; kind = "i"; value = v;
                    break;
                }
                case "removeInt": kind = "i"; op = "remove"; break;
                case "removeLong": kind = "l"; op = "remove"; break;
                case "removeFloat": kind = "r"; op = "remove"; break;
                case "removeString": kind = "s"; op = "remove"; break;
                case "removeBool": kind = "b"; op = "remove"; break;
                case "addFlag": kind = "g"; op = "set"; value = true; break;
                case "removeFlag": kind = "g"; op = "remove"; break;
                default: return true;
            }
            if (kind == null) return true;
            bool remove = op == "remove";
            // nothing changes (per-frame code repeating itself): no input
            if (Same(__instance, key, kind, remove, value)) return false;
            string enc = Encode(remove, kind, value);
            if (enc == null) return true;
            WorldCalls.SuppressDedupe = true;
            try { Apply(owner, id, kind + key, enc); }   // caught by WorldCalls and sent
            finally { WorldCalls.SuppressDedupe = false; }
            Note(__instance, kind + key, remove, value);
            // this PC's Chronicle copy already holds the change; until the tick applies it, ticks here must see
            // the same old data as everywhere else (this PC's own reads get the pending value from the overlay)
            if (kind == "s" && ChronicleKeys.Contains(key)) ForgetChronicle(id);
            Sent++;
            _sentByKey.TryGetValue(key, out int c);
            _sentByKey[key] = c + 1;
            Report();
            return false;
        }

        private static string Kind(object v)
        {
            switch (v)
            {
                case int _: return "i";
                case long _: return "l";
                case float _: return "r";
                case string _: return "s";
                case bool _: return "b";
                case null: return "s";
            }
            return null;
        }

        private static string Encode(bool remove, string kind, object v)
        {
            if (remove) return "-";
            switch (kind)
            {
                case "i": return ((int)v).ToString(CultureInfo.InvariantCulture);
                case "l": return ((long)v).ToString(CultureInfo.InvariantCulture);
                case "r": return BitConverter.SingleToInt32Bits((float)v).ToString(CultureInfo.InvariantCulture);
                case "b": case "g": return (bool)v ? "1" : "0";
                case "s": return v == null ? "~" : "=" + (string)v;
            }
            return null;
        }

        private static object Decode(string kind, string enc)
        {
            switch (kind)
            {
                case "i": return int.Parse(enc, CultureInfo.InvariantCulture);
                case "l": return long.Parse(enc, CultureInfo.InvariantCulture);
                case "r": return BitConverter.Int32BitsToSingle(int.Parse(enc, CultureInfo.InvariantCulture));
                case "b": case "g": return enc == "1";
                case "s": return enc == "~" ? null : enc.Substring(1);
            }
            return null;
        }

        /// <summary>The value as this PC sees it now (its pending write, else the data).</summary>
        private static bool Same(BaseSystemData d, string key, string kind, bool remove, object value)
        {
            if (_pending.TryGetValue(d, out Dictionary<string, Pending> mine) && mine.TryGetValue(kind + key, out Pending p))
                return p.removed == remove && (remove || Equals(p.value, value));
            bool has = Has(d, key, kind, out object cur);
            return remove ? !has : has && Equals(cur, value);
        }

        private static bool Has(BaseSystemData d, string key, string kind, out object v)
        {
            v = null;
            switch (kind)
            {
                case "i": if (Dict(d.custom_data_int) is Dictionary<string, int> ddi && ddi.TryGetValue(key, out int i)) { v = i; return true; } return false;
                case "l": if (Dict(d.custom_data_long) is Dictionary<string, long> ddl && ddl.TryGetValue(key, out long l)) { v = l; return true; } return false;
                case "r": if (Dict(d.custom_data_float) is Dictionary<string, float> ddf && ddf.TryGetValue(key, out float f)) { v = f; return true; } return false;
                case "b": if (Dict(d.custom_data_bool) is Dictionary<string, bool> ddb && ddb.TryGetValue(key, out bool b)) { v = b; return true; } return false;
                case "s": if (Dict(d.custom_data_string) is Dictionary<string, string> dds && dds.TryGetValue(key, out string s)) { v = s; return true; } return false;
                case "g": if (d.custom_data_flags != null && d.custom_data_flags.Contains(key)) { v = true; return true; } return false;
            }
            return false;
        }

        private static void Note(BaseSystemData d, string kk, bool remove, object value)
        {
            if (!_pending.TryGetValue(d, out Dictionary<string, Pending> mine)) _pending[d] = mine = new Dictionary<string, Pending>();
            mine[kk] = new Pending { value = value, removed = remove, at = Time.unscaledTime };
        }

        private static readonly HashSet<string> ChronicleKeys = new HashSet<string> { "fp_kin", "fp_mem", "fp_you" };
        private static System.Collections.IDictionary _chronRecords;

        private static void ForgetChronicle(long id)
        {
            try
            {
                Type ch = WorldfallBridge.Assembly?.GetType("FirstPerson.Chronicle", false);
                if (_chronRecords == null && ch != null) _chronRecords = AccessTools.Field(ch, "Records")?.GetValue(null) as System.Collections.IDictionary;
                _chronRecords?.Remove(id);
            }
            catch { }
        }

        /// <summary>Relayed (WorldCalls): one data write, in a tick on every PC.</summary>
        public static void Apply(string owner, long id, string kindKey, string enc)
        {
            if (!LockstepClock.InTick || kindKey == null || kindKey.Length < 1) return;
            if (!_byName.TryGetValue(owner, out Func<long, BaseSystemData> find)) return;
            BaseSystemData d = find(id);
            if (d == null) return;
            string kind = kindKey.Substring(0, 1), key = kindKey.Substring(1);
            bool remove = enc == "-";
            object v = remove ? null : Decode(kind, enc);
            bool was = _replaying;
            _replaying = true;
            try
            {
                switch (kind)
                {
                    case "i": if (remove) d.removeInt(key); else d.set(key, (int)v); break;
                    case "l": if (remove) d.removeLong(key); else d.set(key, (long)v); break;
                    case "r": if (remove) d.removeFloat(key); else d.set(key, (float)v); break;
                    case "b": if (remove) d.removeBool(key); else d.set(key, (bool)v); break;
                    case "s": if (remove) d.removeString(key); else d.set(key, (string)v); break;
                    case "g": if (remove || !(bool)v) d.removeFlag(key); else d.addFlag(key); break;
                }
                Replayed++;
            }
            finally { _replaying = was; }
            // Worldfall's Chronicle keeps a parsed copy of each person's kin/memories/regards and flushes it whole:
            // a PC whose copy predates this write would put the old value back at its next flush (fp_you "talks"
            // drift 2026-10-10). Every PC drops its copy here, in the same tick, and reads the data again.
            if (kind == "s" && ChronicleKeys.Contains(key)) ForgetChronicle(d.id);
            // this PC's own write has landed: its reads see the data again
            if (_pending.TryGetValue(d, out Dictionary<string, Pending> mine) && mine.TryGetValue(kindKey, out Pending p) && p.removed == remove && (remove || Equals(p.value, v)))
            {
                mine.Remove(kindKey);
                if (mine.Count == 0) _pending.Remove(d);
            }
        }

        // ------------------------------------------------------------------ this PC's reads

        private static bool Overlay(BaseSystemData d, string kk, out Pending p)
        {
            p = null;
            if (_pending.Count == 0 || LockstepClock.InTick || kk == null) return false;
            if (!_pending.TryGetValue(d, out Dictionary<string, Pending> mine) || !mine.TryGetValue(kk, out p)) return false;
            if (Time.unscaledTime - p.at > 5f) { mine.Remove(kk); return false; }   // lost: the data is the truth again
            return true;
        }

        private static void GetInt(BaseSystemData __instance, string pKey, ref int pResult, int pDefault) { if (Overlay(__instance, "i" + pKey, out Pending p)) pResult = p.removed ? pDefault : (int)p.value; }
        private static void GetLong(BaseSystemData __instance, string pKey, ref long pResult, long pDefault) { if (Overlay(__instance, "l" + pKey, out Pending p)) pResult = p.removed ? pDefault : (long)p.value; }
        private static void GetFloat(BaseSystemData __instance, string pKey, ref float pResult, float pDefault) { if (Overlay(__instance, "r" + pKey, out Pending p)) pResult = p.removed ? pDefault : (float)p.value; }
        private static void GetString(BaseSystemData __instance, string pKey, ref string pResult, string pDefault) { if (Overlay(__instance, "s" + pKey, out Pending p)) pResult = p.removed ? pDefault : (string)p.value; }
        private static void GetBool(BaseSystemData __instance, string pKey, ref bool pResult, bool pDefault) { if (Overlay(__instance, "b" + pKey, out Pending p)) pResult = p.removed ? pDefault : (bool)p.value; }

        private static readonly Dictionary<Type, FieldInfo> _dictField = new Dictionary<Type, FieldInfo>();

        /// <summary>A custom data container's dictionary (internal to the game).</summary>
        private static Dictionary<string, T> Dict<T>(CustomDataContainer<T> c)
        {
            if (c == null) return null;
            if (!_dictField.TryGetValue(typeof(T), out FieldInfo f)) _dictField[typeof(T)] = f = AccessTools.Field(typeof(CustomDataContainer<T>), "dict");
            return f?.GetValue(c) as Dictionary<string, T>;
        }

        private static void HasFlagPostfix(BaseSystemData __instance, string pID, ref bool __result)
        {
            if (_pending.Count == 0 || LockstepClock.InTick || pID == null) return;
            if (_pending.TryGetValue(__instance, out Dictionary<string, Pending> mine) && mine.TryGetValue("g" + pID, out Pending p) && Time.unscaledTime - p.at <= 5f)
                __result = !p.removed && (bool)p.value;
        }

        /// <summary>A new epoch: nothing is pending any more.</summary>
        public static void Reset() => _pending.Clear();

        private static void Report()
        {
            if (Time.unscaledTime - _reportAt < 60f) return;
            _reportAt = Time.unscaledTime;
            var sb = new StringBuilder("lockstep: data writes sent in the last minute:");
            foreach (KeyValuePair<string, int> kv in _sentByKey) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            Log.Info(sb.ToString());
            _sentByKey.Clear();
        }

        // ------------------------------------------------------------------ checks

        // fp_bagorder: the owner's bag/hotbar display order (Worldfall's Hotbar writes it on its own PC, e.g. right
        // after a load); what a hotbar key does travels as its own input (Gear.PutOn...). Drift 2026-10-10 epoch 3 t10.
        /// <summary>Keys that are only one PC's cosmetic memory (kept local, left out of the check).</summary>
        public static readonly HashSet<string> LocalKeys = new HashSet<string> { "fp_seen", "fp_bagorder" };

        /// <summary>Keys some frame code writes many times a second (a sweep): kept on this PC from then on.</summary>
        private static readonly HashSet<string> _autoLocal = new HashSet<string>();
        private static readonly Dictionary<string, int> _thisSecond = new Dictionary<string, int>();
        private static float _secondAt;

        private static bool TooOften(string key)
        {
            if (_autoLocal.Contains(key)) return true;
            if (Time.unscaledTime - _secondAt > 1f) { _secondAt = Time.unscaledTime; _thisSecond.Clear(); }
            _thisSecond.TryGetValue(key, out int n);
            _thisSecond[key] = ++n;
            if (n <= 30) return false;
            _autoLocal.Add(key);
            Log.Warn("lockstep: Worldfall writes \"" + key + "\" more than 30 times a second outside ticks (a sweep over many creatures?): kept on each PC from now on. Called from:" + Environment.NewLine + new System.Diagnostics.StackTrace(3, false));
            return true;
        }

        /// <summary>Checksum of every world object's Worldfall data (keys starting fp_), order-independent.</summary>
        public static ulong Hash()
        {
            ulong h = 0;
            MapBox w = World.world;
            if (w == null) return 0;
            var rec = new Dictionary<string, ulong>();
            foreach (Actor a in w.units) if (a != null) h += Rec(rec, "u", _actorData(a));
            foreach (Building b in w.buildings) if (b != null) h += Rec(rec, "b", _buildingData(b));
            foreach (City c in w.cities) if (c != null) h += Rec(rec, "c", c.data);
            foreach (Kingdom k in w.kingdoms) if (k != null) h += Rec(rec, "k", k.data);
            _recent.Enqueue(new KeyValuePair<long, Dictionary<string, ulong>>(LockstepClock.Tick, rec));
            while (_recent.Count > 30) _recent.Dequeue();
            return h;
        }

        /// <summary>The last few checks' per-object hashes (to name what drifted when wfdata differs).</summary>
        private static readonly Queue<KeyValuePair<long, Dictionary<string, ulong>>> _recent = new Queue<KeyValuePair<long, Dictionary<string, ulong>>>();

        private static ulong Rec(Dictionary<string, ulong> rec, string kind, BaseSystemData d)
        {
            ulong v = One(d);
            if (v != 0) rec[kind + d.id] = v;
            return v;
        }

        /// <summary>On a desync: the kept per-object hashes and every object's Worldfall data now, as text.</summary>
        public static void WriteRecent(string path)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (KeyValuePair<long, Dictionary<string, ulong>> t in _recent)
                {
                    var keys = new List<string>(t.Value.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string k in keys) sb.Append("tick ").Append(t.Key).Append(' ').Append(k).Append(' ').Append(t.Value[k].ToString("x16")).Append('\n');
                }
                MapBox w = World.world;
                if (w != null)
                {
                    foreach (Actor a in w.units) if (a != null) { string d = Describe(_actorData(a)); if (d.Length > 0) sb.Append("now u").Append(a.getID()).Append(' ').Append(d).Append('\n'); }
                    foreach (Building b in w.buildings) if (b != null) { string d = Describe(_buildingData(b)); if (d.Length > 0) sb.Append("now b").Append(b.getID()).Append(' ').Append(d).Append('\n'); }
                    foreach (City c in w.cities) if (c != null) { string d = Describe(c.data); if (d.Length > 0) sb.Append("now c").Append(c.getID()).Append(' ').Append(d).Append('\n'); }
                    foreach (Kingdom k in w.kingdoms) if (k != null) { string d = Describe(k.data); if (d.Length > 0) sb.Append("now k").Append(k.getID()).Append(' ').Append(d).Append('\n'); }
                }
                if (w != null)
                {
                    var bl = new List<string>();
                    foreach (Building b in w.buildings)
                        if (b != null) bl.Add("building " + b.getID() + " " + (AccessTools.Field(typeof(Building), "asset").GetValue(b) as BuildingAsset)?.id + " " + (b.current_tile == null ? "-" : b.current_tile.x + "," + b.current_tile.y) + (b.isAlive() ? "" : " dead"));
                    bl.Sort(StringComparer.Ordinal);
                    foreach (string x in bl) sb.Append(x).Append('\n');
                }
                System.IO.File.WriteAllText(path, sb.ToString());
                Log.Info("lockstep: Worldfall data of the last checks written to " + path);
            }
            catch (Exception e) { Log.Warn("lockstep: can't write the Worldfall data: " + e.Message); }
        }

        /// <summary>Test log: one object's Worldfall data as text (sorted).</summary>
        public static string Describe(BaseSystemData d)
        {
            var l = new List<string>();
            if (d == null) return "";
            Add(l, Dict(d.custom_data_int)); Add(l, Dict(d.custom_data_long)); Add(l, Dict(d.custom_data_float)); Add(l, Dict(d.custom_data_bool)); Add(l, Dict(d.custom_data_string));
            if (d.custom_data_flags != null) foreach (string f in d.custom_data_flags) if (f.StartsWith("fp_", StringComparison.Ordinal)) l.Add(f);
            l.Sort(StringComparer.Ordinal);
            return string.Join(" ", l.ToArray());
        }

        private static void Add<T>(List<string> l, Dictionary<string, T> d)
        {
            if (d == null) return;
            foreach (KeyValuePair<string, T> kv in d)
                if (kv.Key.StartsWith("fp_", StringComparison.Ordinal) && !LocalKeys.Contains(kv.Key))
                    l.Add(kv.Key + "=" + (kv.Value is float f ? f.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(kv.Value, CultureInfo.InvariantCulture)));
        }

        private static ulong One(BaseSystemData d)
        {
            if (d == null) return 0;
            ulong h = StateHash.Mix((ulong)d.id);
            bool any = false;
            any |= Mix(ref h, Dict(d.custom_data_int), v => (ulong)v);
            any |= Mix(ref h, Dict(d.custom_data_long), v => (ulong)v);
            any |= Mix(ref h, Dict(d.custom_data_float), v => (ulong)BitConverter.SingleToInt32Bits(v));
            any |= Mix(ref h, Dict(d.custom_data_bool), v => v ? 1UL : 2UL);
            any |= Mix(ref h, Dict(d.custom_data_string), v => (ulong)(uint)Fnv(v));
            if (d.custom_data_flags != null)
                foreach (string f in d.custom_data_flags)
                    if (f.StartsWith("fp_", StringComparison.Ordinal)) { h += StateHash.Mix((ulong)(uint)Fnv(f) ^ 0x51UL); any = true; }
            return any ? StateHash.Mix(h) : 0;
        }

        private static bool Mix<T>(ref ulong h, Dictionary<string, T> d, Func<T, ulong> v)
        {
            if (d == null) return false;
            bool any = false;
            ulong sum = 0;
            foreach (KeyValuePair<string, T> kv in d)
            {
                if (!kv.Key.StartsWith("fp_", StringComparison.Ordinal) || LocalKeys.Contains(kv.Key)) continue;
                sum += StateHash.Mix(((ulong)(uint)Fnv(kv.Key) << 32) ^ v(kv.Value));
                any = true;
            }
            h ^= sum;
            return any;
        }

        private static int Fnv(string s)
        {
            if (s == null) return 0;
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return (int)h;
        }
    }
}
