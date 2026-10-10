using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// A creature alive at 0 health (seen once on a possessed body): the game only kills from its
    /// batch's death-check list, filled by getHit. After every tick, a creature that has stayed
    /// alive at 0 health for 3 ticks is logged once with what decides its death: its batch, whether
    /// that batch is one the game runs, whether it's on the batch's death list, the last attack.
    /// </summary>
    public static class ZeroHpWatch
    {
        private static readonly Dictionary<long, int> _seen = new Dictionary<long, int>();
        private static readonly HashSet<long> _logged = new HashSet<long>();
        private static readonly AccessTools.FieldRef<Actor, BatchActors> _batch = AccessTools.FieldRefAccess<Actor, BatchActors>("batch");

        public static void Install() => LockstepClock.AfterTick += Check;

        public static void Reset() { _seen.Clear(); _logged.Clear(); }

        private static void Check(long tick)
        {
            if (tick % 5 != 0) return;
            try
            {
                foreach (Actor a in World.world.units)
                {
                    if (a == null || !a.isAlive() || a.getHealth() > 0) { if (a != null) _seen.Remove(a.getID()); continue; }
                    long id = a.getID();
                    _seen.TryGetValue(id, out int n);
                    _seen[id] = ++n;
                    if (n < 2 || !_logged.Add(id)) continue;
                    BatchActors b = _batch(a);
                    bool listed = false, running = false;
                    try { listed = b != null && b.c_check_deaths.Contains(a); } catch { }
                    try
                    {
                        object mgr = AccessTools.Field(typeof(ActorManager), "_job_manager")?.GetValue(World.world.units);
                        running = b != null && mgr != null && Traverse.Create(mgr).Field("_batches_active").GetValue() is System.Collections.IList l && l.Contains(b);
                    }
                    catch { }
                    Log.Warn("lockstep: creature #" + id + " (" + a.asset?.id + (LockstepControl.IsControlled(id) ? ", controlled" : "") + ") alive at 0 health since about tick " + (tick - 5)
                        + ": batch " + (b == null ? "none" : "#" + b.GetHashCode()) + (running ? " (runs)" : " (NOT one the game runs)") + ", on its death list " + listed
                        + ", last attack " + CombatSync.LastAttackType(a) + " by " + (CombatSync.LastAttacker(a)?.getID().ToString() ?? "-"));
                }
            }
            catch (Exception e) { if (_logged.Add(-1)) Log.Warn("lockstep: zero-health watch: " + e.Message); }
        }
    }
}
