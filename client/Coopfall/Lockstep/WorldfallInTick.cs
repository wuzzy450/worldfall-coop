using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Some Worldfall features change the world from its per-frame code (held back outside ticks by
    /// LockstepControl's gate). The ones that only need the world and the controlled creatures run
    /// here instead, inside every tick on every PC: the same step, the tick clock in place of
    /// Unity's real-time clock, and their static state reset when an epoch loads.
    /// - Guards: a hurt king's people rally to him (only while someone controls a creature, as in
    ///   Worldfall).
    /// - Creature abilities: creatures near a controlled creature use their X abilities in fights.
    /// - A player's own X ability: aimed on that PC, then paid for and used in a tick (UseAbility,
    ///   relayed like a game call); rushes/leaps move on in ticks (Abilities.Update).
    /// </summary>
    public static class WorldfallInTick
    {
        private static MethodInfo _guards, _creatures, _rush, _for, _canPay, _pay, _refund, _aim;
        private static Type _useType;
        private static FieldInfo _view, _yaw;
        private static Action<string> _noToast = s => { };
        private static readonly List<KeyValuePair<FieldInfo, object>> _statics = new List<KeyValuePair<FieldInfo, object>>();
        private static float _abilityTimer;
        public static bool Ready => _guards != null || _creatures != null;

        public static void Install(Harmony h, Assembly wf)
        {
            Type guards = wf.GetType("FirstPerson.Guards", false), abilities = wf.GetType("FirstPerson.Abilities", false);
            _guards = guards == null ? null : AccessTools.Method(guards, "Update", new[] { typeof(float), typeof(Actor), typeof(Action<string>) });
            _creatures = abilities == null ? null : AccessTools.Method(abilities, "UpdateCreatures", new[] { typeof(Actor), typeof(float) });
            if (_guards == null) Log.Warn("lockstep: Worldfall's Guards.Update not found: kings' defenders stay off in lockstep");
            if (_creatures == null) Log.Warn("lockstep: Worldfall's Abilities.UpdateCreatures not found: creature abilities stay off in lockstep");
            int n = 0;
            if (_guards != null) { n += SwapClock(h, guards); Remember(guards, "_timer", "_round", "LastRallied", "Kings", "Stale", "Candidates"); }
            if (_creatures != null) { n += SwapClock(h, abilities); Remember(abilities, "PendingRush", "_npcTimer", "NpcReadyAt", "CreatureUses", "CreatureUsed"); }
            InstallPlayerAbility(h, wf, abilities);
            HideCollidersInTicks(wf);
            InstallBodyClear(h, wf);
            FindLazyAssets(wf);
            InstallFirstPersonChecks(h, wf);
            try { SteeringInTick.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall steering in ticks not available: " + e); }
            try { VisualClock.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall's figures not on the world's clock: " + e); }
            try { EffectSeeds.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall's effects not on the world's dice: " + e); }
            try { OwnMindInTick.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall OwnMind in ticks not available: " + e); }
            try { ClimbingInTick.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall climbing in ticks not available: " + e); }
            try { PlayerScope.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall per player not available: " + e); }
            Log.Info("lockstep: Worldfall's " + (_guards != null ? "guards " : "") + (_creatures != null ? "creature abilities " : "") + "run in ticks (" + n + " methods on the tick clock)");
        }

        /// <summary>Static fields reset at every epoch load: collections emptied, values zeroed.</summary>
        private static void Remember(Type t, params string[] names)
        {
            foreach (string f in names)
            {
                FieldInfo fi = AccessTools.Field(t, f);
                if (fi == null || !fi.IsStatic) { Log.Warn("lockstep: Worldfall's " + t.Name + "." + f + " not found: it may carry state between epochs"); continue; }
                _statics.Add(new KeyValuePair<FieldInfo, object>(fi, fi.FieldType.IsValueType ? Activator.CreateInstance(fi.FieldType) : null));
            }
        }

        private static void InstallPlayerAbility(Harmony h, Assembly wf, Type abilities)
        {
            Type mod = wf.GetType("FirstPerson.WorldBoxMod", false), ability = wf.GetType("FirstPerson.Ability", false);
            _useType = wf.GetType("FirstPerson.AbilityUse", false);
            if (abilities == null || mod == null || ability == null || _useType == null) { Log.Warn("lockstep: Worldfall's abilities not found: X abilities stay off in lockstep"); return; }
            _rush = AccessTools.Method(abilities, "Update", Type.EmptyTypes);
            _for = AccessTools.Method(abilities, "For");
            _canPay = AccessTools.Method(abilities, "CanPay");
            _pay = AccessTools.Method(abilities, "Pay");
            _refund = AccessTools.Method(abilities, "Refund");
            _aim = AccessTools.Method(mod, "AbilityAim");
            _view = AccessTools.Field(mod, "_view");
            _yaw = _view == null ? null : AccessTools.Field(_view.FieldType, "Yaw");
            MethodInfo release = AccessTools.Method(mod, "ReleaseAbility");
            MethodInfo use = AccessTools.Method(typeof(WorldfallInTick), nameof(UseAbility));
            if (_rush == null || _for == null || _canPay == null || _pay == null || _refund == null || _aim == null || _yaw == null || release == null || !WorldCalls.Register(h, use))
            {
                Log.Warn("lockstep: a Worldfall ability method is missing (renamed?): X abilities stay off in lockstep");
                _rush = null;
                return;
            }
            h.Patch(release, prefix: new HarmonyMethod(typeof(WorldfallInTick), nameof(ReleasePrefix)));
            // paid for in the tick, on every PC
            h.Patch(_pay, prefix: new HarmonyMethod(typeof(WorldfallInTick), nameof(OnlyInTicks)));
            h.Patch(_refund, prefix: new HarmonyMethod(typeof(WorldfallInTick), nameof(OnlyInTicks)));
        }

        private static FieldInfo _eyeOffset;
        private static readonly AccessTools.FieldRef<Actor, bool> _dirtyTile = AccessTools.FieldRefAccess<Actor, bool>("dirty_current_tile");
        private static float _nudgeAt;

        /// <summary>
        /// Worldfall pushes your own body out of walls and furniture every frame, using the
        /// colliders it built around your camera (other PCs don't have them). In lockstep that push
        /// is worked out on your PC only and sent as a relayed Nudge (at most 10 a second).
        /// </summary>
        private static void InstallBodyClear(Harmony h, Assembly wf)
        {
            Type mod = wf.GetType("FirstPerson.WorldBoxMod", false);
            MethodInfo keep = mod == null ? null : AccessTools.Method(mod, "KeepBodyClear", new[] { typeof(Actor) });
            _eyeOffset = mod == null ? null : AccessTools.Field(mod, "_eyeOffset");
            if (keep == null || _eyeOffset == null || !WorldCalls.Register(h, AccessTools.Method(typeof(WorldfallInTick), nameof(Nudge))))
            {
                Log.Warn("lockstep: Worldfall's KeepBodyClear not hooked: your body isn't pushed out of walls in lockstep");
                return;
            }
            h.Patch(keep, prefix: new HarmonyMethod(typeof(WorldfallInTick), nameof(BodyClearPrefix)), postfix: new HarmonyMethod(typeof(WorldfallInTick), nameof(BodyClearPostfix)));
        }

        private struct BodyWas { public bool on; public Vector2 pos, eye; public bool dirty; }

        private static void BodyClearPrefix(object __instance, Actor host, out BodyWas __state)
        {
            __state = default;
            if (!LockstepClock.Active || LockstepClock.InTick || host == null || !LockstepControl.Running) return;
            __state = new BodyWas { on = true, pos = host.current_position, eye = (Vector2)_eyeOffset.GetValue(__instance), dirty = _dirtyTile(host) };
        }

        private static void BodyClearPostfix(object __instance, Actor host, BodyWas __state)
        {
            if (!__state.on) return;
            Vector2 to = host.current_position;
            if (to == __state.pos) return;
            // undo it here; the same push reaches every PC as an input
            host.current_position = __state.pos;
            _dirtyTile(host) = __state.dirty;
            _eyeOffset.SetValue(__instance, __state.eye);
            if ((to - __state.pos).sqrMagnitude < 0.0004f || Time.unscaledTime - _nudgeAt < 0.1f) return;
            _nudgeAt = Time.unscaledTime;
            Nudge(host, to);   // caught by WorldCalls and sent
        }

        /// <summary>Relayed (WorldCalls): a controlled creature pushed out of a wall, in a tick on every PC.</summary>
        public static void Nudge(Actor a, Vector2 to)
        {
            if (!LockstepClock.InTick || a == null || !a.isAlive() || !LockstepControl.IsControlled(a.getID())) return;
            if ((to - a.current_position).sqrMagnitude > 4f) return;   // it moved on since: stale
            a.current_position = to;
            _dirtyTile(a) = true;
        }

        private static bool OnlyInTicks() => !LockstepClock.Active || LockstepClock.InTick;

        /// <summary>This player lets go of an X ability: aim here, use it in a tick everywhere.</summary>
        private static bool ReleasePrefix(object __instance, Actor host, object ab)
        {
            if (!LockstepClock.Active || LockstepClock.InTick || _rush == null) return true;
            if (host == null || !host.isAlive()) return false;
            try
            {
                object[] args = { host, ab, null };
                Vector2 at = (Vector2)_aim.Invoke(__instance, args);
                float yaw = (float)_yaw.GetValue(_view.GetValue(__instance));
                UseAbility(host, at, args[2] as Actor, new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw)));   // caught by WorldCalls and sent
            }
            catch (Exception e) { Log.Error("lockstep: X ability not sent: " + (e.InnerException ?? e).Message); }
            return false;
        }

        /// <summary>The creature's X ability (null: none, or Worldfall's abilities aren't hooked).</summary>
        public static string AbilityName(Actor a)
        {
            object ab = _for == null || a == null ? null : _for.Invoke(null, new object[] { a });
            return ab == null ? null : (string)ab.GetType().GetField("Name").GetValue(ab);
        }

        /// <summary>Relayed (WorldCalls): runs in a tick on every PC.</summary>
        public static void UseAbility(Actor self, Vector2 at, Actor target, Vector2 dir)
        {
            if (!LockstepClock.InTick || self == null || !self.isAlive() || _for == null) return;
            object ab = _for.Invoke(null, new object[] { self });
            if (ab == null) return;
            object[] pay = { self, ab, null };
            if (!(bool)_canPay.Invoke(null, pay)) return;
            _pay.Invoke(null, new object[] { self, ab });
            object use = Activator.CreateInstance(_useType);
            _useType.GetField("Self").SetValue(use, self);
            _useType.GetField("At").SetValue(use, at);
            _useType.GetField("Target").SetValue(use, target);
            _useType.GetField("Dir").SetValue(use, dir);
            var effect = (Delegate)ab.GetType().GetField("Effect").GetValue(ab);
            bool ok = false;
            try { ok = (bool)effect.DynamicInvoke(use); }
            catch (Exception e) { Log.Error("lockstep: X ability failed: " + (e.InnerException ?? e).Message); }
            if (!ok) _refund.Invoke(null, new object[] { self, ab });
        }

        private static IList _near;
        private static readonly List<object> _nearHeld = new List<object>();

        /// <summary>
        /// Worldfall's solid shapes near you (Colliders.Near) are built while drawing this PC's view,
        /// so they differ between PCs. Abilities (Charge's run), boarding and pushes read them; in a
        /// tick that list is empty on every PC (only the world's tiles stop a creature there).
        /// </summary>
        private static void HideCollidersInTicks(Assembly wf)
        {
            Type colliders = wf.GetType("FirstPerson.Colliders", false);
            _near = colliders == null ? null : AccessTools.Field(colliders, "Near")?.GetValue(null) as IList;
            if (_near == null) { Log.Warn("lockstep: Worldfall's Colliders.Near not found: abilities and boats near buildings may differ between PCs"); return; }
            LockstepClock.BeforeTick += t =>
            {
                if (_nearHeld.Count > 0 || _near.Count == 0) return;
                foreach (object c in _near) _nearHeld.Add(c);
                _near.Clear();
            };
            LockstepClock.AfterTick += t => PutCollidersBack();
        }

        /// <summary>After ticks (also called when a frame's ticks stop early): the drawn view's shapes again.</summary>
        public static void PutCollidersBack()
        {
            if (_near == null || _nearHeld.Count == 0) return;
            _near.Clear();
            foreach (object c in _nearHeld) _near.Add(c);
            _nearHeld.Clear();
        }

        private static MethodInfo _keepOnCarcass;
        private static bool _inKeep;
        private static readonly object[] _keepArgs = new object[2];

        /// <summary>
        /// "Is this player in first person?" is a local fact. In ticks Worldfall asks it of the
        /// acting player: the answer comes from that player's controls (the same on every PC).
        /// A kill near a first-person player keeps the carcass for butchering (Butchery's hook on
        /// the game's loot pickup, run inside the simulation): there it is asked of every
        /// controlled creature in turn, in ID order, each in its player's scope.
        /// </summary>
        private static void InstallFirstPersonChecks(Harmony h, Assembly wf)
        {
            Type mod = wf.GetType("FirstPerson.WorldBoxMod", false), butchery = wf.GetType("FirstPerson.Butchery", false);
            MethodInfo fp = mod == null ? null : AccessTools.PropertyGetter(mod, "IsFirstPerson");
            if (fp != null) h.Patch(fp, prefix: new HarmonyMethod(typeof(WorldfallInTick), nameof(IsFirstPersonPrefix)));
            else Log.Warn("lockstep: Worldfall's IsFirstPerson not found: features asking it in ticks may differ between PCs");
            _keepOnCarcass = butchery == null ? null : AccessTools.Method(butchery, "KeepOnCarcass", new[] { typeof(Actor), typeof(Actor) });
            if (_keepOnCarcass != null) h.Patch(_keepOnCarcass, prefix: new HarmonyMethod(typeof(WorldfallInTick), nameof(KeepOnCarcassPrefix)));
            else Log.Warn("lockstep: Worldfall's Butchery.KeepOnCarcass not found: loot from kills near a player may differ between PCs");
        }

        private static bool IsFirstPersonPrefix(ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            Actor b = PlayerScope.CurrentBody;
            __result = b != null && LockstepControl.FirstPersonOf(b.getID());
            return false;
        }

        private static bool KeepOnCarcassPrefix(Actor self, Actor killer, ref bool __result)
        {
            if (!LockstepClock.InTick || PlayerScope.Open || _inKeep) return true;
            __result = false;
            var ids = new List<long>(LockstepControl.ControlledIds());
            ids.Sort();
            _inKeep = true;
            try
            {
                foreach (long id in ids)
                {
                    int player = LockstepControl.OwnerOf(id);
                    if (player == 0) continue;
                    bool keep = false;
                    _keepArgs[0] = self; _keepArgs[1] = killer;
                    PlayerScope.Run(player, () => keep = (bool)_keepOnCarcass.Invoke(null, _keepArgs));
                    if (keep) { __result = true; break; }
                }
            }
            catch (Exception e) { Log.Error("lockstep: carcass check: " + (e.InnerException ?? e).Message); }
            finally { _inKeep = false; }
            return false;
        }

        private static readonly List<MethodInfo> _lazyAssets = new List<MethodInfo>();

        /// <summary>
        /// Worldfall adds some AI tasks (and a loyalty kind) to the game's libraries the first time
        /// its feature runs, which happens only on the PC whose player uses it. A relayed call that
        /// sets such a task (town patrons walking up, family following, a king's levy, ships
        /// sailing) then finds no task on the other PCs. Every PC adds them all at each epoch load.
        /// </summary>
        private static void FindLazyAssets(Assembly wf)
        {
            foreach (string spec in new[] { "FirstPerson.FamilyFollow.RegisterTasks", "FirstPerson.Law.RegisterFavour", "FirstPerson.Royal.RegisterTask",
                "FirstPerson.Royal.RegisterSailTask", "FirstPerson.Royal.RegisterBoardTask", "FirstPerson.Royal.RegisterHoldTask",
                "FirstPerson.Towns.HomeLife.Register", "FirstPerson.Towns.Patrons.Register" })
            {
                int dot = spec.LastIndexOf('.');
                Type t = wf.GetType(spec.Substring(0, dot), false);
                MethodInfo m = t == null ? null : AccessTools.Method(t, spec.Substring(dot + 1), Type.EmptyTypes);
                if (m == null || !m.IsStatic) Log.Warn("lockstep: Worldfall's " + spec + " not found: what it adds may exist on one PC only");
                else _lazyAssets.Add(m);
            }
        }

        private static void AddLazyAssets()
        {
            foreach (MethodInfo m in _lazyAssets)
                try { m.Invoke(null, null); }
                catch (Exception e) { Log.Warn("lockstep: Worldfall's " + m.DeclaringType.Name + "." + m.Name + ": " + (e.InnerException ?? e).Message); }
        }

        public static void Reset()
        {
            _abilityTimer = 0f;
            AddLazyAssets();
            DataCalls.Reset();
            TalkInTick.Reset();
            TownsInTick.Reset();
            BelongingsInTick.Reset();
            PlayerScope.Reset();
            SteeringInTick.Reset();
            ClimbingInTick.Reset();
            EffectSeeds.Reset();
            ZeroHpWatch.Reset();
            foreach (KeyValuePair<FieldInfo, object> kv in _statics)
            {
                object v = kv.Key.GetValue(null);
                if (v is IDictionary d) d.Clear();
                else if (v is IList l) l.Clear();
                else if (!kv.Key.IsInitOnly) kv.Key.SetValue(null, kv.Value);
            }
        }

        /// <summary>Called in every tick after the simulation step.</summary>
        public static void Run()
        {
            PlayerScope.RunTick();
            if (_rush != null)
                try { _rush.Invoke(null, null); }
                catch (Exception e) { Log.Error("lockstep: ability rush: " + (e.InnerException ?? e).Message); }
            if (LockstepControl.Count == 0) return;
            SteeringInTick.Run();
            if (_guards != null)
                try { _guards.Invoke(null, new object[] { LockstepClock.DefaultStep, null, _noToast }); }
                catch (Exception e) { Log.Error("lockstep: guards: " + (e.InnerException ?? e).Message); }
            if (_creatures == null) return;
            // Worldfall checks every 0.5 s around its one player; here around every controlled
            // creature, in ID order, on one shared timer
            _abilityTimer -= LockstepClock.DefaultStep;
            if (_abilityTimer > 0f) return;
            _abilityTimer = 0.5f;
            foreach (long id in LockstepControl.ControlledIds())
            {
                Actor a = World.world.units.get(id);
                if (a == null || !a.isAlive()) continue;
                try { _creatures.Invoke(null, new object[] { a, 1f }); }   // 1 s: past its own 0.5 s timer
                catch (Exception e) { Log.Error("lockstep: creature abilities: " + (e.InnerException ?? e).Message); }
            }
        }

        public static float UnscaledTime() => LockstepClock.InTick ? (float)LockstepClock.SessionTime : Time.unscaledTime;
        public static float RealtimeSinceStartup() => LockstepClock.InTick ? (float)LockstepClock.SessionTime : Time.realtimeSinceStartup;
        public static float UnscaledDeltaTime() => LockstepClock.InTick ? LockstepClock.DefaultStep : Time.unscaledDeltaTime;

        private static Dictionary<MethodInfo, MethodInfo> _swap;
        private static readonly HashSet<Type> _swapped = new HashSet<Type>();

        /// <summary>SwapClock, once per class.</summary>
        public static int SwapClockOf(Harmony h, Type t) => _swapped.Add(t) ? SwapClock(h, t) : 0;

        /// <summary>Every method of the class (and its nested lambda classes) reads the tick clock in ticks.</summary>
        private static int SwapClock(Harmony h, Type t)
        {
            if (_swap == null)
                _swap = new Dictionary<MethodInfo, MethodInfo>
                {
                    { AccessTools.PropertyGetter(typeof(Time), nameof(Time.frameCount)), AccessTools.Method(typeof(FrameClock), nameof(FrameClock.FrameCount)) },
                    { AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime)), AccessTools.Method(typeof(FrameClock), nameof(FrameClock.DeltaTime)) },
                    { AccessTools.PropertyGetter(typeof(Time), nameof(Time.time)), AccessTools.Method(typeof(FrameClock), nameof(FrameClock.TimeNow)) },
                    { AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime)), AccessTools.Method(typeof(WorldfallInTick), nameof(UnscaledTime)) },
                    { AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)), AccessTools.Method(typeof(WorldfallInTick), nameof(RealtimeSinceStartup)) },
                    { AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime)), AccessTools.Method(typeof(WorldfallInTick), nameof(UnscaledDeltaTime)) },
                };
            int n = 0;
            var types = new List<Type> { t };
            for (int i = 0; i < types.Count; i++) types.AddRange(types[i].GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            var transpiler = new HarmonyMethod(typeof(WorldfallInTick), nameof(Transpiler));
            foreach (Type x in types)
            {
                if (x.IsGenericTypeDefinition) continue;
                foreach (MethodInfo m in x.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null || !ReadsClock(m)) continue;
                    try { h.Patch(m, transpiler: transpiler); n++; }
                    catch (Exception e) { Log.Warn("lockstep: couldn't put " + x.Name + "." + m.Name + " on the tick clock: " + e.Message); }
                }
            }
            return n;
        }

        private static bool ReadsClock(MethodInfo m)
        {
            try
            {
                foreach (KeyValuePair<OpCode, object> i in PatchProcessor.ReadMethodBody(m))
                    if (i.Value is MethodInfo target && _swap.ContainsKey(target)) return true;
            }
            catch { }
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            foreach (CodeInstruction c in list)
                if ((c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt) && c.operand is MethodInfo m && _swap.TryGetValue(m, out MethodInfo to))
                {
                    c.opcode = OpCodes.Call;
                    c.operand = to;
                }
            return list;
        }
    }
}
