using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall rolls some outcomes on its own System.Random generators made without a seed
    /// (a bribe taken or refused, which townsman brings a gift, who comes home for the night).
    /// Each PC's generator is at a different point, so in a tick the same roll would differ. In
    /// ticks every such generator draws from one generator seeded from the tick instead (in the
    /// order the calls happen, which is the same on every PC); outside ticks they are untouched.
    /// Generators made with a seed (Worldfall's daily quests, the game's own) keep their sequence.
    /// </summary>
    public static class TickRandom
    {
        private sealed class Mark { }
        private static readonly ConditionalWeakTable<Random, Mark> _unseeded = new ConditionalWeakTable<Random, Mark>();
        private static readonly Mark _mark = new Mark();
        private static Random _tick = new Random(1);
        public static int Drawn;

        public static void Install(Harmony h, Assembly wf)
        {
            try
            {
                h.Patch(AccessTools.Constructor(typeof(Random), Type.EmptyTypes), postfix: new HarmonyMethod(typeof(TickRandom), nameof(CtorPostfix)));
                h.Patch(AccessTools.Method(typeof(Random), "Next", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(TickRandom), nameof(Next0)));
                h.Patch(AccessTools.Method(typeof(Random), "Next", new[] { typeof(int) }), prefix: new HarmonyMethod(typeof(TickRandom), nameof(Next1)));
                h.Patch(AccessTools.Method(typeof(Random), "Next", new[] { typeof(int), typeof(int) }), prefix: new HarmonyMethod(typeof(TickRandom), nameof(Next2)));
                h.Patch(AccessTools.Method(typeof(Random), "NextDouble", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(TickRandom), nameof(NextD)));
            }
            catch (Exception e) { Log.Warn("lockstep: Worldfall's own random generators not tied to the tick: " + e.Message); return; }
            // the ones Worldfall's classes already made (static fields: made before this was installed)
            int n = 0;
            if (wf != null)
            {
                Type[] types;
                try { types = wf.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
                foreach (Type t in types)
                {
                    if (t.IsGenericTypeDefinition || t.Name.EndsWith("Test", StringComparison.Ordinal)) continue;
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType != typeof(Random) || f.IsLiteral) continue;
                        try
                        {
                            if (f.GetValue(null) is Random r && !_unseeded.TryGetValue(r, out _)) { _unseeded.Add(r, _mark); n++; }
                        }
                        catch { }
                    }
                }
            }
            LockstepClock.BeforeTick += t => _tick = new Random(LockstepClock.TickSeed(LockstepClock.Seed ^ 0x52A9D0, t));
            Log.Info("lockstep: Worldfall's unseeded random generators draw from the tick's dice in ticks (" + n + " found)");
        }

        private static void CtorPostfix(Random __instance)
        {
            if (!_unseeded.TryGetValue(__instance, out _)) _unseeded.Add(__instance, _mark);
        }

        private static bool Mine(Random r) => LockstepClock.InTick && !ReferenceEquals(r, _tick) && _unseeded.TryGetValue(r, out _);

        private static bool Next0(Random __instance, ref int __result)
        {
            if (!Mine(__instance)) return true;
            Drawn++;
            __result = _tick.Next();
            return false;
        }

        private static bool Next1(Random __instance, int maxValue, ref int __result)
        {
            if (!Mine(__instance)) return true;
            Drawn++;
            __result = _tick.Next(maxValue);
            return false;
        }

        private static bool Next2(Random __instance, int minValue, int maxValue, ref int __result)
        {
            if (!Mine(__instance)) return true;
            Drawn++;
            __result = _tick.Next(minValue, maxValue);
            return false;
        }

        private static bool NextD(Random __instance, ref double __result)
        {
            if (!Mine(__instance)) return true;
            Drawn++;
            __result = _tick.NextDouble();
            return false;
        }
    }
}
