using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall poses its 3D figures from each PC's real-time clock (walk cycles, breathing, a
    /// ship's heel, a creature's bob), so the same creature was mid-step at a different moment on
    /// each PC. In lockstep those poses read the world's clock instead: the tick time plus how far
    /// this frame is into the next tick, so both PCs show the same pose at the same tick. Only
    /// drawing reads it; the simulation never does. Side effects: figures hold still while the
    /// game is paused and move faster at higher speeds, like the world itself.
    /// </summary>
    public static class VisualClock
    {
        private static double _tickTime;
        private static float _tickAt;

        /// <summary>The world's clock for drawing (Unity's real time when lockstep isn't running).</summary>
        public static float Now()
        {
            if (!LockstepClock.Active) return Time.unscaledTime;
            float frac = Mathf.Clamp(Time.unscaledTime - _tickAt, 0f, LockstepClock.DefaultStep);
            return (float)(_tickTime + frac);
        }

        public static void Install(Harmony h, Assembly wf)
        {
            LockstepClock.AfterTick += t => { _tickTime = LockstepClock.SessionTime; _tickAt = Time.unscaledTime; };
            int n = 0;
            foreach (string spec in new[] { "FirstPerson.People", "FirstPerson.Creatures", "FirstPerson.SceneCollector.ShipHeel", "FirstPerson.Crabzillas", "FirstPerson.Dragons" })
            {
                string[] parts = spec.Split('.');
                bool whole = wf.GetType(spec, false) != null;
                Type t = whole ? wf.GetType(spec, false) : wf.GetType(spec.Substring(0, spec.LastIndexOf('.')), false);
                string only = whole ? null : parts[parts.Length - 1];
                if (t == null) continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (only != null && m.Name != only) continue;
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null || !ReadsRealTime(m)) continue;
                    try { h.Patch(m, transpiler: new HarmonyMethod(typeof(VisualClock), nameof(Transpiler))); n++; }
                    catch (Exception e) { Log.Warn("lockstep: couldn't put " + t.Name + "." + m.Name + " on the world's clock: " + e.Message); }
                }
            }
            Log.Info("lockstep: " + n + " of Worldfall's figure poses follow the world's clock");
        }

        private static readonly MethodInfo _unscaled = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime));
        private static readonly MethodInfo _now = AccessTools.Method(typeof(VisualClock), nameof(Now));

        private static bool ReadsRealTime(MethodInfo m)
        {
            try
            {
                foreach (KeyValuePair<OpCode, object> i in PatchProcessor.ReadMethodBody(m))
                    if (i.Value is MethodInfo c && c == _unscaled) return true;
            }
            catch { }
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            foreach (CodeInstruction c in list)
                if ((c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt) && c.operand is MethodInfo m && m == _unscaled)
                {
                    c.opcode = OpCodes.Call;
                    c.operand = _now;
                }
            return list;
        }
    }
}
