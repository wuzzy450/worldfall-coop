using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Faster epoch loads (re-syncs). WorldBox's loader spreads a load over many frames: some
    /// steps wait 0.2 s or skip a frame so the loading screen can draw, and every few loads it
    /// unloads unused assets. While an epoch save loads, none of that is needed: run the steps
    /// back to back, and don't stop every 0.1 s (and then a frame more) to draw a frame.
    /// </summary>
    public static class FastLoad
    {
        /// <summary>Set while an epoch save is loading.</summary>
        public static bool On;

        public static void Install(Harmony h)
        {
            var add = AccessTools.Method(typeof(SmoothLoader), "add");
            if (add != null) h.Patch(add, prefix: new HarmonyMethod(typeof(FastLoad), nameof(AddPrefix)));
            else Log.Warn("lockstep: SmoothLoader.add not found: re-syncs load at normal speed");
            var update = AccessTools.Method(typeof(SmoothLoader), "update");
            _timer = AccessTools.Field(typeof(SmoothLoader), "_current_timer");
            if (update != null && _timer != null)
                h.Patch(update, transpiler: new HarmonyMethod(typeof(FastLoad), nameof(BudgetTranspiler)), postfix: new HarmonyMethod(typeof(FastLoad), nameof(UpdatePostfix)));
            else Log.Warn("lockstep: SmoothLoader.update not found: re-syncs load at normal speed");
        }

        private static System.Reflection.FieldInfo _timer;

        /// <summary>Seconds of loading work per frame: 0.1 normally, 1 for an epoch save.</summary>
        public static float Budget() => On && Bigger ? 1f : 0.1f;

        /// <summary>A whole second of loading per frame and no extra wait frame (off with -coopfall-slowload).</summary>
        public static readonly bool Bigger = System.Array.Exists(System.Environment.GetCommandLineArgs(), a => a == "-coopfall-slowload") == false;

        // No 'yield' here: an iterator class in a transpiler makes the whole mod fail to load.
        private static IEnumerable<CodeInstruction> BudgetTranspiler(IEnumerable<CodeInstruction> code)
        {
            var list = new List<CodeInstruction>(code);
            int n = 0;
            foreach (CodeInstruction c in list)
                if (c.opcode == OpCodes.Ldc_R4 && c.operand is float f && f == 0.1f)
                {
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(FastLoad), nameof(Budget));
                    n++;
                }
            if (n != 1) Log.Warn("lockstep: SmoothLoader's frame budget found " + n + " times (game changed?)");
            return list;
        }

        private static void UpdatePostfix()
        {
            if (On && Bigger) _timer.SetValue(null, 0f);   // the next step on the next frame, not the one after
        }

        private static void AddPrefix(ref MapLoaderAction pAction, string pId, ref bool pSkipFrame, ref float pNewWaitTimerValue)
        {
            if (!On) return;
            pSkipFrame = false;
            if (pNewWaitTimerValue > 0.001f) pNewWaitTimerValue = 0.001f;
            if (pId == "UnloadUnusedAssets") pAction = () => { };
            // a forced garbage collection (lastGC): 0.25-0.4 s, nothing depends on it
            else if (pId == "Rewriting The World" && Bigger) pAction = () => { };

            MapLoaderAction inner = pAction;
            string id = pId;
            pAction = () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                inner();
                Steps.Add(new KeyValuePair<string, double>(id, sw.Elapsed.TotalMilliseconds));
            };
        }

        private static bool _redraw;

        /// <summary>After an epoch is ready: the tile redraw the load left out (drawing only).</summary>
        public static void RedrawLater()
        {
            if (!_redraw) return;
            _redraw = false;
            try { World.world.redrawTiles(); }
            catch (System.Exception e) { Log.Warn("lockstep: redrawing tiles after the load: " + e.Message); }
        }

        /// <summary>The last epoch load's steps and how long each took (ms).</summary>
        public static readonly List<KeyValuePair<string, double>> Steps = new List<KeyValuePair<string, double>>();

        /// <summary>One log line: the slowest steps of the load that just finished.</summary>
        public static string Report()
        {
            var l = new List<KeyValuePair<string, double>>(Steps);
            double total = 0;
            foreach (KeyValuePair<string, double> kv in l) total += kv.Value;
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            sb.Append(l.Count).Append(" steps, ").Append(total.ToString("F0")).Append(" ms:");
            for (int i = 0; i < l.Count && i < 12; i++) sb.Append(' ').Append(l[i].Key).Append(' ').Append(l[i].Value.ToString("F0"));
            Steps.Clear();
            return sb.ToString();
        }
    }
}
