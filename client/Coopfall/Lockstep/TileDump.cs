using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// On a desync both PCs write every tile's simulation-relevant state (lockstep-tilesN.txt):
    /// the world checksum covers tile types only, but paths and "is this tile free" read buildings,
    /// regions, walls and units on tiles too. tools/compare-watch.py lists the tiles that differ.
    /// </summary>
    public static class TileDump
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo _units = typeof(WorldTile).GetField("_units", Any), _walls = typeof(WorldTile).GetField("_has_walls_around", Any),
            _targeted = typeof(WorldTile).GetField("_targeted_by", Any), _checked = typeof(WorldTile).GetField("is_checked_tile", Any), _score = typeof(WorldTile).GetField("score", Any);
        private static readonly FieldInfo _regionId = typeof(MapRegion).GetField("id", Any);

        public static void Write(string path)
        {
            try
            {
                using (var w = new StreamWriter(path))
                {
                    var sb = new StringBuilder();
                    foreach (WorldTile t in (WorldTile[])typeof(MapBox).GetField("tiles_list", Any).GetValue(World.world))
                    {
                        sb.Length = 0;
                        sb.Append(t.tile_id).Append('\t').Append(t.Type?.id).Append('\t').Append(t.top_type?.id)
                          .Append("\tb").Append(t.building != null ? t.building.getID() : 0)
                          .Append("\tr").Append(t.region != null ? (_regionId?.GetValue(t.region) ?? -2) : -1)
                          .Append("\tz").Append(t.zone != null ? t.zone.id : -1)
                          .Append("\to").Append(t.obstacle_is_around ? 1 : 0)
                          .Append("\tw").Append(_walls != null && (bool)_walls.GetValue(t) ? 1 : 0)
                          .Append("\tu").Append((_units?.GetValue(t) as System.Collections.ICollection)?.Count ?? 0)
                          .Append("\tt").Append((_targeted?.GetValue(t) as Actor)?.getID() ?? 0)
                          .Append("\tc").Append(_checked != null && (bool)_checked.GetValue(t) ? 1 : 0)
                          .Append("\ts").Append(_score?.GetValue(t) ?? 0)
                          .Append("\th").Append(t.health).Append("\tbs").Append(t.burned_stages).Append("\tp").Append(t.pollinated)
                          .Append("\tri").Append(t.road_island != null ? t.road_island.GetHashCode() : 0);
                        w.WriteLine(sb.ToString());
                    }
                }
                Log.Info("lockstep: wrote every tile to " + Path.GetFileName(path));
            }
            catch (Exception e) { Log.Warn("lockstep: couldn't write the tiles: " + e.Message); }
        }
    }
}
