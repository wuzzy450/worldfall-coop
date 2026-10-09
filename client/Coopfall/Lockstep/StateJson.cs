using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Writes a graph of plain objects (Worldfall's per-player campaign state) as JSON, with
    /// world objects (creatures, kingdoms, tiles, assets) by ID, and builds it again after a
    /// load. Delegates, Unity objects and reflection objects are left out (the new object keeps
    /// what its constructor gave it). Every PC builds from the same JSON, so the copies match.
    /// </summary>
    public sealed class StateJson
    {
        private const int MaxDepth = 24;
        private static readonly JObject Skip = new JObject { ["$skip"] = 1 };

        private readonly Dictionary<object, int> _ids = new Dictionary<object, int>(RefEq.I);
        private readonly Dictionary<int, object> _objs = new Dictionary<int, object>();
        private readonly Type _never;
        public int Skipped;

        /// <param name="never">A type never written (Worldfall's mod object).</param>
        public StateJson(Type never) { _never = never; }

        private sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq I = new RefEq();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        private static bool Opaque(Type t) =>
            typeof(Delegate).IsAssignableFrom(t) || typeof(UnityEngine.Object).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t)
            || typeof(System.Threading.Tasks.Task).IsAssignableFrom(t) || t.IsPointer || t == typeof(IntPtr);

        // ------------------------------------------------------------------ write

        public JToken Write(object v) => W(v, 0);

        private JToken W(object v, int depth)
        {
            if (v == null) return JValue.CreateNull();
            Type t = v.GetType();
            if (Opaque(t) || t == _never) { Skipped++; return Skip; }
            // "which world this belongs to" (Law wipes its records when the world changes): the world now
            if (v is MapBox) return new JValue("~W");
            if (t.IsEnum) return new JObject { ["$e"] = Name(t), ["v"] = Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture) };
            if (WorldCalls.IsWorldLeaf(t))
            {
                string s = WorldCalls.Leaf(v);
                if (s == null) { Skipped++; return Skip; }
                return new JValue("~" + s);
            }
            if (t.IsPrimitive || t == typeof(decimal))
                return new JObject { ["$p"] = Name(t), ["v"] = Convert.ToString(v, CultureInfo.InvariantCulture) };
            // the game's own objects belong to the world (saved with it) or are shared tables: never copied
            if (t.Assembly == typeof(Actor).Assembly && !(v is IEnumerable)) { Skipped++; return Skip; }
            if (depth > MaxDepth) { Skipped++; return Skip; }
            int id = 0;
            if (!t.IsValueType)
            {
                if (_ids.TryGetValue(v, out int r)) return new JObject { ["$r"] = r };
                id = _ids.Count + 1;
                _ids[v] = id;
            }
            if (v is Array arr)
            {
                var a = new JArray();
                foreach (object o in arr) a.Add(W(o, depth + 1));
                return new JObject { ["$a"] = Name(t.GetElementType()), ["$id"] = id, ["v"] = a };
            }
            if (v is IDictionary d)
            {
                var a = new JArray();
                foreach (DictionaryEntry e in d) a.Add(new JArray(W(e.Key, depth + 1), W(e.Value, depth + 1)));
                return new JObject { ["$d"] = Name(t), ["$id"] = id, ["v"] = a };
            }
            if (v is IEnumerable en && !(v is string) && Adder(t) != null)
            {
                var a = new JArray();
                foreach (object o in en) a.Add(W(o, depth + 1));
                if (t.Name.StartsWith("Stack`", StringComparison.Ordinal)) { var r = new JArray(); for (int i = a.Count - 1; i >= 0; i--) r.Add(a[i]); a = r; }
                return new JObject { ["$l"] = Name(t), ["$id"] = id, ["v"] = a };
            }
            var f = new JObject();
            foreach (FieldInfo fi in Fields(t))
            {
                object fv;
                try { fv = fi.GetValue(v); } catch { continue; }
                f[Key(fi)] = W(fv, depth + 1);
            }
            return new JObject { ["$o"] = Name(t), ["$id"] = id, ["f"] = f };
        }

        private static string Key(FieldInfo f) => f.DeclaringType.Name + "." + f.Name;

        private static IEnumerable<FieldInfo> Fields(Type t)
        {
            for (Type x = t; x != null && x != typeof(object) && x != typeof(ValueType); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.IsLiteral) yield return f;
        }

        private static MethodInfo Adder(Type t)
        {
            foreach (string n in new[] { "Add", "Enqueue", "Push" })
            {
                MethodInfo m = Array.Find(t.GetMethods(), x => x.Name == n && x.GetParameters().Length == 1);
                if (m != null) return m;
            }
            return null;
        }

        private static string Name(Type t) => t.AssemblyQualifiedName;

        private static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();

        private static Type TypeOf(string aqn)
        {
            if (aqn == null) return null;
            if (_types.TryGetValue(aqn, out Type t)) return t;
            try
            {
                t = Type.GetType(aqn, an =>
                {
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == an.Name) return a;
                    return null;
                }, (a, n, ic) => a != null ? a.GetType(n, false, ic) : Type.GetType(n, false, ic), false);
            }
            catch { t = null; }
            _types[aqn] = t;
            return t;
        }

        // ------------------------------------------------------------------ read

        /// <summary>False: leave the field as it is (left out, or a world object that's gone).</summary>
        public bool Read(JToken tok, Type want, out object v)
        {
            v = null;
            if (tok == null) return false;
            if (tok.Type == JTokenType.Null) return !want.IsValueType || Nullable.GetUnderlyingType(want) != null;
            if (tok.Type == JTokenType.String)
            {
                string s = (string)tok;
                if (s == "~W") { v = World.world; return want.IsAssignableFrom(typeof(MapBox)); }
                return s.StartsWith("~", StringComparison.Ordinal) && WorldCalls.ReadLeaf(s.Substring(1), want, out v);
            }
            if (!(tok is JObject o) || o["$skip"] != null) return false;
            if (o["$r"] != null) return _objs.TryGetValue((int)o["$r"], out v);
            if (o["$e"] != null)
            {
                Type et = TypeOf((string)o["$e"]);
                if (et == null) return false;
                v = Enum.ToObject(et, long.Parse((string)o["v"], CultureInfo.InvariantCulture));
                return true;
            }
            if (o["$p"] != null)
            {
                Type pt = TypeOf((string)o["$p"]);
                if (pt == null) return false;
                v = Convert.ChangeType((string)o["v"], pt, CultureInfo.InvariantCulture);
                return true;
            }
            int id = (int?)o["$id"] ?? 0;
            try
            {
                if (o["$a"] != null)
                {
                    Type et = TypeOf((string)o["$a"]);
                    if (et == null) return false;
                    var items = new List<object>();
                    var arr0 = (JArray)o["v"];
                    Array arr = Array.CreateInstance(et, arr0.Count);
                    if (id != 0) _objs[id] = arr;
                    for (int i = 0; i < arr0.Count; i++) if (Read(arr0[i], et, out object x)) arr.SetValue(x, i);
                    v = arr;
                    return true;
                }
                if (o["$d"] != null)
                {
                    Type dt = TypeOf((string)o["$d"]);
                    if (dt == null) return false;
                    var d = (IDictionary)Activator.CreateInstance(dt, true);
                    if (id != 0) _objs[id] = d;
                    Type[] ga = dt.IsGenericType ? dt.GetGenericArguments() : new[] { typeof(object), typeof(object) };
                    foreach (JToken e in (JArray)o["v"])
                        if (Read(e[0], ga[0], out object k) && k != null && Read(e[1], ga[1], out object x)) d[k] = x;
                    v = d;
                    return true;
                }
                if (o["$l"] != null)
                {
                    Type lt = TypeOf((string)o["$l"]);
                    if (lt == null) return false;
                    object l = Activator.CreateInstance(lt, true);
                    if (id != 0) _objs[id] = l;
                    MethodInfo add = Adder(lt);
                    Type et = add.GetParameters()[0].ParameterType;
                    // a world object that's gone is left out of the list
                    foreach (JToken e in (JArray)o["v"]) if (Read(e, et, out object x) && (x != null || e.Type == JTokenType.Null)) add.Invoke(l, new[] { x });
                    v = l;
                    return true;
                }
                if (o["$o"] != null)
                {
                    Type ot = TypeOf((string)o["$o"]);
                    if (ot == null || !want.IsAssignableFrom(ot)) return false;
                    object x;
                    try { x = Activator.CreateInstance(ot, true); }
                    catch { x = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(ot); }
                    if (id != 0) _objs[id] = x;
                    var f = (JObject)o["f"];
                    foreach (FieldInfo fi in Fields(ot))
                    {
                        JToken ft = f[Key(fi)];
                        if (ft == null || !Read(ft, fi.FieldType, out object fv)) continue;
                        try { fi.SetValue(x, fv); } catch { }
                    }
                    v = x;
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Warn("lockstep: couldn't rebuild a saved " + (want?.Name ?? "?") + ": " + (e.InnerException ?? e).Message);
            }
            return false;
        }
    }
}
