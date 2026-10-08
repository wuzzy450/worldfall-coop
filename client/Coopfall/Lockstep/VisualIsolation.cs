using System;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Code that only exists to look good (particles, slashes, effects, sounds) runs or not depending
    /// on what each player's camera shows, and rolls Randy's dice while doing so. In a tick, every
    /// such call gets its dice state put back afterwards, so what's on screen can't shift the dice
    /// the simulation uses. Also fixes the few places where the camera changes the world itself.
    /// </summary>
    public static class VisualIsolation
    {
        private static AccessTools.FieldRef<Actor, bool> _actorVisible;
        private static bool _installed;

        /// <summary>Visual-only methods ("Type.method", every overload; "Type.prefix*" for all starting so).</summary>
        private static readonly string[] Isolated =
        {
            "EffectsLibrary.spawn", "EffectsLibrary.spawnAt", "EffectsLibrary.spawnAtTile", "EffectsLibrary.spawnSlash",
            "EffectsLibrary.spawnAtTileRandomScale", "EffectsLibrary.spawnExplosionWave",
            "BaseEffectController.update",
            "GlowParticles.spawn",
            "Actor.spawnParticle", "Actor.spawnSlash", "Actor.doCastAnimation", "Actor.startColorEffect",
            "ActionLibrary.flamingWeapon",
            "BehBoatFishing.spawnFishnet",
            "WorldBehaviourActions.buildingSparks",
            "WorldAgeLibrary.trySpawnThunder",
            // the throw animation rolls dice only when the thrower is on screen; nothing else in
            // these steps uses dice
            "BehThrowResources.execute", "BehThrowResourceAnimation.execute",
            "ResourceThrowManager.addNew",
            "MusicBox.play*",
            // a talk bubble's topic is picked (with dice) by whichever asks first: the bubble on
            // screen or the AI
            "Actor.getSocializeTopic", "CommunicationTopicLibrary.getTopicSprite",
            // names are made the first time something asks (often a nameplate on screen), and
            // making one reseeds the shared dice
            "Actor.getName", "OnomasticsData.generateName", "NameGenerator.getName", "NameGenerator.generateName",
            "NameGenerator.generateNameFromTemplate", "NameGenerator.generateNameFromOnomastics",
        };

        public static void Install(Harmony h)
        {
            if (_installed) return;
            _installed = true;
            _actorVisible = AccessTools.FieldRefAccess<Actor, bool>("is_visible");
            var pre = new HarmonyMethod(typeof(VisualIsolation), nameof(SaveDice));
            var post = new HarmonyMethod(typeof(VisualIsolation), nameof(RestoreDice));
            int n = 0;
            foreach (string item in Isolated)
            {
                int dot = item.LastIndexOf('.');
                Type t = AccessTools.TypeByName(item.Substring(0, dot));
                string name = item.Substring(dot + 1);
                int found = 0;
                if (t != null)
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if ((name.EndsWith("*") ? m.Name.StartsWith(name.TrimEnd('*')) : m.Name == name) && !m.IsAbstract && !m.ContainsGenericParameters)
                        {
                            try { h.Patch(m, prefix: pre, postfix: post); found++; }
                            catch (Exception e) { Log.Error("lockstep: can't isolate " + item + ": " + e.Message); }
                        }
                if (found == 0) Log.Error("lockstep: " + item + " not found (game changed?): its dice may follow the camera");
                n += found;
            }
            // freezing/unfreezing a tile flashes it only when its zone is on screen, and lava reads
            // the flash state: in a tick, flash regardless
            foreach (string m in new[] { "freeze", "unfreeze" })
            {
                MethodInfo mi = AccessTools.Method(typeof(WorldTile), m);
                if (mi != null) h.Patch(mi, prefix: new HarmonyMethod(typeof(VisualIsolation), nameof(ZoneVisiblePrefix)), postfix: new HarmonyMethod(typeof(VisualIsolation), nameof(ZoneVisiblePostfix)));
                else Log.Error("lockstep: WorldTile." + m + " not found: lava may follow the camera");
            }
            // corpses are removed sooner when zoomed out or off screen: in a tick, always the
            // off-screen way
            MethodInfo dead = AccessTools.Method(typeof(Actor), "updateDeadAnimation");
            MethodInfo fullLow = AccessTools.Method(typeof(QualityChanger), "isFullLowRes");
            if (dead != null && fullLow != null)
            {
                h.Patch(dead, prefix: new HarmonyMethod(typeof(VisualIsolation), nameof(DeadPrefix)), postfix: new HarmonyMethod(typeof(VisualIsolation), nameof(DeadPostfix)));
                h.Patch(fullLow, prefix: new HarmonyMethod(typeof(VisualIsolation), nameof(FullLowResPrefix)));
            }
            else Log.Error("lockstep: Actor.updateDeadAnimation not found: corpses may vanish at different times");
            Log.Info("lockstep: " + n + " visual methods keep their dice to themselves");
        }

        private static void SaveDice(out Dice.Snapshot __state)
        {
            __state = LockstepClock.InTick ? Dice.Isolate() : default;
        }

        private static void RestoreDice(Dice.Snapshot __state)
        {
            if (__state.rnd != null) Dice.Restore(__state);
        }

        private static void ZoneVisiblePrefix(WorldTile __instance, out bool __state)
        {
            __state = __instance.zone.visible;
            if (LockstepClock.InTick) __instance.zone.visible = true;
        }

        private static void ZoneVisiblePostfix(WorldTile __instance, bool __state) { __instance.zone.visible = __state; }

        private static bool _inDeadAnim;

        private static void DeadPrefix(Actor __instance, out bool __state)
        {
            __state = _actorVisible(__instance);
            if (!LockstepClock.InTick) return;
            _actorVisible(__instance) = false;
            _inDeadAnim = true;
        }

        private static void DeadPostfix(Actor __instance, bool __state)
        {
            if (!_inDeadAnim) return;
            _inDeadAnim = false;
            _actorVisible(__instance) = __state;
        }

        private static bool FullLowResPrefix(ref bool __result)
        {
            if (!_inDeadAnim) return true;
            __result = false;
            return false;
        }
    }
}
