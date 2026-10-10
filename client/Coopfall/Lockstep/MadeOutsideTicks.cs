using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// A watch on the things that can't travel as relayed calls: new items, creatures, buildings,
    /// realms, alliances, wars, cities, armies. Made outside a tick while lockstep runs, they exist
    /// on one PC only (and every later ID differs): the world drifts and is re-synced. Each such
    /// place is logged once with its caller, so the feature behind it can be moved into ticks.
    /// It is only a watch: what is made still exists (stopping it would break the feature for good).
    /// </summary>
    public static class MadeOutsideTicks
    {
        public static int Count;
        public static readonly List<string> Seen = new List<string>();
        private static readonly HashSet<string> _logged = new HashSet<string>();

        public static void Install(Harmony h)
        {
            var targets = new List<MethodBase>();
            void Add(Type t, params string[] names)
            {
                if (t == null) return;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Array.IndexOf(names, m.Name) >= 0 && !m.IsAbstract && !m.ContainsGenericParameters) targets.Add(m);
            }
            Add(typeof(ItemManager), "generateItem");
            Add(typeof(ActorManager), "spawnNewUnit", "createNewUnit", "spawnNewUnitByPlayer");
            Add(typeof(BuildingManager), "addBuilding");
            Add(typeof(City), "makeOwnKingdom");
            Add(typeof(KingdomManager), "makeNewCivKingdom", "makeNewKingdom");
            Add(typeof(AllianceManager), "newAlliance");
            Add(typeof(Alliance), "join", "leave");
            Add(typeof(DiplomacyManager), "startWar");
            Add(typeof(CityManager), "buildNewCity");
            Add(typeof(ArmyManager), "newArmy");
            Add(typeof(ActorEquipmentSlot), "setItem", "takeAwayItem");
            int n = 0;
            foreach (MethodBase m in targets)
                try { h.Patch(m, prefix: new HarmonyMethod(typeof(MadeOutsideTicks), nameof(Prefix))); n++; }
                catch (Exception e) { Log.Warn("lockstep: couldn't watch " + m.DeclaringType.Name + "." + m.Name + ": " + e.Message); }
            Log.Info("lockstep: watching " + n + " ways of making things for calls outside ticks");
        }

        private static void Prefix(MethodBase __originalMethod)
        {
            if (!LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick) return;
            Count++;
            var st = new System.Diagnostics.StackTrace(2, false);
            string caller = "?";
            for (int i = 0; i < st.FrameCount; i++)
            {
                MethodBase f = st.GetFrame(i)?.GetMethod();
                if (f?.DeclaringType == null) continue;
                string ns = f.DeclaringType.Namespace ?? "";
                if (ns.StartsWith("FirstPerson", StringComparison.Ordinal) || ns.StartsWith("Coopfall", StringComparison.Ordinal)) { caller = f.DeclaringType.Name + "." + f.Name; break; }
            }
            string key = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " from " + caller;
            if (!_logged.Add(key)) return;
            Seen.Add(key);
            Log.Warn("lockstep: " + key + " outside a tick: it exists on this PC only. Called from:\n" + st);
        }
    }
}
