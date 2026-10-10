using System;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's OwnMind replaces the game's b2_checkCurrentEnemyTarget/b3_findEnemyTarget (native
    /// detour) and skips both for "your" creature: ControllableUnit._unit_main, this PC's body. So the
    /// guest's body looked for enemies (and rolled dice) on the host but not on the guest; the dice
    /// then split for everyone after it (the recurring "units"/"rng" drift with _timeout_targets).
    /// In ticks, "yours" is every player-controlled creature, the same on every PC.
    /// </summary>
    public static class OwnMindInTick
    {
        private static Func<Actor, bool> _checkCurrent, _checkEnemies;
        private static MethodInfo _ordered;
        private static readonly AccessTools.FieldRef<Actor, bool> _done = AccessTools.FieldRefAccess<Actor, bool>("_update_done"), _skip = AccessTools.FieldRefAccess<Actor, bool>("_beh_skip");

        public static void Install(Harmony h, Assembly wf)
        {
            if (PlayerScope.Off("ownmind")) return;
            Type om = wf.GetType("FirstPerson.OwnMind", false), wm = wf.GetType("FirstPerson.WarMap", false);
            MethodInfo strike = om == null ? null : AccessTools.Method(om, "Strike"), look = om == null ? null : AccessTools.Method(om, "Look");
            _ordered = wm == null ? null : AccessTools.Method(wm, "Ordered", new[] { typeof(Actor), typeof(bool).MakeByRefType() });
            MethodInfo cur = AccessTools.Method(typeof(Actor), "checkCurrentEnemyTarget"), en = AccessTools.Method(typeof(Actor), "checkEnemyTargets");
            if (strike == null || look == null || cur == null || en == null)
            {
                Log.Warn("lockstep: Worldfall's OwnMind not found as expected: a player's creature may look for enemies on one PC only");
                return;
            }
            _checkCurrent = AccessTools.MethodDelegate<Func<Actor, bool>>(cur);
            _checkEnemies = AccessTools.MethodDelegate<Func<Actor, bool>>(en);
            h.Patch(strike, prefix: new HarmonyMethod(typeof(OwnMindInTick), nameof(StrikePrefix)));
            h.Patch(look, prefix: new HarmonyMethod(typeof(OwnMindInTick), nameof(LookPrefix)));
            Log.Info("lockstep: Worldfall's OwnMind treats every player's creature alike in ticks");
        }

        private static bool Ordered(Actor a, out bool firing)
        {
            firing = false;
            if (_ordered == null) return false;
            object[] args = { a, false };
            bool r = (bool)_ordered.Invoke(null, args);
            firing = (bool)args[1];
            return r;
        }

        private static bool StrikePrefix(Actor self)
        {
            if (!LockstepClock.InTick || !LockstepControl.Running) return true;
            if (!_done(self) && !_skip(self) && !LockstepControl.IsControlled(self.getID()) && !(Ordered(self, out bool firing) && firing) && _checkCurrent(self))
                self.skipBehaviour();
            return false;
        }

        private static bool LookPrefix(Actor self)
        {
            if (!LockstepClock.InTick || !LockstepControl.Running) return true;
            if (!_done(self) && !_skip(self) && !LockstepControl.IsControlled(self.getID()) && !Ordered(self, out _) && _checkEnemies(self))
            {
                self.stopMovement();
                self.skipBehaviour();
            }
            return false;
        }
    }
}
