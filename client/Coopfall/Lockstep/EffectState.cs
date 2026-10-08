using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// World-changing effects that keep local state:
    /// - clouds reset their first action timer when reused from the pool but not the second
    ///   (thunder clouds' lightning), so a reused cloud strikes on the previous cloud's schedule;
    /// - clouds fade by the local camera zoom and game speed, and act until they have faded out,
    ///   so each player's zoom changed how long a cloud rains, burns or strikes;
    /// - creatures only animate while on screen, but dragons (and other creatures with special
    ///   components) decide what to do next by their animation frame.
    /// </summary>
    internal static class EffectState
    {
        private static FieldInfo _cloudTimer2;

        public static void Install(Harmony h)
        {
            MethodInfo prepare = AccessTools.DeclaredMethod(typeof(Cloud), "prepare", new System.Type[0]);
            _cloudTimer2 = AccessTools.Field(typeof(Cloud), "_timer_action_2");
            if (prepare != null && _cloudTimer2 != null) h.Patch(prepare, postfix: new HarmonyMethod(typeof(EffectState), nameof(CloudPreparePostfix)));
            else Log.Error("lockstep: Cloud.prepare not found: reused clouds may strike at different times");
            MethodInfo update = AccessTools.DeclaredMethod(typeof(Cloud), "update");
            if (update != null) h.Patch(update, transpiler: new HarmonyMethod(typeof(EffectState), nameof(CloudUpdateTranspiler)));
            else Log.Error("lockstep: Cloud.update not found: clouds may last longer for zoomed-in players");
            MethodInfo anim = AccessTools.Method(typeof(Actor), "u3_spriteAnimation");
            _childrenSpecial = AccessTools.FieldRefAccess<Actor, List<BaseActorComponent>>("children_special");
            if (anim != null) h.Patch(anim, prefix: new HarmonyMethod(typeof(EffectState), nameof(AnimPrefix)), postfix: new HarmonyMethod(typeof(EffectState), nameof(AnimPostfix)));
            else Log.Error("lockstep: Actor.u3_spriteAnimation not found: dragons may follow the camera");
        }

        private static AccessTools.FieldRef<Actor, List<BaseActorComponent>> _childrenSpecial;
        private static readonly AccessTools.FieldRef<Actor, bool> _visible = AccessTools.FieldRefAccess<Actor, bool>("is_visible");

        /// <summary>In a tick, animate creatures with special components as if on screen.</summary>
        private static void AnimPrefix(Actor __instance, out bool __state)
        {
            __state = LockstepClock.InTick && !_visible(__instance) && _childrenSpecial(__instance) != null;
            if (__state) _visible(__instance) = true;
        }

        private static void AnimPostfix(Actor __instance, bool __state)
        {
            if (__state) _visible(__instance) = false;
        }

        private struct AnimState
        {
            public SpriteAnimation anim;
            public Sprite[] frames;
            public int frame;
            public float next, between;
            public bool on, looped, dirty;
            public AnimPlayType play;
        }

        private static readonly List<AnimState> _anims = new List<AnimState>();

        /// <summary>
        /// Animations that the simulation reads (dragons, UFOs, ...) may be touched between ticks by
        /// anything that draws them (another mod rendering the world advanced dragon animations
        /// with the frame time). Keep their state as the last tick left it.
        /// </summary>
        internal static void SaveAnimations(MapBox map)
        {
            _anims.Clear();
            foreach (Actor a in map.units)
            {
                if (a == null || _childrenSpecial(a) == null) continue;
                SpriteAnimation s = a.sprite_animation;
                if (s == null) continue;
                _anims.Add(new AnimState { anim = s, frames = s.frames, frame = s.currentFrameIndex, next = s.nextFrameTime, between = s.timeBetweenFrames, on = s.isOn, looped = s.looped, dirty = s.dirty, play = s.playType });
            }
        }

        internal static void RestoreAnimations()
        {
            foreach (AnimState st in _anims)
            {
                SpriteAnimation s = st.anim;
                if (s == null) continue;
                s.frames = st.frames; s.currentFrameIndex = st.frame; s.nextFrameTime = st.next; s.timeBetweenFrames = st.between;
                s.isOn = st.on; s.looped = st.looped; s.dirty = st.dirty; s.playType = st.play;
            }
            _anims.Clear();
        }

        private static void CloudPreparePostfix(Cloud __instance)
        {
            if (LockstepClock.Active) _cloudTimer2.SetValue(__instance, 0f);   // a new cloud's value
        }

        // No 'yield' here: an iterator class in a transpiler makes the whole mod fail to load.
        private static IEnumerable<CodeInstruction> CloudUpdateTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo ortho = AccessTools.PropertyGetter(typeof(Camera), nameof(Camera.orthographicSize));
            FieldInfo sonic = AccessTools.Field(typeof(WorldTimeScaleAsset), "sonic");
            var list = new List<CodeInstruction>(code);
            int n = 0;
            foreach (CodeInstruction c in list)
            {
                if (c.Calls(ortho)) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(EffectState), nameof(OrthoSize)); n++; }
                else if (sonic != null && c.LoadsField(sonic)) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(EffectState), nameof(Sonic)); n++; }
            }
            if (n < 3) Log.Error("lockstep: Cloud.update changed (" + n + " replacements): clouds may follow the camera");
            return list;
        }

        /// <summary>In a tick, zoomed out far enough that clouds use their full opacity.</summary>
        private static float OrthoSize(Camera c) => LockstepClock.InTick ? 100f : c.orthographicSize;

        private static bool Sonic(WorldTimeScaleAsset a) => !LockstepClock.InTick && a.sonic;
    }
}
