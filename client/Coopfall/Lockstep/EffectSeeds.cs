using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's sparks, chips, volcano smoke and meteor flicker drew from each PC's own dice and
    /// clock (SceneCollector._chipRandom: one unseeded generator; a meteor's look seeded by the
    /// object's hash, which differs per process; flicker from SceneCollector._clock, the PC's own
    /// frame time). In lockstep: the effects' clock is the world's (VisualClock), a meteor's seed
    /// comes from the tick and tile it was thrown at, and chips/sparks/smoke draw from dice seeded
    /// by the building's ID (or the spot) and the tick. Drawing only: the simulation never reads them.
    /// </summary>
    public static class EffectSeeds
    {
        private sealed class Seed { public uint n; }
        private static readonly ConditionalWeakTable<object, Seed> _meteorSeed = new ConditionalWeakTable<object, Seed>();
        private static FieldInfo _chip, _clock, _meteors;
        private static ConstructorInfo _meteorFx;
        private static long _spawnTick = -1;
        private static int _spawnN;
        /// <summary>For the test: seeds given to meteors this epoch (the same on every PC), and how many were drawn with them.</summary>
        public static readonly List<uint> Given = new List<uint>();
        public static int Drawn;

        public static void Install(Harmony h, Assembly wf)
        {
            Type sc = wf.GetType("FirstPerson.SceneCollector", false), mfx = wf.GetType("FirstPerson.Core.MeteorFx", false);
            _chip = sc == null ? null : AccessTools.Field(sc, "_chipRandom");
            _clock = sc == null ? null : AccessTools.Field(sc, "_clock");
            _meteors = sc == null ? null : AccessTools.Field(sc, "_meteors");
            _meteorFx = mfx == null ? null : AccessTools.Constructor(mfx, new[] { typeof(uint) });
            int n = 0;
            MethodInfo collect = sc == null ? null : AccessTools.Method(sc, "Collect");
            if (collect != null && _clock != null) { h.Patch(collect, prefix: new HarmonyMethod(typeof(EffectSeeds), nameof(CollectPrefix))); n++; }
            MethodInfo draw = sc == null ? null : AccessTools.Method(sc, "DrawMeteorFx");
            MethodInfo spawn = AccessTools.Method(typeof(Meteorite), "spawnOn");
            if (draw != null && spawn != null && _meteors != null && _meteorFx != null)
            {
                h.Patch(spawn, postfix: new HarmonyMethod(typeof(EffectSeeds), nameof(SpawnPostfix)));
                h.Patch(draw, prefix: new HarmonyMethod(typeof(EffectSeeds), nameof(DrawMeteorPrefix)));
                n++;
            }
            if (_chip != null)
                foreach (string name in new[] { "Chips", "NukeDropFx", "CollectVolcanoes" })
                {
                    MethodInfo m = AccessTools.Method(sc, name);
                    if (m == null) continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(EffectSeeds), name + "Prefix"));
                    n++;
                }
            if (n < 5) Log.Warn("lockstep: some of Worldfall's effects (sparks, meteors) not found: they may look different on each PC");
            else Log.Info("lockstep: Worldfall's sparks, chips and meteors follow the world's clock and dice");
        }

        private static bool On => LockstepClock.Active;

        public static void Reset() { Given.Clear(); Drawn = 0; _spawnTick = -1; _spawnN = 0; }

        private static void CollectPrefix(object __instance, float dt)
        {
            if (On) _clock.SetValue(__instance, VisualClock.Now() - dt);   // Collect adds dt
        }

        private static uint Mix(long a, long b)
        {
            ulong x = (ulong)a * 0x9E3779B97F4A7C15UL ^ (ulong)b * 0xC2B2AE3D27D4EB4FUL;
            x ^= x >> 29; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 32;
            return (uint)x;
        }

        private static void SpawnPostfix(Meteorite __instance, WorldTile pTile)
        {
            if (!On || __instance == null) return;
            if (_spawnTick != LockstepClock.Tick) { _spawnTick = LockstepClock.Tick; _spawnN = 0; }
            _meteorSeed.Remove(__instance);
            var seed = new Seed { n = Mix(LockstepClock.Tick * 64 + _spawnN++, pTile == null ? 0 : pTile.data.tile_id) };
            _meteorSeed.Add(__instance, seed);
            Given.Add(seed.n);
        }

        private static void DrawMeteorPrefix(object __instance, Meteorite m)
        {
            if (!On || m == null || !_meteorSeed.TryGetValue(m, out Seed s)) return;
            if (!(_meteors.GetValue(__instance) is IDictionary d) || d.Contains(m)) return;
            d[m] = _meteorFx.Invoke(new object[] { s.n });
            Drawn++;
        }

        private static void Dice(object sc, uint seed) => _chip.SetValue(sc, new System.Random((int)seed));

        private static void ChipsPrefix(object __instance, Building b)
        {
            if (On) Dice(__instance, Mix(b == null ? 0 : b.id, LockstepClock.Tick));
        }

        private static void NukeDropFxPrefix(object __instance, float x, float y)
        {
            if (On) Dice(__instance, Mix((long)(x * 16f) * 100003 + (long)(y * 16f), LockstepClock.Tick));
        }

        private static void CollectVolcanoesPrefix(object __instance)
        {
            if (On) Dice(__instance, Mix(0x766F6C63, LockstepClock.Tick));
        }
    }
}
