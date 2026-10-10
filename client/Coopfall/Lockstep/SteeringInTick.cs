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
    /// Doorways come from Worldfall's 3D models, which load in the background (so a PC may not have
    /// a model yet): the host works each nearby house's door out the way the renderer does and
    /// sends it (ShareDoor, relayed); every PC uses it from the same tick on.
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
        private static MethodInfo _special, _family, _model, _mirrored, _doorOf, _setDoor, _info, _infoFloat;
        private static FieldInfo _mMin, _mMax;
        /// <summary>Doorways shared in ticks (building ID: the spot just outside the door), and the ones the host has sent.</summary>
        private static readonly SortedDictionary<long, Vector2> _doors = new SortedDictionary<long, Vector2>();
        private static readonly HashSet<long> _doorAsked = new HashSet<long>();
        private static int _doorFrame;
        public static int Doors => _doors.Count;
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
            Type models = wf.GetType("FirstPerson.Models", false), mesh = wf.GetType("FirstPerson.Core.MeshModel", false);
            _special = models == null ? null : AccessTools.Method(models, "SpecialFamily", new[] { typeof(string) });
            _family = models == null ? null : AccessTools.Method(models, "BuildingFamily", new[] { typeof(Building) });
            _model = models == null ? null : AccessTools.Method(models, "Get", new[] { typeof(string) });
            _mirrored = models == null ? null : AccessTools.Method(models, "Mirrored", new[] { typeof(Building) });
            _doorOf = AccessTools.Method(st, "DoorOf");
            _setDoor = AccessTools.Method(st, "SetDoor");
            _info = mesh == null ? null : AccessTools.Method(mesh, "Info", new[] { typeof(string) });
            _infoFloat = mesh == null ? null : AccessTools.Method(mesh, "InfoFloat", new[] { typeof(string), typeof(float) });
            _mMin = mesh == null ? null : AccessTools.Field(mesh, "Min");
            _mMax = mesh == null ? null : AccessTools.Field(mesh, "Max");
            if (_special == null || _family == null || _model == null || _mirrored == null || _doorOf == null || _setDoor == null || _info == null || _infoFloat == null || _mMin == null || _mMax == null
                || !WorldCalls.Register(h, AccessTools.Method(typeof(SteeringInTick), nameof(ShareDoor))))
            {
                Log.Warn("lockstep: Worldfall's building models or doors not found: creatures don't use doorways in lockstep");
                _setDoor = null;
            }
            Log.Info("lockstep: Worldfall's steering runs in ticks around every controlled creature" + (_setDoor != null ? " (with doorways)" : ""));
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
            _doors.Clear();
            _doorAsked.Clear();
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
            if (look == "House" && _setDoor != null && _doors.TryGetValue(b.getID(), out Vector2 door))
            {
                _two[0] = b; _two[1] = door;
                _setDoor.Invoke(null, _two);
            }
        }

        /// <summary>Relayed (from the host): where a house's doorway is (in a tick, on every PC).</summary>
        public static void ShareDoor(Building b, Vector2 at)
        {
            if (!LockstepClock.InTick || b == null) return;
            _doors[b.getID()] = at;
        }

        /// <summary>
        /// Host, between ticks: the doorways of houses near controlled creatures that aren't shared
        /// yet, worked out as Worldfall's renderer does (model family, sprite width, mirroring).
        /// </summary>
        public static void ShareDoorsFrame()
        {
            if (_setDoor == null || !LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick || LockstepControl.Count == 0) return;
            if (++_doorFrame % 15 != 0) return;
            int sent = 0;
            MapBox w = World.world;
            foreach (long id in LockstepControl.ControlledIds())
            {
                Actor body = w.units.get(id);
                if (body == null || !body.isAlive()) continue;
                int cx = Mathf.FloorToInt(body.current_position.x), cy = Mathf.FloorToInt(body.current_position.y);
                for (int y = cy - 40; y < cy + 40 && sent < 20; y++)
                    for (int x = cx - 40; x < cx + 40 && sent < 20; x++)
                    {
                        Building b = w.GetTile(x, y)?.building;
                        if (b == null || b.current_tile == null || b.current_tile.x != x || b.current_tile.y != y || _doorAsked.Contains(b.getID())) continue;
                        if (!b.isAlive() || (bool)_isRuin.Invoke(b, null) || b.isUnderConstruction()) continue;
                        int got = DoorFor(b, out Vector2 door);
                        if (got == 0) continue;            // its model isn't loaded yet: try again later
                        _doorAsked.Add(b.getID());
                        if (got < 0) continue;            // no doorway (not a house, or a model without one)
                        ShareDoor(b, door);                // caught by WorldCalls and sent
                        sent++;
                    }
            }
        }

        private static readonly object[] _doorArgs = new object[2];

        /// <summary>1: the door; -1: none; 0: not known yet (model loading).</summary>
        private static int DoorFor(Building b, out Vector2 door)
        {
            door = Vector2.zero;
            BuildingAsset asset = _bAsset(b);
            if (asset == null) return -1;
            _one[0] = asset;
            if (_classify.Invoke(null, _one).ToString() != "House") return -1;
            try
            {
                string family = _special.Invoke(null, new object[] { asset.id ?? "" }) as string ?? _family.Invoke(null, new object[] { b }) as string;
                if (family == null) return -1;
                object model = _model.Invoke(null, new object[] { family });
                if (model == null) return 0;
                string kind = _info.Invoke(model, new object[] { "kind" }) as string;
                if (kind != "house" && kind != "hall" && kind != "tent") return -1;
                _doorArgs[0] = model; _doorArgs[1] = null;
                if (!(bool)_doorOf.Invoke(null, _doorArgs)) return -1;
                Vector2 at = (Vector2)_doorArgs[1];
                Sprite sprite = b.checkSpriteToRender();
                if (sprite == null) return 0;
                float ppu = sprite.pixelsPerUnit > 0f ? sprite.pixelsPerUnit : 100f;
                float width = sprite.rect.width / ppu * Mathf.Abs(asset.scale_base.x);
                object min = _mMin.GetValue(model), max = _mMax.GetValue(model);
                FieldInfo vx = min.GetType().GetField("X"), vy = min.GetType().GetField("Y");
                float minX = (float)vx.GetValue(min), maxX = (float)vx.GetValue(max), minY = (float)vy.GetValue(min);
                float scale = width / Mathf.Max(0.5f, (float)_infoFloat.Invoke(model, new object[] { "width", maxX - minX }));
                bool mirrored = (float)_infoFloat.Invoke(model, new object[] { "footing", 0f }) > 0f && (bool)_mirrored.Invoke(null, new object[] { b });
                BuildingFundament f = asset.fundament;
                Vector3 p = b.current_tile.posV3;
                float y0 = p.y + (f == null ? 0f : (f.top - f.bottom) * 0.5f);
                door = new Vector2(p.x + (mirrored ? -at.x : at.x) * scale, y0 + Mathf.Min(at.y, minY) * scale - 0.3f);
                return 1;
            }
            catch (Exception e)
            {
                if (_doorFailed++ == 0) Log.Warn("lockstep: a house's doorway: " + (e.InnerException ?? e).Message);
                return -1;
            }
        }

        private static int _doorFailed;
        private static readonly MethodInfo _isRuin = AccessTools.Method(typeof(Building), "isRuin");
    }
}
