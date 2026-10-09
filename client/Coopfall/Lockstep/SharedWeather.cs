using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's 3D weather in lockstep. The clouds, lightning and tornadoes themselves are the
    /// same on every PC (they are simulated in ticks), but Worldfall shapes them from per-PC
    /// numbers: a storm's billows and a bolt's zigzag from the object's hash code and the real time
    /// it appeared, and its wind gusts from a generator seeded with the PC's uptime and stepped by
    /// the frame. Here effects made in a tick get a number from a per-epoch counter (the same on
    /// every PC) that Worldfall reads instead, and the gusts come from one generator seeded by the
    /// epoch and stepped once per tick; each PC's own gusts copy it every frame. (Off lockstep the
    /// live sync's WeatherSync copies the host's gusts instead.)
    /// </summary>
    public static class SharedWeather
    {
        private sealed class ByRef : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        private static readonly Dictionary<object, int> _ids = new Dictionary<object, int>(new ByRef());
        private static int _next = 1, _seed = 1;
        private static Type _gustsType;
        private static object _gusts;
        private static MethodInfo _step;
        private static FieldInfo[] _copy;
        private static FieldInfo _fury;
        private static AccessTools.FieldRef<BaseEffect, double> _spawned;
        private static bool _installed;

        public static void Install(Harmony h)
        {
            if (_installed) return;
            _installed = true;
            LockstepClock.AfterTick += _ => AfterTick();
            _spawned = AccessTools.FieldRefAccess<BaseEffect, double>("_timestamp_spawned");
            MethodInfo activate = AccessTools.Method(typeof(BaseEffect), "activate");
            if (activate != null) h.Patch(activate, postfix: new HarmonyMethod(typeof(SharedWeather), nameof(ActivatePostfix)));
            else Log.Error("lockstep: BaseEffect.activate not found: Worldfall's storms may look different on each PC");
            Assembly wf = WorldfallBridge.Assembly;
            if (wf == null) return;
            Type scene = wf.GetType("FirstPerson.SceneCollector", false);
            int n = 0;
            if (scene != null)
                foreach (string m in new[] { "AddStorm", "AddBolt", "AddTornado" })
                {
                    MethodInfo mi = AccessTools.Method(scene, m);
                    if (mi == null) { Log.Warn("lockstep: Worldfall's SceneCollector." + m + " not found: its weather may look different on each PC"); continue; }
                    try { h.Patch(mi, transpiler: new HarmonyMethod(typeof(SharedWeather), nameof(SeedTranspiler))); n++; }
                    catch (Exception e) { Log.Warn("lockstep: couldn't share Worldfall's " + m + " seeds: " + e.Message); }
                }
            _gustsType = wf.GetType("FirstPerson.Core.Gusts", false);
            _step = _gustsType == null ? null : AccessTools.Method(_gustsType, "Step");
            if (_step != null)
            {
                var l = new List<FieldInfo>();
                foreach (string f in new[] { "Strength", "DirX", "DirY", "Count" })
                {
                    FieldInfo fi = AccessTools.Field(_gustsType, f);
                    if (fi != null) l.Add(fi);
                }
                _copy = l.ToArray();
                _fury = AccessTools.Field(_gustsType, "Fury");
                h.Patch(_step, prefix: new HarmonyMethod(typeof(SharedWeather), nameof(StepPrefix)));
            }
            else Log.Warn("lockstep: Worldfall's Gusts.Step not found: wind gusts will blow differently on each PC");
            Log.Info("lockstep: Worldfall weather shared (" + n + " seed readers" + (_step != null ? ", gusts" : "") + ")");
        }

        /// <summary>A new epoch (LockstepSession): the counter and the gusts start again from its seed.</summary>
        public static void Reset(int seed)
        {
            _ids.Clear();
            _next = 1;
            _seed = seed;
            _gusts = null;
            if (_gustsType == null) return;
            try { _gusts = Activator.CreateInstance(_gustsType, LockstepClock.TickSeed(seed, 0x6057)); }
            catch (Exception e) { Log.Warn("lockstep: shared gusts: " + (e.InnerException ?? e).Message); }
        }

        /// <summary>Tests: the shared gusts (strength, gust count) and how many effects have a shared number.</summary>
        public static string State()
        {
            if (_gusts == null || _copy == null || _copy.Length < 4) return "no shared gusts";
            return "shared gust " + ((float)_copy[0].GetValue(_gusts)).ToString("0.000") + " #" + _copy[3].GetValue(_gusts) + ", " + _ids.Count + " effects numbered";
        }

        /// <summary>LockstepClock, after every tick.</summary>
        public static void AfterTick()
        {
            if (_gusts == null || _step == null) return;
            // Worldfall's default prevailing wind; the storm boost is left out (it depends on where you stand)
            try { _step.Invoke(_gusts, new object[] { LockstepClock.DefaultStep, 0.6f, 0.2f, 0f }); }
            catch (Exception e) { Log.Warn("lockstep: shared gusts: " + (e.InnerException ?? e).Message); _gusts = null; }
        }

        private static void ActivatePostfix(BaseEffect __instance)
        {
            if (!LockstepClock.Active) return;
            if (LockstepClock.InTick) _ids[__instance] = _next++;
            else _ids.Remove(__instance);   // made by this PC alone: keeps its own look
        }

        private static bool StepPrefix(object __instance)
        {
            if (!LockstepClock.Active || _gusts == null || ReferenceEquals(__instance, _gusts)) return true;
            foreach (FieldInfo f in _copy) f.SetValue(__instance, f.GetValue(_gusts));
            _fury?.SetValue(__instance, 0f);
            return false;
        }

        // ------------------------------------------------------------------ Worldfall's seed readers

        public static int Hash(object o)
        {
            if (LockstepClock.Active && o != null && _ids.TryGetValue(o, out int id)) return LockstepClock.TickSeed(_seed, id);
            return RuntimeHelpers.GetHashCode(o);
        }

        public static int VirtualHash(object o)
        {
            if (LockstepClock.Active && o != null && _ids.TryGetValue(o, out int id)) return LockstepClock.TickSeed(_seed, id);
            return o.GetHashCode();
        }

        public static double Stamp(BaseEffect e)
        {
            if (LockstepClock.Active && e != null && _ids.TryGetValue(e, out int id)) return id * 0.001;
            return _spawned(e);
        }

        private static IEnumerable<CodeInstruction> SeedTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo rtHash = AccessTools.Method(typeof(RuntimeHelpers), "GetHashCode", new[] { typeof(object) });
            FieldInfo stampField = AccessTools.Field(typeof(BaseEffect), "_timestamp_spawned");
            MethodInfo stampGet = AccessTools.PropertyGetter(typeof(BaseEffect), "timestamp_spawned");
            var list = new List<CodeInstruction>(code);
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction c = list[i];
                if (c.Calls(rtHash)) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(SharedWeather), nameof(Hash)); }
                else if (c.operand is MethodInfo hm && hm.Name == "GetHashCode" && !hm.IsStatic && hm.GetParameters().Length == 0)
                {
                    // a constrained. prefix (value types) can't take a static call; effects are classes
                    if (i > 0 && list[i - 1].opcode == OpCodes.Constrained) continue;
                    c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(SharedWeather), nameof(VirtualHash));
                }
                else if (c.LoadsField(stampField) || (stampGet != null && c.Calls(stampGet)))
                {
                    c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(SharedWeather), nameof(Stamp));
                }
            }
            return list;
        }
    }
}
