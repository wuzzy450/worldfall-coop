using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's renderer lets a shot-down UFO fall by writing its height (SceneCollector.StepUfos),
    /// on each PC at its own frame rate and only where it is drawn. In lockstep that write is left
    /// out: the world's own death fall decides the height, the same on every PC.
    /// </summary>
    public static class RendererWrites
    {
        private static readonly FieldInfo _height = AccessTools.Field(typeof(BaseSimObject), "position_height");

        public static void Install(Harmony h, Assembly wf)
        {
            Type scene = wf.GetType("FirstPerson.SceneCollector", false);
            MethodInfo ufos = scene == null ? null : AccessTools.Method(scene, "StepUfos");
            if (ufos == null || _height == null) { Log.Warn("lockstep: Worldfall's SceneCollector.StepUfos not found: a falling UFO may differ between PCs"); return; }
            h.Patch(ufos, transpiler: new HarmonyMethod(typeof(RendererWrites), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo set = AccessTools.Method(typeof(RendererWrites), nameof(SetHeight));
            int n = 0;
            foreach (CodeInstruction c in list)
                if (c.opcode == OpCodes.Stfld && c.operand is FieldInfo f && f == _height) { c.opcode = OpCodes.Call; c.operand = set; n++; }
            if (n == 0) Log.Warn("lockstep: Worldfall's falling UFO height write not found (changed?)");
            return list;
        }

        public static void SetHeight(BaseSimObject o, float h)
        {
            if (!LockstepClock.Active) o.position_height = h;
        }
    }
}
