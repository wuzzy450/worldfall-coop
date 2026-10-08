using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Some powers read the player's mouse instead of the tile they were used on (boulders, the
    /// finger flick), and "the creature near the cursor" is searched among creatures on screen.
    /// Inside a tick the pointer is the tile of the input being applied, the search covers every
    /// creature, and a boulder is thrown right away (its charge-and-release follows the local
    /// mouse every frame, which lockstep can't carry yet).
    /// </summary>
    internal static class InputPointer
    {
        /// <summary>Where the input being applied was used (world position).</summary>
        public static Vector2 Pointer = new Vector2(-1f, -1f);
        private static MethodInfo _releaseMany;

        public static void Install(Harmony h)
        {
            Patch(h, AccessTools.Method(typeof(MapBox), "getMousePos"), nameof(MousePosPrefix));
            Patch(h, AccessTools.Method(typeof(MapBox), "getActorNearCursor"), nameof(NearCursorPrefix));
            Patch(h, AccessTools.Method(typeof(PowerLibrary), "prepareBoulder"), nameof(BoulderPrefix));
            Patch(h, AccessTools.Method(typeof(Boulder), "checkRelease"), nameof(CheckReleasePrefix));
            _releaseMany = AccessTools.Method(typeof(Boulder), "releaseManyBoulders");
            if (_releaseMany == null) Log.Error("lockstep: Boulder.releaseManyBoulders not found: boulders won't work in lockstep");
        }

        private static void Patch(Harmony h, MethodInfo m, string prefix)
        {
            if (m == null) { Log.Error("lockstep: " + prefix.Replace("Prefix", "") + " target not found (game changed?): a power may follow the local mouse"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(InputPointer), prefix));
        }

        private static bool MousePosPrefix(ref Vector2 __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = Pointer;
            return false;
        }

        private static bool NearCursorPrefix(ref Actor __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = null;
            float best = 3f;
            foreach (Actor a in World.world.units)
            {
                if (a == null || !a.isAlive() || !a.asset.can_be_inspected || a.isInsideSomething()) continue;
                float d = Toolbox.DistVec2Float(a.current_position, Pointer);
                if (d <= best && (__result == null || d < best || a.getID() < __result.getID())) { best = d; __result = a; }
            }
            return false;
        }

        private static bool BoulderPrefix(ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            _releaseMany?.Invoke(null, new object[] { Pointer });
            __result = true;
            return false;
        }

        /// <summary>The frame-by-frame release of a charged boulder: not while lockstep runs.</summary>
        private static bool CheckReleasePrefix() => !LockstepClock.Active;
    }
}
