using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// A few disasters change the world from Unity's per-frame Update instead of the simulation:
    /// the earthquake walks its pattern there, and bomb flashes (nuke, napalm, antimatter, the
    /// infinity coin) blow up when their animation reaches a frame. Frames differ per PC, so
    /// while lockstep runs their Update only runs inside a tick: the effects from their own
    /// (tick-driven) update, the earthquake once per tick after the simulation.
    /// </summary>
    internal static class FrameUpdates
    {
        /// <summary>BaseEffect subclasses whose Update is simulation.</summary>
        private static readonly string[] EffectTypes = { "NukeFlash", "NapalmFlash", "AntimatterBombEffect", "EffectInfinityCoin" };
        private static readonly Dictionary<Type, Action<object>> _effectUpdate = new Dictionary<Type, Action<object>>();
        private static Action<object> _quakeUpdate;
        private static FieldInfo _quakeInstance;
        private static bool _running;

        public static void Install(Harmony h)
        {
            var skip = new HarmonyMethod(typeof(FrameUpdates), nameof(SkipOutsideTick));
            foreach (string name in EffectTypes)
            {
                Type t = AccessTools.TypeByName(name);
                MethodInfo m = t == null ? null : AccessTools.DeclaredMethod(t, "Update");
                if (m == null) { Log.Error("lockstep: " + name + ".Update not found (game changed?)"); continue; }
                h.Patch(m, prefix: skip);
                _effectUpdate[t] = MakeCall(m);
            }
            MethodInfo effUpdate = AccessTools.Method(typeof(BaseEffect), "update");
            if (effUpdate != null) h.Patch(effUpdate, postfix: new HarmonyMethod(typeof(FrameUpdates), nameof(EffectUpdatePostfix)));
            else Log.Error("lockstep: BaseEffect.update not found: bombs may go off at different times on each PC");
            MethodInfo quake = AccessTools.DeclaredMethod(typeof(Earthquake), "Update");
            _quakeInstance = AccessTools.Field(typeof(Earthquake), "_instance");
            if (quake != null && _quakeInstance != null) { h.Patch(quake, prefix: skip); _quakeUpdate = MakeCall(quake); }
            else Log.Error("lockstep: Earthquake.Update not found: earthquakes will desync");
        }

        private static Action<object> MakeCall(MethodInfo m) => o => m.Invoke(o, null);

        private static bool SkipOutsideTick() => !LockstepClock.Active || _running;

        private static void EffectUpdatePostfix(BaseEffect __instance)
        {
            if (!LockstepClock.InTick || !_effectUpdate.TryGetValue(__instance.GetType(), out Action<object> call)) return;
            Run(call, __instance);
        }

        /// <summary>Called by LockstepClock once per tick, after the simulation step.</summary>
        internal static void AfterSimulation()
        {
            object q = _quakeInstance?.GetValue(null);
            if (q != null && Earthquake.isQuakeActive()) Run(_quakeUpdate, q);
        }

        private static void Run(Action<object> call, object o)
        {
            _running = true;
            try { call(o); }
            catch (Exception e) { Log.Error("lockstep: " + o.GetType().Name + ".Update failed: " + (e.InnerException ?? e).Message); }
            finally { _running = false; }
        }

        /// <summary>
        /// The earthquake keeps static state across worlds: which pattern is next, the shuffled
        /// order of the patterns and of each pattern's steps. Put it back in a fixed order.
        /// </summary>
        internal static void Normalize()
        {
            object q = _quakeInstance?.GetValue(null);
            if (q == null) return;
            foreach (string f in new[] { "_quake_active" }) AccessTools.Field(typeof(Earthquake), f)?.SetValue(q, false);
            AccessTools.Field(typeof(Earthquake), "_timer")?.SetValue(q, 0f);
            AccessTools.Field(typeof(Earthquake), "_current_print_index")?.SetValue(q, 0);
            List<PrintTemplate> quakes = PrintLibrary.getQuakes();
            quakes.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            FieldInfo stepsField = AccessTools.Field(typeof(PrintTemplate), "steps");
            foreach (PrintTemplate p in quakes)
                if (stepsField?.GetValue(p) is PrintStep[] steps)
                    Array.Sort(steps, (x, y) => x.x != y.x ? x.x.CompareTo(y.x) : x.y != y.y ? x.y.CompareTo(y.y) : x.action.CompareTo(y.action));
        }
    }
}
