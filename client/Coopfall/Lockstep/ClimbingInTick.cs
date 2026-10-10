using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall replaces the game's tile step (BatchActors.u5_curTileAction, DryBoards.Step): a
    /// possessed creature may stand on mountains (Climbing) and creatures on drawn docks don't
    /// swim (DockDecks). Climbing asks "is this ControllableUnit._unit_main", i.e. this PC's own
    /// body, so on the host the host's body was spared the shove and stun dice on a mountain and
    /// on the guest the guest's body was (the guest's-first-epoch drift, ticks 989 and 940).
    /// In ticks: every controlled creature climbs, on every PC; the body a player just left stays
    /// calm on the rock for Worldfall's 30 s, tracked per creature on the world clock. Docks are
    /// registered from drawn 3D models (only the ones this PC has drawn), so in ticks the tile step
    /// ignores them and creatures on a dock over water behave as in vanilla.
    /// </summary>
    public static class ClimbingInTick
    {
        private const double CalmSeconds = 30.0;
        private static MethodInfo _on, _step;
        private static FieldInfo _you, _leftF, _leftId, _leftAt;
        private static readonly HashSet<long> _bodies = new HashSet<long>();
        private static readonly Dictionary<long, double> _left = new Dictionary<long, double>();
        private static readonly List<long> _scratch = new List<long>();

        public static void Install(Harmony h, Assembly wf)
        {
            if (PlayerScope.Off("climbing")) { Log.Info("lockstep: Worldfall's climbing in ticks off (-coopfall-ls-off climbing)"); return; }
            Type climbing = wf.GetType("FirstPerson.Climbing", false), dry = wf.GetType("FirstPerson.DryBoards", false);
            MethodInfo watch = climbing == null ? null : AccessTools.Method(climbing, "Watch");
            MethodInfo step = climbing == null ? null : AccessTools.Method(climbing, "Step");
            MethodInfo climbs = climbing == null ? null : AccessTools.Method(climbing, "Climbs");
            MethodInfo onBoards = dry == null ? null : AccessTools.Method(dry, "OnBoards");
            _on = climbing == null ? null : AccessTools.PropertyGetter(climbing, "On");
            _step = step;
            if (climbing != null) { _you = AccessTools.Field(climbing, "_you"); _leftF = AccessTools.Field(climbing, "_left"); _leftId = AccessTools.Field(climbing, "_leftId"); _leftAt = AccessTools.Field(climbing, "_leftAt"); }
            if (watch == null || step == null || climbs == null || _on == null || _you == null || _leftF == null || _leftId == null || _leftAt == null)
            {
                Log.Warn("lockstep: Worldfall's climbing not found: a player's creature on a mountain may split the world");
                return;
            }
            h.Patch(watch, prefix: new HarmonyMethod(typeof(ClimbingInTick), nameof(WatchPrefix)));
            h.Patch(step, prefix: new HarmonyMethod(typeof(ClimbingInTick), nameof(StepPrefix)));
            h.Patch(climbs, prefix: new HarmonyMethod(typeof(ClimbingInTick), nameof(ClimbsPrefix)));
            if (onBoards != null) h.Patch(onBoards, prefix: new HarmonyMethod(typeof(ClimbingInTick), nameof(OnBoardsPrefix)));
            else Log.Warn("lockstep: Worldfall's dock boards step not found: creatures on docks may differ between PCs");
        }

        public static void Reset() { _bodies.Clear(); _left.Clear(); }

        private static bool On()
        {
            try { return (bool)_on.Invoke(null, null); }
            catch { return false; }
        }

        private static bool Climber(Actor a) => a != null && a.asset != null && !a.asset.is_boat && LockstepControl.IsControlled(a.getID()) && On();

        private static bool ClimbsPrefix(Actor a, ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = Climber(a);
            return false;
        }

        /// <summary>Once per tile step: bodies released since the last one start their calm time.</summary>
        private static bool WatchPrefix()
        {
            if (!LockstepClock.InTick) return true;
            double now = World.world.getCurWorldTime();
            _scratch.Clear();
            foreach (long id in _bodies)
                if (!LockstepControl.IsControlled(id)) _scratch.Add(id);
            foreach (long id in _scratch)
            {
                _bodies.Remove(id);
                Actor a = World.world.units.get(id);
                if (a != null && a.isAlive() && a.asset != null && !a.asset.is_boat) _left[id] = now;
            }
            foreach (long id in LockstepControl.ControlledIds())
            {
                _bodies.Add(id);
                _left.Remove(id);
            }
            return false;
        }

        // Climbing.Step for every player at once: Worldfall's own step, run with its "you" (or "the
        // body you left") set to this creature, so the rules stay Worldfall's
        private static bool _inner;

        private static bool StepPrefix(Actor a, ref bool __result)
        {
            if (!LockstepClock.InTick || _inner) return true;
            __result = false;
            if (a == null) return false;
            long id = a.getID();
            bool you = LockstepControl.IsControlled(id);
            if (!you && !_left.TryGetValue(id, out _)) return false;
            object oldYou = _you.GetValue(null), oldLeft = _leftF.GetValue(null), oldLeftId = _leftId.GetValue(null), oldLeftAt = _leftAt.GetValue(null);
            try
            {
                _inner = true;
                if (you) { _you.SetValue(null, a); _leftF.SetValue(null, null); }
                else { _you.SetValue(null, null); _leftF.SetValue(null, a); _leftId.SetValue(null, id); _leftAt.SetValue(null, _left[id]); }
                __result = (bool)_step.Invoke(null, new object[] { a });
                if (!you && _leftF.GetValue(null) == null) _left.Remove(id);
            }
            finally
            {
                _inner = false;
                _you.SetValue(null, oldYou); _leftF.SetValue(null, oldLeft); _leftId.SetValue(null, oldLeftId); _leftAt.SetValue(null, oldLeftAt);
            }
            return false;
        }

        private static bool OnBoardsPrefix(ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = false;
            return false;
        }

    }
}
