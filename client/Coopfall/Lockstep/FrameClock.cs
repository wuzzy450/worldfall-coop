using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Some simulation code reads Unity's frame clock directly (poop ages every 30th frame,
    /// timers step by Time.deltaTime, ...), which depends on each PC's frame rate. Every game
    /// method that reads Time.frameCount / deltaTime / time gets those reads swapped for these:
    /// tick-based inside a lockstep tick, the real value everywhere else.
    /// </summary>
    public static class FrameClock
    {
        public static int FrameCount() => LockstepClock.InTick ? (int)LockstepClock.Tick : Time.frameCount;
        public static float DeltaTime() => LockstepClock.InTick ? LockstepClock.DefaultStep : Time.deltaTime;
        public static float TimeNow() => LockstepClock.InTick ? (float)LockstepClock.SessionTime : Time.time;

        private static readonly Dictionary<MethodInfo, MethodInfo> _swap = new Dictionary<MethodInfo, MethodInfo>
        {
            { AccessTools.PropertyGetter(typeof(Time), nameof(Time.frameCount)), AccessTools.Method(typeof(FrameClock), nameof(FrameCount)) },
            { AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime)), AccessTools.Method(typeof(FrameClock), nameof(DeltaTime)) },
            { AccessTools.PropertyGetter(typeof(Time), nameof(Time.time)), AccessTools.Method(typeof(FrameClock), nameof(TimeNow)) },
        };

        public static void Install(Harmony h)
        {
            var transpiler = new HarmonyMethod(typeof(FrameClock), nameof(Transpiler));
            Module module = typeof(MapBox).Module;
            int n = 0, failed = 0;
            foreach (Type t in typeof(MapBox).Assembly.GetTypes())
            {
                if (t.IsGenericTypeDefinition) continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || !ReadsClock(m, module)) continue;
                    try { h.Patch(m, transpiler: transpiler); n++; }
                    catch { failed++; }
                }
            }
            Log.Info("lockstep: " + n + " game methods read the tick clock instead of the frame clock in ticks" + (failed > 0 ? " (" + failed + " could not be patched)" : ""));
        }

        /// <summary>Quick IL scan for a call to one of the swapped getters.</summary>
        private static bool ReadsClock(MethodInfo m, Module module)
        {
            byte[] il;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { return false; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28) continue;   // call
                int token = BitConverter.ToInt32(il, i + 1);
                if ((token >> 24) != 0x0A) continue;   // MemberRef (Unity methods live in another assembly)
                try
                {
                    if (module.ResolveMethod(token) is MethodInfo target && _swap.ContainsKey(target)) return true;
                }
                catch { }
            }
            return false;
        }

        // no `yield` here: its hidden iterator class would implement IEnumerable<CodeInstruction>,
        // and the game's mod loader can't load that type before the embedded Harmony is resolved
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            foreach (CodeInstruction ci in list)
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && _swap.TryGetValue(mi, out MethodInfo to))
                    ci.operand = to;
            return list;
        }
    }
}
