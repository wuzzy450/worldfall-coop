using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Deterministic locks for city zones, expansion, citizen border patrols, and singularity powers:
    /// - City.sortZonesByDistanceToCenter: tie-breaks equal-distance zones by zone.id.
    /// - City.recalculateNeighbourZones: rebuilds border_zones and neighbour_zones in zone.id order.
    /// - BehCityActorGetRandomBorderTile: deterministic border zone picking.
    /// - BehKingCheckNewCityFoundation.findZoneForCityOnTheSameIsland2: deterministic wave BFS & candidate zone picking.
    /// - CityZoneGrowth: deterministic candidate zone picking for both getRandomZone and getRandomCheckedZone.
    /// - ActionLibrary.singularityTeleportation: deterministic tile selection from sorted TileType.hashset.
    /// </summary>
    public static class ZonesInTick
    {
        private static readonly List<TileZone> _sortZonePool = new List<TileZone>();
        private static readonly List<WorldTile> _sortTilePool = new List<WorldTile>();
        private static readonly List<TileZone> _growthCandidateZonesPool = new List<TileZone>();

        private static readonly Comparison<TileZone> _zoneOrder = (a, b) =>
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            return a.id.CompareTo(b.id);
        };

        private static readonly Comparison<WorldTile> _tileOrder = (a, b) =>
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            return a.tile_id.CompareTo(b.tile_id);
        };

        private static readonly AccessTools.FieldRef<City, HashSet<TileZone>> _borderZonesRef =
            AccessTools.FieldRefAccess<City, HashSet<TileZone>>("border_zones");
        private static readonly AccessTools.FieldRef<City, HashSet<TileZone>> _neighbourZonesRef =
            AccessTools.FieldRefAccess<City, HashSet<TileZone>>("neighbour_zones");
        private static readonly AccessTools.FieldRef<City, List<TileZone>> _zonesRef =
            AccessTools.FieldRefAccess<City, List<TileZone>>("zones");

        private static AccessTools.FieldRef<BehKingCheckNewCityFoundation, List<TileZone>> _waveRef;
        private static AccessTools.FieldRef<BehKingCheckNewCityFoundation, List<TileZone>> _nextWaveRef;
        private static AccessTools.FieldRef<BehKingCheckNewCityFoundation, HashSet<TileZone>> _checkedZonesRef;
        private static AccessTools.FieldRef<CityZoneGrowth, HashSet<ZoneConnection>> _growthChecked;

        public static void Install(Harmony h)
        {
            try
            {
                MethodInfo sortZones = AccessTools.Method(typeof(City), "sortZonesByDistanceToCenter");
                if (sortZones != null) h.Patch(sortZones, prefix: new HarmonyMethod(typeof(ZonesInTick), nameof(SortZonesByDistanceToCenterPrefix)));
                else Log.Warn("lockstep: City.sortZonesByDistanceToCenter not found: city zone sorting may differ between PCs");

                MethodInfo recalcNeighbours = AccessTools.Method(typeof(City), "recalculateNeighbourZones");
                if (recalcNeighbours != null) h.Patch(recalcNeighbours, postfix: new HarmonyMethod(typeof(ZonesInTick), nameof(RecalculateNeighbourZonesPostfix)));
                else Log.Warn("lockstep: City.recalculateNeighbourZones not found: border and neighbour zones may differ between PCs");

                MethodInfo borderTile = AccessTools.Method(typeof(BehCityActorGetRandomBorderTile), "execute");
                if (borderTile != null) h.Patch(borderTile, prefix: new HarmonyMethod(typeof(ZonesInTick), nameof(GetRandomBorderTilePrefix)));
                else Log.Warn("lockstep: BehCityActorGetRandomBorderTile.execute not found: citizen border patrols may differ between PCs");

                MethodInfo cityFoundation = AccessTools.Method(typeof(BehKingCheckNewCityFoundation), "findZoneForCityOnTheSameIsland2");
                if (cityFoundation != null)
                {
                    _waveRef = AccessTools.FieldRefAccess<BehKingCheckNewCityFoundation, List<TileZone>>("_wave");
                    _nextWaveRef = AccessTools.FieldRefAccess<BehKingCheckNewCityFoundation, List<TileZone>>("_next_wave");
                    _checkedZonesRef = AccessTools.FieldRefAccess<BehKingCheckNewCityFoundation, HashSet<TileZone>>("_checked_zones");
                    h.Patch(cityFoundation, prefix: new HarmonyMethod(typeof(ZonesInTick), nameof(FindZoneForCityOnTheSameIsland2Prefix)));
                }
                else Log.Warn("lockstep: BehKingCheckNewCityFoundation.findZoneForCityOnTheSameIsland2 not found: city foundation may differ between PCs");

                MethodInfo randomChecked = AccessTools.Method(typeof(CityZoneGrowth), "getRandomCheckedZone");
                MethodInfo randomBorder = AccessTools.Method(typeof(CityZoneGrowth), "getRandomZone");
                MethodInfo checkGrowBorder = AccessTools.Method(typeof(CityZoneGrowth), "checkGrowBorder", new[] { typeof(City) });
                if (randomChecked != null && randomBorder != null)
                {
                    _growthChecked = AccessTools.FieldRefAccess<CityZoneGrowth, HashSet<ZoneConnection>>("_zones_checked");
                    h.Patch(randomChecked, prefix: new HarmonyMethod(typeof(ZonesInTick), nameof(GetRandomCheckedZonePrefix)));
                    h.Patch(randomBorder, prefix: new HarmonyMethod(typeof(ZonesInTick), nameof(GetRandomZonePrefix)));
                }
                else Log.Warn("lockstep: CityZoneGrowth methods not found: zone expansion may differ between PCs");
                if (checkGrowBorder != null) h.Patch(checkGrowBorder, prefix: new HarmonyMethod(typeof(ZonesInTick), nameof(CheckGrowBorderPrefix)));
                else Log.Warn("lockstep: CityZoneGrowth.checkGrowBorder not found");

                MethodInfo singularity = AccessTools.Method(typeof(ActionLibrary), "singularityTeleportation");
                if (singularity != null) h.Patch(singularity, prefix: new HarmonyMethod(typeof(ZonesInTick), nameof(SingularityTeleportationPrefix)));
                else Log.Warn("lockstep: ActionLibrary.singularityTeleportation not found: singularity teleportation may differ between PCs");
            }
            catch (Exception e)
            {
                Log.Error("lockstep: ZonesInTick.Install failed: " + e);
            }
        }

        public static void SortCityZones(City city)
        {
            if (city == null) return;
            List<TileZone> zones = _zonesRef(city);
            if (zones != null && zones.Count > 1)
            {
                WorldTile tile = city.getTile();
                if (tile != null)
                {
                    Vector2Int center = tile.pos;
                    zones.Sort((a, b) =>
                    {
                        if (ReferenceEquals(a, b)) return 0;
                        if (a == null) return -1;
                        if (b == null) return 1;
                        int distA = Toolbox.SquaredDistVec2(a.centerTile.pos, center);
                        int distB = Toolbox.SquaredDistVec2(b.centerTile.pos, center);
                        int cmp = distA.CompareTo(distB);
                        return cmp != 0 ? cmp : a.id.CompareTo(b.id);
                    });
                }
                else
                {
                    zones.Sort(_zoneOrder);
                }
            }
            HashSet<TileZone> borders = _borderZonesRef(city);
            if (borders != null && borders.Count > 1)
            {
                _sortZonePool.Clear();
                _sortZonePool.AddRange(borders);
                _sortZonePool.Sort(_zoneOrder);
                borders.Clear();
                for (int i = 0; i < _sortZonePool.Count; i++)
                    borders.Add(_sortZonePool[i]);
            }
            HashSet<TileZone> neighbours = _neighbourZonesRef(city);
            if (neighbours != null && neighbours.Count > 1)
            {
                _sortZonePool.Clear();
                _sortZonePool.AddRange(neighbours);
                _sortZonePool.Sort(_zoneOrder);
                neighbours.Clear();
                for (int i = 0; i < _sortZonePool.Count; i++)
                    neighbours.Add(_sortZonePool[i]);
            }
        }

        public static void Normalize(MapBox world)
        {
            if (world?.cities?.list == null) return;
            for (int i = 0; i < world.cities.list.Count; i++)
            {
                City c = world.cities.list[i];
                if (c == null) continue;
                SortCityZones(c);
            }
        }

        private static bool SortZonesByDistanceToCenterPrefix(City __instance)
        {
            if (__instance == null) return false;
            WorldTile tile = __instance.getTile();
            List<TileZone> zones = _zonesRef(__instance);
            if (tile != null && zones != null && zones.Count > 1)
            {
                Vector2Int center = tile.pos;
                zones.Sort((a, b) =>
                {
                    if (ReferenceEquals(a, b)) return 0;
                    if (a == null) return -1;
                    if (b == null) return 1;
                    int distA = Toolbox.SquaredDistVec2(a.centerTile.pos, center);
                    int distB = Toolbox.SquaredDistVec2(b.centerTile.pos, center);
                    int cmp = distA.CompareTo(distB);
                    return cmp != 0 ? cmp : a.id.CompareTo(b.id);
                });
            }
            return false;
        }

        private static void RecalculateNeighbourZonesPostfix(City __instance)
        {
            if (__instance == null) return;
            HashSet<TileZone> borders = _borderZonesRef(__instance);
            if (borders != null && borders.Count > 1)
            {
                _sortZonePool.Clear();
                _sortZonePool.AddRange(borders);
                _sortZonePool.Sort(_zoneOrder);
                borders.Clear();
                for (int i = 0; i < _sortZonePool.Count; i++)
                    borders.Add(_sortZonePool[i]);
            }
            HashSet<TileZone> neighbours = _neighbourZonesRef(__instance);
            if (neighbours != null && neighbours.Count > 1)
            {
                _sortZonePool.Clear();
                _sortZonePool.AddRange(neighbours);
                _sortZonePool.Sort(_zoneOrder);
                neighbours.Clear();
                for (int i = 0; i < _sortZonePool.Count; i++)
                    neighbours.Add(_sortZonePool[i]);
            }
        }

        private static bool GetRandomBorderTilePrefix(Actor pActor)
        {
            if (pActor?.city == null) return true;
            HashSet<TileZone> borders = _borderZonesRef(pActor.city);
            if (borders != null && borders.Count > 1)
            {
                _sortZonePool.Clear();
                _sortZonePool.AddRange(borders);
                _sortZonePool.Sort(_zoneOrder);
                borders.Clear();
                for (int i = 0; i < _sortZonePool.Count; i++)
                    borders.Add(_sortZonePool[i]);
            }
            return true;
        }

        private static bool FindZoneForCityOnTheSameIsland2Prefix(BehKingCheckNewCityFoundation __instance, Actor pActor, City pMainCity, ref TileZone __result)
        {
            if (pMainCity == null)
            {
                __result = null;
                return false;
            }
            WorldTile tile = pMainCity.getTile();
            if (tile == null)
            {
                __result = null;
                return false;
            }
            List<TileZone> wave = _waveRef(__instance);
            List<TileZone> nextWave = _nextWaveRef(__instance);
            HashSet<TileZone> checkedZones = _checkedZonesRef(__instance);
            wave.Clear();
            nextWave.Clear();
            checkedZones.Clear();

            HashSet<TileZone> neighbours = _neighbourZonesRef(pMainCity);
            if (neighbours != null && neighbours.Count > 0)
            {
                _sortZonePool.Clear();
                _sortZonePool.AddRange(neighbours);
                _sortZonePool.Sort(_zoneOrder);
                for (int i = 0; i < _sortZonePool.Count; i++)
                {
                    TileZone nz = _sortZonePool[i];
                    if (!nz.hasCity())
                    {
                        wave.Add(nz);
                        checkedZones.Add(nz);
                    }
                }
            }

            using ListPool<TileZone> listPool = new ListPool<TileZone>();
            int num = 0;
            while (wave.Count > 0)
            {
                while (wave.Count > 0)
                {
                    TileZone tileZone = wave.Pop();
                    checkedZones.Add(tileZone);
                    if (num > 2 && tileZone.isGoodForNewCity(pActor) && tileZone.centerTile.isSameIsland(tile))
                    {
                        listPool.Add(tileZone);
                    }
                    TileZone[] nbs = tileZone.neighbours;
                    for (int i = 0; i < nbs.Length; i++)
                    {
                        TileZone n = nbs[i];
                        if (checkedZones.Add(n) && !n.hasCity())
                        {
                            nextWave.Add(n);
                        }
                    }
                }
                if (nextWave.Count > 0)
                {
                    nextWave.Sort(_zoneOrder);
                    wave.AddRange(nextWave);
                    nextWave.Clear();
                    num++;
                    if (num >= 4) break;
                }
            }
            wave.Clear();
            checkedZones.Clear();
            if (listPool.Count == 0)
            {
                __result = null;
                return false;
            }
            listPool.Sort(_zoneOrder);
            __result = listPool.GetRandom();
            return false;
        }

        private static bool CheckGrowBorderPrefix(CityZoneGrowth __instance, City pCity, ref TileZone __result)
        {
            if (!LockstepClock.Active) return true;
            if (pCity == null)
            {
                __result = null;
                return false;
            }
            bool tryBorder = SimulationInTick.Det01x(pCity.id, LockstepClock.Tick, 0x3D7) < 0.7f;
            TileZone zone = null;
            if (tryBorder)
            {
                GetRandomZonePrefix(pCity, ref zone);
                if (zone == null)
                {
                    GetRandomCheckedZonePrefix(__instance, pCity, ref zone);
                }
            }
            else
            {
                GetRandomCheckedZonePrefix(__instance, pCity, ref zone);
            }
            __result = zone;
            return false;
        }

        private static bool GetRandomCheckedZonePrefix(CityZoneGrowth __instance, City pCity, ref TileZone __result)
        {
            if (!LockstepClock.Active) return true;
            if (pCity == null)
            {
                __result = null;
                return false;
            }
            HashSet<ZoneConnection> checkedSet = _growthChecked(__instance);
            if (checkedSet == null || checkedSet.Count == 0)
            {
                __result = null;
                return false;
            }
            using ListPool<TileZone> listPool = new ListPool<TileZone>(checkedSet.Count);
            foreach (ZoneConnection item in checkedSet)
            {
                TileZone zone = item.zone;
                if (zone.canBeClaimedByCity(pCity) && !listPool.Contains(zone))
                {
                    listPool.Add(zone);
                }
            }
            if (listPool.Count > 0)
            {
                listPool.Sort(_zoneOrder);
                int pick = (int)(SimulationInTick.Det01x(pCity.id, LockstepClock.Tick, 0x3D9) * listPool.Count);
                if (pick >= listPool.Count) pick = listPool.Count - 1;
                __result = listPool[pick];
                return false;
            }
            __result = null;
            return false;
        }

        private static bool GetRandomZonePrefix(City pCity, ref TileZone __result)
        {
            if (!LockstepClock.Active) return true;
            if (pCity == null)
            {
                __result = null;
                return false;
            }
            HashSet<TileZone> borders = _borderZonesRef(pCity);
            if (borders == null || borders.Count == 0)
            {
                __result = null;
                return false;
            }
            using ListPool<TileZone> list = new ListPool<TileZone>(borders.Count);
            list.AddRange(borders);
            list.Sort(_zoneOrder);
            WorldTile tile = pCity.getTile();
            if (tile == null)
            {
                __result = null;
                return false;
            }
            TileZone zone = tile.zone;
            float num = (float)pCity.getZoneRange() * 0.75f;
            num *= num;

            _growthCandidateZonesPool.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                TileZone item = list[i];
                if (item == null || item.neighbours == null) continue;
                for (int j = 0; j < item.neighbours.Length; j++)
                {
                    TileZone item2 = item.neighbours[j];
                    if (item2 != null && item2.canBeClaimedByCity(pCity) && item2.centerTile != null && item2.centerTile.isSameIsland(tile) && !((float)Toolbox.SquaredDist(zone.x, zone.y, item2.x, item2.y) > num))
                    {
                        if (!_growthCandidateZonesPool.Contains(item2))
                        {
                            _growthCandidateZonesPool.Add(item2);
                        }
                    }
                }
            }
            if (_growthCandidateZonesPool.Count > 0)
            {
                _growthCandidateZonesPool.Sort(_zoneOrder);
                int pick = (int)(SimulationInTick.Det01x(pCity.id, LockstepClock.Tick, 0x3D8) * _growthCandidateZonesPool.Count);
                if (pick >= _growthCandidateZonesPool.Count) pick = _growthCandidateZonesPool.Count - 1;
                __result = _growthCandidateZonesPool[pick];
                _growthCandidateZonesPool.Clear();
                return false;
            }
            _growthCandidateZonesPool.Clear();
            __result = null;
            return false;
        }

        private static void SortTileHashSet(HashSet<WorldTile> set)
        {
            if (set == null || set.Count < 2) return;
            _sortTilePool.Clear();
            _sortTilePool.AddRange(set);
            _sortTilePool.Sort(_tileOrder);
            set.Clear();
            for (int i = 0; i < _sortTilePool.Count; i++)
                set.Add(_sortTilePool[i]);
        }

        private static bool SingularityTeleportationPrefix()
        {
            BiomeAsset biomeAsset = AssetManager.biome_library.get("biome_singularity");
            if (biomeAsset != null)
            {
                SortTileHashSet(biomeAsset.getTileHigh()?.hashset);
                SortTileHashSet(biomeAsset.getTileLow()?.hashset);
            }
            return true;
        }
    }
}
