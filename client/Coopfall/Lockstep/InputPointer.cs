using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Some powers read the player's mouse instead of the tile they were used on (boulders, the
    /// finger flick), and "the creature near the cursor" is searched among creatures on screen.
    /// Inside a tick the pointer is the tile of the input being applied, the search covers every
    /// creature. A boulder is aimed on the thrower's PC (press, drag, release, as in the game) and
    /// travels as one input with both points; in the tick it is thrown from the release point the
    /// way the drag pointed, on every PC. With the "many" key held it falls at once (random throw).
    /// </summary>
    internal static class InputPointer
    {
        /// <summary>Where the input being applied was used (world position).</summary>
        public static Vector2 Pointer = new Vector2(-1f, -1f);
        private static MethodInfo _releaseMany;

        public static void Install(Harmony h)
        {
            Patch(h, AccessTools.Method(typeof(MapBox), "getMousePos"), nameof(MousePosPrefix));
            Patch(h, AccessTools.Method(typeof(MapBox), "getActorNearCursor"), nameof(NearCursorPrefix));
            Patch(h, AccessTools.Method(typeof(PowerLibrary), "prepareBoulder"), nameof(BoulderPrefix));
            Patch(h, AccessTools.Method(typeof(Boulder), "checkRelease"), nameof(CheckReleasePrefix));
            _releaseMany = AccessTools.Method(typeof(Boulder), "releaseManyBoulders");
            if (_releaseMany == null) Log.Error("lockstep: Boulder.releaseManyBoulders not found: boulders won't work in lockstep");
        }

        private static void Patch(Harmony h, MethodInfo m, string prefix)
        {
            if (m == null) { Log.Error("lockstep: " + prefix.Replace("Prefix", "") + " target not found (game changed?): a power may follow the local mouse"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(InputPointer), prefix));
        }

        private static bool MousePosPrefix(ref Vector2 __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = Pointer;
            return false;
        }

        private static bool NearCursorPrefix(ref Actor __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = null;
            float best = 3f;
            foreach (Actor a in World.world.units)
            {
                if (a == null || !a.isAlive() || !a.asset.can_be_inspected || a.isInsideSomething()) continue;
                float d = Toolbox.DistVec2Float(a.current_position, Pointer);
                if (d <= best && (__result == null || d < best || a.getID() < __result.getID())) { best = d; __result = a; }
            }
            return false;
        }

        private static bool BoulderPrefix(ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            if (_aimed)
            {
                // the drag's start point, then the boulder at the release point: an aimed throw
                _chargeStart?.SetValue(null, _start);
                EffectsLibrary.spawnAt("fx_boulder", Pointer, 1f);
                _chargeStart?.SetValue(null, Pointer);
            }
            else _releaseMany?.Invoke(null, new object[] { Pointer });
            __result = true;
            return false;
        }

        private static readonly FieldInfo _chargeStart = AccessTools.Field(typeof(Boulder), "_initial_charge_position");
        private static bool _aimed;
        private static Vector2 _start;
        private const char Sep = '\u0001';

        // ------------------------------------------------------------------ aiming on this PC

        private static bool _charging;
        private static Vector2 _pressAt;
        private static string _brush;

        /// <summary>
        /// PowerSync, a boulder click outside a tick: the drag starts here (nothing is sent yet).
        /// False: not a boulder (or the "many" key is held: thrown at once, as before).
        /// </summary>
        public static bool HoldBoulder(GodPower p)
        {
            if (p == null || p.id != "bowling_ball") return false;
            bool many = false;
            try { many = HotkeyLibrary.many_mod.isHolding(); } catch { }
            if (many) return false;
            if (!_charging)
            {
                _charging = true;
                _pressAt = World.world.getMousePos();
                _brush = Config.current_brush;
            }
            return true;
        }

        /// <summary>Every frame: the mouse let go of a boulder being aimed: one input with both points.</summary>
        public static void Frame(LockstepSession ls)
        {
            if (!_charging) return;
            if (Input.GetMouseButton(0)) return;
            _charging = false;
            Vector2 at = World.world.getMousePos();
            WorldTile tile = World.world.GetTile(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y));
            if (tile == null || ls == null || !ls.Active) return;
            string extra = Sep + F(_pressAt.x) + "," + F(_pressAt.y) + "," + F(at.x) + "," + F(at.y);
            ls.SubmitPower("bowling_ball", tile, (_brush ?? "") + extra);
        }

        private static string F(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>LockstepInput, before a power runs: where it was used, and an aimed boulder's start.</summary>
        public static string Prepare(string brush, WorldTile tile)
        {
            Pointer = new Vector2(tile.x + 0.5f, tile.y + 0.5f);
            _aimed = false;
            if (brush == null) return null;
            int s = brush.IndexOf(Sep);
            if (s < 0) return brush;
            string[] q = brush.Substring(s + 1).Split(',');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (q.Length == 4 && float.TryParse(q[0], System.Globalization.NumberStyles.Float, ci, out float sx) && float.TryParse(q[1], System.Globalization.NumberStyles.Float, ci, out float sy)
                && float.TryParse(q[2], System.Globalization.NumberStyles.Float, ci, out float rx) && float.TryParse(q[3], System.Globalization.NumberStyles.Float, ci, out float ry))
            {
                _aimed = true;
                _start = new Vector2(sx, sy);
                Pointer = new Vector2(rx, ry);
            }
            string b = brush.Substring(0, s);
            return b.Length > 0 ? b : null;
        }

        /// <summary>The frame-by-frame release of a charged boulder: not while lockstep runs.</summary>
        private static bool CheckReleasePrefix() => !LockstepClock.Active;
    }
}
