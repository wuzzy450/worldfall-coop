using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Desync finder: records Randy's generator state after each part of the simulation (creatures,
    /// buildings, kingdoms, ...) for the first MaxTick ticks. Two runs that drift apart can then be
    /// compared part by part to name where the dice rolls first differ.
    /// </summary>
    public static class SectionTrace
    {
        public struct Entry { public long tick; public string section; public uint state; }

        public static bool Enabled;
        public static long MaxTick = 100;
        public static List<Entry> Current = new List<Entry>();
        /// <summary>Creatures whose position is folded into each entry (to find what moves them).</summary>
        public static HashSet<long> Watch = new HashSet<long>();
        private static readonly List<Actor> _watched = new List<Actor>();

        private static FieldInfo _rand;
        private static int _betweenLogged, _stackLogged;
        /// <summary>Log the call stack when a traced method touches these tiles.</summary>
        public static int StackTile = -1, StackTile2 = -1;
        private static readonly AccessTools.FieldRef<Actor, float> _timerAction = AccessTools.FieldRefAccess<Actor, float>("timer_action");
        private static bool _installed;

        private static readonly string[] MapBoxSections =
        {
            "updateDirtyMetaContainersAndCleanup", "updateTimerNutrition", "updateMetaHistory", "updateMapLayers",
            "updateCities", "updateActors", "updateBuildings", "updateWorldBehaviours",
        };

        private static readonly string[] ManagerFields =
        {
            "explosion_checker", "city_zone_helper", "drop_manager", "cultures", "stack_effects", "resource_throw_manager",
            "armies", "kingdoms", "diplomacy", "subspecies", "plots", "clans", "alliances", "wars", "languages",
            "religions", "projectiles", "statuses", "era_manager", "map_stats",
        };

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            _rand = AccessTools.Field(typeof(Randy), "rand");
            var h = new Harmony("coopfall.lockstep.trace");
            var post = new HarmonyMethod(typeof(SectionTrace), nameof(Postfix));
            int n = 0;
            foreach (string m in MapBoxSections)
                n += TryPatch(h, AccessTools.Method(typeof(MapBox), m), post);
            foreach (string f in ManagerFields)
            {
                FieldInfo fi = AccessTools.Field(typeof(MapBox), f);
                if (fi == null) continue;
                foreach (MethodInfo mi in fi.FieldType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if ((mi.Name == "update" || mi.Name == "updateWorldTime") && mi.GetParameters().Length >= 1 && !mi.IsAbstract && mi.DeclaringType == fi.FieldType)
                        n += TryPatch(h, mi, post);
            }
            n += TryPatch(h, AccessTools.Method(AccessTools.TypeByName("TaxiManager"), "update"), post);
            // each job of the creature/building batches (AI, movement, spreading, ...)
            n += PatchBatchJobs(h, typeof(BatchActors), post);
            n += PatchBatchJobs(h, typeof(BatchBuildings), post);
            // decisions of watched creatures: which ones were possible, with what weight
            MethodInfo useOn = AccessTools.Method(typeof(UtilityBasedDecisionSystem), "useOn");
            if (useOn != null) h.Patch(useOn, postfix: new HarmonyMethod(typeof(SectionTrace), nameof(DecisionPostfix)));
            // every behaviour step (BehaviourActionBase<Actor>.execute overrides) of watched creatures
            var behPost = new HarmonyMethod(typeof(SectionTrace), nameof(BehPostfix));
            Type behBase = typeof(BehaviourActionBase<Actor>);
            foreach (Type t in typeof(MapBox).Assembly.GetTypes())
                if (behBase.IsAssignableFrom(t) && !t.IsAbstract)
                {
                    MethodInfo ex = AccessTools.DeclaredMethod(t, "execute", new[] { typeof(Actor) });
                    if (ex != null) n += TryPatch(h, ex, behPost);
                }
            Log.Info("lockstep: section trace on " + n + " methods");
        }

        /// <summary>Also trace these methods ("Type.method"), noting the tile/object they were called with.</summary>
        public static void TraceMethods(string list)
        {
            var h = new Harmony("coopfall.lockstep.trace.extra");
            var post = new HarmonyMethod(typeof(SectionTrace), nameof(ArgPostfix));
            var postResult = new HarmonyMethod(typeof(SectionTrace), nameof(ArgResultPostfix));
            foreach (string item in list.Split(','))
            {
                int dot = item.LastIndexOf('.');
                if (dot <= 0) continue;
                Type t = AccessTools.TypeByName(item.Substring(0, dot));
                string name = item.Substring(dot + 1);
                int n = 0;
                if (t != null)
                    foreach (MethodInfo mi in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (mi.Name == name && !mi.IsAbstract) n += TryPatch(h, mi, mi.ReturnType == typeof(void) ? post : postResult);
                Log.Info("lockstep: tracing " + item + ": " + n + " methods");
            }
        }

        private static void ArgPostfix(MethodBase __originalMethod, object[] __args, object __instance)
        {
            // traced methods are recorded between ticks too: anything changing the world there is a leak
            if (!Enabled || !LockstepClock.Active || LockstepClock.Tick >= MaxTick) return;
            string arg = LockstepClock.InTick ? "" : "BETWEEN TICKS ";
            if (__instance is BaseSimObject self) arg = "[" + self.getID() + "]";
            if (!LockstepClock.InTick && _betweenLogged < 10)
            {
                _betweenLogged++;
                Log.Info("lockstep trace: " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " called between ticks (after tick " + LockstepClock.Tick + ") from: " + Environment.StackTrace);
            }
            if (__args != null && __args.Length > 0)
            {
                object a = __args[0];
                if (a is WorldTile wt)
                {
                    arg += "(tile " + wt.tile_id + ")";
                    if ((wt.tile_id == StackTile || wt.tile_id == StackTile2) && _stackLogged++ < 6)
                        Log.Info("lockstep trace: " + __originalMethod.Name + " on tile " + wt.tile_id + " at tick " + (LockstepClock.Tick + 1) + " from: " + Environment.StackTrace);
                }
                else if (a is BaseSimObject so) arg += "(" + so.GetType().Name + " " + so.getID() + ")";
            }
            Current.Add(new Entry { tick = LockstepClock.Tick + 1, section = arg.StartsWith("BETWEEN") ? arg + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name : __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + arg, state = State() });
        }

        private static void ArgResultPostfix(MethodBase __originalMethod, object[] __args, object __instance, object __result)
        {
            if (!Enabled || !LockstepClock.Active || LockstepClock.Tick >= MaxTick) return;
            ArgPostfix(__originalMethod, __args, __instance);
            Entry e = Current[Current.Count - 1];
            string r = __result is BaseSimObject so ? so.GetType().Name + " " + so.getID() : __result is WorldTile wt ? "tile " + wt.tile_id : __result?.ToString() ?? "null";
            e.section += " -> " + r;
            Current[Current.Count - 1] = e;
        }

        private static void BehPostfix(MethodBase __originalMethod, Actor __0, ai.behaviours.BehResult __result)
        {
            if (!Enabled || !LockstepClock.InTick || LockstepClock.Tick >= MaxTick || Watch.Count == 0 || __0 == null || !Watch.Contains(__0.getID())) return;
            Current.Add(new Entry { tick = LockstepClock.Tick + 1, section = "beh[" + __0.getID() + "] " + __originalMethod.DeclaringType.Name + " -> " + __result, state = State() });
        }

        private static FieldInfo _decActions, _decFactors, _decCount;

        private static void DecisionPostfix(UtilityBasedDecisionSystem __instance, Actor pActor, DecisionAsset __result)
        {
            if (!Enabled || !LockstepClock.InTick || LockstepClock.Tick >= MaxTick || pActor == null || !Watch.Contains(pActor.getID())) return;
            if (_decActions == null)
            {
                _decActions = AccessTools.Field(typeof(UtilityBasedDecisionSystem), "_actions");
                _decFactors = AccessTools.Field(typeof(UtilityBasedDecisionSystem), "_factors");
                _decCount = AccessTools.Field(typeof(UtilityBasedDecisionSystem), "_counter_possible");
            }
            var acts = (DecisionAsset[])_decActions.GetValue(__instance);
            var f = (float[])_decFactors.GetValue(__instance);
            int c = (int)_decCount.GetValue(__instance);
            var sb = new System.Text.StringBuilder("decision[" + pActor.getID() + "] -> " + (__result?.id ?? "null") + " of");
            for (int i = 0; i < c; i++) sb.Append(' ').Append(acts[i].id).Append('=').Append(f[i].ToString("R"));
            Current.Add(new Entry { tick = LockstepClock.Tick + 1, section = sb.ToString(), state = State() });
        }

        private static int PatchBatchJobs(Harmony h, Type batch, HarmonyMethod post)
        {
            int n = 0;
            foreach (MethodInfo mi in batch.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (mi.GetParameters().Length == 0 && mi.ReturnType == typeof(void) && !mi.IsAbstract && !mi.IsConstructor
                    && mi.Name != "clear" && !mi.Name.StartsWith("create") && !mi.Name.StartsWith("clear") && !mi.Name.StartsWith("apply"))
                    n += TryPatch(h, mi, post);
            return n;
        }

        private static int TryPatch(Harmony h, MethodBase m, HarmonyMethod post)
        {
            if (m == null) return 0;
            try { h.Patch(m, postfix: post); return 1; }
            catch (Exception e) { Log.Info("lockstep: trace skip " + m.DeclaringType?.Name + "." + m.Name + ": " + e.Message); return 0; }
        }

        private static void Postfix(MethodBase __originalMethod)
        {
            if (!Enabled || !LockstepClock.InTick || LockstepClock.Tick >= MaxTick) return;
            Current.Add(new Entry { tick = LockstepClock.Tick + 1, section = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name, state = State() });
        }

        private static uint State()
        {
            uint st = Dice.Fingerprint();
            if (Watch.Count == 0) return st;
            if (_watched.Count != Watch.Count)
            {
                _watched.Clear();
                foreach (Actor a in World.world.units) if (a != null && Watch.Contains(a.getID())) _watched.Add(a);
            }
            foreach (Actor a in _watched)
                st = st * 31 + (uint)BitConverter.SingleToInt32Bits(a.current_position.x) * 7 + (uint)BitConverter.SingleToInt32Bits(a.current_position.y)
                    + (uint)BitConverter.SingleToInt32Bits(_timerAction(a)) * 13;
            return st;
        }

        public static void ResetWatch() { _watched.Clear(); }

        /// <summary>Record a named point (for code the patches above don't cover).</summary>
        public static void Mark(string section)
        {
            if (!Enabled || !LockstepClock.InTick || LockstepClock.Tick >= MaxTick) return;
            Current.Add(new Entry { tick = LockstepClock.Tick + 1, section = section, state = State() });
        }

        /// <summary>First part of tick `tick` where the two traces' dice states differ.</summary>
        public static string FirstSplit(List<Entry> a, List<Entry> b, long tick)
        {
            var x = a.FindAll(e => e.tick == tick);
            var y = b.FindAll(e => e.tick == tick);
            int n = Math.Min(x.Count, y.Count);
            for (int i = 0; i < n; i++)
            {
                if (x[i].section != y[i].section)
                {
                    string before = "";
                    for (int k = Math.Max(0, i - 6); k < i; k++) before += " | step " + k + ": " + x[k].section;
                    return "order differs at step " + i + ": " + x[i].section + " vs " + y[i].section + before;
                }
                if (x[i].state != y[i].state) return "after " + x[i].section + " (step " + i + " of the tick; previous: " + (i > 0 ? x[i - 1].section : "tick start") + ")";
            }
            if (x.Count != y.Count) return "different number of steps: " + x.Count + " vs " + y.Count;
            return null;
        }
    }
}
