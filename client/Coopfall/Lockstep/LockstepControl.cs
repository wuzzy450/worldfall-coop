using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Taking over creatures (vanilla possession and Worldfall's first person) in lockstep.
    ///
    /// The game keeps one player's controls in ControllableUnit's statics (movement vector, aim
    /// point, buttons, one-frame presses) and reads them from inside the simulation (the
    /// "possessed" status action). Worldfall fills the same statics in first person and adds its
    /// own (PossessionHooks: pace, climbing, first person on, ...).
    ///
    /// Here each player's controls are sampled every frame and sent as inputs (Possess, Release,
    /// Control); every PC keeps the same table of controllers (creature -> controls). Inside a
    /// tick the statics hold the whole table (all controlled creatures) and, while one controlled
    /// creature runs its possession code, that creature's controls; outside ticks they are this
    /// player's own again (camera, Worldfall's view, menus). Code that changes the world from
    /// per-frame possession code (the game's and Worldfall's) only runs inside ticks.
    /// </summary>
    public static class LockstepControl
    {
        // ------------------------------------------------------------------ controls

        public class Ctl
        {
            public Vector2 move, click;
            public byte held;     // HeldLeft, HeldRight
            public ushort pulse;  // one tick: just pressed, actions, mouse up
            public uint[] wf;     // Worldfall's PossessionHooks values, in WfFields order
            public long house;    // the building Worldfall's house/mine view has them inside (0: outside)

            public const byte HeldLeft = 1, HeldRight = 2;
            public const ushort JustLeft = 1, JustRight = 2, Jump = 4, Talk = 8, Dash = 16, Backstep = 32, Steal = 64, Swear = 128, MouseUp = 256;

            public bool Busy => move != Vector2.zero || held != 0 || (pulse & ~MouseUp) != 0;

            public string Encode()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(H(move.x)).Append(',').Append(H(move.y)).Append(',').Append(H(click.x)).Append(',').Append(H(click.y)).Append(',')
                  .Append(held.ToString("x")).Append(',').Append(pulse.ToString("x"));
                if (wf != null) foreach (uint v in wf) sb.Append(',').Append(v.ToString("x"));
                if (house != 0) sb.Append(';').Append(house.ToString("x"));
                return sb.ToString();
            }

            public static Ctl Decode(string s)
            {
                s = s ?? "";
                long house = 0;
                int semi = s.IndexOf(';');
                if (semi >= 0)
                {
                    long.TryParse(s.Substring(semi + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out house);
                    s = s.Substring(0, semi);
                }
                string[] p = s.Split(',');
                if (p.Length < 6) return null;
                var c = new Ctl
                {
                    move = new Vector2(F(p[0]), F(p[1])),
                    click = new Vector2(F(p[2]), F(p[3])),
                    held = (byte)U(p[4]),
                    pulse = (ushort)U(p[5]),
                    house = house,
                };
                if (p.Length > 6) { c.wf = new uint[p.Length - 6]; for (int i = 6; i < p.Length; i++) c.wf[i - 6] = U(p[i]); }
                return c;
            }

            public bool SameHeld(Ctl o) => o != null && move == o.move && click == o.click && held == o.held && house == o.house && SameWf(wf, o.wf);

            private static bool SameWf(uint[] a, uint[] b)
            {
                if (a == null || b == null) return a == b;
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i] && !WfPulse(i)) return false;
                return true;
            }

            private static string H(float f) => ((uint)BitConverter.SingleToInt32Bits(f)).ToString("x");
            private static float F(string s) => BitConverter.Int32BitsToSingle((int)U(s));
            private static uint U(string s) => uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : 0;
        }

        /// <summary>The shared table: who is controlled, with which controls (same on every PC).</summary>
        private static readonly SortedDictionary<long, Ctl> _table = new SortedDictionary<long, Ctl>();

        public static int Count => _table.Count;
        public static bool IsControlled(long id) => _table.ContainsKey(id);
        public static IEnumerable<long> ControlledIds() => _table.Keys;

        /// <summary>A new epoch: nobody controls anything until they ask again.</summary>
        /// <summary>Who controls each creature in the table (LockstepSession.PlayerKey; same on every PC).</summary>
        private static readonly Dictionary<long, int> _ownerOf = new Dictionary<long, int>();

        /// <summary>The creature this player controls, or null.</summary>
        public static Actor BodyOf(int player)
        {
            if (player == 0) return null;
            foreach (KeyValuePair<long, int> kv in _ownerOf)
                if (kv.Value == player) { Actor a = World.world.units.get(kv.Key); return a != null && a.isAlive() ? a : null; }
            return null;
        }

        public static int OwnerOf(long id) => _ownerOf.TryGetValue(id, out int p) ? p : 0;

        /// <summary>Whether this controlled creature's player is in Worldfall's first person, as every PC has it.</summary>
        public static bool FirstPersonOf(long id) => _wfActive >= 0 && _table.TryGetValue(id, out Ctl c) && c?.wf != null && _wfActive < c.wf.Length && c.wf[_wfActive] != 0;

        /// <summary>Players controlling something, in number order.</summary>
        public static List<int> Players()
        {
            var l = new List<int>(new HashSet<int>(_ownerOf.Values));
            l.Sort();
            return l;
        }

        /// <summary>A controlled creature's controls in the statics (like a possession method in a tick).</summary>
        public static void EnterBody(Actor a) => Enter(a);
        public static void LeaveBody() => Leave();

        public static void Reset() { _ownerOf.Clear(); _table.Clear(); _sentPossess = 0; _lastSent = null; _pulses = 0; _indoors.Clear(); _wantHouse = 0; }

        // ------------------------------------------------------------------ statics

        private static AccessTools.FieldRef<Actor> _main;
        private static AccessTools.FieldRef<HashSet<Actor>> _units;
        private static AccessTools.FieldRef<Vector2> _moveV, _clickV;
        private static AccessTools.FieldRef<bool> _aL, _aR, _jL, _jR, _jump, _talk, _dash, _back, _steal, _swear;
        private static MethodInfo _addStatus;
        private static bool _ready;

        // Worldfall
        private static FieldInfo[] _wfFields;
        private static readonly HashSet<string> WfPulses = new HashSet<string> { "ReleaseAttack", "ScriptedJump" };
        private static bool[] _wfPulse;
        private static int _wfActive = -1, _wfYaw = -1;

        /// <summary>Which way this controlled creature's player looks (Worldfall's first-person yaw), as every PC has it; null if unknown.</summary>
        public static float? YawOf(long id)
        {
            if (_wfYaw < 0 || !_table.TryGetValue(id, out Ctl c) || c?.wf == null || _wfYaw >= c.wf.Length) return null;
            return BitConverter.Int32BitsToSingle((int)c.wf[_wfYaw]);
        }
        private static MethodInfo _wfShake;

        private static bool WfPulse(int i) => _wfPulse != null && i < _wfPulse.Length && _wfPulse[i];

        private class Saved
        {
            public Actor main; public HashSet<Actor> units; public Vector2 move, click;
            public bool aL, aR, jL, jR, jump, talk, dash, back, steal, swear;
            public object[] wf; public Vector2 pointer;
        }

        private static Saved Save()
        {
            var s = new Saved
            {
                main = _main(), units = _units(), move = _moveV(), click = _clickV(),
                aL = _aL(), aR = _aR(), jL = _jL(), jR = _jR(), jump = _jump(), talk = _talk(), dash = _dash(), back = _back(), steal = _steal(), swear = _swear(),
                pointer = InputPointer.Pointer,
            };
            if (_wfFields != null) { s.wf = new object[_wfFields.Length]; for (int i = 0; i < _wfFields.Length; i++) s.wf[i] = _wfFields[i].GetValue(null); }
            return s;
        }

        private static void Restore(Saved s)
        {
            _main() = s.main; _units() = s.units; _moveV() = s.move; _clickV() = s.click;
            _aL() = s.aL; _aR() = s.aR; _jL() = s.jL; _jR() = s.jR; _jump() = s.jump; _talk() = s.talk; _dash() = s.dash; _back() = s.back; _steal() = s.steal; _swear() = s.swear;
            InputPointer.Pointer = s.pointer;
            if (_wfFields != null && s.wf != null) for (int i = 0; i < _wfFields.Length; i++) _wfFields[i].SetValue(null, s.wf[i]);
        }

        /// <summary>Puts a controller's values (or no input at all) into the statics.</summary>
        private static void Load(Ctl c, Actor main)
        {
            _main() = main;
            _moveV() = c?.move ?? Vector2.zero;
            _clickV() = c?.click ?? Vector2.zero;
            byte h = c?.held ?? 0; ushort p = c?.pulse ?? 0;
            _aL() = (h & Ctl.HeldLeft) != 0; _aR() = (h & Ctl.HeldRight) != 0;
            _jL() = (p & Ctl.JustLeft) != 0; _jR() = (p & Ctl.JustRight) != 0;
            _jump() = (p & Ctl.Jump) != 0; _talk() = (p & Ctl.Talk) != 0; _dash() = (p & Ctl.Dash) != 0;
            _back() = (p & Ctl.Backstep) != 0; _steal() = (p & Ctl.Steal) != 0; _swear() = (p & Ctl.Swear) != 0;
            if (c != null) InputPointer.Pointer = c.click;
            if (_wfFields != null)
                for (int i = 0; i < _wfFields.Length; i++)
                {
                    uint v = c?.wf != null && i < c.wf.Length ? c.wf[i] : 0;
                    Type t = _wfFields[i].FieldType;
                    object o = t == typeof(bool) ? (object)(v != 0) : t == typeof(int) ? (object)(int)v : (object)BitConverter.Int32BitsToSingle((int)v);
                    _wfFields[i].SetValue(null, o);
                }
        }

        // ------------------------------------------------------------------ install

        public static void Install(Harmony h)
        {
            if (_ready) return;
            try
            {
                Type cu = typeof(ControllableUnit);
                _main = AccessTools.StaticFieldRefAccess<Actor>(AccessTools.Field(cu, "_unit_main"));
                _units = AccessTools.StaticFieldRefAccess<HashSet<Actor>>(AccessTools.Field(cu, "_units"));
                _moveV = AccessTools.StaticFieldRefAccess<Vector2>(AccessTools.Field(cu, "_movement_vector"));
                _clickV = AccessTools.StaticFieldRefAccess<Vector2>(AccessTools.Field(cu, "_click_vector"));
                _aL = B("_attack_pressed_button_left"); _aR = B("_attack_pressed_button_right");
                _jL = B("_attack_just_pressed_button_left"); _jR = B("_attack_just_pressed_button_right");
                _jump = B("_action_pressed_jump"); _talk = B("_action_pressed_talk"); _dash = B("_action_pressed_dash");
                _back = B("_action_pressed_backstep"); _steal = B("_action_pressed_steal"); _swear = B("_action_pressed_swear");
                _addStatus = AccessTools.Method(cu, "addStatus");

                // the creature's possession code: run with its controller's values
                Ctx(h, AccessTools.Method(typeof(StatusLibrary), "possessedAction"), "pTarget");
                foreach (string m in new[] { "updateMovementPossessedFlip", "getPossessionControlTargetPosition", "getPossessionControlTargetPositionMovementVector" })
                    Ctx(h, AccessTools.Method(typeof(Actor), m), null);
                // per-frame code that changes the world: only inside ticks (as inputs)
                // the game reads this player's keys from inside the simulation step; in lockstep a
                // tick must only see the shared controls, so the keys are read once per frame instead
                _updateControls = AccessTools.Method(cu, "updateControllableUnit");
                if (_updateControls != null) h.Patch(_updateControls, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(UpdateControlsPrefix)));
                else Log.Error("lockstep: ControllableUnit.updateControllableUnit not found: local keys may reach the world");
                Gate(h, AccessTools.Method(cu, "checkPossessionStatus"));
                Gate(h, _addStatus);
                Gate(h, AccessTools.Method(typeof(Actor), "resetAttackTimeout"));
                h.Patch(AccessTools.Method(cu, "clear"), prefix: new HarmonyMethod(typeof(LockstepControl), nameof(ClearPrefix)));
                InstallWorldfall(h);
                WorldCalls.Install(h);   // (was the core gate: those calls now travel as inputs)
                DataCalls.Install(h);
                MadeOutsideTicks.Install(h);
                _ready = true;
                Log.Info("lockstep: possession controls ready" + (_wfFields != null ? " (with Worldfall, " + _wfFields.Length + " first-person values)" : ""));
            }
            catch (Exception e) { Log.Error("lockstep: possession controls not available: " + e); }
        }

        private static MethodInfo _updateControls;
        private static bool _ownUpdate;

        private static bool UpdateControlsPrefix() => _ownUpdate || !LockstepClock.Active;

                private static AccessTools.FieldRef<bool> B(string f) => AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(typeof(ControllableUnit), f));

        private static void Ctx(Harmony h, MethodInfo m, string actorArg)
        {
            if (m == null) { Log.Error("lockstep: a possession method is missing (game changed?): controlled creatures may act on the wrong controls"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(LockstepControl), actorArg != null ? nameof(CtxTargetPrefix) : nameof(CtxActorPrefix)),
                finalizer: new HarmonyMethod(typeof(LockstepControl), nameof(CtxFinalizer)));
        }

        private static void Gate(Harmony h, MethodInfo m)
        {
            if (m == null) { Log.Error("lockstep: a per-frame possession method is missing: it may change the world outside ticks"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(GatePrefix)));
        }

        private static readonly HashSet<string> _blockedLogged = new HashSet<string>();

        /// <summary>Per-frame world changes are not allowed while lockstep runs (outside ticks).</summary>
        private static bool GatePrefix(MethodBase __originalMethod)
        {
            if (!LockstepClock.Active || LockstepClock.InTick) return true;
            if (_blockedLogged.Add(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name) && __originalMethod.DeclaringType.Namespace == "FirstPerson")
                Log.Info("lockstep: Worldfall's " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " changes the world every frame: off while lockstep runs");
            return false;
        }

        /// <summary>
        /// While lockstep runs, the world only changes inside ticks. Per-frame code that would
        /// attack, hit, push, stun or re-task a creature anyway (drawing, other mods' frame
        /// updates) is refused here; the first refusal of each kind is logged with its caller.
        /// </summary>
        private static void InstallCoreGate(Harmony h)
        {
            string[] core =
            {
                "Actor.tryToAttack", "Actor.startAttackCooldown", "Actor.getHit", "Actor.calculateForce", "Actor.makeStunned",
                "Actor.makeConfused", "Actor.cancelAllBeh", "Actor.setTask", "Actor.die", "Actor.addStatusEffect",
                "BaseSimObject.addStatusEffect", "BaseSimObject.finishStatusEffect", "Actor.applyRandomForce", "Actor.takeItems",
            };
            int n = 0;
            foreach (string c in core)
            {
                int dot = c.IndexOf('.');
                Type t = c.StartsWith("Actor.") ? typeof(Actor) : typeof(BaseSimObject);
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != c.Substring(dot + 1) || m.IsAbstract) continue;
                    try { h.Patch(m, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(CoreGatePrefix))); n++; }
                    catch (Exception e) { Log.Warn("lockstep: couldn't guard " + c + ": " + e.Message); }
                }
            }
            Log.Info("lockstep: " + n + " world-changing creature methods only run in ticks");
        }

        private static readonly HashSet<string> _coreLogged = new HashSet<string>();
        /// <summary>An epoch is loaded and running (LockstepSession); loading itself changes the world freely.</summary>
        public static bool Running;

        private static bool CoreGatePrefix(MethodBase __originalMethod)
        {
            if (!Running || !LockstepClock.Active || LockstepClock.InTick) return true;
            string k = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            if (_coreLogged.Add(k) && _coreLogged.Count < 40)
                Log.Warn("lockstep: refused " + k + " outside a tick (it would change one PC's world only). Called from:\n" + new System.Diagnostics.StackTrace(2, false));
            return false;
        }

        /// <summary>
        /// Worldfall: its first-person values (pace, climbing, first person on, aim, ...) are part of
        /// the controls; its per-frame world changes only run in ticks; its first-person aiming
        /// (from the 3D scene only this PC draws) gives way to the game's aim point in ticks.
        /// </summary>
        private static void InstallWorldfall(Harmony h)
        {
            Assembly wf = WorldfallBridge.Assembly;
            if (wf == null) return;
            Type hooks = wf.GetType("FirstPerson.PossessionHooks", false);
            if (hooks != null)
            {
                var skip = new HashSet<string> { "_installed", "_error", "_loggedRuntimeError", "_provenCalls", "_realTime", "_realTimeFrame", "_easedFrame", "YourStepsForTest", "SlideForTest", "SceneHoldsKeys", "_bodyRadius", "WalkFactor", "SprintCap" };
                var l = new List<FieldInfo>();
                foreach (FieldInfo f in hooks.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.IsLiteral || f.IsInitOnly || skip.Contains(f.Name)) continue;
                    if (f.FieldType != typeof(float) && f.FieldType != typeof(bool) && f.FieldType != typeof(int)) continue;
                    l.Add(f);
                }
                l.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                _wfFields = l.ToArray();
                _wfPulse = new bool[_wfFields.Length];
                for (int i = 0; i < _wfFields.Length; i++) { _wfPulse[i] = WfPulses.Contains(_wfFields[i].Name); if (_wfFields[i].Name == "Active") _wfActive = i; if (_wfFields[i].Name == "Yaw") _wfYaw = i; }
                MethodInfo rt = AccessTools.Method(hooks, "RealTime");
                if (rt != null) h.Patch(rt, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(RealTimePrefix)));
                else Log.Warn("lockstep: Worldfall's RealTime not found: a controlled creature's pace may follow the frame rate");
            }
            // first person slows the creatures near your eye (this PC's view): in a tick, the game's pace
            Type pace = wf.GetType("FirstPerson.WalkPace", false);
            MethodInfo of = pace == null ? null : AccessTools.Method(pace, "Of");
            if (of != null) h.Patch(of, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(WalkPacePrefix)));
            else Log.Warn("lockstep: Worldfall's WalkPace.Of not found: creatures near a first-person player may walk differently on each PC");
            // who an attack hits: Worldfall's version follows this PC's first person (your guard,
            // parries, headshots, quests, the friendly-fire setting); in a tick, the game's own
            Type ff = wf.GetType("FirstPerson.FriendlyFire", false);
            MethodInfo caf = ff == null ? null : AccessTools.Method(ff, "CheckAttackFor");
            _canAttack = AccessTools.Method(typeof(BaseSimObject), "canAttackTarget");
            _applyAttack = AccessTools.Method(typeof(MapBox), "applyAttack");
            if (caf != null && _canAttack != null && _applyAttack != null) h.Patch(caf, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(CheckAttackPrefix)));
            else Log.Warn("lockstep: Worldfall's hit check not replaced: fights near a first-person player may differ between PCs");
            Type combat = wf.GetType("FirstPerson.PossessionCombat", false);
            MethodInfo inFp = combat == null ? null : AccessTools.Method(combat, "InFirstPerson");
            if (inFp != null) h.Patch(inFp, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(InFirstPersonPrefix)));
            else Log.Warn("lockstep: Worldfall's InFirstPerson not found: first-person attacks may aim differently on each PC");
            _wfShake = combat == null ? null : AccessTools.Method(combat, "ShakeOffStuns");
            // Worldfall's house and mine rooms put your creature inside the building every frame
            // (and let it out when you leave): that is part of your controls, done in ticks
            Type rooms = wf.GetType("FirstPerson.HouseInterior", false);
            MethodInfo hold = rooms == null ? null : AccessTools.Method(rooms, "HoldIndoors");
            MethodInfo letOut = rooms == null ? null : AccessTools.Method(rooms, "LetOut");
            _wfShelter = rooms == null ? null : AccessTools.Field(rooms, "Shelter");
            if (hold != null && letOut != null)
            {
                h.Patch(hold, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(HoldIndoorsPrefix)));
                h.Patch(letOut, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(LetOutPrefix)));
            }
            else Log.Warn("lockstep: Worldfall's HouseInterior.HoldIndoors/LetOut not found: going inside houses may change one PC's world only");
            // per-frame world changes
            string[] gated =
            {
                "Possess.Keep", "PossessionCombat.ShakeOffStuns",
                "Abilities.Update", "Abilities.UpdateCreatures", "Guards.Update", "FamilyFollow.Update", "FamilyFollow.Send",
                "WarMap.UpkeepFlags", "WarMap.UpkeepFleet", "Service.Update", "Service.Send", "SoldierMap.Update", "Royal.Update",
                "Butchery.Cut",
                // drawing first person moves the creatures around you: crowd steering, and pushing
                // them out of your body
                "Steering.Steer",
                // creatures winding up an attack in first person get their attack timer set
                "Law.SendGuards", "Law.Update", "Economy.Update", "WindUps.Update", "SwingState.KeepBodyClear", "Wind.Look", "StormRun.Board", "StormRun.Hold", "StormRun.March",
            };
            foreach (string g in gated.Concat(CivilInTick.Gated))
            {
                int dot = g.IndexOf('.');
                string cls = g.Substring(0, dot);
                Type t = wf.GetType("FirstPerson." + cls, false);
                if (t == null)
                {
                    try { t = Array.Find(wf.GetTypes(), x => x.Name == cls); }
                    catch (ReflectionTypeLoadException e) { t = Array.Find(e.Types, x => x != null && x.Name == cls); }
                }
                if (t == null) { Log.Warn("lockstep: Worldfall's " + g + " not found (renamed?): it may change the world every frame"); continue; }
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == g.Substring(dot + 1) && !m.IsAbstract && !m.ContainsGenericParameters)
                        try { h.Patch(m, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(GatePrefix))); }
                        catch (Exception e) { Log.Warn("lockstep: couldn't hold back Worldfall's " + g + ": " + e.Message); }
            }
            // first-person hits and boarding travel as inputs (made in a tick on every PC)
            foreach (string g in new[] { "PossessionCombat.SwingHit", "PossessionCombat.Punch", "Boats.Board", "Boats.PutOff" })
            {
                int dot = g.IndexOf('.');
                MethodInfo m = AccessTools.Method(wf.GetType("FirstPerson." + g.Substring(0, dot), false), g.Substring(dot + 1));
                if (m == null || !WorldCalls.Register(h, m)) Log.Warn("lockstep: Worldfall's " + g + " not relayed (renamed?): it may change one PC's world only");
            }
            // family members gather in front of you: Worldfall takes "in front" from this PC's camera
            // when it is your creature, from the creature's walk target otherwise; in a tick it is
            // the yaw from the player's controls (the same on every PC)
            Type family = wf.GetType("FirstPerson.FamilyFollow", false);
            MethodInfo facing = family == null ? null : AccessTools.Method(family, "Facing", new[] { typeof(Actor) });
            if (facing != null) h.Patch(facing, prefix: new HarmonyMethod(typeof(LockstepControl), nameof(FacingPrefix)));
            else Log.Warn("lockstep: Worldfall's FamilyFollow.Facing not found: family members may line up differently on each PC");
            // Worldfall's town patrons walk up to a first-person player: Send sets the task (a relayed
            // game call) and then writes the walk target straight into the creature, on this PC only;
            // the whole call travels instead, and runs in a tick on every PC
            foreach (string g in new[] { "Patrons.Send", "Patrons.Release" })
            {
                int dot = g.IndexOf('.');
                MethodInfo m = AccessTools.Method(wf.GetType("FirstPerson.Towns." + g.Substring(0, dot), false), g.Substring(dot + 1));
                if (m == null || !WorldCalls.Register(h, m)) Log.Warn("lockstep: Worldfall's " + g + " not relayed (renamed?): town patrons may walk on one PC only");
            }
            // some of those run inside ticks instead
            try { WorldfallInTick.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall features in ticks not available: " + e); }
        }

        private static bool FacingPrefix(Actor you, ref float __result)
        {
            if (!LockstepClock.InTick || you == null) return true;
            float? yaw = YawOf(you.getID());
            if (yaw.HasValue) { __result = yaw.Value; return false; }
            // not controlled: Worldfall's own answer for that case (from the walk target)
            __result = 0f;
            WorldTile t = R.Get(you, "tile_target") as WorldTile;
            if (t != null) __result = Mathf.Atan2(t.y + 0.5f - you.current_position.y, t.x + 0.5f - you.current_position.x);
            return false;
        }

        private static bool RealTimePrefix(ref float __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = 1f;   // one tick is one step, whatever the frame rate
            return false;
        }

        private static MethodInfo _canAttack, _applyAttack;

        /// <summary>MapBox.checkAttackFor as the game has it (Worldfall replaces it).</summary>
        private static bool CheckAttackPrefix(AttackData pData, BaseSimObject pTargetToCheck, ref AttackDataResult __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = AttackDataResult.Continue;
            BaseSimObject by = pData.initiator;
            if (pTargetToCheck == null || !pTargetToCheck.isAlive() || by == null || !by.isAlive() || pTargetToCheck == by) return false;
            ParameterInfo[] ps = _canAttack.GetParameters();
            var args = new object[ps.Length];
            args[0] = pTargetToCheck;
            for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            if (!(bool)_canAttack.Invoke(by, args)) return false;
            if (pTargetToCheck is Actor && HasStatus(pTargetToCheck, "dodge")) return false;
            Vector3 target = pTargetToCheck.current_position;
            float d = Toolbox.SquaredDist(target.x, target.y + (float)R.CallN(pTargetToCheck, "getHeight", 0), pData.hit_position.x, pData.hit_position.y + pData.hit_position.z);
            float r = pData.area_of_effect + ((BaseStats)R.Get(pTargetToCheck, "stats"))["size"];
            if (!(d < r * r)) { __result = AttackDataResult.Miss; return false; }
            CivilInTick.BeforeHit(ref pData, by, pTargetToCheck);
            __result = (AttackDataResult)_applyAttack.Invoke(null, new object[] { pData, pTargetToCheck });
            CivilInTick.AfterHit(by, pTargetToCheck, __result.state == ApplyAttackState.Hit, pData.is_projectile);
            if (__result.state == ApplyAttackState.Hit)
            {
                Vector3 hp = pData.hit_position;
                hp.y += hp.z;
                EffectsLibrary.spawnAt(pData.critical ? "fx_hit_critical" : "fx_hit", hp, 0.1f);
            }
            return false;
        }

        private static bool WalkPacePrefix(ref float __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = 1f;
            return false;
        }

        private static bool InFirstPersonPrefix(ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = false;   // aim at the controls' aim point (Worldfall puts its aim there)
            return false;
        }

        // ------------------------------------------------------------------ indoors (Worldfall's rooms)

        private static FieldInfo _wfShelter;
        private static long _wantHouse;
        /// <summary>Controlled creature -> the building its controls keep it in (same on every PC).</summary>
        private static readonly SortedDictionary<long, long> _indoors = new SortedDictionary<long, long>();

        private static bool HoldIndoorsPrefix(Actor body, Building b)
        {
            if (!LockstepClock.Active || LockstepClock.InTick) return true;
            bool shelter = _wfShelter == null || (bool)_wfShelter.GetValue(null);
            if (shelter && body != null && b != null && body == _main()) _wantHouse = b.getID();
            return false;
        }

        private static bool LetOutPrefix(Actor body, Building b)
        {
            if (!LockstepClock.Active || LockstepClock.InTick) return true;
            if (body != null && body == _main() && (b == null || _wantHouse == b.getID())) _wantHouse = 0;
            return false;
        }

        /// <summary>In a tick: what HoldIndoors/LetOut would do, from the controls.</summary>
        private static void KeepIndoors(long id, Actor a, long house)
        {
            _indoors.TryGetValue(id, out long had);
            if (house != 0)
            {
                Building b = World.world.buildings.get(house);
                if (b == null || !b.isAlive()) return;
                if (WorldBoxApi.InsideBuilding(a) != b)
                {
                    R.CallN(a, "stayInBuilding", 1, b);
                    R.CallN(a, "stopMovement", 0);
                    if (had != house) Log.Info("lockstep: #" + id + " went into building #" + house + " (tick " + LockstepClock.Tick + ")");
                }
                // in, not standing by the door: the body is where the house is (like villagers at
                // home: out of reach of attacks, hidden, under the house's roof)
                if ((a.current_position - b.current_position).sqrMagnitude > 0.0001f) WorldBoxApi.SetPosition(a, b.current_position);
                _indoors[id] = house;
            }
            else if (had != 0)
            {
                _indoors.Remove(id);
                if (WorldBoxApi.InsideBuilding(a)?.getID() == had) R.CallN(a, "exitBuilding", 0);
            }
        }

        // ------------------------------------------------------------------ in a tick

        private static Saved _real;
        private static readonly Stack<Saved> _ctx = new Stack<Saved>();
        private static HashSet<Actor> _tickUnits;
        private static Actor _tickMain;

        /// <summary>LockstepClock, after the tick's inputs: the statics hold the shared table.</summary>
        internal static void TickStart()
        {
            if (!_ready) return;
            _real = Save();
            _tickUnits = new HashSet<Actor>();
            _tickMain = null;
            foreach (KeyValuePair<long, Ctl> kv in _table)
            {
                Actor a = World.world.units.get(kv.Key);
                if (a == null || !a.isAlive()) continue;
                _tickUnits.Add(a);
                if (_tickMain == null) _tickMain = a;
            }
            _units() = _tickUnits;
            Load(null, _tickMain);
        }

        /// <summary>LockstepClock, after the tick's inputs were applied.</summary>
        internal static void AfterInputs()
        {
            if (!_ready || _real == null) return;
            // what the per-frame update does while a player is pressing something, and Worldfall's
            // hold on its creature (re-possessed when the status ran out)
            foreach (KeyValuePair<long, Ctl> kv in _table)
            {
                Actor a = World.world.units.get(kv.Key);
                if (a == null || !a.isAlive()) continue;
                Enter(a);
                try
                {
                    Ctl c = kv.Value;
                    if ((c.pulse & Ctl.MouseUp) != 0) R.CallN(a, "resetAttackTimeout", 0);
                    bool fp = _wfActive >= 0 && c.wf != null && _wfActive < c.wf.Length && c.wf[_wfActive] != 0;
                    if (c.Busy || (fp && (!HasStatus(a, "possessed") || LockstepClock.Tick % 100 == 0)))
                    {
                        _addStatus?.Invoke(null, new object[] { a });
                        R.CallN(a, "stopSleeping", 0);
                        R.Set(a, "next_step_position", R.Get(a, "next_step_position_possession"));
                    }
                    if (fp) _wfShake?.Invoke(null, new object[] { a });
                    KeepIndoors(kv.Key, a, c.house);
                }
                catch (Exception e) { Log.Warn("lockstep: controls of #" + kv.Key + ": " + e.Message); }
                finally { Leave(); }
            }
        }

        /// <summary>LockstepClock, after the tick: creatures that died are let go; this player's own controls are back.</summary>
        internal static void TickEnd()
        {
            if (!_ready || _real == null) return;
            var gone = new List<long>();
            foreach (long id in _table.Keys)
            {
                Actor a = World.world.units.get(id);
                if (a == null || !a.isAlive() || (_tickUnits != null && !_tickUnits.Contains(a))) gone.Add(id);
            }
            foreach (long id in gone) { _table.Remove(id); _ownerOf.Remove(id); _indoors.Remove(id); }
            foreach (Ctl c in _table.Values) c.pulse = 0;
            while (_ctx.Count > 0) Restore(_ctx.Pop());
            Restore(_real);
            _real = null;
            _tickUnits = null;
            // this player's creature died in the shared world: let go of it here too
            Actor mine = _main();
            if (mine != null && _sentPossess == mine.getID() && gone.Contains(_sentPossess)) LetGoLocally();
        }

        private static void Enter(Actor a)
        {
            _ctx.Push(Save());
            _table.TryGetValue(a.getID(), out Ctl c);
            Load(c, a);
        }

        private static void Leave()
        {
            if (_ctx.Count > 0) Restore(_ctx.Pop());
        }

        private static void CtxTargetPrefix(BaseSimObject pTarget, out bool __state)
        {
            __state = LockstepClock.InTick && _real != null && pTarget is Actor;
            if (__state) Enter((Actor)pTarget);
        }

        private static void CtxActorPrefix(Actor __instance, out bool __state)
        {
            __state = LockstepClock.InTick && _real != null && __instance != null;
            if (__state) Enter(__instance);
        }

        private static Exception CtxFinalizer(bool __state, Exception __exception)
        {
            if (__state) Leave();
            return __exception;
        }

        /// <summary>
        /// ControllableUnit.clear. In a tick (the last controlled creature died): only the world part,
        /// for the creatures in the shared table. Outside a tick while lockstep runs: only this
        /// player's side (the release reaches the world as an input).
        /// </summary>
        private static bool ClearPrefix()
        {
            if (!LockstepClock.Active || !_ready) return true;
            if (LockstepClock.InTick)
            {
                _units()?.Clear();
                _main() = null;
                return false;
            }
            LetGoLocally();
            return false;
        }

        private static MethodInfo _hasStatus;

        private static bool HasStatus(BaseSimObject a, string id)
        {
            if (_hasStatus == null) _hasStatus = AccessTools.Method(typeof(BaseSimObject), "hasStatus", new[] { typeof(string) });
            return _hasStatus != null && (bool)_hasStatus.Invoke(a, new object[] { id });
        }

        private static void LetGoLocally()
        {
            try { PossessionUI.toggle(pState: false); } catch { }
            try { if (Config.joyControls && R.Get(World.world, "joys") is GameObject joys && joys != null) joys.SetActive(false); } catch { }
            _main() = null;
            _units()?.Clear();
            try { R.CallN(R.Get(World.world, "selected_buttons"), "unselectAll", 0); } catch { }
        }

        // ------------------------------------------------------------------ inputs

        internal static void ApplyPossess(LockstepInput.Input i)
        {
            Actor a = World.world.units.get(i.a);
            if (a == null || !a.isAlive() || !a.canBePossessed() || _table.ContainsKey(i.a)) return;
            _table[i.a] = Ctl.Decode(i.id) ?? new Ctl();
            if (i.player != 0) _ownerOf[i.a] = i.player;
            if (_tickUnits != null) _tickUnits.Add(a);
            Enter(a);
            try { _addStatus?.Invoke(null, new object[] { a }); }
            finally { Leave(); }
        }

        internal static void ApplyRelease(LockstepInput.Input i)
        {
            if (!_table.ContainsKey(i.a)) return;
            Actor a = World.world.units.get(i.a);
            if (a != null && a.isAlive())
            {
                // the world part of ControllableUnit.clear
                Enter(a);
                try
                {
                    R.CallN(a, "finishStatusEffect", 1, "possessed");
                    R.CallN(a, "cancelAllBeh", 0);
                    R.CallN(a, "applyRandomForce", 2, 1.5f, 2f);
                    R.CallN(a, "makeStunned", 1, 1f);
                    R.CallN(a, "makeConfused", 2, 6f, false);
                    R.CallN(a, "setPossessedMovement", 1, false);
                }
                finally { Leave(); }
                _tickUnits?.Remove(a);
                if (_indoors.TryGetValue(i.a, out long had) && WorldBoxApi.InsideBuilding(a)?.getID() == had) R.CallN(a, "exitBuilding", 0);
            }
            _table.Remove(i.a);
            _ownerOf.Remove(i.a);
            _indoors.Remove(i.a);
        }

        internal static void ApplyControl(LockstepInput.Input i)
        {
            if (!_table.TryGetValue(i.a, out Ctl had)) return;
            Ctl c = Ctl.Decode(i.id);
            if (c == null) return;
            c.pulse |= had.pulse;   // two inputs in one tick: keep both presses
            _table[i.a] = c;
        }

        // ------------------------------------------------------------------ this player

        private static long _sentPossess;
        private static Ctl _lastSent;
        private static ushort _pulses;
        private static uint[] _wfPulses;
        private static float _lastSendAt, _possessSentAt;

        /// <summary>Every frame (outside ticks): this player's controls become inputs.</summary>
        /// <summary>Tests: runs right before sampling, to press keys by script.</summary>
        public static Action BeforeSample;

        /// <summary>Tests: scripted controls for this player's creature (outside ticks).</summary>
        public static void Script(Vector2 move, bool attack, bool jump)
        {
            if (!_ready) return;
            _moveV() = move;
            _aL() = attack;
            _jL() |= attack;
            _jump() = jump;
            Actor m = _main();
            if (m != null) _clickV() = m.current_position + move * 3f;
        }

        public static void Sample(LockstepSession ls)
        {
            if (!_ready || !ls.CanSubmit || LockstepClock.InTick) return;
            try { _ownUpdate = true; _updateControls?.Invoke(null, null); }
            catch (Exception e) { Log.Warn("lockstep: reading the possession keys: " + (e.InnerException ?? e).Message); }
            finally { _ownUpdate = false; }
            BeforeSample?.Invoke();
            Actor mine = null;
            try { mine = _main(); } catch { }
            if (mine != null && !mine.isAlive()) mine = null;
            long id = mine?.getID() ?? 0;
            if (id != _sentPossess)
            {
                if (_sentPossess != 0) ls.SubmitControl(LockstepInput.Kind.Release, _sentPossess, null);
                _sentPossess = id;
                _lastSent = null;
                _pulses = 0;
                _wfPulses = null;
                _wantHouse = 0;
                if (id != 0) { _lastSent = Current(); ls.SubmitControl(LockstepInput.Kind.Possess, id, _lastSent.Encode()); _lastSendAt = _possessSentAt = Time.unscaledTime; }
                return;
            }
            if (id == 0) return;
            // a request the host dropped (it was waiting for someone to load): ask again
            if (!_table.ContainsKey(id) && Time.unscaledTime - _possessSentAt > 1.5f)
            {
                _possessSentAt = Time.unscaledTime;
                ls.SubmitControl(LockstepInput.Kind.Possess, id, Current().Encode());
                return;
            }
            // presses last one frame: keep them until they are sent
            if (_jL()) _pulses |= Ctl.JustLeft;
            if (_jR()) _pulses |= Ctl.JustRight;
            if (_jump()) _pulses |= Ctl.Jump;
            if (_talk()) _pulses |= Ctl.Talk;
            if (_dash()) _pulses |= Ctl.Dash;
            if (_back()) _pulses |= Ctl.Backstep;
            if (_steal()) _pulses |= Ctl.Steal;
            if (_swear()) _pulses |= Ctl.Swear;
            try { if (InputHelpers.GetAnyMouseButtonUp()) _pulses |= Ctl.MouseUp; } catch { }
            Ctl now = Current();
            if (_wfFields != null)
            {
                if (_wfPulses == null) _wfPulses = new uint[_wfFields.Length];
                for (int i = 0; i < _wfFields.Length; i++) if (WfPulse(i) && now.wf[i] != 0) _wfPulses[i] = now.wf[i];
            }
            bool changed = !now.SameHeld(_lastSent) || _pulses != 0 || (_wfPulses != null && Array.Exists(_wfPulses, v => v != 0));
            if (!changed || Time.unscaledTime - _lastSendAt < 0.04f) return;
            now.pulse = _pulses;
            if (now.house != 0) now.pulse = 0;   // no swings or jumps from inside a house
            if (_wfPulses != null) for (int i = 0; i < _wfPulses.Length; i++) if (WfPulse(i)) now.wf[i] = _wfPulses[i];
            ls.SubmitControl(LockstepInput.Kind.Control, id, now.Encode());
            _lastSent = now;
            _lastSendAt = Time.unscaledTime;
            _pulses = 0;
            _wfPulses = null;
        }

        /// <summary>This player's held controls (aim rounded to 1/16 tile: the mouse moves all the time).</summary>
        private static Ctl Current()
        {
            Vector2 click = _clickV();
            var c = new Ctl
            {
                move = _moveV(),
                click = new Vector2(Mathf.Round(click.x * 16f) / 16f, Mathf.Round(click.y * 16f) / 16f),
                held = (byte)((_aL() ? Ctl.HeldLeft : 0) | (_aR() ? Ctl.HeldRight : 0)),
                house = _wantHouse,
            };
            // walking around a room isn't walking around the world: the body stays inside
            if (c.house != 0) { c.move = Vector2.zero; c.held = 0; }
            if (_wfFields != null)
            {
                c.wf = new uint[_wfFields.Length];
                for (int i = 0; i < _wfFields.Length; i++)
                {
                    object v = _wfFields[i].GetValue(null);
                    c.wf[i] = v is bool b ? (b ? 1u : 0u) : v is int n ? (uint)n : (uint)BitConverter.SingleToInt32Bits(Mathf.Round((float)v * 256f) / 256f);
                }
            }
            return c;
        }

        /// <summary>A new epoch: this player possesses again from scratch (CoopSession re-possesses after loading).</summary>
        public static void ForgetMine() { _sentPossess = 0; _lastSent = null; _pulses = 0; _wfPulses = null; }
    }
}
