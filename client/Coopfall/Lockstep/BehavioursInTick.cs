using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ai.behaviours;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Deterministic locks for simulation behaviors, AI decisions, God powers, and ocean spreading:
    /// - WorldBehaviourActions.updateGrinReaper: deterministic target selection, ignores UI window in ticks.
    /// - BehCityActorGetRandomDangerZone: deterministic danger zone iteration and tie-breaking.
    /// - BehFingerFindCloseTile & BehFingerFindTarget: deterministic GodFinger target tile selection.
    /// - ActorBagExtensions.getRandomResourceID: no dice advancement from UI windows outside ticks, sorted keys.
    /// - BehUFOSelectTarget: deterministic candidate sorting by actor.id before closest actor calculation.
    /// - Nucleus.generateGenesFromBaseStats: deterministic GenomePart sorting by id and stable stat tie-breaking.
    /// - WorldBehaviourOcean.updateOcean: deterministic tile sorting by tile_id before dice rolls.
    /// </summary>
    public static class BehavioursInTick
    {
        private static readonly List<IMetaObject> _grinMetaPool = new List<IMetaObject>();
        private static readonly List<TileZone> _dangerZonePool = new List<TileZone>();
        private static readonly List<WorldTile> _fingerTilePool = new List<WorldTile>();
        private static readonly List<WorldTile> _fingerRegionPool = new List<WorldTile>();
        private static readonly List<string> _resourceKeysPool = new List<string>();
        private static readonly List<Actor> _ufoCandidatePool = new List<Actor>();
        private static readonly List<GenomePart> _sortedGenomeParts = new List<GenomePart>();
        private static readonly List<WorldTile> _oceanTilesPool = new List<WorldTile>();

        private static AccessTools.FieldRef<HashSet<IMetaObject>> _usedForGrinReaper;
        private static MethodInfo _getHitFullHealthMethod;
        private static FieldInfo _isInMagnetField;
        private static FieldInfo _actorScaleField;

        private static AccessTools.FieldRef<City, HashSet<TileZone>> _dangerZonesRef;
        private static AccessTools.FieldRef<Actor, WorldTile> _behTileTarget;
        private static AccessTools.FieldRef<Actor, ActorData> _actorData;
        private static MethodInfo _findCurrentTileMethod;

        private static AccessTools.FieldRef<BehFinger, GodFinger> _behFingerRef;
        private static AccessTools.FieldRef<GodFinger, HashSet<WorldTile>> _fingerTargetTiles;
        private static FieldInfo _fingerTargetField;
        private static MethodInfo _clearTargetsMethod;
        private static MethodInfo _fillRandomTilesMethod;

        private static AccessTools.FieldRef<UFO, HashSet<Actor>> _ufoAggroTargets;
        private static AccessTools.FieldRef<List<WorldTile>> _oceanTilesToUpdate;
        private static AccessTools.FieldRef<MapBox, ExplosionsEffects> _explosionLayer;
        private static MethodInfo _explodeBombMethod;

        public static void Install(Harmony h)
        {
            try
            {
                _behTileTarget = AccessTools.FieldRefAccess<Actor, WorldTile>("beh_tile_target");
                _actorData = AccessTools.FieldRefAccess<Actor, ActorData>("data");
                _findCurrentTileMethod = AccessTools.Method(typeof(Actor), "findCurrentTile", new[] { typeof(bool) });

                FieldInfo grinField = AccessTools.Field(typeof(WorldBehaviourActions), "_used_for_grin_reaper");
                MethodInfo grinReaper = AccessTools.Method(typeof(WorldBehaviourActions), "updateGrinReaper");
                _getHitFullHealthMethod = AccessTools.Method(typeof(Actor), "getHitFullHealth", new[] { typeof(AttackType) });
                _isInMagnetField = AccessTools.Field(typeof(Actor), "is_in_magnet");
                _actorScaleField = AccessTools.Field(typeof(Actor), "actor_scale");
                if (grinField != null && grinReaper != null)
                {
                    _usedForGrinReaper = AccessTools.StaticFieldRefAccess<HashSet<IMetaObject>>(grinField);
                    h.Patch(grinReaper, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(UpdateGrinReaperPrefix)));
                }
                else Log.Warn("lockstep: WorldBehaviourActions.updateGrinReaper not found: grin reaper kills may differ between PCs");

                MethodInfo dangerZone = AccessTools.Method(typeof(BehCityActorGetRandomDangerZone), "execute");
                if (dangerZone != null)
                {
                    _dangerZonesRef = AccessTools.FieldRefAccess<City, HashSet<TileZone>>("danger_zones");
                    h.Patch(dangerZone, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(BehCityActorGetRandomDangerZonePrefix)));
                }
                else Log.Warn("lockstep: BehCityActorGetRandomDangerZone.execute not found: danger zone responses may differ between PCs");

                FieldInfo fingerField = AccessTools.Field(typeof(BehFinger), "finger");
                MethodInfo fingerClose = AccessTools.Method(typeof(BehFingerFindCloseTile), "execute");
                MethodInfo fingerTarget = AccessTools.Method(typeof(BehFingerFindTarget), "execute");
                if (fingerField != null && fingerClose != null && fingerTarget != null)
                {
                    _behFingerRef = AccessTools.FieldRefAccess<BehFinger, GodFinger>(fingerField);
                    _fingerTargetTiles = AccessTools.FieldRefAccess<GodFinger, HashSet<WorldTile>>("target_tiles");
                    _fingerTargetField = AccessTools.Field(typeof(GodFinger), "finger_target");
                    _clearTargetsMethod = AccessTools.Method(typeof(BehFingerFindTarget), "clearTargets");
                    _fillRandomTilesMethod = AccessTools.Method(typeof(BehFingerFindTarget), "fillRandomTiles");
                    h.Patch(fingerClose, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(BehFingerFindCloseTilePrefix)));
                    h.Patch(fingerTarget, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(BehFingerFindTargetPrefix)));
                }
                else Log.Warn("lockstep: BehFinger target methods not found: GodFinger movements may differ between PCs");

                MethodInfo randomResource = AccessTools.Method(typeof(ActorBagExtensions), "getRandomResourceID");
                if (randomResource != null)
                {
                    h.Patch(randomResource, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(GetRandomResourceIDPrefix)));
                }
                else Log.Warn("lockstep: ActorBagExtensions.getRandomResourceID not found: inventory inspection may leak dice");

                FieldInfo ufoAggro = AccessTools.Field(typeof(UFO), "aggroTargets");
                MethodInfo ufoSelect = AccessTools.Method(typeof(BehUFOSelectTarget), "execute");
                if (ufoAggro != null && ufoSelect != null)
                {
                    _ufoAggroTargets = AccessTools.FieldRefAccess<UFO, HashSet<Actor>>(ufoAggro);
                    h.Patch(ufoSelect, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(BehUFOSelectTargetPrefix)));
                }
                else Log.Warn("lockstep: BehUFOSelectTarget.execute not found: UFO target selection may differ between PCs");

                MethodInfo nucleusGenes = AccessTools.Method(typeof(Nucleus), "generateGenesFromBaseStats");
                if (nucleusGenes != null)
                {
                    h.Patch(nucleusGenes, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(GenerateGenesFromBaseStatsPrefix)));
                }
                else Log.Warn("lockstep: Nucleus.generateGenesFromBaseStats not found: subspecies gene generation may differ between PCs");

                FieldInfo oceanList = AccessTools.Field(typeof(WorldBehaviourOcean), "tiles_to_update");
                MethodInfo oceanUpdate = AccessTools.Method(typeof(WorldBehaviourOcean), "updateOcean");
                FieldInfo explLayer = AccessTools.Field(typeof(MapBox), "explosion_layer");
                if (oceanList != null && oceanUpdate != null && explLayer != null)
                {
                    _oceanTilesToUpdate = AccessTools.StaticFieldRefAccess<List<WorldTile>>(oceanList);
                    _explosionLayer = AccessTools.FieldRefAccess<MapBox, ExplosionsEffects>(explLayer);
                    _explodeBombMethod = AccessTools.Method(typeof(ExplosionsEffects), "explodeBomb", new[] { typeof(WorldTile), typeof(bool) });
                    h.Patch(oceanUpdate, prefix: new HarmonyMethod(typeof(BehavioursInTick), nameof(UpdateOceanPrefix)));
                }
                else Log.Warn("lockstep: WorldBehaviourOcean.updateOcean not found: ocean spreading may differ between PCs");
            }
            catch (Exception e)
            {
                Log.Error("lockstep: BehavioursInTick.Install failed: " + e);
            }
        }

        private static bool UpdateGrinReaperPrefix()
        {
            if (!LockstepClock.InTick) return true;
            if (_usedForGrinReaper == null) return true;
            HashSet<IMetaObject> set = _usedForGrinReaper();
            if (set == null || set.Count == 0) return false;
            set.RemoveWhere(pMeta => pMeta == null || !pMeta.isAlive());
            if (set.Count == 0) return false;

            _grinMetaPool.Clear();
            foreach (IMetaObject m in set)
            {
                if (m != null && m.isAlive()) _grinMetaPool.Add(m);
            }
            if (_grinMetaPool.Count == 0) return false;

            _grinMetaPool.Sort((a, b) => a.getID().CompareTo(b.getID()));
            int idx = Randy.randomInt(0, _grinMetaPool.Count);
            IMetaObject random = _grinMetaPool[idx];
            if (random != null && random.isAlive())
            {
                Actor randomActor = random.getRandomActorForReaper();
                bool inMagnet = _isInMagnetField != null && (bool)_isInMagnetField.GetValue(randomActor);
                if (randomActor != null && !randomActor.isRekt() && !inMagnet)
                {
                    _getHitFullHealthMethod?.Invoke(randomActor, new object[] { AttackType.Smile });
                    float scale = _actorScaleField != null ? (float)_actorScaleField.GetValue(randomActor) : 1f;
                    EffectsLibrary.spawnAt("fx_grin_reaper", randomActor.current_position, scale);
                }
            }
            return false;
        }

        private static bool BehCityActorGetRandomDangerZonePrefix(Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            City city = pActor.city;
            if (city == null || !city.hasZones() || !city.isInDanger() || _dangerZonesRef == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            HashSet<TileZone> dangerZones = _dangerZonesRef(city);
            if (dangerZones == null || dangerZones.Count == 0)
            {
                __result = BehResult.Stop;
                return false;
            }

            _dangerZonePool.Clear();
            foreach (TileZone danger_zone in dangerZones)
            {
                if (danger_zone != null) _dangerZonePool.Add(danger_zone);
            }
            if (_dangerZonePool.Count == 0)
            {
                __result = BehResult.Stop;
                return false;
            }
            _dangerZonePool.Sort((a, b) => a.id.CompareTo(b.id));

            if (Randy.randomChance(0.2f))
            {
                for (int i = 0; i < _dangerZonePool.Count; i++)
                {
                    TileZone danger_zone = _dangerZonePool[i];
                    if (danger_zone.tiles == null || danger_zone.tiles.Length == 0) continue;
                    WorldTile random = danger_zone.tiles.GetRandom();
                    if (random != null && random.isSameIsland(pActor.current_tile))
                    {
                        _behTileTarget(pActor) = random;
                        __result = BehResult.Continue;
                        return false;
                    }
                }
            }

            int num = int.MaxValue;
            WorldTile worldTile = null;
            for (int i = 0; i < _dangerZonePool.Count; i++)
            {
                TileZone danger_zone2 = _dangerZonePool[i];
                WorldTile centerTile = danger_zone2.centerTile;
                if (centerTile == null) continue;
                int num2 = Toolbox.SquaredDistTile(pActor.current_tile, centerTile);
                if (num2 <= num && centerTile.isSameIsland(pActor.current_tile) && (num2 != num || !Randy.randomBool()))
                {
                    num = num2;
                    worldTile = centerTile;
                }
            }
            if (worldTile != null)
            {
                _behTileTarget(pActor) = worldTile;
                __result = BehResult.Continue;
                return false;
            }
            __result = BehResult.Stop;
            return false;
        }

        private static bool BehFingerFindCloseTilePrefix(BehFingerFindCloseTile __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (_behFingerRef == null || _fingerTargetTiles == null) return true;
            GodFinger finger = _behFingerRef(__instance);
            _findCurrentTileMethod?.Invoke(pActor, new object[] { false });
            if (finger == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            HashSet<WorldTile> target_tiles = _fingerTargetTiles(finger);
            if (target_tiles == null || target_tiles.Count == 0)
            {
                __result = BehResult.Stop;
                return false;
            }

            _fingerTilePool.Clear();
            foreach (WorldTile t in target_tiles)
            {
                if (t != null) _fingerTilePool.Add(t);
            }
            if (_fingerTilePool.Count == 0)
            {
                __result = BehResult.Stop;
                return false;
            }
            _fingerTilePool.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));

            if (pActor.current_tile != null && target_tiles.Contains(pActor.current_tile))
            {
                _fingerRegionPool.Clear();
                for (int i = 0; i < _fingerTilePool.Count; i++)
                {
                    if (_fingerTilePool[i].region == pActor.current_tile.region)
                        _fingerRegionPool.Add(_fingerTilePool[i]);
                }
                if (_fingerRegionPool.Count > 0)
                {
                    _behTileTarget(pActor) = _fingerRegionPool[Randy.randomInt(0, _fingerRegionPool.Count)];
                    __result = BehResult.Continue;
                    return false;
                }
            }

            _behTileTarget(pActor) = _fingerTilePool[Randy.randomInt(0, _fingerTilePool.Count)];
            __result = BehResult.Continue;
            return false;
        }

        private static bool BehFingerFindTargetPrefix(BehFingerFindTarget __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (_behFingerRef == null || _fingerTargetTiles == null) return true;
            GodFinger finger = _behFingerRef(__instance);
            _findCurrentTileMethod?.Invoke(pActor, new object[] { false });
            if (finger == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            _clearTargetsMethod?.Invoke(null, new object[] { finger });
            HashSet<WorldTile> target_tiles = _fingerTargetTiles(finger);
            if (target_tiles.Count == 0)
            {
                object targetResult = _fillRandomTilesMethod?.Invoke(__instance, new object[] { pActor.current_tile, target_tiles });
                if (targetResult != null) _fingerTargetField?.SetValue(finger, targetResult);
            }
            if (target_tiles.Count == 0)
            {
                __result = BehResult.Stop;
                return false;
            }
            _fingerTilePool.Clear();
            foreach (WorldTile t in target_tiles)
            {
                if (t != null) _fingerTilePool.Add(t);
            }
            if (_fingerTilePool.Count == 0)
            {
                __result = BehResult.Stop;
                return false;
            }
            _fingerTilePool.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));
            _behTileTarget(pActor) = _fingerTilePool[Randy.randomInt(0, _fingerTilePool.Count)];
            __result = BehResult.Continue;
            return false;
        }

        private static bool GetRandomResourceIDPrefix(ActorBag pBag, ref string __result)
        {
            if (pBag == null)
            {
                __result = string.Empty;
                return false;
            }
            Dictionary<string, ResourceContainer> resources = pBag.getResources();
            if (resources == null || resources.Count == 0)
            {
                __result = string.Empty;
                return false;
            }

            _resourceKeysPool.Clear();
            foreach (string key in resources.Keys)
            {
                if (!string.IsNullOrEmpty(key)) _resourceKeysPool.Add(key);
            }
            if (_resourceKeysPool.Count == 0)
            {
                __result = string.Empty;
                return false;
            }
            _resourceKeysPool.Sort(StringComparer.Ordinal);

            if (!LockstepClock.InTick)
            {
                // Inspector UI (UnitWindow) call outside simulation tick: do NOT advance Randy!
                __result = _resourceKeysPool[0];
                return false;
            }

            int idx = Randy.randomInt(0, _resourceKeysPool.Count);
            __result = _resourceKeysPool[idx];
            return false;
        }

        private static bool BehUFOSelectTargetPrefix(BehUFOSelectTarget __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            UFO actorComponent = pActor.getActorComponent<UFO>();
            if (actorComponent != null && _ufoAggroTargets != null)
            {
                HashSet<Actor> aggroTargets = _ufoAggroTargets(actorComponent);
                if (aggroTargets != null && aggroTargets.Count > 0)
                {
                    _ufoCandidatePool.Clear();
                    foreach (Actor aggroTarget in aggroTargets)
                    {
                        if (aggroTarget != null && aggroTarget.isAlive())
                        {
                            _ufoCandidatePool.Add(aggroTarget);
                        }
                    }
                    aggroTargets.Clear();

                    if (_ufoCandidatePool.Count > 0)
                    {
                        // Sort candidates deterministically by actor.id
                        _ufoCandidatePool.Sort((a, b) => a.id.CompareTo(b.id));

                        Actor closestActor = Toolbox.getClosestActor(_ufoCandidatePool, pActor.current_tile);
                        if (closestActor != null)
                        {
                            if (closestActor.city != null && _actorData != null)
                            {
                                ActorData data = _actorData(pActor);
                                data.get("cityToAttack", out long pResult, -1L);
                                if (pResult == -1L)
                                {
                                    data.set("cityToAttack", closestActor.city.data.id);
                                    data.set("attacksForCity", Randy.randomInt(3, 10));
                                }
                            }
                            _behTileTarget(pActor) = closestActor.current_tile;
                            __result = __instance.forceTask(pActor, "ufo_chase", pClean: false);
                            return false;
                        }
                    }
                }
            }
            __result = BehResult.Continue;
            return false;
        }

        private static bool GenerateGenesFromBaseStatsPrefix(Nucleus __instance, HashSet<GenomePart> pGeneTemplateStats, ref ListPool<string> __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pGeneTemplateStats == null)
            {
                __result = new ListPool<string>();
                return false;
            }

            _sortedGenomeParts.Clear();
            _sortedGenomeParts.AddRange(pGeneTemplateStats);
            _sortedGenomeParts.Sort((a, b) => string.Compare(a.id, b.id, StringComparison.Ordinal));

            ListPool<string> listPool = new ListPool<string>();
            Dictionary<string, float> dictionary = new Dictionary<string, float>();

            for (int i = 0; i < _sortedGenomeParts.Count; i++)
            {
                GenomePart pGeneTemplateStat = _sortedGenomeParts[i];
                string id = pGeneTemplateStat.id;
                if (IsStatGene(id))
                {
                    dictionary[id] = pGeneTemplateStat.value;
                }
                else
                {
                    AddSpecialGenes(id, (int)pGeneTemplateStat.value, listPool);
                }
            }

            bool flag = true;
            int num = 300;
            while (flag)
            {
                flag = false;
                if (num-- < 0)
                {
                    Debug.LogError("generateGenesFromBaseStats infinite loop");
                    break;
                }
                for (int i = 0; i < _sortedGenomeParts.Count; i++)
                {
                    GenomePart tGenomePart = _sortedGenomeParts[i];
                    if (!IsStatGene(tGenomePart.id)) continue;

                    List<GeneAsset> genesWithStat = AssetManager.gene_library.getGenesWithStat(tGenomePart.id);
                    if (genesWithStat == null || genesWithStat.Count == 0) continue;

                    if (Randy.randomChance(0.8f))
                    {
                        genesWithStat.Sort((pG1, pG2) => SortByStatValue(pG1, pG2, tGenomePart.id));
                    }
                    else
                    {
                        genesWithStat.Shuffle();
                    }

                    for (int j = 0; j < genesWithStat.Count; j++)
                    {
                        GeneAsset geneAsset = genesWithStat[j];
                        bool flag2 = true;
                        foreach (BaseStatsContainer item in geneAsset.base_stats.getList())
                        {
                            if (!dictionary.ContainsKey(item.id) || dictionary[item.id] < item.value)
                            {
                                flag2 = false;
                                break;
                            }
                        }
                        if (flag2)
                        {
                            foreach (BaseStatsContainer item2 in geneAsset.base_stats_meta.getList())
                            {
                                if (!dictionary.ContainsKey(item2.id) || dictionary[item2.id] < item2.value)
                                {
                                    flag2 = false;
                                    break;
                                }
                            }
                        }
                        if (!flag2) continue;

                        foreach (BaseStatsContainer item3 in geneAsset.base_stats.getList())
                        {
                            dictionary[item3.id] -= item3.value;
                        }
                        foreach (BaseStatsContainer item4 in geneAsset.base_stats_meta.getList())
                        {
                            dictionary[item4.id] -= item4.value;
                        }
                        listPool.Add(geneAsset.id);
                        flag = true;
                        break;
                    }
                    if (flag) break;
                }
            }

            __result = listPool;
            return false;
        }

        private static bool IsStatGene(string pID)
        {
            switch (pID)
            {
                case "bonus_male":
                case "bonus_female":
                case "bonus_sex_random":
                case "bad":
                    return false;
                default:
                    return true;
            }
        }

        private static void AddSpecialGenes(string pSpecialGeneID, int pAmount, ListPool<string> pResultGenes)
        {
            for (int i = 0; i < pAmount; i++)
            {
                if (pSpecialGeneID == "bonus_sex_random")
                {
                    if (Randy.randomBool())
                        pResultGenes.Add("bonus_male");
                    else
                        pResultGenes.Add("bonus_female");
                }
                else
                {
                    pResultGenes.Add(pSpecialGeneID);
                }
            }
        }

        private static int SortByStatValue(GeneAsset pA, GeneAsset pB, string pStatID)
        {
            if (!pA.base_stats.hasStat(pStatID) && !pB.base_stats.hasStat(pStatID))
            {
                return string.Compare(pA.id, pB.id, StringComparison.Ordinal);
            }
            if (!pA.base_stats.hasStat(pStatID))
            {
                return 1;
            }
            if (!pB.base_stats.hasStat(pStatID))
            {
                return -1;
            }
            int cmp = pB.base_stats[pStatID].CompareTo(pA.base_stats[pStatID]);
            if (cmp != 0) return cmp;
            return string.Compare(pA.id, pB.id, StringComparison.Ordinal);
        }

        private static bool IsOceanAround(WorldTile tile)
        {
            if (tile == null || tile.neighbours == null) return false;
            for (int i = 0; i < tile.neighbours.Length; i++)
            {
                if (tile.neighbours[i] != null && tile.neighbours[i].Type.layer_type == TileLayerType.Ocean)
                    return true;
            }
            return false;
        }

        private static bool UpdateOceanPrefix()
        {
            if (!LockstepClock.InTick) return true;
            if (WorldBehaviourOcean.tiles == null || WorldBehaviourOcean.tiles.Count == 0) return false;

            _oceanTilesPool.Clear();
            foreach (WorldTile t in WorldBehaviourOcean.tiles)
            {
                if (t != null) _oceanTilesPool.Add(t);
            }
            if (_oceanTilesPool.Count == 0) return false;

            _oceanTilesPool.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));

            List<WorldTile> toUpdate = _oceanTilesToUpdate();
            toUpdate.Clear();

            for (int i = 0; i < _oceanTilesPool.Count; i++)
            {
                WorldTile tile = _oceanTilesPool[i];
                if (tile.world_edge)
                {
                    if (!((float)Randy.randomInt(0, 100) < 30f))
                    {
                        toUpdate.Add(tile);
                    }
                }
                else if (IsOceanAround(tile) && !((float)Randy.randomInt(0, 100) < 30f))
                {
                    toUpdate.Add(tile);
                }
            }

            for (int i = 0; i < toUpdate.Count; i++)
            {
                WorldTile worldTile = toUpdate[i];
                if (worldTile.Type.can_be_filled_with_ocean)
                {
                    if (worldTile.Type.explodable_by_ocean && _explosionLayer != null && _explodeBombMethod != null)
                    {
                        _explodeBombMethod.Invoke(_explosionLayer(World.world), new object[] { worldTile, false });
                    }
                    else
                    {
                        MapAction.setOcean(worldTile);
                    }
                }
            }

            return false;
        }
    }
}
