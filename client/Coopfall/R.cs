using System;
using System.Collections.Generic;
using System.Reflection;

namespace Coopfall
{
    /// <summary>
    /// Cached reflection for WorldBox members that are internal/private in Assembly-CSharp.
    /// Lookups walk the base classes, so private members of a base type are found too.
    /// A member that doesn't exist (other game version) resolves to null and calls become no-ops.
    /// </summary>
    public static class R
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<string, MethodInfo> _methods = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();
        private static readonly HashSet<string> _warned = new HashSet<string>();

        public static MethodInfo Method(Type t, string name, params Type[] args)
        {
            string key = t.FullName + "." + name + "(" + string.Join(",", Array.ConvertAll(args, a => a.Name)) + ")";
            if (_methods.TryGetValue(key, out MethodInfo m)) return m;
            for (Type c = t; c != null && m == null; c = c.BaseType)
                m = c.GetMethod(name, Any, null, args, null);
            if (m == null) Warn("method " + key + " not found");
            _methods[key] = m;
            return m;
        }

        /// <summary>First method with that name and parameter count (for methods with optional parameters).</summary>
        public static MethodInfo MethodN(Type t, string name, int argCount)
        {
            string key = t.FullName + "." + name + "/" + argCount;
            if (_methods.TryGetValue(key, out MethodInfo m)) return m;
            for (Type c = t; c != null && m == null; c = c.BaseType)
                foreach (MethodInfo mi in c.GetMethods(Any))
                    if (mi.Name == name && mi.GetParameters().Length == argCount) { m = mi; break; }
            if (m == null) Warn("method " + key + " not found");
            _methods[key] = m;
            return m;
        }

        public static FieldInfo Field(Type t, string name)
        {
            string key = t.FullName + "." + name;
            if (_fields.TryGetValue(key, out FieldInfo f)) return f;
            for (Type c = t; c != null && f == null; c = c.BaseType)
                f = c.GetField(name, Any);
            if (f == null) Warn("field " + key + " not found");
            _fields[key] = f;
            return f;
        }

        public static object Get(object o, string field)
        {
            if (o == null) return null;
            FieldInfo f = Field(o.GetType(), field);
            try { return f?.GetValue(o); } catch { return null; }
        }

        public static object GetStatic(Type t, string field)
        {
            FieldInfo f = Field(t, field);
            try { return f?.GetValue(null); } catch { return null; }
        }

        public static void Set(object o, string field, object value)
        {
            if (o == null) return;
            FieldInfo f = Field(o.GetType(), field);
            try { f?.SetValue(o, value); } catch (Exception e) { Warn("set " + field + ": " + e.Message); }
        }

        /// <summary>Calls an instance method; argument types are taken from the declared types passed in.</summary>
        public static object Call(object o, string name, Type[] types, params object[] args)
        {
            if (o == null) return null;
            MethodInfo m = Method(o.GetType(), name, types);
            if (m == null) return null;
            try { return m.Invoke(o, args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        public static object Call0(object o, string name) { return Call(o, name, Type.EmptyTypes); }

        /// <summary>Calls a method by parameter count, filling missing trailing arguments with their defaults.</summary>
        public static object CallN(object o, string name, int argCount, params object[] args)
        {
            if (o == null) return null;
            MethodInfo m = MethodN(o.GetType(), name, argCount);
            if (m == null) return null;
            ParameterInfo[] ps = m.GetParameters();
            var full = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                full[i] = i < args.Length ? args[i] : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
            try { return m.Invoke(o, full); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        /// <summary>
        /// Compiled getter/setter for an internal field, for per-frame use (plain reflection is too slow
        /// for every creature every frame). Falls back to reflection if the runtime can't emit code.
        /// </summary>
        public static void FastField<TObj, TVal>(string name, out Func<TObj, TVal> get, out Action<TObj, TVal> set)
        {
            FieldInfo f = Field(typeof(TObj), name);
            get = null; set = null;
            if (f == null) { get = _ => default(TVal); set = (o, v) => { }; return; }
            try
            {
                var g = new System.Reflection.Emit.DynamicMethod("get_" + name, typeof(TVal), new[] { typeof(TObj) }, typeof(R).Module, true);
                var il = g.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldfld, f);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                get = (Func<TObj, TVal>)g.CreateDelegate(typeof(Func<TObj, TVal>));
                var s = new System.Reflection.Emit.DynamicMethod("set_" + name, null, new[] { typeof(TObj), typeof(TVal) }, typeof(R).Module, true);
                il = s.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Stfld, f);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                set = (Action<TObj, TVal>)s.CreateDelegate(typeof(Action<TObj, TVal>));
            }
            catch (Exception e)
            {
                Warn("fast field " + name + ": " + e.Message);
                get = o => (TVal)f.GetValue(o);
                set = (o, v) => f.SetValue(o, v);
            }
        }

        private static void Warn(string msg)
        {
            if (_warned.Add(msg)) Log.Warn("reflection: " + msg);
        }

        // ---------------------------------------------------------------- game shortcuts

        public static MapStats MapStats { get { return Get(World.world, "map_stats") as MapStats; } }
        public static WorldLaws WorldLaws { get { return Get(World.world, "world_laws") as WorldLaws; } }
        public static ZoneCalculator Zones { get { return Get(World.world, "zone_calculator") as ZoneCalculator; } }
    }
}
