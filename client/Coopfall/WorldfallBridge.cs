using System;
using System.Reflection;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Optional link to Worldfall (FirstPerson.WorldBoxMod in Worldfall.dll), found by reflection so
    /// Coopfall still runs without it. Worldfall draws its 3D frame in OnGUI at GUI.depth -1000;
    /// Coopfall draws at a lower depth so it stays on top. This bridge lets Coopfall:
    /// * know when the 3D first-person view covers the screen,
    /// * project world points into that view (name tags, chat bubbles over remote players),
    /// * keep Worldfall's own E/Q/T/F/R prompts quiet while you type in the co-op chat,
    /// * switch to Worldfall's top-down view (its V key) while a co-op window needs the mouse.
    /// Every member is looked up once; anything missing just disables that feature.
    /// </summary>
    public static class WorldfallBridge
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type _type;
        private static float _nextLookup;
        private static bool _logged;
        private static PropertyInfo _piInstance, _piIsFp, _piShown, _piTalkBusy, _piGameKeysMuted, _piDiving;
        private static MethodInfo _miStarHead, _miProject;
        private static FieldInfo _fiConvClosed, _fiViewEnabled, _fiWidth, _fiHeight;

        public static bool Present { get { Lookup(); return _type != null; } }
        /// <summary>Worldfall's assembly (null without Worldfall).</summary>
        public static System.Reflection.Assembly Assembly => Present ? _type.Assembly : null;

        private static void Lookup()
        {
            if (_type != null || Time.unscaledTime < _nextLookup) return;
            _nextLookup = Time.unscaledTime + 5f;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType("FirstPerson.WorldBoxMod", false);
                    if (t == null) continue;
                    _piInstance = t.GetProperty("Instance", Any);
                    _piIsFp = t.GetProperty("IsFirstPerson", Any);
                    _piShown = t.GetProperty("Shown", Any);
                    _piTalkBusy = t.GetProperty("TalkBusy", Any);
                    _piGameKeysMuted = t.GetProperty("GameKeysMuted", Any);
                    _piDiving = t.GetProperty("Diving", Any);
                    _miStarHead = t.GetMethod("StarHead", Any, null, new[] { typeof(Actor), typeof(Vector3).MakeByRefType() }, null);
                    _fiConvClosed = t.GetField("_convClosedFrame", Any);
                    _fiViewEnabled = t.GetField("_viewEnabled", Any);
                    if (_piShown != null)
                    {
                        Type proj = _piShown.PropertyType;
                        // Worldfall 0.9.4 added a 7-parameter overload: ask for the 6-parameter one
                        // (x, y, z, out col, out row, out depth) by its exact signature
                        Type fo = typeof(float).MakeByRefType();
                        _miProject = proj.GetMethod("Project", Any, null, new[] { typeof(float), typeof(float), typeof(float), fo, fo, fo }, null);
                        _fiWidth = proj.GetField("Width", Any);
                        _fiHeight = proj.GetField("Height", Any);
                    }
                    _type = t;
                    Log.Info("Worldfall found: fp=" + (_piIsFp != null) + " project=" + (_miProject != null) + " head=" + (_miStarHead != null) +
                             " talk=" + (_piTalkBusy != null) + " mute=" + (_fiConvClosed != null) + " view=" + (_fiViewEnabled != null));
                    return;
                }
                if (!_logged) { _logged = true; Log.Info("Worldfall not loaded (looking again every 5 s)"); }
            }
            catch (Exception e) { Log.Warn("Worldfall lookup: " + e.Message); }
        }

        private static object Instance
        {
            get
            {
                Lookup();
                try { return _piInstance?.GetValue(null, null); } catch { return null; }
            }
        }

        // Worldfall's wind gusts (SceneCollector.Gusts): their whole state, so a guest's gusts follow the host's.
        private static readonly string[] GustFields = { "Strength", "DirX", "DirY", "_wait", "_len", "_t", "_peak", "_angle", "_veer", "_clock" };
        private static FieldInfo[] _gustFields;
        private static FieldInfo _fiGusts, _fiGustCount;

        private static object Gusts()
        {
            object inst = Instance;
            if (inst == null) return null;
            LookupView();
            if (_fiScene == null) return null;
            object scene = _fiScene.GetValue(inst);
            if (scene == null) return null;
            if (_fiGusts == null) _fiGusts = scene.GetType().GetField("Gusts", Any);
            object g = _fiGusts?.GetValue(scene);
            if (g != null && _gustFields == null)
            {
                var list = new FieldInfo[GustFields.Length];
                for (int i = 0; i < list.Length; i++)
                {
                    list[i] = g.GetType().GetField(GustFields[i], Any);
                    if (list[i] == null || list[i].FieldType != typeof(float)) { Log.Warn("Worldfall gusts: no " + GustFields[i] + " - wind not synced"); _fiGusts = null; return null; }
                }
                _gustFields = list;
                _fiGustCount = g.GetType().GetField("Count", Any);
                Log.Info("Worldfall gusts: synced");
            }
            return _gustFields == null ? null : g;
        }

        /// <summary>The host's gust state (10 numbers), or null without Worldfall.</summary>
        public static float[] GustState()
        {
            try
            {
                object g = Gusts();
                if (g == null) return null;
                var v = new float[_gustFields.Length];
                for (int i = 0; i < v.Length; i++) v[i] = (float)_gustFields[i].GetValue(g);
                return v;
            }
            catch { return null; }
        }

        /// <summary>Guest: take over the host's gusts (their timing, strength and direction).</summary>
        public static bool SetGustState(float[] v)
        {
            try
            {
                object g = Gusts();
                if (g == null || v == null || v.Length != _gustFields.Length) return false;
                float wasLen = (float)_gustFields[4].GetValue(g);
                for (int i = 0; i < v.Length; i++) _gustFields[i].SetValue(g, v[i]);
                if (wasLen <= 0f && v[4] > 0f && _fiGustCount != null) _fiGustCount.SetValue(g, (int)_fiGustCount.GetValue(g) + 1);   // a new gust here too
                return true;
            }
            catch { return false; }
        }

        private static bool GetBool(PropertyInfo pi)
        {
            object inst = Instance;
            if (inst == null || pi == null) return false;
            try { return (bool)pi.GetValue(inst, null); } catch { return false; }
        }

        /// <summary>True while Worldfall's 3D first-person picture covers the screen.</summary>
        public static bool FirstPerson { get { return GetBool(_piIsFp); } }

        private static PropertyInfo _piGodAlpha;

        /// <summary>Worldfall's 3D god view (the zoomed-in map, not possessing) is covering the 2D map.</summary>
        public static bool GodView
        {
            get
            {
                object inst = Instance;
                if (inst == null) return false;
                try
                {
                    if (_piGodAlpha == null) _piGodAlpha = _type.GetProperty("GodAlpha", Any);
                    return _piGodAlpha != null && (float)_piGodAlpha.GetValue(inst, null) >= 0.5f;
                }
                catch { return false; }
            }
        }

        /// <summary>Worldfall is animating into or out of first person.</summary>
        public static bool Diving { get { return GetBool(_piDiving); } }

        /// <summary>Worldfall is showing a conversation, naming, a notice board or heir choice: Enter belongs to it.</summary>
        public static bool WantsKeys { get { return GetBool(_piTalkBusy) || GetBool(_piGameKeysMuted); } }

        /// <summary>
        /// Call every frame while the co-op chat is open: Worldfall then treats its talk/door/board
        /// prompts as busy, so typing E, Q, T, F or R doesn't trigger them.
        /// </summary>
        public static void MutePrompts()
        {
            object inst = Instance;
            if (inst == null || _fiConvClosed == null) return;
            try { _fiConvClosed.SetValue(inst, Time.frameCount); } catch { }
        }

        /// <summary>Worldfall's own V toggle (first person / top-down) while possessing.</summary>
        public static bool ViewEnabled
        {
            get
            {
                object inst = Instance;
                if (inst == null || _fiViewEnabled == null) return false;
                try { return (bool)_fiViewEnabled.GetValue(inst); } catch { return false; }
            }
            set
            {
                object inst = Instance;
                if (inst == null || _fiViewEnabled == null) return;
                try { _fiViewEnabled.SetValue(inst, value); } catch { }
            }
        }

        private static PropertyInfo _piViewYaw;
        private static MethodInfo _miSetYaw;
        private static FieldInfo _fiScene, _fiLookAt, _fiHeadings;

        private static void LookupView()
        {
            if (_type == null || _piViewYaw != null) return;
            _piViewYaw = _type.GetProperty("ViewYaw", Any);
            _miSetYaw = _type.GetMethod("SetYaw", Any, null, new[] { typeof(float) }, null);
            _fiScene = _type.GetField("_scene", Any);
            if (_fiScene != null)
            {
                _fiLookAt = _fiScene.FieldType.GetField("LookAt", Any);
                _fiHeadings = _fiScene.FieldType.GetField("_headings", Any);
            }
            Log.Info("Worldfall view: yaw=" + (_piViewYaw != null) + " setYaw=" + (_miSetYaw != null) + " lookAt=" + (_fiLookAt != null) + " headings=" + (_fiHeadings != null));
        }

        /// <summary>Direction the first-person view looks at (radians; x = cos, y = sin), or NaN.</summary>
        public static float ViewYaw
        {
            get
            {
                object inst = Instance;
                LookupView();
                if (inst == null || _piViewYaw == null) return float.NaN;
                try { return (float)_piViewYaw.GetValue(inst, null); } catch { return float.NaN; }
            }
        }

        public static void SetViewYaw(float radians)
        {
            object inst = Instance;
            LookupView();
            if (inst == null || _miSetYaw == null) return;
            try { _miSetYaw.Invoke(inst, new object[] { radians }); } catch { }
        }

        private static System.Collections.IDictionary SceneDict(FieldInfo f)
        {
            object inst = Instance;
            LookupView();
            if (inst == null || _fiScene == null || f == null) return null;
            try { return f.GetValue(_fiScene.GetValue(inst)) as System.Collections.IDictionary; } catch { return null; }
        }

        /// <summary>Makes Worldfall's 3D view turn this creature towards a point (another player's view direction).</summary>
        public static void SetLookAt(Actor a, Vector2 at)
        {
            var d = SceneDict(_fiLookAt);
            if (d != null && a != null) d[a] = at;
        }

        /// <summary>Diagnostics: the point Worldfall turns this creature towards, if any.</summary>
        public static bool GetLookAt(Actor a, out Vector2 at)
        {
            at = Vector2.zero;
            var d = SceneDict(_fiLookAt);
            if (d == null || a == null || !d.Contains(a)) return false;
            at = (Vector2)d[a];
            return true;
        }

        public static void ClearLookAt(Actor a)
        {
            var d = SceneDict(_fiLookAt);
            if (d != null && a != null && d.Contains(a)) d.Remove(a);
        }

        /// <summary>The way Worldfall currently draws a creature facing in 3D (radians), for diagnostics.</summary>
        public static bool Heading(Actor a, out float angle, out Vector2 drawn)
        {
            angle = float.NaN; drawn = Vector2.zero;
            var d = SceneDict(_fiHeadings);
            if (d == null || a == null || !d.Contains(a)) return false;
            try
            {
                object h = d[a];
                // Only creatures Worldfall drew in the last few frames (others keep an old heading).
                object scene = _fiScene.GetValue(Instance);
                FieldInfo ff = scene.GetType().GetField("_frame", Any);
                int seen = (int)h.GetType().GetField("SeenFrame", Any).GetValue(h);
                if (ff != null && seen < (int)ff.GetValue(scene) - 3) return false;
                angle = (float)h.GetType().GetField("Angle", Any).GetValue(h);
                drawn = (Vector2)h.GetType().GetField("Drawn", Any).GetValue(h);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Turns Worldfall's drawn 3D body to this angle (radians) right away, skipping its smooth turning.</summary>
        public static void SetHeading(Actor a, float angle)
        {
            var d = SceneDict(_fiHeadings);
            if (d == null || a == null || !d.Contains(a)) return;
            try
            {
                object h = d[a];
                Type t = h.GetType();
                t.GetField("Angle", Any).SetValue(h, angle);
                FieldInfo rate = t.GetField("TurnRate", Any);
                if (rate != null) rate.SetValue(h, 0f);
                if (t.IsValueType) d[a] = h;
            }
            catch { }
        }

        // ---------------------------------------------------------------- house interiors

        private static FieldInfo _fiInterior;
        private static PropertyInfo _piInside, _piHouse;

        /// <summary>The building my creature is inside in Worldfall's 3D view (its house interiors), or null.</summary>
        public static Building InsideHouse
        {
            get
            {
                object inst = Instance;
                if (inst == null) return null;
                try
                {
                    if (_fiInterior == null)
                    {
                        _fiInterior = _type.GetField("Interior", Any);
                        Type hi = _fiInterior?.FieldType;
                        _piInside = hi?.GetProperty("Inside", Any);
                        _piHouse = hi?.GetProperty("House", Any);
                    }
                    object room = _fiInterior?.GetValue(inst);
                    if (room == null || _piInside == null || !(bool)_piInside.GetValue(room, null)) return null;
                    return _piHouse?.GetValue(room, null) as Building;
                }
                catch { return null; }
            }
        }

        private static FieldInfo _fiEye, _fiWalkers, _fiWalkerList, _fiWho, _fiPos, _fiHeading, _fiWait, _fiPath;
        private static Type _walkerType;

        private static object Room()
        {
            object inst = Instance;
            if (inst == null || InsideHouse == null) return null;
            try
            {
                object room = _fiInterior.GetValue(inst);
                if (_fiEye == null)
                {
                    Type hi = room.GetType();
                    _fiEye = hi.GetField("Eye", Any);
                    _fiWalkers = hi.GetField("_walkers", Any);
                    _fiWalkerList = _fiWalkers?.FieldType.GetField("_list", Any);
                    _walkerType = _fiWalkers?.FieldType.GetNestedType("Walker", Any);
                    _fiWho = _walkerType?.GetField("Who", Any);
                    _fiPos = _walkerType?.GetField("Pos", Any);
                    _fiHeading = _walkerType?.GetField("Heading", Any);
                    _fiWait = _walkerType?.GetField("Wait", Any);
                    _fiPath = _walkerType?.GetField("Path", Any);
                    Log.Info("Worldfall rooms: eye=" + (_fiEye != null) + " walkers=" + (_fiWalkerList != null) + " walker=" + (_walkerType != null));
                }
                return room;
            }
            catch { return null; }
        }

        /// <summary>Where I stand inside the house room (room coordinates), if I'm in one.</summary>
        public static bool RoomEye(out Vector2 eye)
        {
            eye = Vector2.zero;
            object room = Room();
            if (room == null || _fiEye == null) return false;
            try { eye = (Vector2)_fiEye.GetValue(room); return true; } catch { return false; }
        }

        private static System.Collections.IList Walkers(object room)
        {
            try { return _fiWalkerList?.GetValue(_fiWalkers.GetValue(room)) as System.Collections.IList; } catch { return null; }
        }

        /// <summary>
        /// Shows another player's creature in the room I'm in, standing where they stand in theirs.
        /// Worldfall fills a room with the people inside once, on entering, and walks them itself:
        /// this adds them as one of its room walkers and pins it to their position every frame.
        /// </summary>
        public static void ShowInRoom(Actor who, Vector2 pos, float heading)
        {
            object room = Room();
            var list = room != null ? Walkers(room) : null;
            if (list == null || _walkerType == null || who == null) return;
            try
            {
                object w = null;
                foreach (object x in list) if (_fiWho.GetValue(x) == who) { w = x; break; }
                if (w == null)
                {
                    w = Activator.CreateInstance(_walkerType, true);
                    _fiWho.SetValue(w, who);
                    list.Add(w);
                }
                _fiPos.SetValue(w, pos);
                if (!float.IsNaN(heading)) _fiHeading?.SetValue(w, heading);
                _fiWait?.SetValue(w, 1e6f);                    // never sets off on its own
                (_fiPath?.GetValue(w) as System.Collections.IList)?.Clear();
            }
            catch (Exception e) { Log.Warn("room walker: " + e.Message); }
        }

        public static void RemoveFromRoom(Actor who)
        {
            if (who == null || _fiWho == null) return;
            object room = Room();
            var list = room != null ? Walkers(room) : null;
            if (list == null) return;
            try
            {
                for (int i = list.Count - 1; i >= 0; i--)
                    if (_fiWho.GetValue(list[i]) == who) list.RemoveAt(i);
            }
            catch { }
        }

        /// <summary>Is this player's creature one of the people in my house room?</summary>
        public static bool RoomHas(Actor who)
        {
            object room = Room();
            var list = room != null ? Walkers(room) : null;
            if (list == null || who == null) return false;
            try { foreach (object x in list) if (_fiWho.GetValue(x) == who) return true; } catch { }
            return false;
        }

        /// <summary>Scripted tests: walk forward inside the house room (Worldfall's own room movement).</summary>
        public static bool RoomStep(float forward, float side, float seconds)
        {
            object room = Room();
            if (room == null) return false;
            try
            {
                MethodInfo m = room.GetType().GetMethod("Move", Any);
                float yaw = ViewYaw;
                for (float t = 0f; t < seconds; t += 0.05f) m.Invoke(room, new object[] { forward, side, float.IsNaN(yaw) ? 0f : yaw, 0.05f });
                return true;
            }
            catch (Exception e) { Log.Warn("room step: " + (e.InnerException ?? e).Message); return false; }
        }

        /// <summary>Scripted tests: Worldfall's own test hooks (enter/leave a house, press the ability key).</summary>
        public static bool TestCall(string method, params object[] args)
        {
            object inst = Instance;
            if (inst == null) return false;
            try
            {
                foreach (MethodInfo m in _type.GetMethods(Any))
                    if (m.Name == method && m.GetParameters().Length == args.Length) { m.Invoke(inst, args); return true; }
                Log.Warn("Worldfall has no " + method + "/" + args.Length);
            }
            catch (Exception e) { Log.Warn("Worldfall " + method + ": " + (e.InnerException ?? e).Message); }
            return false;
        }

        public static bool IsEnterable(Building b)
        {
            object inst = Instance;
            if (inst == null || b == null) return false;
            try
            {
                if (_fiInterior == null) { var _ = InsideHouse; }
                object room = _fiInterior?.GetValue(inst);
                MethodInfo m = room?.GetType().GetMethod("IsEnterable", Any);
                return m != null && (bool)m.Invoke(room, new object[] { b });
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- chopping / mining / gathering

        private static FieldInfo _fiWork, _fiWorkTarget, _fiYieldAt;
        private static float _lastYieldAt = -1f;

        /// <summary>
        /// Call once a frame while possessing: true on the frame Worldfall finished chopping a tree,
        /// mining a rock or gathering (it set its yield time and called Building.extractResources).
        /// </summary>
        public static bool PollWorkDone(out Building b)
        {
            b = null;
            object inst = Instance;
            if (inst == null) return false;
            try
            {
                if (_fiWork == null)
                {
                    _fiWork = _type.GetField("Work", Any);
                    _fiWorkTarget = _fiWork?.FieldType.GetField("Target", Any);
                    _fiYieldAt = _fiWork?.FieldType.GetField("_yieldAt", Any);
                    Log.Info("Worldfall work: work=" + (_fiWork != null) + " target=" + (_fiWorkTarget != null) + " yield=" + (_fiYieldAt != null));
                }
                object work = _fiWork?.GetValue(inst);
                if (work == null || _fiYieldAt == null || _fiWorkTarget == null) return false;
                float at = (float)_fiYieldAt.GetValue(work);
                if (_lastYieldAt < 0f) { _lastYieldAt = at; return false; }
                if (at == _lastYieldAt) return false;
                _lastYieldAt = at;
                b = _fiWorkTarget.GetValue(work) as Building;
                return b != null;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- species abilities (X)

        private static bool _abLooked;
        private static FieldInfo _fiLastAbility, _fiSinceAbility, _fiAimAt, _fiById, _fiAbEffect;
        private static FieldInfo _fiUseSelf, _fiUseAt, _fiUseDir, _fiUseToast;
        private static Type _useType;
        private static float _lastSince = float.MaxValue;

        private static void LookupAbilities()
        {
            if (_abLooked || _type == null) return;
            _abLooked = true;
            _fiLastAbility = _type.GetField("LastAbility", Any);
            _fiSinceAbility = _type.GetField("SinceAbility", Any);
            _fiAimAt = _type.GetField("AbilityAimAt", Any);
            Type abs = _type.Assembly.GetType("FirstPerson.Abilities", false);
            Type ab = _type.Assembly.GetType("FirstPerson.Ability", false);
            _useType = _type.Assembly.GetType("FirstPerson.AbilityUse", false);
            _fiById = abs?.GetField("ById", Any);
            _fiAbEffect = ab?.GetField("Effect", Any);
            _fiUseSelf = _useType?.GetField("Self", Any);
            _fiUseAt = _useType?.GetField("At", Any);
            _fiUseDir = _useType?.GetField("Dir", Any);
            _fiUseToast = _useType?.GetField("Toast", Any);
            Log.Info("Worldfall abilities: last=" + (_fiLastAbility != null) + " since=" + (_fiSinceAbility != null) +
                     " byId=" + (_fiById != null) + " effect=" + (_fiAbEffect != null) + " use=" + (_useType != null));
        }

        /// <summary>
        /// Call once a frame while possessing: true on the frame my creature used its ability (X),
        /// with the ability's id in Abilities.ById and where it was aimed.
        /// </summary>
        public static bool PollMyAbility(out string id, out Vector2 at)
        {
            id = null; at = Vector2.zero;
            object inst = Instance;
            LookupAbilities();
            if (inst == null || _fiSinceAbility == null || _fiLastAbility == null || _fiById == null) return false;
            try
            {
                float since = (float)_fiSinceAbility.GetValue(inst);
                bool used = since < _lastSince - 0.05f;   // the timer restarts when an ability goes off
                _lastSince = since;
                if (!used) return false;
                object ab = _fiLastAbility.GetValue(inst);
                if (ab == null) return false;
                foreach (System.Collections.DictionaryEntry e in (System.Collections.IDictionary)_fiById.GetValue(null))
                    if (e.Value == ab) { id = (string)e.Key; break; }
                if (id == null) return false;
                if (_fiAimAt != null) at = (Vector2)_fiAimAt.GetValue(inst);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Plays another player's ability on their creature here, aimed where they aimed it.</summary>
        public static bool UseAbility(Actor self, string id, Vector2 at)
        {
            Lookup();
            LookupAbilities();
            if (self == null || id == null || _fiById == null || _fiAbEffect == null || _useType == null) return false;
            try
            {
                var byId = (System.Collections.IDictionary)_fiById.GetValue(null);
                object ab = byId.Contains(id) ? byId[id] : null;
                if (ab == null || !(_fiAbEffect.GetValue(ab) is Delegate effect)) return false;
                object use = Activator.CreateInstance(_useType);
                _fiUseSelf?.SetValue(use, self);
                _fiUseAt?.SetValue(use, at);
                Vector2 dir = at - self.current_position;
                _fiUseDir?.SetValue(use, dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right);
                _fiUseToast?.SetValue(use, (Action<string>)(_ => { }));
                return (bool)effect.DynamicInvoke(use);
            }
            catch (Exception e) { Log.Warn("ability " + id + ": " + (e.InnerException ?? e).Message); return false; }
        }

        /// <summary>
        /// Projects a point above a unit's head into screen pixels (top-left origin) of Worldfall's
        /// current 3D frame. depth = distance in tiles.
        /// </summary>
        /// <summary>Raw numbers of the last ProjectHead call (diagnostics).</summary>
        public static string LastProjection = "";

        private static FieldInfo _fiRenderer;
        private static PropertyInfo _piRendererProj;

        /// <param name="god">Worldfall's god view is showing: its frame comes from the renderer, not the first-person "Shown".</param>
        public static bool ProjectHead(Actor a, float lift, out Vector2 screen, out float depth, bool god = false)
        {
            LastProjection = "";
            screen = Vector2.zero;
            depth = 0f;
            object inst = Instance;
            if (inst == null || _miStarHead == null || _miProject == null || _piShown == null || a == null) return false;
            try
            {
                var headArgs = new object[] { a, null };
                if (!(bool)_miStarHead.Invoke(inst, headArgs)) return false;
                Vector3 head = (Vector3)headArgs[1];
                object shown;
                if (god)
                {
                    if (_fiRenderer == null) { _fiRenderer = _type.GetField("Renderer", Any); _piRendererProj = _fiRenderer?.FieldType.GetProperty("Projection", Any); }
                    if (_piRendererProj == null) return false;
                    shown = _piRendererProj.GetValue(_fiRenderer.GetValue(inst), null);
                }
                else shown = _piShown.GetValue(inst, null);
                int w = (int)_fiWidth.GetValue(shown), h = (int)_fiHeight.GetValue(shown);
                if (w <= 0 || h <= 0) return false;
                var args = new object[] { head.x, head.y, head.z + lift, 0f, 0f, 0f };
                if (!(bool)_miProject.Invoke(shown, args)) return false;
                float col = (float)args[3], row = (float)args[4];
                depth = (float)args[5];
                LastProjection = "col " + col.ToString("0") + " row " + row.ToString("0") + " of " + w + "x" + h + " head z " + head.z.ToString("0.00");
                if (col < -w * 0.05f || col > w * 1.05f || row < -h * 0.05f || row > h * 1.05f) return false;
                screen = new Vector2(col / w * Screen.width, row / h * Screen.height);
                return true;
            }
            catch { return false; }
        }
    }
}
