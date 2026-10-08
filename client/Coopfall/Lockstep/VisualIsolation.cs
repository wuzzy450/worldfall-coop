using System;
using System.Collections.Generic;
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
            "EffectsLibrary.spawnSlash", "EffectsLibrary.spawnExplosionWave",
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
            // effects: isolated unless they change the world (clouds, meteorites, tornadoes, ...)
            var effPre = new HarmonyMethod(typeof(VisualIsolation), nameof(EffectSpawnPrefix));
            foreach (MethodInfo m in typeof(EffectsLibrary).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if ((m.Name == "spawn" || m.Name == "spawnAt" || m.Name == "spawnAtTile" || m.Name == "spawnAtTileRandomScale")
                    && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string))
                { h.Patch(m, prefix: effPre, postfix: post); n++; }
            MethodInfo ctrlUpdate = AccessTools.Method(typeof(BaseEffectController), "update");
            if (ctrlUpdate != null) { h.Patch(ctrlUpdate, prefix: new HarmonyMethod(typeof(VisualIsolation), nameof(EffectUpdatePrefix)), postfix: post); n++; }
            MethodInfo check = AccessTools.Method(typeof(EffectsLibrary), "check");
            if (check != null) h.Patch(check, prefix: new HarmonyMethod(typeof(VisualIsolation), nameof(EffectCheckPrefix)));
            else Log.Error("lockstep: EffectsLibrary.check not found: zoomed-out players may miss world-changing effects");
            MethodInfo flash = AccessTools.Method(typeof(PixelFlashEffects), "flashPixel");
            if (flash != null) h.Patch(flash, prefix: new HarmonyMethod(typeof(VisualIsolation), nameof(FlashPrefix)));
            else Log.Error("lockstep: PixelFlashEffects.flashPixel not found: visual effects may change lava");
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

        /// <summary>How deep we are in visual-only code (inside a tick).</summary>
        private static int _depth;

        private static void SaveDice(out Dice.Snapshot __state)
        {
            __state = LockstepClock.InTick ? Dice.Isolate() : default;
            if (__state.rnd != null) _depth++;
        }

        private static void RestoreDice(Dice.Snapshot __state)
        {
            if (__state.rnd == null) return;
            Dice.Restore(__state);
            _depth--;
        }

        /// <summary>Effects whose code changes the world; everything else is decoration.</summary>
        private static readonly HashSet<string> GameplayEffectTypes = new HashSet<string>
        {
            "AntimatterBombEffect", "Boulder", "Cloud", "EffectInfinityCoin", "Meteorite", "NapalmFlash",
            "NukeFlash", "Santa", "SpawnEffect", "Spores", "TornadoEffect",
        };
        private static readonly Dictionary<string, bool> _gameplayById = new Dictionary<string, bool>();
        private static readonly Dictionary<BaseEffectController, bool> _gameplayByCtrl = new Dictionary<BaseEffectController, bool>();
        private static MethodInfo _stackGet;

        private static bool IsGameplay(BaseEffectController c)
        {
            if (c == null) return false;
            if (_gameplayByCtrl.TryGetValue(c, out bool g)) return g;
            BaseEffect fx = c.prefab == null ? null : c.prefab.GetComponent<BaseEffect>();
            for (Type t = fx?.GetType(); t != null && !g; t = t.BaseType) g = GameplayEffectTypes.Contains(t.Name);
            _gameplayByCtrl[c] = g;
            return g;
        }

        private static bool IsGameplay(string id)
        {
            if (id == null) return false;
            if (_gameplayById.TryGetValue(id, out bool g)) return g;
            if (_stackGet == null) _stackGet = AccessTools.Method(typeof(StackEffects), "get");
            object stack = AccessTools.Field(typeof(MapBox), "stack_effects")?.GetValue(World.world);
            BaseEffectController c = null;
            try { c = _stackGet?.Invoke(stack, new object[] { id }) as BaseEffectController; } catch { }
            g = IsGameplay(c);
            _gameplayById[id] = g;
            return g;
        }

        private static void EffectSpawnPrefix(string __0, out Dice.Snapshot __state)
        {
            __state = default;
            if (!LockstepClock.InTick || IsGameplay(__0)) return;
            __state = Dice.Isolate();
            _depth++;
        }

        private static void EffectUpdatePrefix(BaseEffectController __instance, out Dice.Snapshot __state)
        {
            __state = default;
            if (!LockstepClock.InTick || IsGameplay(__instance)) return;
            __state = Dice.Isolate();
            _depth++;
        }

        /// <summary>World-changing effects spawn even for a player zoomed out to the minimap.</summary>
        private static bool EffectCheckPrefix(string pID, ref BaseEffect __result)
        {
            if (!LockstepClock.InTick || !IsGameplay(pID)) return true;
            EffectAsset a = AssetManager.effects_library.get(pID);
            if (a == null) { __result = null; return false; }
            if (a.cooldown_interval > 0.0 && a.checkIsUnderCooldown()) { __result = null; return false; }
            object stack = AccessTools.Field(typeof(MapBox), "stack_effects")?.GetValue(World.world);
            var c = _stackGet?.Invoke(stack, new object[] { pID }) as BaseEffectController;
            __result = c?.spawnNew();
            return false;
        }

        /// <summary>
        /// Tile flashes are read by lava; a flash asked for by visual-only code (picking its tile
        /// with the scratch dice) must not land in the shared world.
        /// </summary>
        private static bool FlashPrefix() => _depth == 0 || !LockstepClock.InTick;

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
