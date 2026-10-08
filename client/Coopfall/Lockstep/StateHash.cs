using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>One creature's state at one tick, for pinpointing a desync.</summary>
    public struct UnitRec
    {
        public long id;
        public int px, py;       // float bits of current_position
        public int hp;
        public int tile;
        public int asset;
        public int timer;        // float bits of timer_action
        public ulong cool;       // decision cooldowns
        public int task;         // AI job + task + step
        public int path;         // current path, target tile, moving

        public ulong Hash()
        {
            ulong h = StateHash.Mix((ulong)id);
            h = StateHash.Mix(h ^ (uint)px); h = StateHash.Mix(h ^ (uint)py);
            h = StateHash.Mix(h ^ (uint)hp); h = StateHash.Mix(h ^ (uint)tile);
            h = StateHash.Mix(h ^ (uint)asset); h = StateHash.Mix(h ^ (uint)timer); h = StateHash.Mix(h ^ cool); h = StateHash.Mix(h ^ (uint)task); h = StateHash.Mix(h ^ (uint)path);
            return h;
        }

        public string Diff(UnitRec o)
        {
            var s = new List<string>();
            if (px != o.px || py != o.py) s.Add("position " + Pos() + " vs " + o.Pos());
            if (hp != o.hp) s.Add("health " + hp + " vs " + o.hp);
            if (tile != o.tile) s.Add("tile " + tile + " vs " + o.tile);
            if (asset != o.asset) s.Add("kind changed");
            if (timer != o.timer) s.Add("action timer " + BitConverter.Int32BitsToSingle(timer) + " vs " + BitConverter.Int32BitsToSingle(o.timer));
            if (cool != o.cool) s.Add("decision cooldowns");
            if (task != o.task) s.Add("AI task");
            if (path != o.path) s.Add("path/target");
            return string.Join(", ", s);
        }

        private string Pos() => BitConverter.Int32BitsToSingle(px).ToString("R") + "," + BitConverter.Int32BitsToSingle(py).ToString("R");
    }

    /// <summary>World checksum after one tick, split by part so a mismatch says where to look.</summary>
    public class TickHash
    {
        public long tick;
        public ulong units, buildings, meta, rng, tiles;
        public int unitCount, buildingCount;
        /// <summary>Per-creature detail (only when asked for).</summary>
        public Dictionary<long, UnitRec> detail;

        public string FirstDiff(TickHash o)
        {
            if (unitCount != o.unitCount) return "creature count " + unitCount + " vs " + o.unitCount;
            if (units != o.units) return "creatures";
            if (buildingCount != o.buildingCount) return "building count " + buildingCount + " vs " + o.buildingCount;
            if (buildings != o.buildings) return "buildings";
            if (tiles != o.tiles) return "tiles";
            if (meta != o.meta) return "world time / kingdoms / cities";
            if (rng != o.rng) return "random number state";
            return null;
        }

        public string Line() => tick + "\t" + units.ToString("x16") + "\t" + unitCount + "\t" + buildings.ToString("x16") + "\t" + buildingCount + "\t" + tiles.ToString("x16") + "\t" + meta.ToString("x16") + "\t" + rng.ToString("x16");
        public const string Header = "tick\tunits\tunit_count\tbuildings\tbuilding_count\ttiles\tmeta\trng";
    }

    /// <summary>
    /// Checksums the simulated world. Object sets are combined order-independently (sums of
    /// per-object hashes), so the result compares state, not the order objects are stored in.
    /// </summary>
    public static class StateHash
    {
        /// <summary>Tiles are the slowest part: hash them every N ticks (and at tick 0).</summary>
        public static int TileEvery = 25;

        private static readonly AccessTools.FieldRef<Actor, float> _timerAction = AccessTools.FieldRefAccess<Actor, float>("timer_action");
        private static readonly AccessTools.FieldRef<Actor, double[]> _cooldowns = AccessTools.FieldRefAccess<Actor, double[]>("_decision_cooldowns");
        private static readonly AccessTools.FieldRef<Actor, AiSystemActor> _ai = AccessTools.FieldRefAccess<Actor, AiSystemActor>("ai");
        private static readonly AccessTools.FieldRef<Actor, WorldTile> _tileTarget = AccessTools.FieldRefAccess<Actor, WorldTile>("tile_target");
        private static readonly System.Reflection.FieldInfo _aiTask = AccessTools.Field(typeof(AiSystemActor), "task");
        private static readonly System.Reflection.FieldInfo _aiTaskIndex = AccessTools.Field(typeof(AiSystemActor), "task_index");
        private static readonly AccessTools.FieldRef<Building, BuildingAsset> _bAsset = AccessTools.FieldRefAccess<Building, BuildingAsset>("asset");
        private static readonly AccessTools.FieldRef<MapBox, MapStats> _mapStats = AccessTools.FieldRefAccess<MapBox, MapStats>("map_stats");
        private static readonly AccessTools.FieldRef<MapBox, WorldTile[]> _tiles = AccessTools.FieldRefAccess<MapBox, WorldTile[]>("tiles_list");
        private static readonly System.Reflection.FieldInfo _randField = AccessTools.Field(typeof(Randy), "rand");

        public static ulong Mix(ulong x)
        {
            x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL; x ^= x >> 33;
            return x;
        }

        private static int Str(string s)
        {
            if (s == null) return 0;
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return (int)h;
        }

        public static UnitRec Rec(Actor a)
        {
            return new UnitRec
            {
                id = a.getID(),
                px = BitConverter.SingleToInt32Bits(a.current_position.x),
                py = BitConverter.SingleToInt32Bits(a.current_position.y),
                hp = a.getHealth(),
                tile = a.current_tile != null ? a.current_tile.tile_id : -1,
                asset = Str(a.asset?.id),
                timer = BitConverter.SingleToInt32Bits(_timerAction(a)),
                cool = Cooldowns(a),
                task = Task(a),
                path = Path(a),
            };
        }

        private static int Task(Actor a)
        {
            AiSystemActor ai = _ai(a);
            if (ai == null) return 0;
            int h = Str(ai.job?.id);
            h = h * 31 + Str((_aiTask?.GetValue(ai) as Asset)?.id);
            h = h * 31 + (int)(_aiTaskIndex?.GetValue(ai) ?? 0);
            return h;
        }

        private static int Path(Actor a)
        {
            int h = a.current_path_index;
            foreach (WorldTile t in a.current_path) h = h * 31 + t.tile_id;
            WorldTile target = _tileTarget(a);
            h = h * 31 + (target != null ? target.tile_id : -1);
            return h * 2 + (a.is_moving ? 1 : 0);
        }

        private static ulong Cooldowns(Actor a)
        {
            double[] c = _cooldowns(a);
            if (c == null) return 0;
            ulong h = 0;
            for (int i = 0; i < c.Length; i++) h = Mix(h ^ (ulong)BitConverter.DoubleToInt64Bits(c[i]));
            return h;
        }

        public static TickHash Compute(long tick, bool detail)
        {
            MapBox w = World.world;
            var t = new TickHash { tick = tick };
            if (detail) t.detail = new Dictionary<long, UnitRec>();

            foreach (Actor a in w.units)
            {
                if (a == null) continue;
                UnitRec r = Rec(a);
                t.units += r.Hash();
                t.unitCount++;
                if (detail) t.detail[r.id] = r;
            }

            foreach (Building b in w.buildings)
            {
                if (b == null) continue;
                ulong h = Mix((ulong)b.getID());
                h = Mix(h ^ (uint)b.getHealth());
                h = Mix(h ^ (uint)(b.current_tile != null ? b.current_tile.tile_id : -1));
                h = Mix(h ^ (uint)Str(_bAsset(b)?.id));
                t.buildings += h;
                t.buildingCount++;
            }

            ulong m = Mix((ulong)BitConverter.DoubleToInt64Bits(_mapStats(w).world_time));
            m = Mix(m ^ (ulong)w.kingdoms.Count);
            m = Mix(m ^ (ulong)w.cities.Count);
            t.meta = m;

            // Randy's generator state after the tick: catches a different number of dice rolls
            // even when the world still looks the same.
            if (_randField != null)
            {
                var r = (Unity.Mathematics.Random)_randField.GetValue(null);
                t.rng = Mix(r.state);
            }

            if (TileEvery > 0 && tick % TileEvery == 0)
            {
                WorldTile[] tiles = _tiles(w);
                bool[] fires = w.tile_manager.fires;
                ulong th = 0;
                for (int i = 0; i < tiles.Length; i++)
                {
                    WorldTile tile = tiles[i];
                    if (tile == null) continue;
                    int type = tile.Type != null ? tile.Type.index_id : -1;
                    bool fire = fires != null && i < fires.Length && fires[i];
                    uint v = (uint)(type * 31 + tile.health * 2 + (fire ? 1 : 0));
                    v = v * 31 + (uint)tile.burned_stages;
                    v = v * 31 + (uint)(tile.main_type != null ? tile.main_type.index_id : -1);
                    th += Mix(((ulong)(uint)i << 32) ^ v);
                }
                t.tiles = th;
            }
            return t;
        }
    }
}
