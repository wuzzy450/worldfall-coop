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
    /// Worldfall's crowd steering (creatures walk around buildings and each other near a
    /// first-person player) runs from its renderer around this PC's camera, with obstacles built
    /// while drawing. In lockstep it runs here instead: in every tick, around every controlled
    /// creature (sorted by ID), with obstacles made from the world's buildings (their asset
    /// footprint, the same on every PC), on the tick clock. The renderer's own Begin/Add/SetDoor
    /// calls are skipped while lockstep runs (it still reads the result: Hidden, Solid, InsideAt).
    /// Doorways (taken from drawn 3D models) are not used: creatures go into houses as in vanilla.
    /// </summary>
    public static class SteeringInTick
    {
        private const float Range = 36f, BodyRadius = 0.22f;
        private static Type _collider;
        private static MethodInfo _begin, _add, _steer, _reset, _walks, _classify, _isSolid;
        private static FieldInfo _x, _y, _radius, _halfW, _halfD, _box, _states, _hiddenFrame;
        private static readonly List<FieldInfo> _lists = new List<FieldInfo>();
        private static FieldInfo _cleaned;
        private static bool _ours;
        private static readonly AccessTools.FieldRef<Building, BuildingAsset> _bAsset = AccessTools.FieldRefAccess<Building, BuildingAsset>("asset");
        public static bool Ready => _steer != null;
        /// <summary>Creatures moved by steering since the epoch loaded (the same on every PC).</summary>
        public static long Steered;

        public static void Install(Harmony h, Assembly wf)
        {
            if (PlayerScope.Off("steering")) { Log.Info("lockstep: Worldfall's steering off (-coopfall-ls-off steering)"); return; }
            Type st = wf.GetType("FirstPerson.Steering", false), scene = wf.GetType("FirstPerson.SceneCollector", false), style = wf.GetType("FirstPerson.AssetStyle", false);
            _collider = wf.GetType("FirstPerson.Collider", false);
            if (st == null || scene == null || style == null || _collider == null) { Log.Warn("lockstep: Worldfall's steering not found: creatures walk through buildings near players in lockstep"); return; }
            _begin = AccessTools.Method(st, "Begin");
            _add = AccessTools.Method(st, "Add");
            _steer = AccessTools.Method(st, "Steer");
            _reset = AccessTools.Method(st, "Reset");
            _walks = AccessTools.Method(scene, "Walks");
            _classify = AccessTools.Method(style, "Classify");
            _isSolid = AccessTools.Method(style, "IsSolid");
            _x = AccessTools.Field(_collider, "X"); _y = AccessTools.Field(_collider, "Y"); _radius = AccessTools.Field(_collider, "Radius");
            _halfW = AccessTools.Field(_collider, "HalfW"); _halfD = AccessTools.Field(_collider, "HalfD"); _box = AccessTools.Field(_collider, "Box");
            _states = AccessTools.Field(st, "States");
            Type state = st.GetNestedType("State", BindingFlags.NonPublic);
            _hiddenFrame = state == null ? null : AccessTools.Field(state, "HiddenFrame");
            _cleaned = AccessTools.Field(st, "_cleaned");
            foreach (string f in new[] { "_crowd", "_crowdNext", "_pending", "_live", "Stale" })
            {
                FieldInfo fi = AccessTools.Field(st, f);
                if (fi != null) _lists.Add(fi);
            }
            if (_begin == null || _add == null || _steer == null || _reset == null || _walks == null || _classify == null || _isSolid == null || _x == null || _box == null || _states == null || _hiddenFrame == null)
            {
                Log.Warn("lockstep: a Worldfall steering member is missing (renamed?): steering stays off in lockstep");
                _steer = null;
                return;
            }
            // the renderer's frame calls would replace the tick's obstacles and grid
            var skip = new HarmonyMethod(typeof(SteeringInTick), nameof(OnlyOurs));
            foreach (string m in new[] { "Begin", "Add", "SetDoor", "SetRadius", "Reset" })
                h.Patch(AccessTools.Method(st, m), prefix: skip);
            foreach (string m in new[] { "Hidden", "HiddenLastFrame" })
                h.Patch(AccessTools.Method(st, m), prefix: new HarmonyMethod(typeof(SteeringInTick), nameof(HiddenPrefix)));
            WorldfallInTick.SwapClockOf(h, st);
            // which way a creature slides along a wall came from its object hash (differs per process)
            h.Patch(AccessTools.Method(st, "SteerOne"), transpiler: new HarmonyMethod(typeof(SteeringInTick), nameof(HashTranspiler)));
            Log.Info("lockstep: Worldfall's steering runs in ticks around every controlled creature");
        }

        private static bool OnlyOurs() => !LockstepClock.Active || _ours;

        /// <summary>Drawing asks whether a creature is inside a building now: last tick's answer.</summary>
        private static bool HiddenPrefix(Actor a, ref bool __result)
        {
            if (!LockstepClock.Active) return true;
            __result = false;
            if (a == null || !(_states.GetValue(null) is IDictionary d) || !d.Contains(a)) return false;
            __result = (int)_hiddenFrame.GetValue(d[a]) >= (int)LockstepClock.Tick - 1;
            return false;
        }

        public static int StableHash(object o) => o is Actor a ? (int)a.getID() : 0;

        private static IEnumerable<CodeInstruction> HashTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo hash = AccessTools.Method(typeof(object), nameof(GetHashCode));
            var list = new List<CodeInstruction>(instructions);
            foreach (CodeInstruction c in list)
                if ((c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && c.operand is MethodInfo m && m.Name == "GetHashCode" && m.GetParameters().Length == 0 && (m == hash || m.DeclaringType.IsAssignableFrom(typeof(Actor))))
                {
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(SteeringInTick), nameof(StableHash));
                }
            return list;
        }

        /// <summary>At every epoch load: no steering state carried over.</summary>
        public static void Reset()
        {
            Steered = 0;
            if (_steer == null) return;
            _ours = true;
            try
            {
                _reset.Invoke(null, null);
                foreach (FieldInfo f in _lists) (f.GetValue(null) as IList)?.Clear();
                _cleaned?.SetValue(null, 0);
            }
            catch (Exception e) { Log.Error("lockstep: steering reset: " + (e.InnerException ?? e).Message); }
            finally { _ours = false; }
        }

        private static readonly HashSet<Building> _seen = new HashSet<Building>();
        private static readonly HashSet<Actor> _steered = new HashSet<Actor>();
        private static readonly List<long> _ids = new List<long>();
        private static readonly List<Building> _near = new List<Building>();
        private static readonly object[] _one = new object[1], _two = new object[2], _four = new object[4];

        /// <summary>Called in every tick after the simulation step.</summary>
        public static void Run()
        {
            if (_steer == null || LockstepControl.Count == 0) return;
            _ids.Clear();
            _ids.AddRange(LockstepControl.ControlledIds());
            _ids.Sort();
            _steered.Clear();
            _ours = true;
            try
            {
                foreach (long id in _ids)
                {
                    Actor body = World.world.units.get(id);
                    if (body == null || !body.isAlive()) continue;
                    Around(body);
                }
            }
            catch (Exception e) { Log.Error("lockstep: steering: " + (e.InnerException ?? e).Message); }
            finally { _ours = false; }
        }

        private static void Around(Actor body)
        {
            Vector2 c = body.current_position;
            int cx = Mathf.FloorToInt(c.x), cy = Mathf.FloorToInt(c.y);
            MapBox w = World.world;
            // the world's buildings within reach, in tile order
            _seen.Clear();
            _near.Clear();
            for (int y = cy - 42; y < cy + 42; y++)
                for (int x = cx - 42; x < cx + 42; x++)
                {
                    WorldTile t = w.GetTile(x, y);
                    Building b = t?.building;
                    if (b != null && _seen.Add(b)) _near.Add(b);
                }
            foreach (Building b in _near) AddObstacle(b);
            _four[0] = c.x; _four[1] = c.y; _four[2] = body; _four[3] = BodyRadius;
            _begin.Invoke(null, _four);
            List<Actor> units = w.units.getSimpleList();
            for (int i = 0; i < units.Count; i++)
            {
                Actor a = units[i];
                if (a == null || !a.isAlive() || a.isInsideSomething() || LockstepControl.IsControlled(a.getID())) continue;
                Vector2 p = a.current_position;
                float dx = p.x - c.x, dy = p.y - c.y;
                if (dx * dx + dy * dy >= Range * Range || _steered.Contains(a)) continue;
                _one[0] = a;
                if (!(bool)_walks.Invoke(null, _one)) continue;
                _steered.Add(a);
                _two[0] = a; _two[1] = LockstepClock.DefaultStep;
                if ((bool)_steer.Invoke(null, _two)) Steered++;
            }
        }

        private static void AddObstacle(Building b)
        {
            BuildingAsset asset = _bAsset(b);
            if (!b.isAlive() || asset == null || b.current_tile == null) return;
            _one[0] = b;
            if (!(bool)_isSolid.Invoke(null, _one)) return;
            _one[0] = asset;
            string look = _classify.Invoke(null, _one).ToString();
            if (look == "Plant" || look == "Landform") return;
            BuildingFundament f = asset.fundament;
            float wide = f == null ? 1f : f.left + f.right + 1, deep = f == null ? 1f : Mathf.Max(1f, f.top + f.bottom + 1);
            Vector3 at = b.current_tile.posV3;
            object col = Activator.CreateInstance(_collider);
            if (look == "House")
            {
                _box.SetValue(col, true);
                _x.SetValue(col, at.x);
                _y.SetValue(col, at.y + (f == null ? 0f : (f.top - f.bottom) * 0.5f));
                _halfW.SetValue(col, wide * 0.47f);
                _halfD.SetValue(col, deep * 0.47f);
            }
            else
            {
                _x.SetValue(col, at.x);
                _y.SetValue(col, at.y);
                _radius.SetValue(col, look == "Tree" ? 0.25f : Mathf.Clamp(wide * 0.34f, 0.1f, 1.5f));
            }
            _two[0] = col; _two[1] = b;
            _add.Invoke(null, _two);
        }
    }
}
