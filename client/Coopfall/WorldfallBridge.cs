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
                        _miProject = proj.GetMethod("Project", Any);
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

        private static bool GetBool(PropertyInfo pi)
        {
            object inst = Instance;
            if (inst == null || pi == null) return false;
            try { return (bool)pi.GetValue(inst, null); } catch { return false; }
        }

        /// <summary>True while Worldfall's 3D first-person picture covers the screen.</summary>
        public static bool FirstPerson { get { return GetBool(_piIsFp); } }

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

        /// <summary>
        /// Projects a point above a unit's head into screen pixels (top-left origin) of Worldfall's
        /// current 3D frame. depth = distance in tiles.
        /// </summary>
        /// <summary>Raw numbers of the last ProjectHead call (diagnostics).</summary>
        public static string LastProjection = "";

        public static bool ProjectHead(Actor a, float lift, out Vector2 screen, out float depth)
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
                object shown = _piShown.GetValue(inst, null);
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
