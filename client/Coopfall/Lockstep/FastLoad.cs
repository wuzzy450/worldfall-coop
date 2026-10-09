using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Faster epoch loads (re-syncs). WorldBox's loader spreads a load over many frames: some
    /// steps wait 0.2 s or skip a frame so the loading screen can draw, and every few loads it
    /// unloads unused assets. While an epoch save loads, none of that is needed: run the steps
    /// back to back (the loader still yields every 0.1 s of work).
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
        }

        private static void AddPrefix(ref MapLoaderAction pAction, string pId, ref bool pSkipFrame, ref float pNewWaitTimerValue)
        {
            if (!On) return;
            pSkipFrame = false;
            if (pNewWaitTimerValue > 0.001f) pNewWaitTimerValue = 0.001f;
            if (pId == "UnloadUnusedAssets") pAction = () => { };
        }
    }
}
