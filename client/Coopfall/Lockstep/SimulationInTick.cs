using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ai.behaviours;
using ai;
using tools;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Deterministic simulation locks for world behaviors, combat, diplomacy, and unit control:
    /// 1. WorldBehaviourActions.updateFarmDecay: deterministic tile sorting before Fisher-Yates shuffle.
    /// 2. Boat.unloadPassengers: deterministic passenger sorting by actor.id before disembark / applyRandomForce.
    /// 3. War succession & army sorting: stable kingdom.id tie-breaking in War and KingdomListComponent.sortByArmy.
    /// 4. City.updateCapture: deterministic capture winner tie-breaking by kingdom.id on equal capture points.
    /// 5. Toolbox.getClosestActor & BehDragonCheckAttackTile: deterministic closest actor tie-breaking by actor.id.
    /// 6. ItemCrafting.getItemAssetToCraft: copies candidate list before shuffle to prevent mutating persistent culture weapon assets.
    /// 7. KingdomBehCheckKing.checkShatteredCrownEvent: deterministic initial order and stable city.id tie-breaking.
    /// 8. Alliance.recalculate: sorts kingdoms_list by kingdom.id before merging wars.
    /// 9. ControllableUnit.trySelectNewMain: picks lowest actor.id deterministically without consuming dice.
    /// 10. ListSorters & Clan chief succession: stable actor.id tie-breaking in all ListSorters methods.
    /// 11. SuccessionTool.getKingFromLeaders: deterministic candidate pre-sorting and actor.id tie-breaking.
    /// 12. CityBehCheckLeader: deterministic citizen and clan leader pre-sorting and actor.id tie-breaking.
    /// 13. KingdomBehCheckKing.checkKingdomChaos: candidate cities sorted by city.id before LoopRandom.
    /// 14. DiplomacyHelpersRebellion.checkMoreAlignedCities: candidate cities sorted by city.id before LoopRandom.
    /// 15. WorldBehaviourTilesTemperatureFreezeWaves: static clear on load, candidate sorting by tile_id before shuffle/update.
    /// 16. BehFindLover.execute: deterministic closest chunk and city lover selection with actor.id tie-breaking.
    /// 17. BehFindHouse.execute: deterministic candidate family members sorting and resident eviction by actor ID.
    /// 18. KingdomBehCheckCapital.execute: stable city.id tie-breaking on equal building count.
    /// 19. DiplomacyManager.sortByPower: stable kingdom.id tie-breaking on equal power.
    /// 20. CityBehCheckAttackZone.execute: deterministic candidate building sorting and closest enemy city tie-breaking.
    /// 21. CityBehBorderSteal.execute: deterministic candidate building and border zone picking.
    /// 22. Culture.recalcPreferredWeapons: deterministic sorting of preferred weapon subtypes and weapon assets.
    /// 23. DiplomacyHelpers.getAllianceTarget: pre-sorting neutral candidate kingdoms by kingdom.id before LoopRandom.
    /// 24. City.recalculateCityTile: deterministic highest-priority building search with building.id tie-breaking.
    /// 25. CityZoneAbandon.sorter: stable minimum zone.id tie-breaking on equal disconnected cluster zone counts.
    /// 26. CityResources.foodSorter: stable string ID tie-breaking on equal food storage quantities.
    /// 27. Toolbox.sortTilesByDistance: stable tile_id tie-breaking on equal squared tile distance.
    /// 28. PlotsLibrary: deterministic candidate sorting for rites and rebellion.
    /// 29. WorldBehaviourActions.updateRoadDecay: deterministic zone grid pick and tile iteration.
    /// 30. Toolbox spatial distance tie-breakers: stable ID tie-breakers on getClosestTile, getClosestBuilding, getClosestRegion, sortRegionsByDistance, getClosestActor.
    /// 31. BehCityActorGetRandomIdleTile & BehCityActorGetRandomZoneTile: deterministic candidate zone and tile selection.
    /// 32. BehTeleportHome.execute: deterministic candidate zone and tile selection.
    /// 33. BehDragonCheckAttackCity & BehUFOFindTarget: candidate buildings sorted by id before targeting.
    /// 34. CityBehBuild: deterministic building upgrades, roads, docks, and building list tie-breaking.
    /// 35. CityBehSupplyKingdomCities & City.getRandomStockpile: deterministic stockpile and sister city selection.
    /// 36. ActorTool: deterministic candidate building and zone searches.
    /// 37. BehCityActorCheckAttack.execute: deterministic soldier attack zone and neighbour selection.
    /// 38. WarManager.checkDeadObjects: stable war.id tie-breaking on equal died_time.
    /// 39. DiplomacyManager.startWar & DiplomacyHelpersRebellion.startRebellion: deterministic allied war joining in kingdom.id order.
    /// 40. BehGoToStablePlace.execute: deterministic island landfall and region tie-breaking.
    /// 41. DropsLibrary.action_paint: deterministic candidate zone selection.
    /// 42. Actor.checkEnemyTargets & BaseSimObject.findEnemyObjectTarget: no shared PRNG draws from
    ///     parallel job threads - deterministic per-actor re-search timeout, deterministic enemy list
    ///     order instead of LoopRandom, aggression targets sorted by actor id.
    /// 43. ActorManager.checkSleepingUnits: sleeping candidates gathered and sorted by actor id (was statuses.list.LoopRandom).
    /// 44. ai.ActorTool.getDockTradeTarget: island docks sorted by building id before selection.
    /// 45. BehFindNearbyPotentialCivCityToJoin.getPotentialCityNearby: candidate cities sorted by city id (was cities.list.LoopRandom).
    /// 46. BuildingHelper.tryToPlaceBuilding: candidate tiles sorted by tile_id before placement (was pList.LoopRandom).
    /// 47. BabyHelper.traitsInherit: inherited trait pool built from parents' traits sorted by trait id before GetRandom draws.
    /// 48. SubspeciesTraitLibrary.getRandomMutationTraitToAdd/Remove: mutation pots sorted by trait id before GetRandom draws.
    /// </summary>
    public static class SimulationInTick
    {
        // Pools for deterministic allocations
        private static readonly List<Actor> _boatPassengersPool = new List<Actor>();
        private static readonly List<EquipmentAsset> _craftingItemsPool = new List<EquipmentAsset>();
        private static readonly List<City> _crownCitiesPool = new List<City>();
        private static readonly List<Actor> _crownActorsPool = new List<Actor>();
        private static readonly List<Kingdom> _crownKingdomsPool = new List<Kingdom>();
        private static readonly Dictionary<long, int> _crownDict = new Dictionary<long, int>();
        private static readonly List<Actor> _dragonTargetsPool = new List<Actor>();
        private static readonly List<Actor> _loverCandidatesPool = new List<Actor>();
        private static readonly List<Actor> _familyUnitsPool = new List<Actor>();
        private static readonly List<long> _houseResidentsPool = new List<long>();
        private static readonly List<Building> _attackBuildingsPool = new List<Building>();
        private static readonly List<TileZone> _attackZonesPool = new List<TileZone>();
        private static readonly List<Building> _stealBuildingsPool = new List<Building>();
        private static readonly List<TileZone> _stealZonesPool = new List<TileZone>();

        // 28-41 Internal field refs & methods
        private static AccessTools.FieldRef<WarManager, List<War>> _warManagerEndWarsRef;
        private static AccessTools.FieldRef<City, List<Building>> _cityStockpilesRef;
        private static AccessTools.FieldRef<City, List<WorldTile>> _cityRoadTilesRef;
        private static AccessTools.FieldRef<MapBox, ZoneCalculator> _zoneCalculatorRef;
        private static AccessTools.FieldRef<MapBox, IslandsCalculator> _islandsCalculatorRef;
        private static MethodInfo _hasGroundMethod;
        private static Func<IslandsCalculator, bool> _hasGroundFunc;
        private static MethodInfo _getRandomIslandGroundMethod;
        private static Func<IslandsCalculator, bool, TileIsland> _getRandomIslandGroundFunc;
        private static MethodInfo _canBuildFromMethod;
        private static Func<BuildingManager, WorldTile, BuildingAsset, City, BuildPlacingType, bool, bool> _canBuildFromFunc;
        private static MethodInfo _canBeUpgradedMethod;
        private static Func<Building, bool> _canBeUpgradedFunc;
        private static MethodInfo _isGoodIslandMethod;
        private static Func<TileIsland, Actor, bool> _isGoodIslandFunc;
        private static MethodInfo _removeWarObjectMethod;
        private static AccessTools.FieldRef<Building, List<WorldTile>> _buildingTilesRef;
        private static MethodInfo _spawnOnMethod;
        private static MethodInfo _getBuildingToBuildMethod;
        private static MethodInfo _getBuildingOfTypeMethod;
        private static MethodInfo _startWarMethod;
        private static AccessTools.FieldRef<BaseSimObject, BaseStats> _baseStatsRef;

        // 18-27 Internal field refs
        private static AccessTools.FieldRef<City, WorldTile> _cityTileRef;
        private static AccessTools.FieldRef<City, City> _targetAttackCityRef;
        private static AccessTools.FieldRef<City, TileZone> _targetAttackZoneRef;
        private static AccessTools.FieldRef<City, Vector2> _cityCenterRef;
        private static AccessTools.FieldRef<Culture, List<string>> _preferredSubtypesRef;
        private static AccessTools.FieldRef<Culture, List<EquipmentAsset>> _preferredAssetsRef;
        private static AccessTools.FieldRef<City, List<TileZone>> _cityZonesRef;
        private static MethodInfo _removeZoneMethod;
        private static MethodInfo _addZoneMethod;

        // 15. Freeze waves field refs
        private static FieldInfo _freezeNextWaveField;
        private static FieldInfo _freezeCurrentWaveField;
        private static FieldInfo _freezeWaveNumberField;

        // 10-17 Internal field refs
        private static AccessTools.FieldRef<Actor, ActorData> _actorDataRef;
        private static AccessTools.FieldRef<Actor, BaseStats> _actorStatsRef;
        private static AccessTools.FieldRef<City, HashSet<City>> _cityNeighboursCitiesRef;
        private static AccessTools.FieldRef<MapBox, MapChunkManager> _mapChunkManagerRef;

        // 1. Farm decay field refs & methods
        private static AccessTools.FieldRef<int> _lastUsedWorldIdRef;
        private static AccessTools.FieldRef<List<WorldTile>> _tempWheatListRef;
        private static AccessTools.FieldRef<Building, BuildingAsset> _buildingAssetRef;
        private static MethodInfo _startDestroyBuildingMethod;

        // 2. Boat passenger field refs & methods
        private static AccessTools.FieldRef<Boat, HashSet<Actor>> _boatPassengersRef;
        private static MethodInfo _disembarkToMethod;
        private static MethodInfo _applyRandomForceMethod;

        // 3. War succession field refs
        private static AccessTools.FieldRef<War, List<Kingdom>> _warListAttackersRef;
        private static AccessTools.FieldRef<War, List<Kingdom>> _warListDefendersRef;

        // 4. City capture field refs & methods
        private static AccessTools.FieldRef<City, Kingdom> _cityKingdomRef;
        private static AccessTools.FieldRef<City, Dictionary<Kingdom, int>> _capturingUnitsRef;
        private static AccessTools.FieldRef<City, float> _timerCaptureRef;
        private static AccessTools.FieldRef<City, float> _captureTicksRef;
        private static AccessTools.FieldRef<City, Kingdom> _beingCapturedByRef;
        private static MethodInfo _clearCaptureMethod;
        private static MethodInfo _finishCaptureMethod;
        private static MethodInfo _countBuildingsTypeMethod;

        // 5. Dragon behavior field refs & methods
        private static AccessTools.FieldRef<BehDragon, Dragon> _behDragonRef;
        private static AccessTools.FieldRef<Dragon, HashSet<Actor>> _dragonAggroTargetsRef;
        private static AccessTools.FieldRef<Dragon, WorldTile> _dragonLastLandedRef;
        private static MethodInfo _canLandMethod;
        private static MethodInfo _landAttackRangeMethod;
        private static MethodInfo _randomTileWithinLandAttackRangeMethod;
        private static MethodInfo _targetWithinSlideMethod;
        private static MethodInfo _isFlyingMethod;
        private static AccessTools.FieldRef<Actor, WorldTile> _behTileTargetRef;

        // 7. Shattered crown methods
        private static MethodInfo _makeOwnKingdomMethod;

        // 9. Controllable unit field refs
        private static AccessTools.FieldRef<HashSet<Actor>> _controllableUnitsRef;
        private static AccessTools.FieldRef<Actor> _unitMainRef;

        // 42-48 Internal field refs & delegates
        private delegate EnemyFinderData FindEnemiesDelegate(WorldTile pTile, Kingdom pKingdom, int pChunkRange);
        private delegate BaseSimObject CheckObjectListDelegate(BaseSimObject pSelf, IEnumerable<BaseSimObject> pList, bool pAttackBuildings, bool pFindClosest, bool pIgnoreStunned, int pMaxDist);
        private static AccessTools.FieldRef<Actor, bool> _strangeUrgeRef;
        private static AccessTools.FieldRef<Actor, bool> _hasAttackTargetRef;
        private static AccessTools.FieldRef<Actor, float> _timeoutTargetsRef;
        private static AccessTools.FieldRef<Actor, HashSet<long>> _aggressionTargetsRef;
        private static AccessTools.FieldRef<Actor, AiSystemActor> _aiActorRef;
        private static AccessTools.FieldRef<AiSystemActor, BehaviourTaskActor> _aiTaskRef;
        private static MethodInfo _findEnemiesFromMethod;
        private static FindEnemiesDelegate _findEnemiesDel;
        private static MethodInfo _checkObjectListMethod;
        private static CheckObjectListDelegate _checkObjectListDel;
        private static AccessTools.FieldRef<ActorManager, double> _sleepTimestampRef;
        private static AccessTools.FieldRef<Docks, Building> _docksBuildingRef;
        private static MethodInfo _addBuildingMethod;
        private static Func<BuildingManager, BuildingAsset, WorldTile, bool, bool, BuildPlacingType, Building> _addBuildingFunc;
        private static AccessTools.FieldRef<SubspeciesTraitLibrary, List<SubspeciesTrait>> _potMutationAddRef;
        private static AccessTools.FieldRef<SubspeciesTraitLibrary, List<SubspeciesTrait>> _potMutationRemoveRef;

        public static void Install(Harmony h)
        {
            try
            {
                // 1. Farm decay
                FieldInfo lastWorldIdField = AccessTools.Field(typeof(WorldBehaviourActions), "_last_used_world_id");
                FieldInfo tempWheatField = AccessTools.Field(typeof(WorldBehaviourActions), "_temp_wheat_list");
                MethodInfo updateFarmDecay = AccessTools.Method(typeof(WorldBehaviourActions), "updateFarmDecay");
                _startDestroyBuildingMethod = AccessTools.Method(typeof(Building), "startDestroyBuilding");
                _buildingAssetRef = AccessTools.FieldRefAccess<Building, BuildingAsset>("asset");
                if (lastWorldIdField != null && tempWheatField != null && updateFarmDecay != null)
                {
                    _lastUsedWorldIdRef = AccessTools.StaticFieldRefAccess<int>(lastWorldIdField);
                    _tempWheatListRef = AccessTools.StaticFieldRefAccess<List<WorldTile>>(tempWheatField);
                    h.Patch(updateFarmDecay, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(UpdateFarmDecayPrefix)));
                }
                else Log.Warn("lockstep: WorldBehaviourActions.updateFarmDecay not found");

                // 2. Boat passengers
                FieldInfo passengersField = AccessTools.Field(typeof(Boat), "_passengers");
                MethodInfo unloadPassengers = AccessTools.Method(typeof(Boat), "unloadPassengers", new[] { typeof(WorldTile), typeof(bool) });
                _disembarkToMethod = AccessTools.Method(typeof(Actor), "disembarkTo", new[] { typeof(Boat), typeof(WorldTile) });
                _applyRandomForceMethod = AccessTools.Method(typeof(Actor), "applyRandomForce", new[] { typeof(float), typeof(float) });
                if (passengersField != null && unloadPassengers != null)
                {
                    _boatPassengersRef = AccessTools.FieldRefAccess<Boat, HashSet<Actor>>(passengersField);
                    h.Patch(unloadPassengers, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(UnloadPassengersPrefix)));
                }
                else Log.Warn("lockstep: Boat.unloadPassengers not found");

                // 3. War army sort & main attacker/defender succession
                MethodInfo sortByArmy = AccessTools.Method(typeof(KingdomListComponent), "sortByArmy", new[] { typeof(Kingdom), typeof(Kingdom) });
                MethodInfo trySelectMainAttacker = AccessTools.Method(typeof(War), "trySelectNewMainAttacker");
                MethodInfo trySelectMainDefender = AccessTools.Method(typeof(War), "trySelectNewMainDefender");
                FieldInfo attackersField = AccessTools.Field(typeof(War), "_list_attackers");
                FieldInfo defendersField = AccessTools.Field(typeof(War), "_list_defenders");
                if (sortByArmy != null)
                {
                    h.Patch(sortByArmy, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortByArmyPrefix)));
                }
                if (attackersField != null && defendersField != null)
                {
                    _warListAttackersRef = AccessTools.FieldRefAccess<War, List<Kingdom>>(attackersField);
                    _warListDefendersRef = AccessTools.FieldRefAccess<War, List<Kingdom>>(defendersField);
                    if (trySelectMainAttacker != null)
                        h.Patch(trySelectMainAttacker, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TrySelectNewMainAttackerPrefix)));
                    if (trySelectMainDefender != null)
                        h.Patch(trySelectMainDefender, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TrySelectNewMainDefenderPrefix)));
                }
                else Log.Warn("lockstep: War attacker/defender succession methods not found");

                // 4. City capture
                MethodInfo updateCapture = AccessTools.Method(typeof(City), "updateCapture", new[] { typeof(float) });
                FieldInfo capUnitsField = AccessTools.Field(typeof(City), "_capturing_units");
                FieldInfo timerCapField = AccessTools.Field(typeof(City), "_timer_capture");
                FieldInfo capTicksField = AccessTools.Field(typeof(City), "_capture_ticks");
                FieldInfo beingCapField = AccessTools.Field(typeof(City), "being_captured_by");
                _cityKingdomRef = AccessTools.FieldRefAccess<City, Kingdom>("kingdom");
                _clearCaptureMethod = AccessTools.Method(typeof(City), "clearCapture");
                _finishCaptureMethod = AccessTools.Method(typeof(City), "finishCapture", new[] { typeof(Kingdom) });
                _countBuildingsTypeMethod = AccessTools.Method(typeof(City), "countBuildingsType", new[] { typeof(string) });
                if (updateCapture != null && capUnitsField != null && timerCapField != null && capTicksField != null && beingCapField != null)
                {
                    _capturingUnitsRef = AccessTools.FieldRefAccess<City, Dictionary<Kingdom, int>>(capUnitsField);
                    _timerCaptureRef = AccessTools.FieldRefAccess<City, float>(timerCapField);
                    _captureTicksRef = AccessTools.FieldRefAccess<City, float>(capTicksField);
                    _beingCapturedByRef = AccessTools.FieldRefAccess<City, Kingdom>(beingCapField);
                    h.Patch(updateCapture, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(UpdateCapturePrefix)));
                }
                else Log.Warn("lockstep: City.updateCapture not found");

                // 5. Toolbox.getClosestActor & BehDragonCheckAttackTile
                MethodInfo closestActorHash = AccessTools.Method(typeof(Toolbox), "getClosestActor", new[] { typeof(HashSet<Actor>), typeof(WorldTile) });
                MethodInfo closestActorList = AccessTools.Method(typeof(Toolbox), "getClosestActor", new[] { typeof(List<Actor>), typeof(WorldTile) });
                if (closestActorHash != null)
                    h.Patch(closestActorHash, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestActorHashSetPrefix)));
                if (closestActorList != null)
                    h.Patch(closestActorList, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestActorListPrefix)));

                FieldInfo behDragonField = AccessTools.Field(typeof(BehDragon), "dragon");
                MethodInfo dragonAttackTile = AccessTools.Method(typeof(BehDragonCheckAttackTile), "execute", new[] { typeof(Actor) });
                _behTileTargetRef = AccessTools.FieldRefAccess<Actor, WorldTile>("beh_tile_target");
                _dragonAggroTargetsRef = AccessTools.FieldRefAccess<Dragon, HashSet<Actor>>("aggroTargets");
                _dragonLastLandedRef = AccessTools.FieldRefAccess<Dragon, WorldTile>("lastLanded");
                _canLandMethod = AccessTools.Method(typeof(Dragon), "canLand", new[] { typeof(Actor) });
                _landAttackRangeMethod = AccessTools.Method(typeof(Dragon), "landAttackRange", new[] { typeof(WorldTile) });
                _randomTileWithinLandAttackRangeMethod = AccessTools.Method(typeof(Dragon), "randomTileWithinLandAttackRange", new[] { typeof(WorldTile) });
                _targetWithinSlideMethod = AccessTools.Method(typeof(Dragon), "targetWithinSlide", new[] { typeof(WorldTile) });
                _isFlyingMethod = AccessTools.Method(typeof(Actor), "isFlying");
                if (behDragonField != null && dragonAttackTile != null)
                {
                    _behDragonRef = AccessTools.FieldRefAccess<BehDragon, Dragon>(behDragonField);
                    h.Patch(dragonAttackTile, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehDragonCheckAttackTilePrefix)));
                }
                else Log.Warn("lockstep: BehDragonCheckAttackTile.execute not found");

                // 6. ItemCrafting.getItemAssetToCraft
                MethodInfo getItemAssetToCraft = AccessTools.Method(typeof(ItemCrafting), "getItemAssetToCraft",
                    new[] { typeof(Actor), typeof(List<EquipmentAsset>), typeof(City), typeof(int), typeof(bool) });
                if (getItemAssetToCraft != null)
                {
                    h.Patch(getItemAssetToCraft, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetItemAssetToCraftPrefix)));
                }
                else Log.Warn("lockstep: ItemCrafting.getItemAssetToCraft not found");

                // 7. Shattered crown event
                MethodInfo shatteredCrown = AccessTools.Method(typeof(KingdomBehCheckKing), "checkShatteredCrownEvent",
                    new[] { typeof(Kingdom), typeof(Actor), typeof(Clan) });
                _makeOwnKingdomMethod = AccessTools.Method(typeof(City), "makeOwnKingdom", new[] { typeof(Actor), typeof(bool), typeof(bool) });
                if (shatteredCrown != null && _makeOwnKingdomMethod != null)
                {
                    h.Patch(shatteredCrown, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CheckShatteredCrownEventPrefix)));
                }
                else Log.Warn("lockstep: KingdomBehCheckKing.checkShatteredCrownEvent not found");

                // 8. Alliance recalculate
                MethodInfo allianceRecalc = AccessTools.Method(typeof(Alliance), "recalculate");
                if (allianceRecalc != null)
                {
                    h.Patch(allianceRecalc, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(AllianceRecalculatePrefix)));
                }
                else Log.Warn("lockstep: Alliance.recalculate not found");

                // 9. Controllable unit main actor selection
                MethodInfo trySelectMain = AccessTools.Method(typeof(ControllableUnit), "trySelectNewMain");
                FieldInfo ctrlUnitsField = AccessTools.Field(typeof(ControllableUnit), "_units");
                FieldInfo ctrlMainField = AccessTools.Field(typeof(ControllableUnit), "_unit_main");
                if (trySelectMain != null && ctrlUnitsField != null && ctrlMainField != null)
                {
                    _controllableUnitsRef = AccessTools.StaticFieldRefAccess<HashSet<Actor>>(ctrlUnitsField);
                    _unitMainRef = AccessTools.StaticFieldRefAccess<Actor>(ctrlMainField);
                    h.Patch(trySelectMain, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TrySelectNewMainPrefix)));
                }
                else Log.Warn("lockstep: ControllableUnit.trySelectNewMain not found");

                // 10. ListSorters determinism
                MethodInfo sortAgeTraits = AccessTools.Method(typeof(ListSorters), "sortUnitsSortedByAgeAndTraits", new[] { typeof(ListPool<Actor>), typeof(Culture) });
                if (sortAgeTraits != null) h.Patch(sortAgeTraits, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitsSortedByAgeAndTraitsPrefix)));
                MethodInfo sortAgeOld = AccessTools.Method(typeof(ListSorters), "sortUnitByAgeOldFirst", new[] { typeof(Actor), typeof(Actor) });
                if (sortAgeOld != null) h.Patch(sortAgeOld, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitByAgeOldFirstPrefix)));
                MethodInfo sortAgeYoung = AccessTools.Method(typeof(ListSorters), "sortUnitByAgeYoungFirst", new[] { typeof(Actor), typeof(Actor) });
                if (sortAgeYoung != null) h.Patch(sortAgeYoung, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitByAgeYoungFirstPrefix)));
                MethodInfo sortRenown = AccessTools.Method(typeof(ListSorters), "sortUnitByRenown", new[] { typeof(Actor), typeof(Actor) });
                if (sortRenown != null) h.Patch(sortRenown, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitByRenownPrefix)));
                MethodInfo sortCoins = AccessTools.Method(typeof(ListSorters), "sortUnitByGoldCoins", new[] { typeof(Actor), typeof(Actor) });
                if (sortCoins != null) h.Patch(sortCoins, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitByGoldCoinsPrefix)));
                MethodInfo sortStats = AccessTools.Method(typeof(ListSorters), "sortUnitByStats", new[] { typeof(Actor), typeof(Actor), typeof(string) });
                if (sortStats != null) h.Patch(sortStats, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitByStatsPrefix)));
                MethodInfo sortKills = AccessTools.Method(typeof(ListSorters), "sortUnitByKills", new[] { typeof(Actor), typeof(Actor) });
                if (sortKills != null) h.Patch(sortKills, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitByKillsPrefix)));
                MethodInfo sortGender = AccessTools.Method(typeof(ListSorters), "sortUnitByGender", new[] { typeof(Actor), typeof(Actor), typeof(ActorSex) });
                if (sortGender != null) h.Patch(sortGender, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortUnitByGenderPrefix)));

                // 11. Kingdom succession from city leaders
                MethodInfo getKingFromLeaders = AccessTools.Method(typeof(SuccessionTool), "getKingFromLeaders", new[] { typeof(Kingdom) });
                if (getKingFromLeaders != null) h.Patch(getKingFromLeaders, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetKingFromLeadersPrefix)));
                else Log.Warn("lockstep: SuccessionTool.getKingFromLeaders not found");

                // 12. City leader appointment
                MethodInfo checkFindLeader = AccessTools.Method(typeof(CityBehCheckLeader), "checkFindLeader", new[] { typeof(City) });
                if (checkFindLeader != null) h.Patch(checkFindLeader, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CheckFindLeaderPrefix)));
                else Log.Warn("lockstep: CityBehCheckLeader.checkFindLeader not found");
                MethodInfo tryGetClanLeader = AccessTools.Method(typeof(CityBehCheckLeader), "tryGetClanLeader", new[] { typeof(City) });
                if (tryGetClanLeader != null) h.Patch(tryGetClanLeader, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TryGetClanLeaderPrefix)));
                else Log.Warn("lockstep: CityBehCheckLeader.tryGetClanLeader not found");

                // 13. Kingdom chaos and fracture
                MethodInfo checkKingdomChaos = AccessTools.Method(typeof(KingdomBehCheckKing), "checkKingdomChaos", new[] { typeof(Kingdom) });
                if (checkKingdomChaos != null) h.Patch(checkKingdomChaos, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CheckKingdomChaosPrefix)));
                else Log.Warn("lockstep: KingdomBehCheckKing.checkKingdomChaos not found");

                // 14. Aligned city rebellion defection
                MethodInfo checkMoreAlignedCities = AccessTools.Method(typeof(DiplomacyHelpersRebellion), "checkMoreAlignedCities", new[] { typeof(Kingdom), typeof(Kingdom), typeof(ListPool<City>), typeof(bool) });
                if (checkMoreAlignedCities != null) h.Patch(checkMoreAlignedCities, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CheckMoreAlignedCitiesPrefix)));
                else Log.Warn("lockstep: DiplomacyHelpersRebellion.checkMoreAlignedCities not found");

                // 15. Freeze waves update
                MethodInfo updateTileFreezeWaves = AccessTools.Method(typeof(WorldBehaviourTilesTemperatureFreezeWaves), "updateTileFreezeWaves");
                _freezeNextWaveField = AccessTools.Field(typeof(WorldBehaviourTilesTemperatureFreezeWaves), "_nextFreezeWave");
                _freezeCurrentWaveField = AccessTools.Field(typeof(WorldBehaviourTilesTemperatureFreezeWaves), "_currentWave");
                _freezeWaveNumberField = AccessTools.Field(typeof(WorldBehaviourTilesTemperatureFreezeWaves), "_waveNumber");
                if (updateTileFreezeWaves != null && _freezeNextWaveField != null && _freezeCurrentWaveField != null && _freezeWaveNumberField != null)
                {
                    h.Patch(updateTileFreezeWaves, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(UpdateTileFreezeWavesPrefix)));
                }
                else Log.Warn("lockstep: WorldBehaviourTilesTemperatureFreezeWaves.updateTileFreezeWaves not found");

                // 16. Lover selection
                MethodInfo behFindLoverExec = AccessTools.Method(typeof(BehFindLover), "execute", new[] { typeof(Actor) });
                if (behFindLoverExec != null) h.Patch(behFindLoverExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehFindLoverExecutePrefix)));
                else Log.Warn("lockstep: BehFindLover.execute not found");

                // 17. Family house eviction and assignment
                MethodInfo behFindHouseExec = AccessTools.Method(typeof(BehFindHouse), "execute", new[] { typeof(Actor) });
                if (behFindHouseExec != null) h.Patch(behFindHouseExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehFindHouseExecutePrefix)));
                else Log.Warn("lockstep: BehFindHouse.execute not found");

                _actorDataRef = AccessTools.FieldRefAccess<Actor, ActorData>("data");
                _actorStatsRef = AccessTools.FieldRefAccess<Actor, BaseStats>("stats");
                _cityNeighboursCitiesRef = AccessTools.FieldRefAccess<City, HashSet<City>>("neighbours_cities");
                _mapChunkManagerRef = AccessTools.FieldRefAccess<MapBox, MapChunkManager>("map_chunk_manager");

                // 18. Kingdom capital selection
                MethodInfo checkCapital = AccessTools.Method(typeof(KingdomBehCheckCapital), "execute", new[] { typeof(Kingdom) });
                if (checkCapital != null) h.Patch(checkCapital, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(KingdomBehCheckCapitalExecutePrefix)));
                else Log.Warn("lockstep: KingdomBehCheckCapital.execute not found");

                // 19. Diplomacy supreme kingdom power sort
                MethodInfo sortByPower = AccessTools.Method(typeof(DiplomacyManager), "sortByPower", new[] { typeof(Kingdom), typeof(Kingdom) });
                if (sortByPower != null) h.Patch(sortByPower, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortByPowerPrefix)));
                else Log.Warn("lockstep: DiplomacyManager.sortByPower not found");

                // 20. City attack target zone & enemy city selection
                MethodInfo checkAttackZone = AccessTools.Method(typeof(CityBehCheckAttackZone), "execute", new[] { typeof(City) });
                if (checkAttackZone != null) h.Patch(checkAttackZone, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CityBehCheckAttackZoneExecutePrefix)));
                else Log.Warn("lockstep: CityBehCheckAttackZone.execute not found");

                // 21. City border steal
                MethodInfo borderSteal = AccessTools.Method(typeof(CityBehBorderSteal), "execute", new[] { typeof(City) });
                if (borderSteal != null) h.Patch(borderSteal, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CityBehBorderStealExecutePrefix)));
                else Log.Warn("lockstep: CityBehBorderSteal.execute not found");

                // 22. Culture preferred weapons
                MethodInfo recalcPref = AccessTools.Method(typeof(Culture), "recalcPreferredWeapons");
                if (recalcPref != null) h.Patch(recalcPref, postfix: new HarmonyMethod(typeof(SimulationInTick), nameof(RecalcPreferredWeaponsPostfix)));
                else Log.Warn("lockstep: Culture.recalcPreferredWeapons not found");

                // 23. Alliance target search
                MethodInfo getAllianceTarget = AccessTools.Method(typeof(DiplomacyHelpers), "getAllianceTarget", new[] { typeof(Kingdom) });
                if (getAllianceTarget != null) h.Patch(getAllianceTarget, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetAllianceTargetPrefix)));
                else Log.Warn("lockstep: DiplomacyHelpers.getAllianceTarget not found");

                // 24. City recalculate city tile
                MethodInfo recalcTile = AccessTools.Method(typeof(City), "recalculateCityTile");
                if (recalcTile != null) h.Patch(recalcTile, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(RecalculateCityTilePrefix)));
                else Log.Warn("lockstep: City.recalculateCityTile not found");

                // 25. City zone cluster abandon
                MethodInfo abandonSorter = AccessTools.Method(typeof(CityZoneAbandon), "sorter", new[] { typeof(ListPool<TileZone>), typeof(ListPool<TileZone>) });
                if (abandonSorter != null) h.Patch(abandonSorter, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CityZoneAbandonSorterPrefix)));
                else Log.Warn("lockstep: CityZoneAbandon.sorter not found");

                // 26. City food diet sorter
                MethodInfo foodSorter = AccessTools.Method(typeof(CityResources), "foodSorter", new[] { typeof(CityStorageSlot), typeof(CityStorageSlot) });
                if (foodSorter != null) h.Patch(foodSorter, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(FoodSorterPrefix)));
                else Log.Warn("lockstep: CityResources.foodSorter not found");

                // 27. Toolbox sort tiles by distance
                MethodInfo sortTiles = AccessTools.Method(typeof(Toolbox), "sortTilesByDistance", new[] { typeof(WorldTile), typeof(ListPool<WorldTile>) });
                if (sortTiles != null) h.Patch(sortTiles, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortTilesByDistancePrefix)));
                else Log.Warn("lockstep: Toolbox.sortTilesByDistance not found");

                // 28. PlotsLibrary divine rites & rebellion determinism
                MethodInfo plotsInit = AccessTools.Method(typeof(PlotsLibrary), "init");
                if (plotsInit != null) h.Patch(plotsInit, postfix: new HarmonyMethod(typeof(SimulationInTick), nameof(PlotsLibraryInitPostfix)));
                MethodInfo summonUnitsAction = AccessTools.Method(typeof(PlotsLibrary), "summonUnitsAction", new[] { typeof(Actor), typeof(string), typeof(int), typeof(int), typeof(bool) });
                if (summonUnitsAction != null) h.Patch(summonUnitsAction, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SummonUnitsActionPrefix)));
                MethodInfo tryStartReligionRite = AccessTools.Method(typeof(PlotsLibrary), "tryStartReligionRiteOnEnemyCity", new[] { typeof(Actor), typeof(PlotAsset), typeof(bool) });
                if (tryStartReligionRite != null) h.Patch(tryStartReligionRite, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TryStartReligionRiteOnEnemyCityPrefix)));
                MethodInfo tryStartCauseReb = AccessTools.Method(typeof(PlotsLibrary), "tryStartCauseRebellion", new[] { typeof(Actor), typeof(PlotAsset), typeof(bool) });
                if (tryStartCauseReb != null) h.Patch(tryStartCauseReb, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TryStartCauseRebellionPrefix)));
                HookPlotAssets();

                // 29. World road decay determinism
                MethodInfo updateRoadDecay = AccessTools.Method(typeof(WorldBehaviourActions), "updateRoadDecay");
                if (updateRoadDecay != null) h.Patch(updateRoadDecay, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(UpdateRoadDecayPrefix)));

                // 30. Spatial distance tie-breakers in Toolbox
                MethodInfo getClosestTileArr = AccessTools.Method(typeof(Toolbox), "getClosestTile", new[] { typeof(WorldTile[]), typeof(WorldTile) });
                if (getClosestTileArr != null) h.Patch(getClosestTileArr, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestTileArrayPrefix)));
                MethodInfo getClosestTileList = AccessTools.Method(typeof(Toolbox), "getClosestTile", new[] { typeof(List<WorldTile>), typeof(WorldTile) });
                if (getClosestTileList != null) h.Patch(getClosestTileList, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestTileListPrefix)));
                MethodInfo getClosestTilePool = AccessTools.Method(typeof(Toolbox), "getClosestTile", new[] { typeof(ListPool<WorldTile>), typeof(WorldTile) });
                if (getClosestTilePool != null) h.Patch(getClosestTilePool, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestTileListPoolPrefix)));
                MethodInfo getClosestBldg = AccessTools.Method(typeof(Toolbox), "getClosestBuilding", new[] { typeof(List<Building>), typeof(WorldTile) });
                if (getClosestBldg != null) h.Patch(getClosestBldg, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestBuildingPrefix)));
                MethodInfo getClosestReg = AccessTools.Method(typeof(Toolbox), "getClosestRegion", new[] { typeof(List<MapRegion>), typeof(WorldTile) });
                if (getClosestReg != null) h.Patch(getClosestReg, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestRegionPrefix)));
                MethodInfo sortRegs = AccessTools.Method(typeof(Toolbox), "sortRegionsByDistance", new[] { typeof(WorldTile), typeof(List<MapRegion>) });
                if (sortRegs != null) h.Patch(sortRegs, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(SortRegionsByDistancePrefix)));
                MethodInfo getClosestActorPool = AccessTools.Method(typeof(Toolbox), "getClosestActor", new[] { typeof(ListPool<Actor>), typeof(WorldTile) });
                if (getClosestActorPool != null) h.Patch(getClosestActorPool, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetClosestActorListPoolPrefix)));

                // 31. City actor idle wandering zone selection
                MethodInfo idleTileExec = AccessTools.Method(typeof(BehCityActorGetRandomIdleTile), "execute", new[] { typeof(Actor) });
                if (idleTileExec != null) h.Patch(idleTileExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehCityActorGetRandomIdleTileExecutePrefix)));
                MethodInfo zoneTileExec = AccessTools.Method(typeof(BehCityActorGetRandomZoneTile), "execute", new[] { typeof(Actor) });
                if (zoneTileExec != null) h.Patch(zoneTileExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehCityActorGetRandomZoneTileExecutePrefix)));

                // 32. Unit teleport home target zone determinism
                MethodInfo teleHomeExec = AccessTools.Method(typeof(BehTeleportHome), "execute", new[] { typeof(Actor) });
                if (teleHomeExec != null) h.Patch(teleHomeExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehTeleportHomeExecutePrefix)));

                // 33. Dragon & UFO city target building selection
                MethodInfo dragonAtkCityExec = AccessTools.Method(typeof(BehDragonCheckAttackCity), "execute", new[] { typeof(Actor) });
                if (dragonAtkCityExec != null) h.Patch(dragonAtkCityExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehDragonCheckAttackCityExecutePrefix)));
                MethodInfo ufoFindTgtExec = AccessTools.Method(typeof(BehUFOFindTarget), "execute", new[] { typeof(Actor) });
                if (ufoFindTgtExec != null) h.Patch(ufoFindTgtExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehUFOFindTargetExecutePrefix)));

                // 34. City building upgrades, roads & docks iteration
                MethodInfo tryUpgradeBldgs = AccessTools.Method(typeof(CityBehBuild), "tryToUpgradeBuildings", new[] { typeof(City) });
                if (tryUpgradeBldgs != null) h.Patch(tryUpgradeBldgs, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TryToUpgradeBuildingsPrefix)));
                MethodInfo upgRandBldg = AccessTools.Method(typeof(CityBehBuild), "upgradeRandomBuilding", new[] { typeof(City) });
                if (upgRandBldg != null) h.Patch(upgRandBldg, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(UpgradeRandomBuildingPrefix)));
                MethodInfo getHouseTile = AccessTools.Method(typeof(CityBehBuild), "getOnHouseTile", new[] { typeof(City), typeof(BuildingAsset) });
                if (getHouseTile != null) h.Patch(getHouseTile, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetOnHouseTilePrefix)));
                MethodInfo getDockTile = AccessTools.Method(typeof(CityBehBuild), "getDockTile", new[] { typeof(City) });
                if (getDockTile != null) h.Patch(getDockTile, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetDockTilePrefix)));
                MethodInfo tryBuildZones = AccessTools.Method(typeof(CityBehBuild), "tryToBuildInZones", new[] { typeof(List<TileZone>), typeof(BuildingAsset), typeof(City), typeof(bool) });
                if (tryBuildZones != null) h.Patch(tryBuildZones, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TryToBuildInZonesPrefix)));
                MethodInfo getBldgListId = AccessTools.Method(typeof(City), "getBuildingListOfID", new[] { typeof(string) });
                if (getBldgListId != null) h.Patch(getBldgListId, postfix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetBuildingListOfIDPostfix)));
                MethodInfo makeRoads = AccessTools.Method(typeof(CityBehBuild), "makeRoadsBuildings", new[] { typeof(City), typeof(Building) });
                if (makeRoads != null) h.Patch(makeRoads, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(MakeRoadsBuildingsPrefix)));

                // 35. City resource supply sister city allocation
                MethodInfo getRandStockpile = AccessTools.Method(typeof(City), "getRandomStockpile");
                if (getRandStockpile != null) h.Patch(getRandStockpile, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetRandomStockpilePrefix)));
                MethodInfo supplyKingdomCitiesExec = AccessTools.Method(typeof(CityBehSupplyKingdomCities), "execute", new[] { typeof(City) });
                if (supplyKingdomCitiesExec != null) h.Patch(supplyKingdomCitiesExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CityBehSupplyKingdomCitiesExecutePrefix)));

                // 36. Actor target search in city zones
                MethodInfo findNewBldgTgt = AccessTools.Method(typeof(ActorTool), "findNewBuildingTarget", new[] { typeof(Actor), typeof(string), typeof(bool) });
                if (findNewBldgTgt != null) h.Patch(findNewBldgTgt, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(FindNewBuildingTargetPrefix)));
                MethodInfo findNewTgtZones = AccessTools.Method(typeof(ActorTool), "findNewTargetInZones", new[] { typeof(Actor), typeof(string), typeof(ListPool<Building>) });
                if (findNewTgtZones != null) h.Patch(findNewTgtZones, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(FindNewTargetInZonesPrefix)));

                // 37. City soldier attack zone selection
                MethodInfo checkAttackExec = AccessTools.Method(typeof(BehCityActorCheckAttack), "execute", new[] { typeof(Actor) });
                if (checkAttackExec != null) h.Patch(checkAttackExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehCityActorCheckAttackExecutePrefix)));

                // 38. War history pruning equal died time tie-break
                MethodInfo checkDeadObj = AccessTools.Method(typeof(WarManager), "checkDeadObjects");
                if (checkDeadObj != null) h.Patch(checkDeadObj, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CheckDeadObjectsPrefix)));

                // 39. Allied war entry unsorted hashset iteration
                MethodInfo startWar = AccessTools.Method(typeof(DiplomacyManager), "startWar", new[] { typeof(Kingdom), typeof(Kingdom), typeof(WarTypeAsset), typeof(bool) });
                if (startWar != null) h.Patch(startWar, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(StartWarPrefix)));
                MethodInfo startRebellion = AccessTools.Method(typeof(DiplomacyHelpersRebellion), "startRebellion", new[] { typeof(Actor), typeof(Plot), typeof(bool) });
                if (startRebellion != null) h.Patch(startRebellion, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(StartRebellionPrefix)));

                // 40. Tile island swim to safety equal distance region tie-break
                MethodInfo goToStableExec = AccessTools.Method(typeof(BehGoToStablePlace), "execute", new[] { typeof(Actor) });
                if (goToStableExec != null) h.Patch(goToStableExec, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(BehGoToStablePlaceExecutePrefix)));

                // 41. City territory paint drop zone selection over hashset order
                MethodInfo actPaint = AccessTools.Method(typeof(DropsLibrary), "action_paint", new[] { typeof(WorldTile), typeof(string) });
                if (actPaint != null) h.Patch(actPaint, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(ActionPaintPrefix)));

                // 42. Enemy target search PRNG race on parallel job threads
                MethodInfo findEnemyObjTgt = AccessTools.Method(typeof(BaseSimObject), "findEnemyObjectTarget", new[] { typeof(bool) });
                if (findEnemyObjTgt != null) h.Patch(findEnemyObjTgt, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(FindEnemyObjectTargetPrefix)));
                else Log.Warn("lockstep: BaseSimObject.findEnemyObjectTarget not found");
                MethodInfo checkEnemyTgts = AccessTools.Method(typeof(Actor), "checkEnemyTargets");
                if (checkEnemyTgts != null) h.Patch(checkEnemyTgts, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CheckEnemyTargetsPrefix)));
                else Log.Warn("lockstep: Actor.checkEnemyTargets not found");
                _strangeUrgeRef = AccessTools.FieldRefAccess<Actor, bool>("_has_status_strange_urge");
                _hasAttackTargetRef = AccessTools.FieldRefAccess<Actor, bool>("has_attack_target");
                _timeoutTargetsRef = AccessTools.FieldRefAccess<Actor, float>("_timeout_targets");
                _aggressionTargetsRef = AccessTools.FieldRefAccess<Actor, HashSet<long>>("_aggression_targets");
                _aiActorRef = AccessTools.FieldRefAccess<Actor, AiSystemActor>("ai");
                _aiTaskRef = AccessTools.FieldRefAccess<AiSystemActor, BehaviourTaskActor>("task");
                _findEnemiesFromMethod = AccessTools.Method(typeof(EnemiesFinder), "findEnemiesFrom", new[] { typeof(WorldTile), typeof(Kingdom), typeof(int) });
                if (_findEnemiesFromMethod != null) _findEnemiesDel = (FindEnemiesDelegate)Delegate.CreateDelegate(typeof(FindEnemiesDelegate), _findEnemiesFromMethod);
                _checkObjectListMethod = AccessTools.Method(typeof(BaseSimObject), "checkObjectList", new[] { typeof(IEnumerable<BaseSimObject>), typeof(bool), typeof(bool), typeof(bool), typeof(int) });
                if (_checkObjectListMethod != null) _checkObjectListDel = (CheckObjectListDelegate)Delegate.CreateDelegate(typeof(CheckObjectListDelegate), _checkObjectListMethod);

                // 43. Sleeping units status iteration order
                MethodInfo checkSleepUnits = AccessTools.Method(typeof(ActorManager), "checkSleepingUnits");
                if (checkSleepUnits != null) h.Patch(checkSleepUnits, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(CheckSleepingUnitsPrefix)));
                else Log.Warn("lockstep: ActorManager.checkSleepingUnits not found");
                _sleepTimestampRef = AccessTools.FieldRefAccess<ActorManager, double>("_timestamp_sleeping_units");

                // 44. Island docks trade target selection
                MethodInfo dockTradeTgt = AccessTools.Method(typeof(ActorTool), "getDockTradeTarget", new[] { typeof(ListPool<Docks>), typeof(Actor) });
                if (dockTradeTgt != null) h.Patch(dockTradeTgt, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetDockTradeTargetPrefix)));
                else Log.Warn("lockstep: ai.ActorTool.getDockTradeTarget(ListPool<Docks>) not found");
                _docksBuildingRef = AccessTools.FieldRefAccess<Docks, Building>("building");

                // 45. Civ city migration candidate cities
                MethodInfo cityNearby = AccessTools.Method(typeof(BehFindNearbyPotentialCivCityToJoin), "getPotentialCityNearby", new[] { typeof(WorldTile), typeof(Actor) });
                if (cityNearby != null) h.Patch(cityNearby, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetPotentialCityNearbyPrefix)));
                else Log.Warn("lockstep: BehFindNearbyPotentialCivCityToJoin.getPotentialCityNearby not found");

                // 46. Building placement candidate tiles
                MethodInfo tryPlaceBldg = AccessTools.Method(typeof(BuildingHelper), "tryToPlaceBuilding", new[] { typeof(BuildingAsset), typeof(List<WorldTile>) });
                if (tryPlaceBldg != null) h.Patch(tryPlaceBldg, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TryToPlaceBuildingPrefix)));
                else Log.Warn("lockstep: BuildingHelper.tryToPlaceBuilding not found");
                _addBuildingMethod = AccessTools.Method(typeof(BuildingManager), "addBuilding", new[] { typeof(BuildingAsset), typeof(WorldTile), typeof(bool), typeof(bool), typeof(BuildPlacingType) });
                if (_addBuildingMethod != null) _addBuildingFunc = (Func<BuildingManager, BuildingAsset, WorldTile, bool, bool, BuildPlacingType, Building>)Delegate.CreateDelegate(typeof(Func<BuildingManager, BuildingAsset, WorldTile, bool, bool, BuildPlacingType, Building>), _addBuildingMethod);

                // 47. Newborn trait inheritance candidate order
                MethodInfo traitsInheritM = AccessTools.Method(typeof(BabyHelper), "traitsInherit", new[] { typeof(Actor), typeof(Actor), typeof(Actor) });
                if (traitsInheritM != null) h.Patch(traitsInheritM, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(TraitsInheritPrefix)));
                else Log.Warn("lockstep: BabyHelper.traitsInherit not found");

                // 48. Subspecies mutation trait selection
                MethodInfo mutAdd = AccessTools.Method(typeof(SubspeciesTraitLibrary), "getRandomMutationTraitToAdd");
                if (mutAdd != null) h.Patch(mutAdd, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetRandomMutationTraitToAddPrefix)));
                else Log.Warn("lockstep: SubspeciesTraitLibrary.getRandomMutationTraitToAdd not found");
                MethodInfo mutRemove = AccessTools.Method(typeof(SubspeciesTraitLibrary), "getRandomMutationTraitToRemove");
                if (mutRemove != null) h.Patch(mutRemove, prefix: new HarmonyMethod(typeof(SimulationInTick), nameof(GetRandomMutationTraitToRemovePrefix)));
                else Log.Warn("lockstep: SubspeciesTraitLibrary.getRandomMutationTraitToRemove not found");
                _potMutationAddRef = AccessTools.FieldRefAccess<SubspeciesTraitLibrary, List<SubspeciesTrait>>("_pot_mutation_traits_add");
                _potMutationRemoveRef = AccessTools.FieldRefAccess<SubspeciesTraitLibrary, List<SubspeciesTrait>>("_pot_mutation_traits_remove");

                _warManagerEndWarsRef = AccessTools.FieldRefAccess<WarManager, List<War>>("_end_wars");
                _cityStockpilesRef = AccessTools.FieldRefAccess<City, List<Building>>("stockpiles");
                _cityRoadTilesRef = AccessTools.FieldRefAccess<City, List<WorldTile>>("road_tiles_to_build");
                _zoneCalculatorRef = AccessTools.FieldRefAccess<MapBox, ZoneCalculator>("zone_calculator");
                _islandsCalculatorRef = AccessTools.FieldRefAccess<MapBox, IslandsCalculator>("islands_calculator");
                _hasGroundMethod = AccessTools.Method(typeof(IslandsCalculator), "hasGround");
                if (_hasGroundMethod != null) _hasGroundFunc = (Func<IslandsCalculator, bool>)Delegate.CreateDelegate(typeof(Func<IslandsCalculator, bool>), _hasGroundMethod);
                _getRandomIslandGroundMethod = AccessTools.Method(typeof(IslandsCalculator), "getRandomIslandGround", new[] { typeof(bool) });
                if (_getRandomIslandGroundMethod != null) _getRandomIslandGroundFunc = (Func<IslandsCalculator, bool, TileIsland>)Delegate.CreateDelegate(typeof(Func<IslandsCalculator, bool, TileIsland>), _getRandomIslandGroundMethod);
                _canBuildFromMethod = AccessTools.Method(typeof(BuildingManager), "canBuildFrom", new[] { typeof(WorldTile), typeof(BuildingAsset), typeof(City), typeof(BuildPlacingType), typeof(bool) });
                if (_canBuildFromMethod != null) _canBuildFromFunc = (Func<BuildingManager, WorldTile, BuildingAsset, City, BuildPlacingType, bool, bool>)Delegate.CreateDelegate(typeof(Func<BuildingManager, WorldTile, BuildingAsset, City, BuildPlacingType, bool, bool>), _canBuildFromMethod);
                _canBeUpgradedMethod = AccessTools.Method(typeof(Building), "canBeUpgraded");
                if (_canBeUpgradedMethod != null) _canBeUpgradedFunc = (Func<Building, bool>)Delegate.CreateDelegate(typeof(Func<Building, bool>), _canBeUpgradedMethod);
                _isGoodIslandMethod = AccessTools.Method(typeof(TileIsland), "isGoodIslandForActor", new[] { typeof(Actor) });
                if (_isGoodIslandMethod != null) _isGoodIslandFunc = (Func<TileIsland, Actor, bool>)Delegate.CreateDelegate(typeof(Func<TileIsland, Actor, bool>), _isGoodIslandMethod);
                _removeWarObjectMethod = AccessTools.Method(typeof(WarManager), "removeObject", new[] { typeof(War) });
                _buildingTilesRef = AccessTools.FieldRefAccess<Building, List<WorldTile>>("tiles");

                _cityTileRef = AccessTools.FieldRefAccess<City, WorldTile>("_city_tile");
                _targetAttackCityRef = AccessTools.FieldRefAccess<City, City>("target_attack_city");
                _targetAttackZoneRef = AccessTools.FieldRefAccess<City, TileZone>("target_attack_zone");
                _cityCenterRef = AccessTools.FieldRefAccess<City, Vector2>("city_center");
                _preferredSubtypesRef = AccessTools.FieldRefAccess<Culture, List<string>>("_preferred_weapons_craft_subtypes");
                _preferredAssetsRef = AccessTools.FieldRefAccess<Culture, List<EquipmentAsset>>("_preferred_weapons_craft_assets");
                _cityZonesRef = AccessTools.FieldRefAccess<City, List<TileZone>>("zones");
                _removeZoneMethod = AccessTools.Method(typeof(City), "removeZone", new[] { typeof(TileZone) });
                _addZoneMethod = AccessTools.Method(typeof(City), "addZone", new[] { typeof(TileZone) });
                _spawnOnMethod = AccessTools.Method(typeof(Actor), "spawnOn", new[] { typeof(WorldTile), typeof(float) });
                _getBuildingToBuildMethod = AccessTools.Method(typeof(City), "getBuildingToBuild");
                _getBuildingOfTypeMethod = AccessTools.Method(typeof(City), "getBuildingOfType", new[] { typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(TileIsland) });
                _startWarMethod = AccessTools.Method(typeof(DiplomacyManager), "startWar", new[] { typeof(Kingdom), typeof(Kingdom), typeof(WarTypeAsset), typeof(bool) });
                _baseStatsRef = AccessTools.FieldRefAccess<BaseSimObject, BaseStats>("stats");
            }
            catch (Exception e)
            {
                Log.Error("lockstep: SimulationInTick.Install failed: " + e);
            }
        }

        // ==========================================
        // 1. Farm Decay Determinism
        // ==========================================
        private static bool UpdateFarmDecayPrefix()
        {
            if (!LockstepClock.InTick) return true;
            if (_lastUsedWorldIdRef == null || _tempWheatListRef == null) return true;

            if (MapBox.current_world_seed_id != _lastUsedWorldIdRef())
            {
                _lastUsedWorldIdRef() = MapBox.current_world_seed_id;
                _tempWheatListRef().Clear();
            }

            List<WorldTile> tempWheatList = _tempWheatListRef();
            if (tempWheatList.Count == 0)
            {
                if (TopTileLibrary.field != null && TopTileLibrary.field.hashset != null)
                {
                    foreach (WorldTile item in TopTileLibrary.field.hashset)
                    {
                        if (item != null) tempWheatList.Add(item);
                    }
                    tempWheatList.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));
                    tempWheatList.Shuffle();
                }
            }

            for (int i = 0; i < 10; i++)
            {
                if (tempWheatList.Count == 0) break;
                WorldTile worldTile = tempWheatList.Pop();
                if (worldTile == null || !worldTile.Type.farm_field) continue;

                City city = worldTile.zone?.city;
                if (city == null)
                {
                    MapAction.decreaseTile(worldTile, pDamage: false);
                }
                else if (!city.calculated_farm_fields.Contains(worldTile) && !city.calculated_place_for_farms.Contains(worldTile))
                {
                    if (worldTile.hasBuilding() && _buildingAssetRef != null)
                    {
                        BuildingAsset asset = _buildingAssetRef(worldTile.building);
                        if (asset != null && asset.wheat)
                        {
                            _startDestroyBuildingMethod?.Invoke(worldTile.building, null);
                        }
                    }
                    MapAction.decreaseTile(worldTile, pDamage: false);
                }
            }
            return false;
        }

        // ==========================================
        // 2. Boat Passenger Unloading
        // ==========================================
        private static bool UnloadPassengersPrefix(Boat __instance, WorldTile pTile, bool pRandomForce)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (_boatPassengersRef == null) return true;

            HashSet<Actor> passengers = _boatPassengersRef(__instance);
            if (passengers == null || passengers.Count == 0)
            {
                __instance.taxi_target = null;
                return false;
            }

            _boatPassengersPool.Clear();
            foreach (Actor passenger in passengers)
            {
                if (passenger != null && passenger.isAlive())
                {
                    _boatPassengersPool.Add(passenger);
                }
            }
            _boatPassengersPool.Sort((a, b) => a.id.CompareTo(b.id));

            for (int i = 0; i < _boatPassengersPool.Count; i++)
            {
                Actor passenger = _boatPassengersPool[i];
                if (passenger.isAlive())
                {
                    _disembarkToMethod?.Invoke(passenger, new object[] { __instance, pTile });
                    if (pRandomForce)
                    {
                        _applyRandomForceMethod?.Invoke(passenger, new object[] { 1.5f, 2f });
                    }
                }
            }
            passengers.Clear();
            __instance.taxi_target = null;
            return false;
        }

        // ==========================================
        // 3. War Army Sort & Succession
        // ==========================================
        private static bool SortByArmyPrefix(Kingdom p1, Kingdom p2, ref int __result)
        {
            if (p1 == null && p2 == null) { __result = 0; return false; }
            if (p1 == null) { __result = 1; return false; }
            if (p2 == null) { __result = -1; return false; }

            int cmp = p2.countTotalWarriors().CompareTo(p1.countTotalWarriors());
            if (cmp != 0)
            {
                __result = cmp;
                return false;
            }
            __result = p1.id.CompareTo(p2.id);
            return false;
        }

        private static bool TrySelectNewMainAttackerPrefix(War __instance, ref bool __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (__instance.countAttackers() <= 1)
            {
                __result = false;
                return false;
            }
            List<Kingdom> attackers = _warListAttackersRef != null ? _warListAttackersRef(__instance) : null;
            if (attackers == null) return true;

            attackers.Sort((p1, p2) =>
            {
                if (p1 == null && p2 == null) return 0;
                if (p1 == null) return 1;
                if (p2 == null) return -1;
                int cmp = p2.countTotalWarriors().CompareTo(p1.countTotalWarriors());
                if (cmp != 0) return cmp;
                return p1.id.CompareTo(p2.id);
            });

            for (int i = 0; i < attackers.Count; i++)
            {
                Kingdom list_attacker = attackers[i];
                if (list_attacker != null && list_attacker.id != __instance.data.main_attacker)
                {
                    __instance.data.main_attacker = list_attacker.id;
                    __result = true;
                    return false;
                }
            }
            __result = false;
            return false;
        }

        private static bool TrySelectNewMainDefenderPrefix(War __instance, ref bool __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (__instance.countDefenders() <= 1)
            {
                __result = false;
                return false;
            }
            List<Kingdom> defenders = _warListDefendersRef != null ? _warListDefendersRef(__instance) : null;
            if (defenders == null) return true;

            defenders.Sort((p1, p2) =>
            {
                if (p1 == null && p2 == null) return 0;
                if (p1 == null) return 1;
                if (p2 == null) return -1;
                int cmp = p2.countTotalWarriors().CompareTo(p1.countTotalWarriors());
                if (cmp != 0) return cmp;
                return p1.id.CompareTo(p2.id);
            });

            for (int i = 0; i < defenders.Count; i++)
            {
                Kingdom list_defender = defenders[i];
                if (list_defender != null && list_defender.id != __instance.data.main_defender)
                {
                    __instance.data.main_defender = list_defender.id;
                    __result = true;
                    return false;
                }
            }
            __result = false;
            return false;
        }

        // ==========================================
        // 4. City Capture Tie-Breaking
        // ==========================================
        private static bool UpdateCapturePrefix(City __instance, float pElapsed)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (_capturingUnitsRef == null || _timerCaptureRef == null || _captureTicksRef == null || _beingCapturedByRef == null || _cityKingdomRef == null)
                return true;

            if (__instance.last_visual_capture_ticks == 0 && !__instance.isGettingCaptured())
                return false;

            float captureTicks = _captureTicksRef(__instance);
            if ((int)captureTicks != __instance.last_visual_capture_ticks)
            {
                if ((int)captureTicks > __instance.last_visual_capture_ticks)
                    __instance.last_visual_capture_ticks++;
                else
                    __instance.last_visual_capture_ticks--;
            }
            __instance.last_visual_capture_ticks = Mathf.Clamp(__instance.last_visual_capture_ticks, 0, 100);

            float timerCapture = _timerCaptureRef(__instance);
            if (timerCapture > 0f)
            {
                _timerCaptureRef(__instance) = timerCapture - pElapsed;
                return false;
            }
            _timerCaptureRef(__instance) = 0.1f;

            Kingdom cityKingdom = _cityKingdomRef(__instance);
            if (_countBuildingsTypeMethod != null)
            {
                int num = (int)_countBuildingsTypeMethod.Invoke(__instance, new object[] { "type_watch_tower" });
                if (num > 0 && cityKingdom != null)
                {
                    __instance.addCapturePoints(cityKingdom, 10 * num);
                }
            }

            Dictionary<Kingdom, int> capturingUnits = _capturingUnitsRef(__instance);
            Kingdom kingdom = null;
            if (capturingUnits != null)
            {
                foreach (KeyValuePair<Kingdom, int> kvp in capturingUnits)
                {
                    Kingdom key = kvp.Key;
                    if (key == null) continue;
                    if (kingdom == null)
                    {
                        kingdom = key;
                    }
                    else
                    {
                        int countKey = kvp.Value;
                        int countKingdom = capturingUnits.ContainsKey(kingdom) ? capturingUnits[kingdom] : 0;
                        if (countKey > countKingdom)
                        {
                            kingdom = key;
                        }
                        else if (countKey == countKingdom && key.id < kingdom.id)
                        {
                            kingdom = key;
                        }
                    }
                }
            }

            if (kingdom == null)
            {
                captureTicks -= 0.5f;
                _captureTicksRef(__instance) = captureTicks;
                if (captureTicks <= 0f)
                {
                    _clearCaptureMethod?.Invoke(__instance, null);
                }
                return false;
            }

            bool flag = false;
            if (cityKingdom != null && capturingUnits != null && capturingUnits.ContainsKey(cityKingdom) && capturingUnits[cityKingdom] > 0 && __instance.countWarriors() > 0)
            {
                flag = true;
            }

            Kingdom beingCapturedBy = _beingCapturedByRef(__instance);
            if (beingCapturedBy != null && !beingCapturedBy.isAlive())
            {
                _beingCapturedByRef(__instance) = null;
                beingCapturedBy = null;
            }

            bool flag2 = false;
            if (cityKingdom == kingdom)
            {
                flag2 = true;
            }
            if (flag && capturingUnits != null && capturingUnits.Count == 1)
            {
                flag2 = true;
            }

            if (flag2)
            {
                captureTicks -= 1f;
                _captureTicksRef(__instance) = captureTicks;
                if (captureTicks <= 0f)
                {
                    _clearCaptureMethod?.Invoke(__instance, null);
                }
            }
            else
            {
                if (cityKingdom != null && (!kingdom.isEnemy(cityKingdom) || (flag && !(captureTicks < 5f))))
                {
                    return false;
                }
                if (beingCapturedBy == null || beingCapturedBy == kingdom)
                {
                    captureTicks += 1f + 1f * pElapsed;
                    _captureTicksRef(__instance) = captureTicks;
                    _beingCapturedByRef(__instance) = kingdom;
                    if (captureTicks >= 100f)
                    {
                        _finishCaptureMethod?.Invoke(__instance, new object[] { kingdom });
                    }
                }
                else if (kingdom.isEnemy(beingCapturedBy))
                {
                    captureTicks -= 0.5f;
                    _captureTicksRef(__instance) = captureTicks;
                    if (captureTicks <= 0f)
                    {
                        _clearCaptureMethod?.Invoke(__instance, null);
                    }
                }
                else
                {
                    captureTicks += 1f + 1f * pElapsed;
                    _captureTicksRef(__instance) = captureTicks;
                    if (captureTicks >= 100f)
                    {
                        _finishCaptureMethod?.Invoke(__instance, new object[] { beingCapturedBy });
                    }
                }
            }
            return false;
        }

        // ==========================================
        // 5. Toolbox & Dragon Closest Actor Determinism
        // ==========================================
        private static bool GetClosestActorHashSetPrefix(HashSet<Actor> pCollection, WorldTile pTile, ref Actor __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (pCollection == null || pCollection.Count == 0 || pTile == null)
            {
                __result = null;
                return false;
            }

            Actor result = null;
            int minSqDist = int.MaxValue;
            Vector2Int pos = pTile.pos;

            foreach (Actor item in pCollection)
            {
                if (item != null && !item.isRekt() && item.current_tile != null)
                {
                    Vector2Int pos2 = item.current_tile.pos;
                    int dist = Toolbox.SquaredDist(pos.x, pos.y, pos2.x, pos2.y);
                    if (dist < minSqDist)
                    {
                        minSqDist = dist;
                        result = item;
                    }
                    else if (dist == minSqDist && (result == null || item.id < result.id))
                    {
                        result = item;
                    }
                }
            }
            __result = result;
            return false;
        }

        private static bool GetClosestActorListPrefix(List<Actor> pCollection, WorldTile pTile, ref Actor __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (pCollection == null || pCollection.Count == 0 || pTile == null)
            {
                __result = null;
                return false;
            }

            Actor result = null;
            int minSqDist = int.MaxValue;
            Vector2Int pos = pTile.pos;
            int count = pCollection.Count;

            for (int i = 0; i < count; i++)
            {
                Actor item = pCollection[i];
                if (item != null && !item.isRekt() && item.current_tile != null)
                {
                    Vector2Int pos2 = item.current_tile.pos;
                    int dist = Toolbox.SquaredDist(pos.x, pos.y, pos2.x, pos2.y);
                    if (dist < minSqDist)
                    {
                        minSqDist = dist;
                        result = item;
                    }
                    else if (dist == minSqDist && (result == null || item.id < result.id))
                    {
                        result = item;
                    }
                }
            }
            __result = result;
            return false;
        }

        private static bool BehDragonCheckAttackTilePrefix(BehDragonCheckAttackTile __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (_behDragonRef == null || _behTileTargetRef == null || _dragonAggroTargetsRef == null) return true;

            Dragon dragon = _behDragonRef(__instance);
            if (dragon == null)
            {
                __result = BehResult.Continue;
                return false;
            }

            HashSet<Actor> aggroTargets = _dragonAggroTargetsRef(dragon);
            if (aggroTargets == null || aggroTargets.Count == 0)
            {
                __result = BehResult.Continue;
                return false;
            }

            Actor closestActor = Toolbox.getClosestActor(aggroTargets, pActor.current_tile);
            if (closestActor != null && closestActor.isAlive() && closestActor.current_tile != null)
            {
                WorldTile landTile = (WorldTile)_randomTileWithinLandAttackRangeMethod?.Invoke(dragon, new object[] { closestActor.current_tile });
                _behTileTargetRef(pActor) = landTile;

                WorldTile lastLanded = _dragonLastLandedRef != null ? _dragonLastLandedRef(dragon) : null;
                bool inLandRange = (bool)(_landAttackRangeMethod?.Invoke(dragon, new object[] { closestActor.current_tile }) ?? false);
                bool canLand = (bool)(_canLandMethod?.Invoke(null, new object[] { pActor }) ?? false);

                if (pActor.current_tile != lastLanded && inLandRange && canLand)
                {
                    __result = __instance.forceTask(pActor, "dragon_land");
                    return false;
                }
            }

            bool isFlying = (bool)(_isFlyingMethod?.Invoke(pActor, null) ?? false);
            if (isFlying)
            {
                _dragonTargetsPool.Clear();
                foreach (Actor t in aggroTargets)
                {
                    if (t != null && t.isAlive()) _dragonTargetsPool.Add(t);
                }
                _dragonTargetsPool.Sort((a, b) => a.id.CompareTo(b.id));

                for (int i = 0; i < _dragonTargetsPool.Count; i++)
                {
                    Actor aggroTarget = _dragonTargetsPool[i];
                    if (aggroTarget.current_tile != null)
                    {
                        bool withinSlide = (bool)(_targetWithinSlideMethod?.Invoke(dragon, new object[] { aggroTarget.current_tile }) ?? false);
                        if (withinSlide)
                        {
                            __result = __instance.forceTask(pActor, "dragon_slide");
                            return false;
                        }
                    }
                }
            }

            __result = BehResult.Continue;
            return false;
        }

        // ==========================================
        // 6. Item Crafting Asset Selection
        // ==========================================
        private static bool HasEnoughResourcesToCraft(Actor pActor, EquipmentAsset pAsset, City pCity)
        {
            if (pActor == null || pAsset == null || pCity == null) return false;
            int totalCost = pAsset.get_total_cost;
            if (!pActor.hasEnoughMoney(totalCost)) return false;
            if (pAsset.cost_resource_id_1 != "none" && pAsset.cost_resource_1 > pCity.getResourcesAmount(pAsset.cost_resource_id_1))
                return false;
            if (pAsset.cost_resource_id_2 != "none" && pAsset.cost_resource_2 > pCity.getResourcesAmount(pAsset.cost_resource_id_2))
                return false;
            return true;
        }

        private static bool GetItemAssetToCraftPrefix(Actor pActor, List<EquipmentAsset> pItemList, City pCity, int pCurrentItemValue, bool pShuffle, ref EquipmentAsset __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (pItemList == null || pItemList.Count == 0)
            {
                __result = null;
                return false;
            }

            List<EquipmentAsset> listToUse = pItemList;
            if (pShuffle)
            {
                _craftingItemsPool.Clear();
                _craftingItemsPool.AddRange(pItemList);
                _craftingItemsPool.Shuffle();
                listToUse = _craftingItemsPool;
            }

            for (int num = listToUse.Count - 1; num >= 0; num--)
            {
                EquipmentAsset equipmentAsset = listToUse[num];
                if (equipmentAsset != null && equipmentAsset.equipment_value > pCurrentItemValue && HasEnoughResourcesToCraft(pActor, equipmentAsset, pCity))
                {
                    __result = equipmentAsset;
                    return false;
                }
            }
            __result = null;
            return false;
        }

        // ==========================================
        // 7. Shattered Crown Event
        // ==========================================
        private static bool CheckShatteredCrownEventPrefix(Kingdom pMainKingdom, Actor pMainKing, Clan pRoyalClan)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (!WorldLawLibrary.world_law_rebellions.isEnabled() || pRoyalClan == null)
            {
                return false;
            }

            _crownActorsPool.Clear();
            _crownCitiesPool.Clear();

            if (pRoyalClan.units != null)
            {
                foreach (Actor unit in pRoyalClan.units)
                {
                    if (unit != null && !unit.isRekt() && unit != pMainKing && !unit.isKing())
                    {
                        _crownActorsPool.Add(unit);
                    }
                }
            }

            IEnumerable<City> cities = pMainKingdom.getCities();
            if (cities != null)
            {
                foreach (City city3 in cities)
                {
                    if (city3 != null && city3 != pMainKingdom.capital)
                    {
                        _crownCitiesPool.Add(city3);
                    }
                }
            }

            if (_crownActorsPool.Count == 0 || _crownCitiesPool.Count == 0)
            {
                return false;
            }

            // Deterministic pre-sort before shuffling
            _crownActorsPool.Sort((a, b) => a.id.CompareTo(b.id));
            _crownCitiesPool.Sort((a, b) => a.id.CompareTo(b.id));

            _crownDict.Clear();
            _crownKingdomsPool.Clear();
            _crownDict[pMainKingdom.id] = pMainKingdom.countCities();

            _crownCitiesPool.Shuffle();
            _crownActorsPool.Shuffle();

            _crownCitiesPool.Sort((a, b) =>
            {
                int num = 0;
                for (int i = 0; i < _crownActorsPool.Count; i++) if (_crownActorsPool[i].city == a) num++;
                int value = 0;
                for (int i = 0; i < _crownActorsPool.Count; i++) if (_crownActorsPool[i].city == b) value++;
                int cmp = num.CompareTo(value);
                if (cmp != 0) return cmp;
                return a.id.CompareTo(b.id);
            });

            bool flag = false;
            while (_crownCitiesPool.Count > 0 && _crownActorsPool.Count > 0)
            {
                City city = _crownCitiesPool.Pop();
                Actor actor = null;
                if (city.hasLeader() && _crownActorsPool.Contains(city.leader))
                {
                    actor = city.leader;
                }
                else
                {
                    for (int i = 0; i < _crownActorsPool.Count; i++)
                    {
                        Actor current3 = _crownActorsPool[i];
                        if (current3.city == city)
                        {
                            actor = current3;
                            break;
                        }
                    }
                }
                if (actor == null)
                {
                    actor = _crownActorsPool[_crownActorsPool.Count - 1];
                }
                _crownActorsPool.Remove(actor);

                if (actor.isCityLeader())
                {
                    actor.city.removeLeader();
                }

                Kingdom kingdom = (Kingdom)_makeOwnKingdomMethod?.Invoke(city, new object[] { actor, false, true });
                if (kingdom != null)
                {
                    _crownKingdomsPool.Add(kingdom);
                    _crownDict[kingdom.id] = 1;
                    _crownDict[pMainKingdom.id]--;
                }
                actor.stopBeingWarrior();
                actor.joinCity(city);
                flag = true;
            }

            while (_crownCitiesPool.Count > 0 && _crownKingdomsPool.Count > 0)
            {
                City city2 = _crownCitiesPool.Pop();
                Kingdom random = _crownKingdomsPool.GetRandom();
                if (random != null && _crownDict[random.id] < _crownDict[pMainKingdom.id])
                {
                    _crownDict[random.id]++;
                    _crownDict[pMainKingdom.id]--;
                    city2.joinAnotherKingdom(random, pCaptured: false, pRebellion: true);
                }
            }

            if (flag)
            {
                if (pMainKingdom.hasAlliance())
                {
                    pMainKingdom.getAlliance()?.leave(pMainKingdom);
                }
                WorldLog.logShatteredCrown(pMainKingdom);
            }
            return false;
        }

        // ==========================================
        // 8. Alliance Recalculate Determinism
        // ==========================================
        private static bool AllianceRecalculatePrefix(Alliance __instance)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (__instance.kingdoms_list == null) return true;

            __instance.kingdoms_list.Clear();
            if (__instance.kingdoms_hashset != null)
            {
                __instance.kingdoms_list.AddRange(__instance.kingdoms_hashset);
                if (__instance.kingdoms_list.Count > 1)
                {
                    __instance.kingdoms_list.Sort((a, b) => a.id.CompareTo(b.id));
                }
            }
            __instance.mergeWars();
            return false;
        }

        // ==========================================
        // 9. Controllable Unit Main Selection
        // ==========================================
        private static bool TrySelectNewMainPrefix()
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            HashSet<Actor> units = _controllableUnitsRef != null ? _controllableUnitsRef() : null;
            if (units == null || units.Count == 0)
            {
                ControllableUnit.clear();
                return false;
            }

            Actor lowest = null;
            foreach (Actor a in units)
            {
                if (a != null && a.isAlive())
                {
                    if (lowest == null || a.id < lowest.id)
                    {
                        lowest = a;
                    }
                }
            }

            if (lowest != null && _unitMainRef != null)
            {
                _unitMainRef() = lowest;
            }
            else
            {
                ControllableUnit.clear();
            }
            return false;
        }

        // ==========================================
        // 10. ListSorters Determinism & Clan Chief Succession
        // ==========================================
        private static bool SortUnitsSortedByAgeAndTraitsPrefix(ListPool<Actor> pUnits, Culture pCulture)
        {
            if (pUnits == null || pUnits.Count <= 1) return false;
            pUnits.Sort((a, b) => CompareUnitsByAgeAndTraits(a, b, pCulture));
            return false;
        }

        private static int CompareUnitsByAgeAndTraits(Actor pActor1, Actor pActor2, Culture pCulture)
        {
            if (ReferenceEquals(pActor1, pActor2)) return 0;
            if (pActor1 == null) return 1;
            if (pActor2 == null) return -1;

            ActorData d1 = _actorDataRef != null ? _actorDataRef(pActor1) : null;
            ActorData d2 = _actorDataRef != null ? _actorDataRef(pActor2) : null;
            BaseStats st1 = _actorStatsRef != null ? _actorStatsRef(pActor1) : null;
            BaseStats st2 = _actorStatsRef != null ? _actorStatsRef(pActor2) : null;

            if (pCulture != null)
            {
                bool patriarchy = pCulture.hasTrait("patriarchy");
                bool matriarchy = pCulture.hasTrait("matriarchy");
                if (patriarchy || matriarchy)
                {
                    ActorSex topGender = patriarchy ? ActorSex.Male : ActorSex.Female;
                    if (d1 != null && d2 != null && d1.sex != d2.sex)
                    {
                        return d1.sex == topGender ? -1 : 1;
                    }
                }

                if (pCulture.hasTrait("diplomatic_ascension"))
                {
                    float s1 = st1 != null ? st1.get("diplomacy") : 0f;
                    float s2 = st2 != null ? st2.get("diplomacy") : 0f;
                    int cmpStat = -s1.CompareTo(s2);
                    if (cmpStat != 0) return cmpStat;
                }
                else if (pCulture.hasTrait("warriors_ascension"))
                {
                    float s1 = st1 != null ? st1.get("warfare") : 0f;
                    float s2 = st2 != null ? st2.get("warfare") : 0f;
                    int cmpStat = -s1.CompareTo(s2);
                    if (cmpStat != 0) return cmpStat;
                }
                else if (pCulture.hasTrait("fames_crown"))
                {
                    int r1 = d1 != null ? d1.renown : 0;
                    int r2 = d2 != null ? d2.renown : 0;
                    int cmpRenown = -r1.CompareTo(r2);
                    if (cmpRenown != 0) return cmpRenown;
                }
                else if (pCulture.hasTrait("golden_rule"))
                {
                    int m1 = d1 != null ? d1.money : 0;
                    int m2 = d2 != null ? d2.money : 0;
                    int cmpMoney = -m1.CompareTo(m2);
                    if (cmpMoney != 0) return cmpMoney;
                }

                if (pCulture.hasTrait("ultimogeniture"))
                {
                    double t1 = d1 != null ? d1.created_time : 0;
                    double t2 = d2 != null ? d2.created_time : 0;
                    int cmpAge = t2.CompareTo(t1);
                    if (cmpAge != 0) return cmpAge;
                }
                else
                {
                    double t1 = d1 != null ? d1.created_time : 0;
                    double t2 = d2 != null ? d2.created_time : 0;
                    int cmpAge = -t2.CompareTo(t1);
                    if (cmpAge != 0) return cmpAge;
                }
            }
            else
            {
                double t1 = d1 != null ? d1.created_time : 0;
                double t2 = d2 != null ? d2.created_time : 0;
                int cmpAge = -t2.CompareTo(t1);
                if (cmpAge != 0) return cmpAge;
            }

            return pActor1.id.CompareTo(pActor2.id);
        }

        private static bool SortUnitByAgeOldFirstPrefix(Actor pActor1, Actor pActor2, ref int __result)
        {
            if (ReferenceEquals(pActor1, pActor2)) { __result = 0; return false; }
            if (pActor1 == null) { __result = 1; return false; }
            if (pActor2 == null) { __result = -1; return false; }
            ActorData d1 = _actorDataRef != null ? _actorDataRef(pActor1) : null;
            ActorData d2 = _actorDataRef != null ? _actorDataRef(pActor2) : null;
            double t1 = d1 != null ? d1.created_time : 0;
            double t2 = d2 != null ? d2.created_time : 0;
            int cmp = -t2.CompareTo(t1);
            if (cmp == 0) cmp = pActor1.id.CompareTo(pActor2.id);
            __result = cmp;
            return false;
        }

        private static bool SortUnitByAgeYoungFirstPrefix(Actor pActor1, Actor pActor2, ref int __result)
        {
            if (ReferenceEquals(pActor1, pActor2)) { __result = 0; return false; }
            if (pActor1 == null) { __result = 1; return false; }
            if (pActor2 == null) { __result = -1; return false; }
            ActorData d1 = _actorDataRef != null ? _actorDataRef(pActor1) : null;
            ActorData d2 = _actorDataRef != null ? _actorDataRef(pActor2) : null;
            double t1 = d1 != null ? d1.created_time : 0;
            double t2 = d2 != null ? d2.created_time : 0;
            int cmp = t2.CompareTo(t1);
            if (cmp == 0) cmp = pActor1.id.CompareTo(pActor2.id);
            __result = cmp;
            return false;
        }

        private static bool SortUnitByRenownPrefix(Actor pActor1, Actor pActor2, ref int __result)
        {
            if (ReferenceEquals(pActor1, pActor2)) { __result = 0; return false; }
            if (pActor1 == null) { __result = 1; return false; }
            if (pActor2 == null) { __result = -1; return false; }
            ActorData d1 = _actorDataRef != null ? _actorDataRef(pActor1) : null;
            ActorData d2 = _actorDataRef != null ? _actorDataRef(pActor2) : null;
            int r1 = d1 != null ? d1.renown : 0;
            int r2 = d2 != null ? d2.renown : 0;
            int cmp = -r1.CompareTo(r2);
            if (cmp == 0) cmp = pActor1.id.CompareTo(pActor2.id);
            __result = cmp;
            return false;
        }

        private static bool SortUnitByGoldCoinsPrefix(Actor pActor1, Actor pActor2, ref int __result)
        {
            if (ReferenceEquals(pActor1, pActor2)) { __result = 0; return false; }
            if (pActor1 == null) { __result = 1; return false; }
            if (pActor2 == null) { __result = -1; return false; }
            ActorData d1 = _actorDataRef != null ? _actorDataRef(pActor1) : null;
            ActorData d2 = _actorDataRef != null ? _actorDataRef(pActor2) : null;
            int m1 = d1 != null ? d1.money : 0;
            int m2 = d2 != null ? d2.money : 0;
            int cmp = -m1.CompareTo(m2);
            if (cmp == 0) cmp = pActor1.id.CompareTo(pActor2.id);
            __result = cmp;
            return false;
        }

        private static bool SortUnitByStatsPrefix(Actor pActor1, Actor pActor2, string pStatId, ref int __result)
        {
            if (ReferenceEquals(pActor1, pActor2)) { __result = 0; return false; }
            if (pActor1 == null) { __result = 1; return false; }
            if (pActor2 == null) { __result = -1; return false; }
            BaseStats st1 = _actorStatsRef != null ? _actorStatsRef(pActor1) : null;
            BaseStats st2 = _actorStatsRef != null ? _actorStatsRef(pActor2) : null;
            float s1 = st1 != null ? st1.get(pStatId) : 0f;
            float s2 = st2 != null ? st2.get(pStatId) : 0f;
            int cmp = -s1.CompareTo(s2);
            if (cmp == 0) cmp = pActor1.id.CompareTo(pActor2.id);
            __result = cmp;
            return false;
        }

        private static bool SortUnitByKillsPrefix(Actor pActor1, Actor pActor2, ref int __result)
        {
            if (ReferenceEquals(pActor1, pActor2)) { __result = 0; return false; }
            if (pActor1 == null) { __result = 1; return false; }
            if (pActor2 == null) { __result = -1; return false; }
            ActorData d1 = _actorDataRef != null ? _actorDataRef(pActor1) : null;
            ActorData d2 = _actorDataRef != null ? _actorDataRef(pActor2) : null;
            int k1 = d1 != null ? d1.kills : 0;
            int k2 = d2 != null ? d2.kills : 0;
            int cmp = -k1.CompareTo(k2);
            if (cmp == 0) cmp = pActor1.id.CompareTo(pActor2.id);
            __result = cmp;
            return false;
        }

        private static bool SortUnitByGenderPrefix(Actor pActor1, Actor pActor2, ActorSex pTopGender, ref int __result)
        {
            if (ReferenceEquals(pActor1, pActor2)) { __result = 0; return false; }
            if (pActor1 == null) { __result = 1; return false; }
            if (pActor2 == null) { __result = -1; return false; }
            ActorData d1 = _actorDataRef != null ? _actorDataRef(pActor1) : null;
            ActorData d2 = _actorDataRef != null ? _actorDataRef(pActor2) : null;
            ActorSex s1 = d1 != null ? d1.sex : ActorSex.None;
            ActorSex s2 = d2 != null ? d2.sex : ActorSex.None;
            if (s1 == s2)
            {
                __result = pActor1.id.CompareTo(pActor2.id);
                return false;
            }
            __result = s1 == pTopGender ? -1 : 1;
            return false;
        }

        // ==========================================
        // 11. Kingdom Succession From Leaders Determinism
        // ==========================================
        private static bool GetKingFromLeadersPrefix(Kingdom pKingdom, ref Actor __result)
        {
            if (pKingdom == null) { __result = null; return false; }
            Actor actor = null;
            using ListPool<Actor> listPool = new ListPool<Actor>();
            foreach (City city in pKingdom.getCities())
            {
                if (city != null && city.hasLeader() && city.leader != null)
                {
                    listPool.Add(city.leader);
                }
            }
            if (listPool.Count == 0)
            {
                __result = null;
                return false;
            }

            if (listPool.Count > 1)
            {
                listPool.Sort((a, b) => a.id.CompareTo(b.id));
            }

            int num = 0;
            foreach (ref Actor item in listPool)
            {
                Actor current2 = item;
                int num2 = ai.ActorTool.attributeDice(current2);
                if (actor == null || num2 > num || (num2 == num && current2.id < actor.id))
                {
                    num = num2;
                    actor = current2;
                }
            }
            __result = actor;
            return false;
        }

        // ==========================================
        // 12. City Leader Appointment Determinism
        // ==========================================
        private static bool CheckFindLeaderPrefix(City pCity)
        {
            if (pCity == null || pCity.hasLeader() || pCity.isGettingCaptured())
            {
                return false;
            }
            Actor actor = TryGetClanLeaderDeterministic(pCity);
            if (actor != null)
            {
                if (actor.city != pCity)
                {
                    actor.stopBeingWarrior();
                }
                actor.joinCity(pCity);
                pCity.setLeader(actor, pNew: true);
                return false;
            }
            int num = 0;
            using ListPool<Actor> eligibleCitizens = new ListPool<Actor>();
            if (pCity.units != null)
            {
                foreach (Actor unit in pCity.units)
                {
                    if (unit == null || unit.isKing() || unit.isCityLeader()) continue;
                    if (unit.is_profession_citizen)
                    {
                        eligibleCitizens.Add(unit);
                    }
                }
            }
            if (eligibleCitizens.Count > 1)
            {
                eligibleCitizens.Sort((a, b) => a.id.CompareTo(b.id));
            }
            foreach (ref Actor item in eligibleCitizens)
            {
                Actor unit = item;
                int num2 = 1;
                if (unit.isFavorite())
                {
                    num2 += 2;
                }
                int num3 = ai.ActorTool.attributeDice(unit, num2);
                if (actor == null || num3 > num || (num3 == num && unit.id < actor.id))
                {
                    actor = unit;
                    num = num3;
                }
            }
            if (actor != null)
            {
                pCity.setLeader(actor, pNew: true);
            }
            return false;
        }

        private static bool TryGetClanLeaderPrefix(City pCity, ref Actor __result)
        {
            __result = TryGetClanLeaderDeterministic(pCity);
            return false;
        }

        private static Actor TryGetClanLeaderDeterministic(City pCity)
        {
            Kingdom kingdom = pCity != null && _cityKingdomRef != null ? _cityKingdomRef(pCity) : null;
            if (kingdom == null) return null;
            Clan clan = null;
            if (kingdom.data.royal_clan_id.hasValue())
            {
                clan = World.world.clans.get(kingdom.data.royal_clan_id);
            }
            using ListPool<Actor> listPool = new ListPool<Actor>();
            using ListPool<Actor> listPool2 = new ListPool<Actor>();
            foreach (City city in kingdom.getCities())
            {
                if (city == null) continue;
                foreach (Actor unit in city.getUnits())
                {
                    if (unit != null && unit.isUnitFitToRule() && !unit.isKing() && !unit.isCityLeader() && unit.hasClan())
                    {
                        if (clan != null && unit.clan == clan)
                        {
                            listPool.Add(unit);
                        }
                        else
                        {
                            listPool2.Add(unit);
                        }
                    }
                }
            }
            if (listPool.Count > 1) listPool.Sort((a, b) => a.id.CompareTo(b.id));
            if (listPool2.Count > 1) listPool2.Sort((a, b) => a.id.CompareTo(b.id));

            if (listPool.Count > 0)
            {
                if (pCity.hasCulture())
                {
                    return ListSorters.getUnitSortedByAgeAndTraits(listPool, pCity.culture);
                }
                listPool.Sort(ListSorters.sortUnitByAgeOldFirst);
                return listPool[0];
            }
            if (listPool2.Count > 0)
            {
                if (pCity.hasCulture())
                {
                    return ListSorters.getUnitSortedByAgeAndTraits(listPool2, pCity.culture);
                }
                listPool2.Sort(ListSorters.sortUnitByAgeOldFirst);
                return listPool2[0];
            }
            return null;
        }

        // ==========================================
        // 13. Kingdom Chaos and Fracture Determinism
        // ==========================================
        private static bool CheckKingdomChaosPrefix(Kingdom pMainKingdom)
        {
            if (pMainKingdom == null) return false;
            bool flag = false;
            using ListPool<City> listPool = new ListPool<City>(pMainKingdom.countCities());
            foreach (City city in pMainKingdom.getCities())
            {
                if (city != null && city != pMainKingdom.capital && city.hasLeader())
                {
                    listPool.Add(city);
                }
            }
            if (listPool.Count == 0)
            {
                return false;
            }
            if (listPool.Count > 1)
            {
                listPool.Sort((a, b) => a.id.CompareTo(b.id));
            }

            foreach (City item in listPool.LoopRandom())
            {
                Actor leader = item.leader;
                if (leader != null && leader.isAlive())
                {
                    _makeOwnKingdomMethod?.Invoke(item, new object[] { leader, false, true });
                    flag = true;
                }
            }
            if (flag)
            {
                if (pMainKingdom.hasAlliance())
                {
                    pMainKingdom.getAlliance()?.leave(pMainKingdom);
                }
                WorldLog.logFracturedKingdom(pMainKingdom);
            }
            return false;
        }

        // ==========================================
        // 14. Aligned City Rebellion Defection Determinism
        // ==========================================
        private static bool CheckMoreAlignedCitiesPrefix(Kingdom pNewKingdom, Kingdom pOldKingdom, ListPool<City> pNewCities, bool pCheckForHappiness, ref bool __result)
        {
            using ListPool<City> listPool = new ListPool<City>(World.world.cities.Count);
            AddNeighbourCitiesDeterministic(listPool, pNewCities);
            if (listPool.Count == 0 && pOldKingdom != null)
            {
                listPool.AddRange(pOldKingdom.getCities());
            }
            if (listPool.Count == 0)
            {
                __result = false;
                return false;
            }
            if (listPool.Count > 1)
            {
                listPool.Sort((a, b) => a.id.CompareTo(b.id));
            }

            foreach (City item in listPool.LoopRandom())
            {
                Kingdom itemKingdom = item != null && _cityKingdomRef != null ? _cityKingdomRef(item) : null;
                if (item != null && itemKingdom == pOldKingdom && !item.isCapitalCity() && (!pCheckForHappiness || !item.isHappy()) && !Randy.randomBool())
                {
                    item.joinAnotherKingdom(pNewKingdom, pCaptured: false, pRebellion: true);
                    __result = true;
                    return false;
                }
            }
            __result = true;
            return false;
        }

        private static void AddNeighbourCitiesDeterministic(ListPool<City> pTempCitiesToCheck, ListPool<City> pRebelledCities)
        {
            if (pRebelledCities == null) return;
            for (int i = 0; i < pRebelledCities.Count; i++)
            {
                pRebelledCities[i]?.recalculateNeighbourCities();
            }
            for (int i = 0; i < pRebelledCities.Count; i++)
            {
                City c = pRebelledCities[i];
                HashSet<City> neighbours = c != null && _cityNeighboursCitiesRef != null ? _cityNeighboursCitiesRef(c) : null;
                if (neighbours != null)
                {
                    pTempCitiesToCheck.AddRange(neighbours);
                }
            }
        }

        // ==========================================
        // 15. Freeze Waves Determinism & Static Reset
        // ==========================================
        private static bool UpdateTileFreezeWavesPrefix()
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            List<WorldTile> nextWave = _freezeNextWaveField?.GetValue(null) as List<WorldTile>;
            List<WorldTile> currentWave = _freezeCurrentWaveField?.GetValue(null) as List<WorldTile>;
            if (nextWave == null || currentWave == null) return true;

            int waveNumber = (int)(_freezeWaveNumberField?.GetValue(null) ?? 0);

            if (waveNumber == 60)
            {
                nextWave.Clear();
                for (int i = 0; i < currentWave.Count; i++)
                {
                    WorldTile worldTile = currentWave[i];
                    if (worldTile != null && worldTile.canBeFrozen() && worldTile.heat <= 0)
                    {
                        nextWave.Add(worldTile);
                        if (nextWave.Count > 20) break;
                    }
                }
                if (nextWave.Count > 1)
                {
                    nextWave.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));
                }
                nextWave.Shuffle();
                waveNumber = 0;
            }

            currentWave.Clear();
            if (nextWave.Count == 0)
            {
                int num = 3 + Randy.randomInt(0, 3);
                while (num-- > 0)
                {
                    MapChunkManager mcm = _mapChunkManagerRef != null ? _mapChunkManagerRef(World.world) : null;
                    MapChunk random = mcm != null && mcm.chunks != null ? mcm.chunks.GetRandom() : null;
                    if (random != null && random.tiles != null)
                    {
                        int num2 = 0;
                        foreach (WorldTile item in random.tiles.LoopRandom())
                        {
                            if (item != null && item.canBeFrozen() && item.heat <= 0)
                            {
                                currentWave.Add(item);
                                num2++;
                                if (num2 > 5) break;
                            }
                        }
                    }
                }
            }
            else
            {
                currentWave = nextWave;
                nextWave = new List<WorldTile>();
                _freezeNextWaveField.SetValue(null, nextWave);
            }

            if (currentWave.Count > 1)
            {
                currentWave.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));
            }

            for (int j = 0; j < currentWave.Count; j++)
            {
                WorldTile worldTile2 = currentWave[j];
                if (worldTile2 != null && worldTile2.canBeFrozen() && (waveNumber <= 3 || !Randy.randomChance(0.7f)) && worldTile2.freeze(5))
                {
                    if (worldTile2.neighboursAll != null)
                    {
                        nextWave.AddRange(worldTile2.neighboursAll);
                    }
                }
            }

            waveNumber++;
            _freezeWaveNumberField.SetValue(null, waveNumber);
            _freezeCurrentWaveField.SetValue(null, currentWave);
            return false;
        }

        // ==========================================
        // 16. Lover Selection Determinism
        // ==========================================
        private static bool BehFindLoverExecutePrefix(Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (pActor == null) { __result = BehResult.Stop; return false; }
            if (pActor.hasLover()) { __result = BehResult.Stop; return false; }

            Actor targetLover = null;
            _loverCandidatesPool.Clear();
            if (pActor.current_tile != null)
            {
                IEnumerable<Actor> chunkUnits = Finder.getUnitsFromChunk(pActor.current_tile, 1);
                if (chunkUnits != null)
                {
                    foreach (Actor u in chunkUnits)
                    {
                        if (CheckIfPossibleLoverDeterministic(pActor, u))
                        {
                            _loverCandidatesPool.Add(u);
                        }
                    }
                }
            }

            if (_loverCandidatesPool.Count > 0)
            {
                Vector2 pos = pActor.current_position;
                _loverCandidatesPool.Sort((a, b) =>
                {
                    float da = (a.current_position.x - pos.x) * (a.current_position.x - pos.x) + (a.current_position.y - pos.y) * (a.current_position.y - pos.y);
                    float db = (b.current_position.x - pos.x) * (b.current_position.x - pos.x) + (b.current_position.y - pos.y) * (b.current_position.y - pos.y);
                    int cmp = da.CompareTo(db);
                    return cmp != 0 ? cmp : a.id.CompareTo(b.id);
                });
                targetLover = _loverCandidatesPool[0];
            }

            if (targetLover == null && pActor.hasCity() && pActor.city != null && pActor.city.units != null)
            {
                _loverCandidatesPool.Clear();
                foreach (Actor u in pActor.city.getUnits())
                {
                    if (CheckIfPossibleLoverDeterministic(pActor, u) && u.inOwnCityBorders())
                    {
                        _loverCandidatesPool.Add(u);
                    }
                }
                if (_loverCandidatesPool.Count > 0)
                {
                    _loverCandidatesPool.Sort((a, b) => a.id.CompareTo(b.id));
                    targetLover = _loverCandidatesPool.GetRandom();
                }
            }

            if (targetLover != null)
            {
                pActor.becomeLoversWith(targetLover);
            }
            __result = BehResult.Continue;
            return false;
        }

        private static bool CheckIfPossibleLoverDeterministic(Actor pActor, Actor pTarget)
        {
            if (pTarget == null || pTarget == pActor) return false;
            if (!pTarget.hasSubspecies()) return false;
            if (!pTarget.isAlive()) return false;
            if (!pTarget.canFallInLoveWith(pActor)) return false;
            return true;
        }

        // ==========================================
        // 17. Family House Eviction & Assignment Determinism
        // ==========================================
        private static bool BehFindHouseExecutePrefix(Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.Active && !LockstepClock.InTick) return true;
            if (pActor == null || pActor.city == null) { __result = BehResult.Stop; return false; }
            if (pActor.hasHouse()) { __result = BehResult.Stop; return false; }

            Building building = null;
            if (pActor.city.buildings != null)
            {
                for (int i = 0; i < pActor.city.buildings.Count; i++)
                {
                    Building b = pActor.city.buildings[i];
                    if (b != null && !b.isUnderConstruction() && b.hasResidentSlots())
                    {
                        building = b;
                        break;
                    }
                }
            }

            if (building == null && pActor.hasFamily() && pActor.family != null && pActor.family.units != null)
            {
                Family family = pActor.family;
                _familyUnitsPool.Clear();
                foreach (Actor item in family.units)
                {
                    if (item != null && item != pActor && item.hasHouse() && item.city == pActor.city)
                    {
                        _familyUnitsPool.Add(item);
                    }
                }

                if (_familyUnitsPool.Count > 0)
                {
                    _familyUnitsPool.Sort((a, b) => a.id.CompareTo(b.id));
                    int num = 0;
                    foreach (Actor item in _familyUnitsPool.LoopRandom())
                    {
                        if (++num > 5) break;
                        Building b = CheckBuildingDeterministic(item.home_building, family);
                        if (b != null)
                        {
                            building = b;
                            break;
                        }
                    }
                }
            }

            if (building == null)
            {
                __result = BehResult.Stop;
                return false;
            }

            pActor.setHomeBuilding(building);
            BuildingAsset ba = building != null && _buildingAssetRef != null ? _buildingAssetRef(building) : null;
            if (ba != null)
            {
                pActor.changeHappiness("just_found_house", ba.housing_happiness);
            }
            __result = BehResult.Continue;
            return false;
        }

        private static Building CheckBuildingDeterministic(Building pGetHomeBuilding, Family pFamily)
        {
            if (pGetHomeBuilding == null || pGetHomeBuilding.residents == null) return null;
            _houseResidentsPool.Clear();
            _houseResidentsPool.AddRange(pGetHomeBuilding.residents);
            if (_houseResidentsPool.Count > 1)
            {
                _houseResidentsPool.Sort();
            }
            for (int i = 0; i < _houseResidentsPool.Count; i++)
            {
                long resId = _houseResidentsPool[i];
                Actor actor = World.world.units.get(resId);
                if (actor != null && actor.isAlive() && actor.family == pFamily)
                {
                    actor.clearHomeBuilding();
                    return pGetHomeBuilding;
                }
            }
            return null;
        }

        // ==========================================
        // 18. Kingdom Capital Selection Tie-Break
        // ==========================================
        private static bool KingdomBehCheckCapitalExecutePrefix(Kingdom pKingdom, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pKingdom == null || !pKingdom.hasCities())
            {
                __result = BehResult.Continue;
                return false;
            }
            if (pKingdom.hasCapital() && pKingdom.capital.isAlive())
            {
                __result = BehResult.Continue;
                return false;
            }
            City best = null;
            if (pKingdom.cities != null)
            {
                for (int i = 0; i < pKingdom.cities.Count; i++)
                {
                    City city = pKingdom.cities[i];
                    if (city == null || !city.isAlive()) continue;
                    if (best == null)
                    {
                        best = city;
                    }
                    else
                    {
                        int count1 = city.buildings != null ? city.buildings.Count : 0;
                        int count2 = best.buildings != null ? best.buildings.Count : 0;
                        if (count1 > count2 || (count1 == count2 && city.id < best.id))
                        {
                            best = city;
                        }
                    }
                }
            }
            pKingdom.setCapital(best);
            __result = BehResult.Continue;
            return false;
        }

        // ==========================================
        // 19. Supreme Kingdom Power Sort Tie-Break
        // ==========================================
        private static bool SortByPowerPrefix(Kingdom o1, Kingdom o2, ref int __result)
        {
            if (ReferenceEquals(o1, o2)) { __result = 0; return false; }
            if (o1 == null) { __result = 1; return false; }
            if (o2 == null) { __result = -1; return false; }
            int cmp = o2.power.CompareTo(o1.power);
            if (cmp != 0)
            {
                __result = cmp;
                return false;
            }
            __result = o1.id.CompareTo(o2.id);
            return false;
        }

        // ==========================================
        // 20. City Attack Target Zone & Enemy City Selection
        // ==========================================
        private static bool CityBehCheckAttackZoneExecutePrefix(City pCity, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pCity == null) { __result = BehResult.Continue; return false; }
            City targetCity = _targetAttackCityRef != null ? _targetAttackCityRef(pCity) : null;
            TileZone targetZone = _targetAttackZoneRef != null ? _targetAttackZoneRef(pCity) : null;

            bool valid = true;
            Kingdom ourK = _cityKingdomRef != null ? _cityKingdomRef(pCity) : null;
            Kingdom targetK = targetCity != null && _cityKingdomRef != null ? _cityKingdomRef(targetCity) : null;
            if (targetCity == null)
            {
                valid = false;
            }
            else if (!targetCity.isAlive() || !pCity.hasAnyWarriors() || ourK == null || targetK == null || !targetK.isEnemy(ourK) || !targetCity.reachableFrom(pCity))
            {
                valid = false;
            }

            if (!valid)
            {
                if (_targetAttackCityRef != null) _targetAttackCityRef(pCity) = null;
                if (_targetAttackZoneRef != null) _targetAttackZoneRef(pCity) = null;
                targetCity = null;
                targetZone = null;
            }

            if (targetCity != null && targetZone != null && targetZone.city != targetCity)
            {
                if (_targetAttackZoneRef != null) _targetAttackZoneRef(pCity) = null;
                targetZone = null;
            }

            if (pCity.hasAttackZoneOrder())
            {
                __result = BehResult.Continue;
                return false;
            }

            if (targetCity == null)
            {
                if (!pCity.isOkToSendArmy())
                {
                    __result = BehResult.Continue;
                    return false;
                }
                targetCity = FindTargetCityDeterministic(pCity);
            }

            if (targetCity == null)
            {
                __result = BehResult.Continue;
                return false;
            }

            if (_targetAttackCityRef != null) _targetAttackCityRef(pCity) = targetCity;

            if (targetCity.buildings != null && targetCity.buildings.Count > 0)
            {
                _attackBuildingsPool.Clear();
                for (int i = 0; i < targetCity.buildings.Count; i++)
                {
                    Building b = targetCity.buildings[i];
                    if (b != null && b.current_tile != null && b.current_tile.zone != null)
                    {
                        _attackBuildingsPool.Add(b);
                    }
                }
                if (_attackBuildingsPool.Count > 0)
                {
                    _attackBuildingsPool.Sort((a, b) => a.id.CompareTo(b.id));
                    Building random = _attackBuildingsPool[Randy.randomInt(0, _attackBuildingsPool.Count)];
                    if (_targetAttackZoneRef != null) _targetAttackZoneRef(pCity) = random.current_tile.zone;
                }
            }
            else
            {
                List<TileZone> ourZones = _cityZonesRef != null ? _cityZonesRef(pCity) : null;
                if (ourZones != null && ourZones.Count > 0)
                {
                    _attackZonesPool.Clear();
                    for (int i = 0; i < ourZones.Count; i++)
                    {
                        TileZone z = ourZones[i];
                        if (z != null) _attackZonesPool.Add(z);
                    }
                    if (_attackZonesPool.Count > 0)
                    {
                        _attackZonesPool.Sort((a, b) => a.id.CompareTo(b.id));
                        if (_targetAttackZoneRef != null) _targetAttackZoneRef(pCity) = _attackZonesPool[Randy.randomInt(0, _attackZonesPool.Count)];
                    }
                }
            }

            __result = BehResult.Continue;
            return false;
        }

        private static City FindTargetCityDeterministic(City pOurCity)
        {
            Kingdom kingdom = pOurCity != null && _cityKingdomRef != null ? _cityKingdomRef(pOurCity) : null;
            if (kingdom == null) return null;
            City result = null;
            float minDist = float.MaxValue;
            using ListPool<City> listPool = new ListPool<City>(World.world.cities.Count);
            World.world.wars.getWarCities(kingdom, listPool);
            if (listPool.Count > 1)
            {
                listPool.Sort((a, b) => a.id.CompareTo(b.id));
            }
            Vector2 ourCenter = _cityCenterRef != null ? _cityCenterRef(pOurCity) : Vector2.zero;
            foreach (ref City item in listPool)
            {
                City current = item;
                if (current == null) continue;
                Vector2 currentCenter = _cityCenterRef != null ? _cityCenterRef(current) : Vector2.zero;
                float d = Toolbox.SquaredDistVec2Float(currentCenter, ourCenter);
                if (current.reachableFrom(pOurCity))
                {
                    if (d < minDist || (Mathf.Approximately(d, minDist) && (result == null || current.id < result.id)))
                    {
                        result = current;
                        minDist = d;
                    }
                }
            }
            return result;
        }

        // ==========================================
        // 21. City Border Steal Determinism
        // ==========================================
        private static bool CityBehBorderStealExecutePrefix(City pCity, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pCity == null) { __result = BehResult.Stop; return false; }
            if (!DebugConfig.isOn(DebugOption.SystemZoneGrowth)) { __result = BehResult.Stop; return false; }
            if (!WorldLawLibrary.world_law_border_stealing.isEnabled()) { __result = BehResult.Stop; return false; }
            if (pCity.status.population == 0 || pCity.buildings.Count == 0) { __result = BehResult.Stop; return false; }

            _stealBuildingsPool.Clear();
            for (int i = 0; i < pCity.buildings.Count; i++)
            {
                Building b = pCity.buildings[i];
                if (b != null && b.current_tile != null && b.current_tile.zone != null && b.current_tile.zone.neighbours != null)
                {
                    _stealBuildingsPool.Add(b);
                }
            }
            if (_stealBuildingsPool.Count == 0)
            {
                __result = BehResult.Continue;
                return false;
            }
            _stealBuildingsPool.Sort((a, b) => a.id.CompareTo(b.id));

            Kingdom ourKingdom = _cityKingdomRef != null ? _cityKingdomRef(pCity) : null;
            for (int i = 0; i < 3; i++)
            {
                Building randBuilding = _stealBuildingsPool[Randy.randomInt(0, _stealBuildingsPool.Count)];
                TileZone[] neighbours = randBuilding.current_tile.zone.neighbours;
                _stealZonesPool.Clear();
                foreach (TileZone tileZone in neighbours)
                {
                    if (tileZone != null && tileZone.city != null && tileZone.city != pCity &&
                        !tileZone.hasAnyBuildingsInSet(BuildingList.Civs))
                    {
                        Kingdom theirKingdom = _cityKingdomRef != null ? _cityKingdomRef(tileZone.city) : null;
                        if (ourKingdom != null && theirKingdom != null && theirKingdom.isEnemy(ourKingdom))
                        {
                            _stealZonesPool.Add(tileZone);
                        }
                    }
                }
                if (_stealZonesPool.Count > 0)
                {
                    _stealZonesPool.Sort((a, b) => a.id.CompareTo(b.id));
                    TileZone toSteal = _stealZonesPool[0];
                    if (toSteal.city != null)
                    {
                        _removeZoneMethod?.Invoke(toSteal.city, new object[] { toSteal });
                    }
                    _addZoneMethod?.Invoke(pCity, new object[] { toSteal });
                    break;
                }
            }
            __result = BehResult.Continue;
            return false;
        }

        // ==========================================
        // 22. Culture Preferred Weapon Determinism
        // ==========================================
        private static void RecalcPreferredWeaponsPostfix(Culture __instance)
        {
            if (__instance == null) return;
            List<string> subtypes = _preferredSubtypesRef != null ? _preferredSubtypesRef(__instance) : null;
            if (subtypes != null && subtypes.Count > 1)
            {
                subtypes.Sort(StringComparer.Ordinal);
            }
            List<EquipmentAsset> assets = _preferredAssetsRef != null ? _preferredAssetsRef(__instance) : null;
            if (assets != null && assets.Count > 1)
            {
                assets.Sort((a, b) =>
                {
                    if (ReferenceEquals(a, b)) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    return string.CompareOrdinal(a.id, b.id);
                });
            }
        }

        // ==========================================
        // 23. Alliance Target Search Determinism
        // ==========================================
        private static bool GetAllianceTargetPrefix(Kingdom pKingdomStarter, ref Kingdom __result)
        {
            if (pKingdomStarter == null || pKingdomStarter.isSupreme())
            {
                __result = null;
                return false;
            }
            using ListPool<Kingdom> listPool = World.world.wars.getNeutralKingdoms(pKingdomStarter, pOnlyWithoutWars: true, pOnlyWithoutAlliances: true);
            if (listPool.Count == 0)
            {
                __result = null;
                return false;
            }
            if (listPool.Count > 1)
            {
                listPool.Sort((a, b) => a.id.CompareTo(b.id));
            }
            foreach (Kingdom item in listPool.LoopRandom())
            {
                if (item != null && item.hasKing() && !item.isSupreme() && !item.king.hasPlot() &&
                    pKingdomStarter.isOpinionTowardsKingdomGood(item) &&
                    item.getRenown() >= PlotsLibrary.alliance_create.min_renown_kingdom)
                {
                    bool flag = false;
                    if (pKingdomStarter.countCities() <= 2 && item.countCities() <= 2 &&
                        !pKingdomStarter.hasNearbyKingdoms() && !item.hasNearbyKingdoms())
                    {
                        flag = true;
                    }
                    if (!flag && DiplomacyHelpers.areKingdomsClose(item, pKingdomStarter))
                    {
                        flag = true;
                    }
                    if (flag)
                    {
                        __result = item;
                        return false;
                    }
                }
            }
            __result = null;
            return false;
        }

        // ==========================================
        // 24. City Center Tile Selection Determinism
        // ==========================================
        private static bool RecalculateCityTilePrefix(City __instance)
        {
            if (__instance == null) return false;
            if (_cityTileRef != null) _cityTileRef(__instance) = null;
            Building building = null;
            if (__instance.buildings_dict_type != null && __instance.buildings_dict_type.TryGetValue("type_bonfire", out var bfList) && bfList != null && bfList.Count > 0)
            {
                building = bfList[0];
            }
            if (building != null && building.current_tile != null)
            {
                if (_cityTileRef != null) _cityTileRef(__instance) = building.current_tile;
                return false;
            }
            List<Building> bList = __instance.buildings;
            if (bList != null && bList.Count > 0)
            {
                for (int i = 0; i < bList.Count; i++)
                {
                    Building item = bList[i];
                    if (item == null || item.current_tile == null) continue;
                    BuildingAsset asset = _buildingAssetRef != null ? _buildingAssetRef(item) : null;
                    if (asset == null || asset.docks || item.current_tile.Type.ocean) continue;

                    if (building == null)
                    {
                        building = item;
                    }
                    else
                    {
                        BuildingAsset curAsset = _buildingAssetRef != null ? _buildingAssetRef(building) : null;
                        int curPriority = curAsset != null ? curAsset.priority : 0;
                        int itemPriority = asset.priority;
                        if (itemPriority > curPriority || (itemPriority == curPriority && item.id < building.id))
                        {
                            building = item;
                        }
                    }
                }
            }
            if (building != null && building.current_tile != null)
            {
                if (_cityTileRef != null) _cityTileRef(__instance) = building.current_tile;
                return false;
            }
            List<TileZone> zList = _cityZonesRef != null ? _cityZonesRef(__instance) : null;
            if (zList != null && zList.Count > 0)
            {
                for (int i = 0; i < zList.Count; i++)
                {
                    TileZone tileZone = zList[i];
                    if (tileZone != null && tileZone.centerTile != null && !tileZone.centerTile.Type.ocean)
                    {
                        if (_cityTileRef != null) _cityTileRef(__instance) = tileZone.centerTile;
                        break;
                    }
                }
            }
            return false;
        }

        // ==========================================
        // 25. City Zone Disconnected Cluster Abandon Sorter Tie-Break
        // ==========================================
        private static bool CityZoneAbandonSorterPrefix(ListPool<TileZone> pList1, ListPool<TileZone> pList2, ref int __result)
        {
            if (ReferenceEquals(pList1, pList2)) { __result = 0; return false; }
            if (pList1 == null) { __result = 1; return false; }
            if (pList2 == null) { __result = -1; return false; }
            int cmp = pList2.Count.CompareTo(pList1.Count);
            if (cmp != 0)
            {
                __result = cmp;
                return false;
            }
            int min1 = int.MaxValue;
            for (int i = 0; i < pList1.Count; i++) if (pList1[i] != null && pList1[i].id < min1) min1 = pList1[i].id;
            int min2 = int.MaxValue;
            for (int i = 0; i < pList2.Count; i++) if (pList2[i] != null && pList2[i].id < min2) min2 = pList2[i].id;
            __result = min1.CompareTo(min2);
            return false;
        }

        // ==========================================
        // 26. City Food Diet Sorter Tie-Break
        // ==========================================
        private static bool FoodSorterPrefix(CityStorageSlot o1, CityStorageSlot o2, ref int __result)
        {
            if (ReferenceEquals(o1, o2)) { __result = 0; return false; }
            if (o1 == null) { __result = 1; return false; }
            if (o2 == null) { __result = -1; return false; }
            int cmp = o2.amount.CompareTo(o1.amount);
            if (cmp != 0)
            {
                __result = cmp;
                return false;
            }
            __result = string.CompareOrdinal(o1.id, o2.id);
            return false;
        }

        // ==========================================
        // 27. Toolbox Sort Tiles By Distance Tie-Break
        // ==========================================
        private static bool SortTilesByDistancePrefix(WorldTile pTile, ListPool<WorldTile> pTiles)
        {
            if (pTiles == null || pTiles.Count <= 1 || pTile == null) return false;
            pTiles.Sort((x, y) =>
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return 1;
                if (y == null) return -1;
                int cmp = Toolbox.SquaredDistTile(pTile, x).CompareTo(Toolbox.SquaredDistTile(pTile, y));
                if (cmp != 0) return cmp;
                return x.tile_id.CompareTo(y.tile_id);
            });
            return false;
        }

        public static void Normalize(MapBox world)
        {
            HookPlotAssets();
        }

        // ==========================================
        // 28. Plots & Divine Rites Determinism
        // ==========================================
        public static void PlotsLibraryInitPostfix()
        {
            HookPlotAssets();
        }

        public static void HookPlotAssets()
        {
            try
            {
                if (AssetManager.plots_library == null) return;
                PlotAsset reb = AssetManager.plots_library.get("cause_rebellion");
                if (reb != null)
                {
                    reb.action = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        City target_city = pActor.plot.target_city;
                        if (target_city == null || target_city.isCapitalCity()) return false;
                        if (target_city.units == null || target_city.units.Count == 0) return false;
                        List<Actor> units = new List<Actor>(target_city.getUnits());
                        units.Sort((a, b) => a.id.CompareTo(b.id));
                        Actor random = units[Randy.randomInt(0, units.Count)];
                        if (random == null || !random.isAlive()) return false;
                        DiplomacyHelpersRebellion.startRebellion(random, pActor.plot, pCheckForHappiness: false);
                        return true;
                    };
                }
                PlotAsset meteor = AssetManager.plots_library.get("summon_meteor_rain");
                if (meteor != null)
                {
                    meteor.action = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        City target_city4 = pActor.plot.target_city;
                        List<TileZone> cZones4 = target_city4 != null && _cityZonesRef != null ? _cityZonesRef(target_city4) : null;
                        if (target_city4 == null || cZones4 == null || cZones4.Count == 0) return false;
                        List<TileZone> zones = new List<TileZone>(cZones4);
                        zones.Sort((a, b) => a.id.CompareTo(b.id));
                        int num2 = 7;
                        int num3 = Randy.randomInt(3, num2 + 1);
                        for (int j = 0; j < num3; j++)
                        {
                            TileZone z = zones[Randy.randomInt(0, zones.Count)];
                            Meteorite.spawnMeteoriteDisaster(z.getRandomTile(), pActor);
                        }
                        return true;
                    };
                }
                PlotAsset quake = AssetManager.plots_library.get("summon_earthquake");
                if (quake != null)
                {
                    quake.action = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        City target_city = pActor.plot.target_city;
                        List<TileZone> cZones = target_city != null && _cityZonesRef != null ? _cityZonesRef(target_city) : null;
                        if (target_city == null || cZones == null || cZones.Count == 0) return false;
                        List<TileZone> zones = new List<TileZone>(cZones);
                        zones.Sort((a, b) => a.id.CompareTo(b.id));
                        TileZone z = zones[Randy.randomInt(0, zones.Count)];
                        Earthquake.startQuake(z.getRandomTile(), EarthquakeType.SmallDisaster);
                        return true;
                    };
                }
                PlotAsset storm = AssetManager.plots_library.get("summon_thunderstorm");
                if (storm != null)
                {
                    storm.action = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        City target_city3 = pActor.plot.target_city;
                        List<TileZone> cZones3 = target_city3 != null && _cityZonesRef != null ? _cityZonesRef(target_city3) : null;
                        if (target_city3 == null || cZones3 == null || cZones3.Count == 0) return false;
                        List<TileZone> zones = new List<TileZone>(cZones3);
                        zones.Sort((a, b) => a.id.CompareTo(b.id));
                        TileZone z = zones[Randy.randomInt(0, zones.Count)];
                        WorldTile randomTile = z.getRandomTile();
                        MapBox.spawnLightningMedium(randomTile, 0.15f, pActor);
                        int num = Randy.randomInt(5, 13);
                        for (int i = 0; i < num; i++)
                        {
                            WorldTile tTargetTileL = Toolbox.getRandomTileWithinDistance(randomTile, 10);
                            DelayedActionsManager.addAction(pDelay: Randy.randomFloat(0.1f, 0.75f) * (float)i, pAction: delegate
                            {
                                MapBox.spawnLightningMedium(tTargetTileL, 0.15f, pActor);
                            });
                        }
                        return true;
                    };
                }
            }
            catch (Exception e)
            {
                Log.Warn("lockstep: HookPlotAssets error: " + e);
            }
        }

        private static bool SummonUnitsActionPrefix(Actor pActor, string pActorAssetId, int pMin, int pMax, bool pJoinCasters, ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.plot == null) { __result = false; return false; }
            City target_city = pActor.plot.target_city;
            List<TileZone> cZones = target_city != null && _cityZonesRef != null ? _cityZonesRef(target_city) : null;
            if (target_city == null || cZones == null || cZones.Count == 0)
            {
                __result = false;
                return false;
            }
            List<TileZone> zones = new List<TileZone>(cZones);
            zones.Sort((a, b) => a.id.CompareTo(b.id));
            int num = Randy.randomInt(pMin, pMax + 1);
            for (int i = 0; i < num; i++)
            {
                TileZone z = zones[Randy.randomInt(0, zones.Count)];
                WorldTile randomTile = z.getRandomTile();
                Actor actor = World.world.units.createNewUnit(pActorAssetId, randomTile, pMiracleSpawn: false, 0f, null, null, pSpawnWithItems: true, pAdultAge: true, pGiveOwnerlessItems: false, pJoinCasters);
                EffectsLibrary.spawn("fx_spawn", randomTile);
                if (pJoinCasters && actor != null && pActor.isKingdomCiv())
                {
                    if (actor.subspecies.isJustCreated())
                    {
                        actor.subspecies.addTrait("prefrontal_cortex");
                    }
                    actor.joinKingdom(pActor.kingdom);
                }
            }
            __result = true;
            return false;
        }

        private static bool TryStartReligionRiteOnEnemyCityPrefix(Actor pActor, PlotAsset pPlotAsset, bool pForced, ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.kingdom == null) { __result = false; return false; }
            using ListPool<Kingdom> listPool = pActor.kingdom.getEnemiesKingdoms();
            if (listPool.Count == 0) { __result = false; return false; }
            listPool.Sort((a, b) => a.id.CompareTo(b.id));
            Kingdom random = listPool[Randy.randomInt(0, listPool.Count)];
            if (!random.hasCities()) { __result = false; return false; }
            List<City> cities = new List<City>(random.getCities());
            cities.Sort((a, b) => a.id.CompareTo(b.id));
            World.world.plots.newPlot(pActor, pPlotAsset, pForced).target_city = cities[Randy.randomInt(0, cities.Count)];
            __result = true;
            return false;
        }

        private static bool TryStartCauseRebellionPrefix(Actor pActor, PlotAsset pPlotAsset, bool pForced, ref bool __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.kingdom == null) { __result = false; return false; }
            using ListPool<Kingdom> listPool = pActor.kingdom.getEnemiesKingdoms();
            if (listPool.Count == 0) { __result = false; return false; }
            listPool.Sort((a, b) => a.id.CompareTo(b.id));
            Kingdom random = listPool[Randy.randomInt(0, listPool.Count)];
            if (!random.hasCities()) { __result = false; return false; }
            using ListPool<City> listPool2 = new ListPool<City>();
            foreach (City city in random.getCities())
            {
                if (!city.isCapitalCity()) listPool2.Add(city);
            }
            if (listPool2.Count == 0) { __result = false; return false; }
            listPool2.Sort((a, b) => a.id.CompareTo(b.id));
            World.world.plots.newPlot(pActor, pPlotAsset, pForced).target_city = listPool2[Randy.randomInt(0, listPool2.Count)];
            __result = true;
            return false;
        }

        // ==========================================
        // 29. World Road Decay Determinism
        // ==========================================
        private static bool UpdateRoadDecayPrefix()
        {
            if (!LockstepClock.InTick) return true;
            ZoneCalculator zc = _zoneCalculatorRef != null ? _zoneCalculatorRef(World.world) : null;
            if (zc == null || zc.zones == null || zc.zones.Count == 0) return false;
            int totalZones = zc.zones_total_x * zc.zones_total_y;
            if (totalZones <= 0) totalZones = zc.zones.Count;
            int randIdx = Randy.randomInt(0, totalZones);
            int rx = randIdx % zc.zones_total_x;
            int ry = randIdx / zc.zones_total_x;
            TileZone targetZone = zc.getZone(rx, ry);
            if (targetZone == null && zc.zones.Count > 0)
            {
                targetZone = zc.zones[randIdx % zc.zones.Count];
            }
            if (targetZone == null) return false;
            if ((targetZone.hasCity() && targetZone.city.hasCulture() && targetZone.city.culture.canUseRoads()) || Randy.randomBool())
            {
                return false;
            }
            if (targetZone.tiles != null && targetZone.tiles.Length > 0)
            {
                int count = targetZone.tiles.Length;
                int startOffset = Randy.randomInt(0, count);
                for (int i = 0; i < count; i++)
                {
                    WorldTile item = targetZone.tiles[(startOffset + i) % count];
                    if (!Randy.randomBool() && item.Type.road)
                    {
                        MapAction.decreaseTile(item, pDamage: false);
                    }
                }
            }
            return false;
        }

        // ==========================================
        // 30. Spatial Distance Tie-Breakers in Toolbox
        // ==========================================
        private static bool GetClosestTileArrayPrefix(WorldTile[] pArray, WorldTile pTarget, ref WorldTile __result)
        {
            if (pArray == null || pTarget == null) { __result = null; return false; }
            WorldTile result = null;
            int num = int.MaxValue;
            for (int i = 0; i < pArray.Length; i++)
            {
                WorldTile t = pArray[i];
                if (t == null) continue;
                int d = Toolbox.SquaredDist(pTarget.x, pTarget.y, t.x, t.y);
                if (d < num || (d == num && result != null && t.tile_id < result.tile_id))
                {
                    num = d;
                    result = t;
                }
            }
            __result = result;
            return false;
        }

        private static bool GetClosestTileListPrefix(List<WorldTile> pArray, WorldTile pTarget, ref WorldTile __result)
        {
            if (pArray == null || pTarget == null) { __result = null; return false; }
            WorldTile result = null;
            int num = int.MaxValue;
            for (int i = 0; i < pArray.Count; i++)
            {
                WorldTile t = pArray[i];
                if (t == null) continue;
                int d = Toolbox.SquaredDist(pTarget.x, pTarget.y, t.x, t.y);
                if (d < num || (d == num && result != null && t.tile_id < result.tile_id))
                {
                    num = d;
                    result = t;
                }
            }
            __result = result;
            return false;
        }

        private static bool GetClosestTileListPoolPrefix(ListPool<WorldTile> pArray, WorldTile pTarget, ref WorldTile __result)
        {
            if (pArray == null || pTarget == null) { __result = null; return false; }
            WorldTile result = null;
            int num = int.MaxValue;
            for (int i = 0; i < pArray.Count; i++)
            {
                WorldTile t = pArray[i];
                if (t == null) continue;
                int d = Toolbox.SquaredDist(pTarget.x, pTarget.y, t.x, t.y);
                if (d < num || (d == num && result != null && t.tile_id < result.tile_id))
                {
                    num = d;
                    result = t;
                }
            }
            __result = result;
            return false;
        }

        private static bool GetClosestBuildingPrefix(List<Building> pCollection, WorldTile pTile, ref Building __result)
        {
            if (pCollection == null || pTile == null) { __result = null; return false; }
            Building result = null;
            int num = int.MaxValue;
            Vector2Int pos = pTile.pos;
            for (int i = 0; i < pCollection.Count; i++)
            {
                Building b = pCollection[i];
                if (b == null || b.current_tile == null) continue;
                Vector2Int pos2 = b.current_tile.pos;
                int d = Toolbox.SquaredDist(pos.x, pos.y, pos2.x, pos2.y);
                if (d < num || (d == num && result != null && b.id < result.id))
                {
                    num = d;
                    result = b;
                }
            }
            __result = result;
            return false;
        }

        private static bool GetClosestRegionPrefix(List<MapRegion> pArray, WorldTile pTarget, ref MapRegion __result)
        {
            if (pArray == null || pTarget == null) { __result = null; return false; }
            MapRegion result = null;
            int num = int.MaxValue;
            for (int i = 0; i < pArray.Count; i++)
            {
                MapRegion r = pArray[i];
                if (r == null || r.tiles == null || r.tiles.Count == 0) continue;
                int d = Toolbox.SquaredDist(pTarget.pos.x, pTarget.pos.y, r.tiles[0].pos.x, r.tiles[0].pos.y);
                if (d < num || (d == num && result != null && r.id < result.id))
                {
                    num = d;
                    result = r;
                }
            }
            __result = result;
            return false;
        }

        private static bool SortRegionsByDistancePrefix(WorldTile pTile, List<MapRegion> pRegions)
        {
            if (pRegions == null || pRegions.Count <= 1 || pTile == null) return false;
            pRegions.Sort((MapRegion x, MapRegion y) =>
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null || x.tiles == null || x.tiles.Count == 0) return 1;
                if (y == null || y.tiles == null || y.tiles.Count == 0) return -1;
                int cmp = Toolbox.SquaredDistTile(pTile, x.tiles[0]).CompareTo(Toolbox.SquaredDistTile(pTile, y.tiles[0]));
                if (cmp != 0) return cmp;
                return x.id.CompareTo(y.id);
            });
            return false;
        }

        private static bool GetClosestActorListPoolPrefix(ListPool<Actor> pCollection, WorldTile pTile, ref Actor __result)
        {
            if (pCollection == null || pTile == null) { __result = null; return false; }
            Actor result = null;
            int num = int.MaxValue;
            Vector2Int pos = pTile.pos;
            for (int i = 0; i < pCollection.Count; i++)
            {
                Actor a = pCollection[i];
                if (a == null || a.current_tile == null) continue;
                Vector2Int pos2 = a.current_tile.pos;
                int d = Toolbox.SquaredDist(pos.x, pos.y, pos2.x, pos2.y);
                if (d < num || (d == num && result != null && a.id < result.id))
                {
                    num = d;
                    result = a;
                }
            }
            __result = result;
            return false;
        }

        // ==========================================
        // 31. City Actor Idle Wandering Zone Selection
        // ==========================================
        private static bool BehCityActorGetRandomIdleTileExecutePrefix(BehCityActorGetRandomIdleTile __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.city == null || !pActor.city.hasZones())
            {
                __result = BehResult.Stop;
                return false;
            }
            WorldTile worldTile = TryToGetBonfireTileDeterministic(pActor);
            if (worldTile == null)
            {
                worldTile = GetRandomCityZoneTileDeterministic(pActor);
            }
            if (worldTile == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            if (_behTileTargetRef != null) _behTileTargetRef(pActor) = worldTile;
            __result = BehResult.Continue;
            return false;
        }

        private static WorldTile GetRandomCityZoneTileDeterministic(Actor pActor)
        {
            WorldTile worldTile = null;
            if (!pActor.current_tile.isSameCityHere(pActor.city) || Randy.randomChance(0.2f))
            {
                List<TileZone> cZones = pActor.city != null && _cityZonesRef != null ? _cityZonesRef(pActor.city) : null;
                if (cZones == null || cZones.Count == 0) return null;
                List<TileZone> sortedZones = new List<TileZone>(cZones);
                sortedZones.Sort((a, b) => a.id.CompareTo(b.id));
                TileZone chosenZone = sortedZones[Randy.randomInt(0, sortedZones.Count)];
                if (chosenZone.tiles == null || chosenZone.tiles.Length == 0) return null;
                worldTile = chosenZone.tiles[Randy.randomInt(0, chosenZone.tiles.Length)];
                if (!worldTile.isSameIsland(pActor.current_tile))
                {
                    return null;
                }
            }
            else
            {
                MapRegion reg = pActor.current_tile.region;
                if (reg != null && reg.tiles != null && reg.tiles.Count > 0)
                {
                    worldTile = reg.tiles[Randy.randomInt(0, reg.tiles.Count)];
                }
            }
            return worldTile;
        }

        private static WorldTile TryToGetBonfireTileDeterministic(Actor pActor)
        {
            if (pActor?.city == null) return null;
            Building bonfire = _getBuildingOfTypeMethod != null
                ? (Building)_getBuildingOfTypeMethod.Invoke(pActor.city, new object[] { "type_bonfire", true, false, false, pActor.current_island })
                : null;
            if (bonfire == null) return null;
            List<WorldTile> bTiles = _buildingTilesRef != null ? _buildingTilesRef(bonfire) : null;
            if (bTiles == null || bTiles.Count == 0) return null;
            WorldTile random = bTiles[Randy.randomInt(0, bTiles.Count)];
            if (Randy.randomChance(0.3f))
            {
                MapRegion mapRegion = bonfire.current_tile.region;
                if (mapRegion != null && mapRegion.neighbours != null && mapRegion.neighbours.Count > 0 && Randy.randomChance(0.3f))
                {
                    mapRegion = mapRegion.neighbours[Randy.randomInt(0, mapRegion.neighbours.Count)];
                }
                if (mapRegion != null && mapRegion.tiles != null && mapRegion.tiles.Count > 0)
                {
                    random = mapRegion.tiles[Randy.randomInt(0, mapRegion.tiles.Count)];
                }
            }
            return random;
        }

        private static bool BehCityActorGetRandomZoneTileExecutePrefix(BehCityActorGetRandomZoneTile __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.city == null || !pActor.city.hasZones())
            {
                __result = BehResult.Stop;
                return false;
            }
            WorldTile worldTile = null;
            if (pActor.current_tile.zone.city != pActor.city || Randy.randomChance(0.2f))
            {
                List<TileZone> cZones = _cityZonesRef != null ? _cityZonesRef(pActor.city) : null;
                if (cZones == null || cZones.Count == 0)
                {
                    __result = BehResult.Stop;
                    return false;
                }
                List<TileZone> sortedZones = new List<TileZone>(cZones);
                sortedZones.Sort((a, b) => a.id.CompareTo(b.id));
                TileZone chosen = sortedZones[Randy.randomInt(0, sortedZones.Count)];
                worldTile = chosen.getRandomTile();
                if (!worldTile.isSameIsland(pActor.current_tile))
                {
                    __result = BehResult.Stop;
                    return false;
                }
            }
            else
            {
                MapRegion reg = pActor.current_tile.region;
                if (reg != null && reg.tiles != null && reg.tiles.Count > 0)
                {
                    worldTile = reg.tiles[Randy.randomInt(0, reg.tiles.Count)];
                }
            }
            if (_behTileTargetRef != null) _behTileTargetRef(pActor) = worldTile;
            __result = BehResult.Continue;
            return false;
        }

        // ==========================================
        // 32. Unit Teleport Home Target Zone Determinism
        // ==========================================
        private static bool BehTeleportHomeExecutePrefix(BehTeleportHome __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || !pActor.hasCity()) { __result = BehResult.Stop; return false; }
            City city = pActor.getCity();
            List<TileZone> cZones = city != null && _cityZonesRef != null ? _cityZonesRef(city) : null;
            if (city == null || !city.hasZones() || cZones == null || cZones.Count == 0) { __result = BehResult.Stop; return false; }
            List<TileZone> sortedZones = new List<TileZone>(cZones);
            sortedZones.Sort((a, b) => a.id.CompareTo(b.id));
            TileZone random = sortedZones[Randy.randomInt(0, sortedZones.Count)];
            if (random == null || random.tiles == null || random.tiles.Length == 0) { __result = BehResult.Stop; return false; }
            WorldTile random2 = random.tiles[Randy.randomInt(0, random.tiles.Length)];
            ActionLibrary.teleportEffect(pActor, random2);
            pActor.cancelAllBeh();
            if (_spawnOnMethod != null) _spawnOnMethod.Invoke(pActor, new object[] { random2, 0f });
            pActor.doCastAnimation();
            __result = BehResult.Continue;
            return false;
        }

        // ==========================================
        // 33. Dragon & UFO City Target Building Selection
        // ==========================================
        private static bool BehDragonCheckAttackCityExecutePrefix(BehDragonCheckAttackCity __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null) return true;
            WorldTile bTarget = _behTileTargetRef != null ? _behTileTargetRef(pActor) : null;
            if (bTarget != null) { __result = BehResult.Continue; return false; }
            ActorData aData = _actorDataRef != null ? _actorDataRef(pActor) : null;
            if (aData == null) { __result = BehResult.Continue; return false; }
            aData.get("cityToAttack", out var pResult, -1L);
            if (!pResult.hasValue()) { __result = BehResult.Continue; return false; }
            aData.get("attacksForCity", out var pResult2, 0);
            if (pResult2 < 1)
            {
                aData.removeLong("cityToAttack");
                __result = BehResult.Continue;
                return false;
            }
            City city = World.world.cities.get(pResult);
            bool flag = true;
            if (city != null && city.isAlive() && city.buildings != null && city.buildings.Count > 0)
            {
                List<Building> buildings = new List<Building>(city.buildings);
                buildings.Sort((a, b) => a.id.CompareTo(b.id));
                Building chosenBuilding = buildings[Randy.randomInt(0, buildings.Count)];
                if (chosenBuilding.current_tile != null && chosenBuilding.current_tile.zone != null && chosenBuilding.current_tile.zone.tiles != null && chosenBuilding.current_tile.zone.tiles.Length > 0)
                {
                    WorldTile[] ztiles = chosenBuilding.current_tile.zone.tiles;
                    WorldTile random = ztiles[Randy.randomInt(0, ztiles.Length)];
                    Dragon dragon = _behDragonRef != null ? _behDragonRef(__instance) : null;
                    WorldTile landTarget = (dragon != null && _randomTileWithinLandAttackRangeMethod != null)
                        ? (WorldTile)_randomTileWithinLandAttackRangeMethod.Invoke(dragon, new object[] { random })
                        : random;
                    if (_behTileTargetRef != null) _behTileTargetRef(pActor) = landTarget;
                    aData.set("attacksForCity", --pResult2);
                    flag = false;
                }
            }
            if (flag)
            {
                aData.removeLong("cityToAttack");
                aData.removeInt("attacksForCity");
            }
            __result = BehResult.Continue;
            return false;
        }

        private static bool BehUFOFindTargetExecutePrefix(BehUFOFindTarget __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null) return true;
            if (_behTileTargetRef != null) _behTileTargetRef(pActor) = null;
            ActorData aData = _actorDataRef != null ? _actorDataRef(pActor) : null;
            if (aData == null) { __result = BehResult.Continue; return false; }
            aData.get("attacksForCity", out var pResult, 0);
            aData.get("cityToAttack", out var pResult2, -1L);
            City city = (pResult2.hasValue() ? World.world.cities.get(pResult2) : null);
            if (pResult > 0 && city != null)
            {
                if (!city.isAlive() || city.buildings == null || city.buildings.Count == 0)
                {
                    if (_behTileTargetRef != null) _behTileTargetRef(pActor) = null;
                    pResult = 0;
                    aData.removeLong("cityToAttack");
                }
                else
                {
                    List<Building> buildings = new List<Building>(city.buildings);
                    buildings.Sort((a, b) => a.id.CompareTo(b.id));
                    Building random = buildings[Randy.randomInt(0, buildings.Count)];
                    if (random.current_tile != null && random.current_tile.zone != null && random.current_tile.zone.tiles != null && random.current_tile.zone.tiles.Length > 0)
                    {
                        WorldTile[] ztiles = random.current_tile.zone.tiles;
                        if (_behTileTargetRef != null) _behTileTargetRef(pActor) = ztiles[Randy.randomInt(0, ztiles.Length)];
                        pResult--;
                    }
                }
            }
            else if (pResult <= 0)
            {
                if (_behTileTargetRef != null) _behTileTargetRef(pActor) = null;
                aData.removeLong("cityToAttack");
            }
            if (pResult > 0)
            {
                aData.set("attacksForCity", pResult);
            }
            else
            {
                aData.removeInt("attacksForCity");
            }
            WorldTile curTarget = _behTileTargetRef != null ? _behTileTargetRef(pActor) : null;
            if (curTarget == null)
            {
                WorldTile worldTile = Toolbox.getRandomTileWithinDistance(pActor.current_tile, 100);
                IslandsCalculator ic = _islandsCalculatorRef != null ? _islandsCalculatorRef(World.world) : null;
                if (ic == null || (_hasGroundFunc != null && !_hasGroundFunc(ic)))
                {
                    if (_behTileTargetRef != null) _behTileTargetRef(pActor) = worldTile;
                    __result = BehResult.Continue;
                    return false;
                }
                int num = 5;
                while (!worldTile.Type.ground && num > 0)
                {
                    worldTile = Toolbox.getRandomTileWithinDistance(pActor.current_tile, 100);
                    num--;
                }
                if (!worldTile.Type.ground && _getRandomIslandGroundFunc != null && _getRandomIslandGroundFunc(ic, true) != null)
                {
                    Span<Vector2Int> pArray = stackalloc Vector2Int[8];
                    for (int i = 0; i < 8; i++)
                    {
                        pArray[i] = ic.tryGetRandomGround().pos;
                    }
                    Vector2Int closestTile = Toolbox.getClosestTile(pArray, pActor.current_tile);
                    worldTile = World.world.GetTileSimple(closestTile.x, closestTile.y);
                }
                if (_behTileTargetRef != null) _behTileTargetRef(pActor) = worldTile;
            }
            __result = BehResult.Continue;
            return false;
        }

        // ==========================================
        // 34. City Building Upgrades, Roads & Docks Iteration
        // ==========================================
        private static bool TryToUpgradeBuildingsPrefix(City pCity)
        {
            if (!LockstepClock.InTick) return true;
            if (pCity == null || pCity.buildings == null || pCity.buildings.Count == 0) return false;
            List<Building> buildings = new List<Building>(pCity.buildings);
            buildings.Sort((a, b) => a.id.CompareTo(b.id));
            int count = buildings.Count;
            int offset = Randy.randomInt(0, count);
            for (int i = 0; i < count; i++)
            {
                Building item = buildings[(offset + i) % count];
                bool canUpg = _canBeUpgradedFunc != null ? _canBeUpgradedFunc(item) : false;
                if (item != null && canUpg)
                {
                    CityBehBuild.upgradeBuilding(item, pCity);
                    break;
                }
            }
            return false;
        }

        private static bool UpgradeRandomBuildingPrefix(City pCity)
        {
            if (!LockstepClock.InTick) return true;
            if (pCity == null || pCity.buildings == null || pCity.buildings.Count == 0) return false;
            List<Building> buildings = new List<Building>(pCity.buildings);
            buildings.Sort((a, b) => a.id.CompareTo(b.id));
            int count = buildings.Count;
            int offset = Randy.randomInt(0, count);
            for (int i = 0; i < count; i++)
            {
                Building item = buildings[(offset + i) % count];
                bool canUpg = _canBeUpgradedFunc != null ? _canBeUpgradedFunc(item) : false;
                if (item != null && canUpg)
                {
                    CityBehBuild.upgradeBuilding(item, pCity);
                    break;
                }
            }
            return false;
        }

        private static bool GetOnHouseTilePrefix(City pCity, BuildingAsset pAsset, ref WorldTile __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pCity == null || pCity.buildings == null || pCity.buildings.Count == 0) { __result = null; return false; }
            List<Building> buildings = new List<Building>(pCity.buildings);
            buildings.Sort((a, b) => a.id.CompareTo(b.id));
            int count = buildings.Count;
            int offset = Randy.randomInt(0, count);
            for (int i = 0; i < count; i++)
            {
                Building item = buildings[(offset + i) % count];
                BuildingAsset bAsset = _buildingAssetRef != null ? _buildingAssetRef(item) : null;
                if (item != null && bAsset != null && bAsset.priority <= pAsset.priority && bAsset.hasHousingSlots() && CityBehBuild.isGoodTileForBuilding(item.current_tile, pAsset, pCity))
                {
                    __result = item.current_tile;
                    return false;
                }
            }
            __result = null;
            return false;
        }

        private static bool GetDockTilePrefix(City pCity, ref WorldTile __result)
        {
            if (!LockstepClock.InTick) return true;
            List<TileZone> cZones = pCity != null && _cityZonesRef != null ? _cityZonesRef(pCity) : null;
            if (pCity == null || cZones == null || cZones.Count == 0) { __result = null; return false; }
            BuildingAsset buildingDockAsset = pCity.getActorAsset().getBuildingDockAsset();
            if (buildingDockAsset == null) { __result = null; return false; }
            OceanHelper.clearOceanPools();
            OceanHelper.saveOceanPoolsWithDocks(pCity);
            List<TileZone> zones = new List<TileZone>(cZones);
            zones.Sort((a, b) => a.id.CompareTo(b.id));
            int zCount = zones.Count;
            int zOffset = Randy.randomInt(0, zCount);
            for (int i = 0; i < zCount; i++)
            {
                TileZone item = zones[(zOffset + i) % zCount];
                if (item.tiles_with_liquid == 0) continue;
                MapChunk chunk = item.chunk;
                if (chunk == null || chunk.regions == null || chunk.regions.Count <= 1) continue;
                int tCount = item.tiles.Length;
                int tOffset = Randy.randomInt(0, tCount);
                for (int j = 0; j < tCount; j++)
                {
                    WorldTile item2 = item.tiles[(tOffset + j) % tCount];
                    if (item2.Type.ocean && _canBuildFromFunc != null && _canBuildFromFunc(World.world.buildings, item2, buildingDockAsset, pCity, BuildPlacingType.New, false))
                    {
                        __result = item2;
                        return false;
                    }
                }
            }
            __result = null;
            return false;
        }

        private static bool TryToBuildInZonesPrefix(List<TileZone> pList, BuildingAsset pBuildingAsset, City pCity, bool pForceCenterZone, ref WorldTile __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pList == null || pList.Count == 0) { __result = null; return false; }
            pList.Sort((a, b) => a.id.CompareTo(b.id));
            return true;
        }

        private static void GetBuildingListOfIDPostfix(ref List<Building> __result)
        {
            if (!LockstepClock.InTick) return;
            if (__result != null && __result.Count > 1)
            {
                __result.Sort((a, b) => a.id.CompareTo(b.id));
            }
        }

        private static bool MakeRoadsBuildingsPrefix(City pCity, Building pBuilding)
        {
            if (!LockstepClock.InTick) return true;
            if (pCity == null || pBuilding == null) return false;
            List<WorldTile> roadTiles = _cityRoadTilesRef != null ? _cityRoadTilesRef(pCity) : null;
            BuildingAsset pAsset = _buildingAssetRef != null ? _buildingAssetRef(pBuilding) : null;
            if ((roadTiles != null && roadTiles.Count > 0) || pAsset == null || !pAsset.build_road_to)
            {
                return false;
            }
            WorldTile current_tile = pBuilding.current_tile;
            if (current_tile == null || current_tile.Type.liquid)
            {
                return false;
            }
            using ListPool<WorldTile> listPool = new ListPool<WorldTile>(pCity.buildings.Count);
            foreach (Building building in pCity.buildings)
            {
                BuildingAsset bAsset = _buildingAssetRef != null ? _buildingAssetRef(building) : null;
                if (building != pBuilding && bAsset != null && bAsset.build_road_to && !building.current_tile.Type.liquid && building.current_tile.isSameIsland(pBuilding.current_tile))
                {
                    listPool.Add(building.current_tile);
                }
            }
            if (listPool.Count != 0)
            {
                listPool.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));
                bool pForceFinished = DebugConfig.isOn(DebugOption.CityFastConstruction);
                WorldTile closestTile = Toolbox.getClosestTile(listPool, current_tile);
                if (closestTile != null)
                {
                    listPool.Remove(closestTile);
                    MapAction.makeRoadBetween(closestTile, current_tile, pCity, pForceFinished);
                }
                closestTile = Toolbox.getClosestTile(listPool, current_tile);
                if (closestTile != null)
                {
                    MapAction.makeRoadBetween(closestTile, current_tile, pCity, pForceFinished);
                }
            }
            return false;
        }

        // ==========================================
        // 35. City Resource Supply Sister City Allocation
        // ==========================================
        private static bool GetRandomStockpilePrefix(City __instance, ref Building __result)
        {
            if (!LockstepClock.InTick) return true;
            if (__instance == null || !__instance.hasStockpiles())
            {
                __result = null;
                return false;
            }
            List<Building> spList = _cityStockpilesRef != null ? _cityStockpilesRef(__instance) : null;
            if (spList == null || spList.Count == 0)
            {
                __result = null;
                return false;
            }
            List<Building> stockpiles = new List<Building>(spList);
            stockpiles.Sort((a, b) => a.id.CompareTo(b.id));
            int count = stockpiles.Count;
            int offset = Randy.randomInt(0, count);
            for (int i = 0; i < count; i++)
            {
                Building item = stockpiles[(offset + i) % count];
                if (item != null && item.isUsable())
                {
                    __result = item;
                    return false;
                }
            }
            __result = null;
            return false;
        }

        private static bool CityBehSupplyKingdomCitiesExecutePrefix(CityBehSupplyKingdomCities __instance, City pCity, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            Kingdom cityKingdom = _cityKingdomRef != null ? _cityKingdomRef(pCity) : null;
            if (pCity == null || !pCity.hasCulture() || cityKingdom == null || cityKingdom.countCities() == 1 || pCity.data.timer_supply > 0f || !pCity.hasStockpiles())
            {
                __result = BehResult.Stop;
                return false;
            }
            Building randomStockpile = pCity.getRandomStockpile();
            if (randomStockpile == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            List<CityStorageSlot> listPool = new List<CityStorageSlot>();
            foreach (CityStorageSlot slot in randomStockpile.resources.getSlots())
            {
                if (slot.amount > slot.asset.supply_bound_give)
                {
                    listPool.Add(slot);
                }
            }
            if (listPool.Count == 0)
            {
                __result = BehResult.Stop;
                return false;
            }
            listPool.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

            List<City> sisterCities = new List<City>(cityKingdom.getCities());
            sisterCities.Sort((a, b) => a.id.CompareTo(b.id));

            int cCount = sisterCities.Count;
            int cOffset = Randy.randomInt(0, cCount);
            for (int i = 0; i < cCount; i++)
            {
                City item = sisterCities[(cOffset + i) % cCount];
                if (item == pCity) continue;
                int sCount = listPool.Count;
                int sOffset = Randy.randomInt(0, sCount);
                for (int j = 0; j < sCount; j++)
                {
                    CityStorageSlot item2 = listPool[(sOffset + j) % sCount];
                    if (item.getResourcesAmount(item2.id) < item2.asset.supply_bound_take)
                    {
                        pCity.takeResource(item2.id, item2.asset.supply_give);
                        item.addResourcesToRandomStockpile(item2.id, item2.asset.supply_give);

                        float num = 30f;
                        BaseStats lStats = pCity.hasLeader() && _baseStatsRef != null ? _baseStatsRef(pCity.leader) : null;
                        if (lStats != null)
                        {
                            num *= lStats["multiplier_supply_timer"];
                        }
                        if (num < 10f) num = 10f;
                        pCity.data.timer_supply = num;

                        __result = BehResult.Continue;
                        return false;
                    }
                }
            }
            __result = BehResult.Continue;
            return false;
        }

        // ==========================================
        // 36. Actor Target Search In City Zones
        // ==========================================
        private static bool FindNewBuildingTargetPrefix(Actor pActor, string pType, bool pOnlyFreeTile, ref Building __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.city == null) { __result = null; return false; }
            using ListPool<Building> listPool = new ListPool<Building>(64);
            switch (pType)
            {
                case "new_building":
                {
                    Building buildingToBuild = _getBuildingToBuildMethod != null ? (Building)_getBuildingToBuildMethod.Invoke(pActor.city, null) : null;
                    List<WorldTile> bTiles = buildingToBuild != null && _buildingTilesRef != null ? _buildingTilesRef(buildingToBuild) : null;
                    if (buildingToBuild != null && bTiles != null && bTiles.Count != 0)
                    {
                        WorldTile constructionTile = buildingToBuild.getConstructionTile();
                        if (constructionTile != null && constructionTile.isSameIsland(pActor.current_tile))
                        {
                            __result = buildingToBuild;
                            return false;
                        }
                    }
                    break;
                }
                case "random_house_building":
                {
                    if (pActor.city.buildings != null && pActor.city.buildings.Count > 0)
                    {
                        List<Building> bList = new List<Building>(pActor.city.buildings);
                        bList.Sort((a, b) => a.id.CompareTo(b.id));
                        int bCount = bList.Count;
                        int bOffset = Randy.randomInt(0, bCount);
                        for (int i = 0; i < bCount; i++)
                        {
                            Building item = bList[(bOffset + i) % bCount];
                            BuildingAsset bAsset = _buildingAssetRef != null ? _buildingAssetRef(item) : null;
                            if (item.isSameIslandAs(pActor) && !item.isUnderConstruction() && item.isUsable() && bAsset != null && bAsset.hasHousingSlots())
                            {
                                __result = item;
                                return false;
                            }
                        }
                    }
                    break;
                }
                case "ruins":
                {
                    List<TileZone> cZones = _cityZonesRef != null ? _cityZonesRef(pActor.city) : null;
                    if (cZones != null && cZones.Count > 0)
                    {
                        List<TileZone> zList = new List<TileZone>(cZones);
                        zList.Sort((a, b) => a.id.CompareTo(b.id));
                        int zCount = zList.Count;
                        int zOffset = Randy.randomInt(0, zCount);
                        for (int i = 0; i < zCount; i++)
                        {
                            TileZone item2 = zList[(zOffset + i) % zCount];
                            HashSet<Building> hashset = item2.getHashset(BuildingList.Ruins);
                            if (hashset != null) listPool.AddRange(hashset);
                            HashSet<Building> hashset2 = item2.getHashset(BuildingList.Abandoned);
                            if (hashset2 != null) listPool.AddRange(hashset2);
                            if (listPool.Count > 0) break;
                        }
                    }
                    break;
                }
                case "type_mine":
                case "type_bonfire":
                case "type_training_dummies":
                {
                    Building buildingOfType = _getBuildingOfTypeMethod != null
                        ? (Building)_getBuildingOfTypeMethod.Invoke(pActor.city, new object[] { pType, true, false, pOnlyFreeTile, pActor.current_island })
                        : null;
                    if (buildingOfType != null)
                    {
                        __result = buildingOfType;
                        return false;
                    }
                    break;
                }
                default:
                {
                    __result = ai.ActorTool.findNewTargetInZones(pActor, pType, listPool);
                    return false;
                }
            }
            if (listPool.Count == 0)
            {
                __result = null;
                return false;
            }
            listPool.Sort((a, b) => a.id.CompareTo(b.id));
            __result = listPool[Randy.randomInt(0, listPool.Count)];
            return false;
        }

        private static bool FindNewTargetInZonesPrefix(Actor pActor, string pType, ListPool<Building> pPossibleBuildings, ref Building __result)
        {
            if (!LockstepClock.InTick) return true;
            List<TileZone> cZones2 = pActor?.city != null && _cityZonesRef != null ? _cityZonesRef(pActor.city) : null;
            if (pActor == null || pActor.city == null || cZones2 == null) { __result = null; return false; }
            List<TileZone> sortedZones = new List<TileZone>(cZones2);
            sortedZones.Sort((a, b) => a.id.CompareTo(b.id));
            for (int i = 0; i < sortedZones.Count; i++)
            {
                TileZone zone = sortedZones[i];
                HashSet<Building> hashSet = null;
                switch (pType)
                {
                    case "type_flower":
                    case "type_vegetation":
                        hashSet = zone.getHashset(BuildingList.Flora);
                        break;
                    case "type_tree":
                        hashSet = zone.getHashset(BuildingList.Trees);
                        break;
                    case "type_mineral":
                        hashSet = zone.getHashset(BuildingList.Minerals);
                        break;
                    case "type_fruits":
                        hashSet = zone.getHashset(BuildingList.Food);
                        break;
                    case "type_hive":
                        hashSet = zone.getHashset(BuildingList.Hives);
                        break;
                    case "type_poop":
                        hashSet = zone.getHashset(BuildingList.Poops);
                        break;
                }
                if (hashSet == null || hashSet.Count == 0) continue;
                List<Building> candidatesInZone = new List<Building>(hashSet);
                candidatesInZone.Sort((a, b) => a.id.CompareTo(b.id));
                for (int j = 0; j < candidatesInZone.Count; j++)
                {
                    Building item = candidatesInZone[j];
                    BuildingAsset asset = _buildingAssetRef != null ? _buildingAssetRef(item) : null;
                    if (asset != null && !item.current_tile.isTargeted() && item.isSameIslandAs(pActor) &&
                        ((asset.building_type != BuildingType.Building_Fruits && asset.building_type != BuildingType.Building_Tree) || item.hasResourcesToCollect()) &&
                        (asset.building_type != BuildingType.Building_Tree || asset.can_be_chopped_down))
                    {
                        pPossibleBuildings.Add(item);
                    }
                }
                if (pPossibleBuildings.Count > 0) break;
            }
            if (pPossibleBuildings.Count == 0)
            {
                __result = null;
                return false;
            }
            pPossibleBuildings.Sort((a, b) => a.id.CompareTo(b.id));
            __result = pPossibleBuildings[Randy.randomInt(0, pPossibleBuildings.Count)];
            return false;
        }

        // ==========================================
        // 37. City Soldier Attack Zone Selection
        // ==========================================
        private static bool BehCityActorCheckAttackExecutePrefix(BehCityActorCheckAttack __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.city == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            TileZone target_attack_zone = _targetAttackZoneRef != null ? _targetAttackZoneRef(pActor.city) : null;
            if (target_attack_zone == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            City city = target_attack_zone.city;
            if (city == null) { __result = BehResult.Stop; return false; }
            if (pActor.army != null && pActor.army.isGroupInCityAndHaveLeader() && !pActor.city.isOkToSendArmy())
            {
                __result = BehResult.Stop;
                return false;
            }
            if (pActor.current_tile == null || target_attack_zone.centerTile == null || !target_attack_zone.centerTile.isSameIsland(pActor.current_tile))
            {
                __result = BehResult.Stop;
                return false;
            }
            if (pActor.current_tile.zone == null || pActor.current_tile.zone.city != target_attack_zone.city)
            {
                if (target_attack_zone.tiles != null && target_attack_zone.tiles.Length > 0)
                {
                    if (_behTileTargetRef != null) _behTileTargetRef(pActor) = target_attack_zone.tiles[Randy.randomInt(0, target_attack_zone.tiles.Length)];
                    __result = BehResult.Continue;
                    return false;
                }
            }
            Building buildingOfType = _getBuildingOfTypeMethod != null
                ? (Building)_getBuildingOfTypeMethod.Invoke(city, new object[] { "type_watch_tower", false, false, false, pActor.current_island })
                : null;
            if (buildingOfType != null && buildingOfType.current_tile != null && buildingOfType.current_tile.region != null && buildingOfType.current_tile.region.tiles != null && buildingOfType.current_tile.region.tiles.Count > 0)
            {
                List<WorldTile> rt = buildingOfType.current_tile.region.tiles;
                if (_behTileTargetRef != null) _behTileTargetRef(pActor) = rt[Randy.randomInt(0, rt.Count)];
                __result = BehResult.Continue;
                return false;
            }
            TileZone[] neighbours_all = (pActor.current_tile.zone != null) ? pActor.current_tile.zone.neighbours_all : null;
            if (neighbours_all != null && neighbours_all.Length > 0)
            {
                List<TileZone> sortedNeighbours = new List<TileZone>(neighbours_all);
                sortedNeighbours.Sort((a, b) => a.id.CompareTo(b.id));
                for (int i = 0; i < sortedNeighbours.Count; i++)
                {
                    TileZone tileZone = sortedNeighbours[i];
                    if (tileZone.city == city && tileZone.tiles != null && tileZone.tiles.Length > 0)
                    {
                        WorldTile random = tileZone.tiles[Randy.randomInt(0, tileZone.tiles.Length)];
                        if (random.isSameIsland(pActor.current_tile))
                        {
                            if (_behTileTargetRef != null) _behTileTargetRef(pActor) = random;
                            __result = BehResult.Continue;
                            return false;
                        }
                    }
                }
            }
            List<TileZone> cZones = _cityZonesRef != null ? _cityZonesRef(city) : null;
            if (cZones != null && cZones.Count > 0)
            {
                List<TileZone> cityZones = new List<TileZone>(cZones);
                cityZones.Sort((a, b) => a.id.CompareTo(b.id));
                for (int i = 0; i < cityZones.Count; i++)
                {
                    TileZone zone = cityZones[i];
                    if (zone.tiles != null && zone.tiles.Length > 0)
                    {
                        WorldTile random2 = zone.tiles[Randy.randomInt(0, zone.tiles.Length)];
                        if (random2.isSameIsland(pActor.current_tile))
                        {
                            if (_behTileTargetRef != null) _behTileTargetRef(pActor) = random2;
                            __result = BehResult.Continue;
                            return false;
                        }
                    }
                }
            }
            __result = BehResult.Stop;
            return false;
        }

        // ==========================================
        // 38. War History Pruning Equal Died Time Tie-Break
        // ==========================================
        private static bool CheckDeadObjectsPrefix(WarManager __instance)
        {
            if (!LockstepClock.InTick) return true;
            if (__instance == null || __instance.Count <= 20) return false;
            List<War> endWars = _warManagerEndWarsRef != null ? _warManagerEndWarsRef(__instance) : null;
            if (endWars == null) return true;
            endWars.Clear();
            using (IEnumerator<War> enumerator = __instance.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    War current = enumerator.Current;
                    if (!current.isFavorite() && current.hasEnded())
                    {
                        endWars.Add(current);
                    }
                }
            }
            if (endWars.Count != 0)
            {
                int b2 = __instance.Count - 20;
                endWars.Sort((War a, War b) =>
                {
                    int cmp = a.data.died_time.CompareTo(b.data.died_time);
                    if (cmp != 0) return cmp;
                    return a.id.CompareTo(b.id);
                });
                int num = Mathf.Min(endWars.Count, b2);
                for (int i = 0; i < num; i++)
                {
                    War pObject = endWars[i];
                    if (_removeWarObjectMethod != null)
                    {
                        _removeWarObjectMethod.Invoke(__instance, new object[] { pObject });
                    }
                }
                endWars.Clear();
            }
            return false;
        }

        // ==========================================
        // 39. Allied War Entry Unsorted Hashset Iteration
        // ==========================================
        private static bool StartWarPrefix(DiplomacyManager __instance, Kingdom pAttacker, Kingdom pDefender, WarTypeAsset pAsset, bool pLog, ref War __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pAsset == null || pAsset.total_war) return true;
            if (pAttacker == pDefender) { __result = null; return false; }
            if (World.world.wars.getWar(pAttacker, pDefender) != null) { __result = null; return false; }
            if (pLog)
            {
                WorldLog.logNewWar(pAttacker, pDefender);
            }
            War war = World.world.wars.newWar(pAttacker, pDefender, pAsset);
            if (pAsset.alliance_join)
            {
                Alliance alliance = pAttacker.getAlliance();
                Alliance alliance2 = pDefender.getAlliance();
                if (alliance != null && alliance.kingdoms_hashset != null)
                {
                    List<Kingdom> attackers = new List<Kingdom>(alliance.kingdoms_hashset);
                    attackers.Sort((a, b) => a.id.CompareTo(b.id));
                    for (int i = 0; i < attackers.Count; i++)
                    {
                        war.joinAttackers(attackers[i]);
                    }
                }
                if (alliance2 != null && alliance2.kingdoms_hashset != null)
                {
                    List<Kingdom> defenders = new List<Kingdom>(alliance2.kingdoms_hashset);
                    defenders.Sort((a, b) => a.id.CompareTo(b.id));
                    for (int i = 0; i < defenders.Count; i++)
                    {
                        war.joinDefenders(defenders[i]);
                    }
                }
            }
            __result = war;
            return false;
        }

        private static bool StartRebellionPrefix(Actor pActor, Plot pPlot, bool pCheckForHappiness)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.city == null) return true;
            City city = pActor.city;
            Kingdom kingdom = _cityKingdomRef != null ? _cityKingdomRef(city) : null;
            if (kingdom == null) return true;
            if (pActor.isCityLeader())
            {
                pActor.city.removeLeader();
            }
            Kingdom kingdom2 = _makeOwnKingdomMethod != null
                ? (Kingdom)_makeOwnKingdomMethod.Invoke(city, new object[] { pActor, true, false })
                : null;
            if (kingdom2 == null) return true;
            using ListPool<City> listPool = new ListPool<City>();
            listPool.Add(city);
            pActor.joinCity(city);
            War war = null;
            foreach (War war2 in kingdom.getWars())
            {
                if (war2.isMainAttacker(kingdom) && war2.getAsset() == WarTypeLibrary.rebellion)
                {
                    war = war2;
                    war.joinDefenders(kingdom2);
                    break;
                }
            }
            if (war == null)
            {
                war = _startWarMethod != null
                    ? (War)_startWarMethod.Invoke(World.world.diplomacy, new object[] { kingdom, kingdom2, WarTypeLibrary.rebellion, true })
                    : null;
                if (war != null && kingdom.hasAlliance() && kingdom.getAlliance() != null && kingdom.getAlliance().kingdoms_hashset != null)
                {
                    List<Kingdom> allies = new List<Kingdom>(kingdom.getAlliance().kingdoms_hashset);
                    allies.Sort((a, b) => a.id.CompareTo(b.id));
                    for (int i = 0; i < allies.Count; i++)
                    {
                        Kingdom item = allies[i];
                        if (item != kingdom && item.isOpinionTowardsKingdomGood(kingdom))
                        {
                            war.joinAttackers(item);
                        }
                    }
                }
            }
            return false;
        }

        // ==========================================
        // 40. Tile Island Swim To Safety Equal Distance Region Tie-Break
        // ==========================================
        private static bool BehGoToStablePlaceExecutePrefix(BehGoToStablePlace __instance, Actor pActor, ref BehResult __result)
        {
            if (!LockstepClock.InTick) return true;
            if (pActor == null || pActor.current_tile == null || pActor.current_tile.region == null || pActor.current_tile.region.island == null)
            {
                __result = BehResult.Stop;
                return false;
            }
            TileIsland island = pActor.current_tile.region.island;
            bool isGood = _isGoodIslandFunc != null ? _isGoodIslandFunc(island, pActor) : true;
            if (isGood)
            {
                __result = BehResult.Stop;
                return false;
            }
            MapRegion bestNearby = FindIslandNearbyDeterministic(pActor);
            if (bestNearby != null && bestNearby.tiles != null && bestNearby.tiles.Count > 0)
            {
                if (_behTileTargetRef != null) _behTileTargetRef(pActor) = bestNearby.tiles[Randy.randomInt(0, bestNearby.tiles.Count)];
                __result = BehResult.Continue;
                return false;
            }
            List<KeyValuePair<int, MapRegion>> candidateRegions = new List<KeyValuePair<int, MapRegion>>(4);
            Vector2Int pos = pActor.current_tile.pos;
            IslandsCalculator ic = _islandsCalculatorRef != null ? _islandsCalculatorRef(World.world) : null;
            if (ic == null || ic.islands == null) return true;
            ListPool<TileIsland> islandsList = ic.islands;
            for (int i = 0; i < islandsList.Count; i++)
            {
                TileIsland tileIsland = islandsList[i];
                if (!CheckIslandDeterministic(island, tileIsland, pActor)) continue;
                SelectBorderRegionsDeterministic(tileIsland, island, out var pOut, out var pOut2);
                MapRegion mapRegion = null;
                int num = int.MaxValue;
                foreach (MapRegion item in pOut)
                {
                    if (pOut2.Contains(item) && item.tiles != null && item.tiles.Count > 0)
                    {
                        int num2 = Toolbox.SquaredDist(pos.x, pos.y, item.tiles[0].pos.x, item.tiles[0].pos.y);
                        if (num2 < num || (num2 == num && mapRegion != null && item.id < mapRegion.id))
                        {
                            num = num2;
                            mapRegion = item;
                        }
                    }
                }
                if (mapRegion == null) continue;
                List<WorldTile> edgeTiles = mapRegion.getEdgeTiles();
                if (edgeTiles == null || edgeTiles.Count == 0) continue;
                WorldTile randomEdge = edgeTiles[Randy.randomInt(0, edgeTiles.Count)];
                float num3 = Toolbox.DistTile(pActor.current_tile, randomEdge);
                if (candidateRegions.Count > 0 && (float)(candidateRegions[0].Key + 15) < num3) continue;
                if (candidateRegions.Count < 4)
                {
                    candidateRegions.Add(new KeyValuePair<int, MapRegion>((int)num3, mapRegion));
                    continue;
                }
                candidateRegions.Sort((x, y) =>
                {
                    int cmp = x.Key.CompareTo(y.Key);
                    if (cmp != 0) return cmp;
                    return x.Value.id.CompareTo(y.Value.id);
                });
                if ((float)candidateRegions[3].Key > num3)
                {
                    candidateRegions[3] = new KeyValuePair<int, MapRegion>((int)num3, mapRegion);
                }
            }
            if (candidateRegions.Count > 0)
            {
                candidateRegions.Sort((x, y) =>
                {
                    int cmp = x.Key.CompareTo(y.Key);
                    if (cmp != 0) return cmp;
                    return x.Value.id.CompareTo(y.Value.id);
                });
                int bestKey = candidateRegions[0].Key;
                candidateRegions.RemoveAll(x => x.Key - 15 > bestKey);
            }
            if (Randy.randomChance(0.8f) && candidateRegions.Count > 0)
            {
                MapRegion chosen = candidateRegions[Randy.randomInt(0, candidateRegions.Count)].Value;
                if (chosen.tiles != null && chosen.tiles.Count > 0)
                {
                    if (_behTileTargetRef != null) _behTileTargetRef(pActor) = chosen.tiles[Randy.randomInt(0, chosen.tiles.Count)];
                }
            }
            else
            {
                MapRegion mapRegion3 = (!Randy.randomChance(0.5f) && pActor.current_tile.region.neighbours != null && pActor.current_tile.region.neighbours.Count > 0)
                    ? pActor.current_tile.region.neighbours[Randy.randomInt(0, pActor.current_tile.region.neighbours.Count)]
                    : pActor.current_tile.region;
                if (mapRegion3 != null && mapRegion3.tiles != null && mapRegion3.tiles.Count > 0)
                {
                    if (_behTileTargetRef != null) _behTileTargetRef(pActor) = mapRegion3.tiles[Randy.randomInt(0, mapRegion3.tiles.Count)];
                }
            }
            __result = BehResult.Continue;
            return false;
        }

        private static void SelectBorderRegionsDeterministic(TileIsland pIsland1, TileIsland pIsland2, out HashSet<MapRegion> pOut1, out HashSet<MapRegion> pOut2)
        {
            if (pIsland1.outsideRegionEdges.Count + pIsland2.insideRegionEdges.Count < pIsland1.insideRegionEdges.Count + pIsland2.outsideRegionEdges.Count)
            {
                if (pIsland1.outsideRegionEdges.Count > pIsland2.insideRegionEdges.Count)
                {
                    pOut1 = pIsland2.insideRegionEdges;
                    pOut2 = pIsland1.outsideRegionEdges;
                }
                else
                {
                    pOut2 = pIsland2.insideRegionEdges;
                    pOut1 = pIsland1.outsideRegionEdges;
                }
            }
            else if (pIsland1.insideRegionEdges.Count > pIsland2.outsideRegionEdges.Count)
            {
                pOut1 = pIsland2.outsideRegionEdges;
                pOut2 = pIsland1.insideRegionEdges;
            }
            else
            {
                pOut2 = pIsland2.outsideRegionEdges;
                pOut1 = pIsland1.insideRegionEdges;
            }
        }

        private static bool CheckIslandDeterministic(TileIsland pCurrentIsland, TileIsland pIsland, Actor pActor)
        {
            if (pCurrentIsland == pIsland) return false;
            bool isGood = _isGoodIslandFunc != null ? _isGoodIslandFunc(pIsland, pActor) : true;
            if (!isGood) return false;
            if (!((pCurrentIsland.getTileCount() <= pIsland.getTileCount()) ? pCurrentIsland.isConnectedWith(pIsland) : pIsland.isConnectedWith(pCurrentIsland))) return false;
            if (pIsland.insideRegionEdges.Count == 0) return false;
            return true;
        }

        private static MapRegion FindIslandNearbyDeterministic(Actor pActor)
        {
            if (pActor?.current_tile == null) return null;
            MapChunk curChunk = pActor.current_tile.chunk;
            if (curChunk == null) return null;
            MapChunk[] neighbours = curChunk.neighbours_all;
            int totalChunks = 1 + (neighbours?.Length ?? 0);
            TileIsland island = pActor.current_tile.region?.island;
            MapRegion bestReg = null;
            int bestDist = int.MaxValue;
            for (int i = 0; i < totalChunks; i++)
            {
                MapChunk mapChunk = i == 0 ? curChunk : neighbours[i - 1];
                if (mapChunk == null || mapChunk.regions == null) continue;
                for (int j = 0; j < mapChunk.regions.Count; j++)
                {
                    MapRegion mapRegion = mapChunk.regions[j];
                    if (mapRegion == null || !CheckIslandDeterministic(island, mapRegion.island, pActor)) continue;
                    List<WorldTile> edgeTiles = mapRegion.getEdgeTiles();
                    if (edgeTiles != null && edgeTiles.Count != 0)
                    {
                        WorldTile closestTile = Toolbox.getClosestTile(edgeTiles, pActor.current_tile);
                        if (closestTile != null)
                        {
                            int num = Toolbox.SquaredDistTile(pActor.current_tile, closestTile);
                            if (num < bestDist || (num == bestDist && bestReg != null && mapRegion.id < bestReg.id))
                            {
                                bestReg = mapRegion;
                                bestDist = num;
                            }
                        }
                    }
                }
            }
            return bestReg;
        }

        // ==========================================
        // 41. City Territory Paint Drop Zone Selection Over Hashset Order
        // ==========================================
        private static bool ActionPaintPrefix(WorldTile pTile, string pDropID)
        {
            if (!LockstepClock.InTick) return true;
            if (pTile == null || pTile.zone == null) return false;
            TileZone zone = pTile.zone;
            if (!zone.hasCity()) return false;
            City city = zone.city;
            if (city == null) return false;
            HashSet<TileZone> paintZones = new HashSet<TileZone>();
            World.world.city_zone_helper.city_growth.getZoneToClaim(null, city, pDebug: true, paintZones, 1);
            List<TileZone> candidates = new List<TileZone>();
            foreach (TileZone item in paintZones)
            {
                if (item.hasCity()) continue;
                TileZone[] neighbours = item.neighbours;
                if (neighbours == null) continue;
                for (int i = 0; i < neighbours.Length; i++)
                {
                    if (neighbours[i].city == city)
                    {
                        candidates.Add(item);
                        break;
                    }
                }
            }
            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => a.id.CompareTo(b.id));
                TileZone random = candidates[Randy.randomInt(0, candidates.Count)];
                if (_addZoneMethod != null)
                {
                    _addZoneMethod.Invoke(city, new object[] { random });
                }
                city.setAbandonedZonesDirty();
            }
            return false;
        }

        // ==========================================
        // 42. Enemy Target Search PRNG Race on Parallel Job Threads
        // ==========================================
        /// <summary>A stable draw per object, independent of the shared PRNG and of the tick: parallel
        /// job threads can't race it, and both PCs draw the same value even between ticks.</summary>
        private static float Det01(long pId, int pSalt)
        {
            ulong x = (ulong)(uint)(pSalt ^ unchecked((int)pId)) * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)(pId >> 32) * 0xC2B2AE3D27D4EB4FUL;
            x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33;
            return (x & 0xFFFFFF) / 16777216f;
        }

        private static bool FindEnemyObjectTargetPrefix(BaseSimObject __instance, bool pAttackBuildings, ref BaseSimObject __result)
        {
            if (!LockstepClock.Active) return true;
            if (__instance == null) { __result = null; return false; }
            if (_findEnemiesDel == null || _checkObjectListDel == null) return true;
            __result = FindEnemyTargetDeterministic(__instance, pAttackBuildings);
            return false;
        }

        private static BaseSimObject FindEnemyTargetDeterministic(BaseSimObject pSelf, bool pAttackBuildings)
        {
            if (pSelf == null || pSelf.current_tile == null) return null;
            EnemyFinderData data = _findEnemiesDel(pSelf.current_tile, pSelf.kingdom, -1);
            if (data == null || data.isEmpty()) return null;
            bool flag = true;
            if (data.list.Count > 50)
            {
                // was Randy.randomChance(0.6f): the shared PRNG can't be drawn from parallel job threads
                flag = Det01(pSelf.getID(), 0xF1AC50) < 0.6f;
            }
            // fixed order instead of LoopRandom (which also draws the shared PRNG from job threads)
            return _checkObjectListDel(pSelf, data.list, pAttackBuildings, flag, false, int.MaxValue);
        }

        private static bool CheckEnemyTargetsPrefix(Actor __instance, ref bool __result)
        {
            if (!LockstepClock.Active) return true;
            if (__instance == null) { __result = false; return false; }
            if (_timeoutTargetsRef == null || _aggressionTargetsRef == null) return true;
            if (!__instance.isAllowedToLookForEnemies()) { __result = false; return false; }
            if (__instance.isInWaterAndCantAttack()) { __result = false; return false; }
            if (_strangeUrgeRef != null && _strangeUrgeRef(__instance)) { __result = false; return false; }
            if (_hasAttackTargetRef != null && _hasAttackTargetRef(__instance))
            {
                bool inCombat = false;
                if (_aiActorRef != null && _aiTaskRef != null)
                {
                    AiSystemActor ai = _aiActorRef(__instance);
                    BehaviourTaskActor task = ai != null ? _aiTaskRef(ai) : null;
                    inCombat = task != null && task.in_combat;
                }
                if (!__instance.hasTask() || !inCombat) __instance.setTask("fighting", true, true);
                __result = false;
                return false;
            }
            if (_timeoutTargetsRef(__instance) > 0f) { __result = false; return false; }
            // was 0.1f + Randy.randomFloat(0f, 1f): drawn from parallel job threads, the thread
            // completion order swapped the PRNG sequence between Host and Guest (creature desyncs)
            _timeoutTargetsRef(__instance) = 0.1f + Det01(__instance.getID(), 0x716E7);
            bool canAttackBuildings = __instance.asset != null && __instance.asset.can_attack_buildings;
            BaseSimObject target = _findEnemiesDel != null && _checkObjectListDel != null
                ? FindEnemyTargetDeterministic(__instance, canAttackBuildings)
                : null;
            HashSet<long> aggression = _aggressionTargetsRef(__instance);
            if (target == null && aggression.Count > 0)
            {
                List<Actor> candidates = new List<Actor>(aggression.Count);
                foreach (long aggressionId in aggression)
                {
                    Actor a = World.world.units.get(aggressionId);
                    if (a != null && !a.isRekt()) candidates.Add(a);
                }
                if (candidates.Count > 0)
                {
                    // the hashset's order can differ per PC: sort by actor id before the closest check
                    candidates.Sort((a, b) => a.id.CompareTo(b.id));
                    using ListPool<Actor> listPool = new ListPool<Actor>(candidates.Count);
                    foreach (Actor a in candidates) listPool.Add(a);
                    if (_checkObjectListDel != null)
                    {
                        target = _checkObjectListDel(__instance, listPool, canAttackBuildings, true, true, 30);
                    }
                }
                else
                {
                    aggression.Clear();
                }
            }
            if (target == null) { __result = false; return false; }
            __instance.startFightingWith(target);
            __result = true;
            return false;
        }

        // ==========================================
        // 43. Sleeping Units Status Iteration Order
        // ==========================================
        private static bool CheckSleepingUnitsPrefix(ActorManager __instance)
        {
            if (!LockstepClock.Active) return true;
            if (__instance == null || _sleepTimestampRef == null) return true;
            if (World.world.getWorldTimeElapsedSince(_sleepTimestampRef(__instance)) < 10.0) return false;
            __instance.cached_sleeping_units.Clear();
            _sleepTimestampRef(__instance) = World.world.getCurWorldTime();
            // was World.world.statuses.list.LoopRandom(): the statuses list is rebuilt from a hashset,
            // so its order (and which sleeping units were cached first) differed per PC
            List<Actor> candidates = new List<Actor>();
            foreach (Status item in World.world.statuses.list)
            {
                if (item == null || item.is_finished || item.asset == null || item.asset.id != "sleeping") continue;
                if (item.sim_object is Actor a && a.isAlive()) candidates.Add(a);
            }
            candidates.Sort((x, y) => x.id.CompareTo(y.id));
            List<Actor> cached = __instance.cached_sleeping_units;
            for (int i = 0; i < candidates.Count && cached.Count <= 10; i++) cached.Add(candidates[i]);
            return false;
        }

        // ==========================================
        // 44. Island Docks Trade Target Selection
        // ==========================================
        private static bool GetDockTradeTargetPrefix(ListPool<Docks> pList, Actor pActor, ref Docks __result)
        {
            if (!LockstepClock.Active) return true;
            if (pList == null || pList.Count == 0) { __result = null; return false; }
            if (pActor == null || _docksBuildingRef == null) return true;
            // was pList.LoopRandom(): the docks pool order (and the first usable dock) differed per PC
            List<Docks> candidates = new List<Docks>(pList.Count);
            foreach (Docks item in pList) if (item != null) candidates.Add(item);
            candidates.Sort((a, b) =>
            {
                Building ba = _docksBuildingRef(a);
                Building bb = _docksBuildingRef(b);
                return (ba != null ? ba.id : long.MaxValue).CompareTo(bb != null ? bb.id : long.MaxValue);
            });
            foreach (Docks item in candidates)
            {
                Building building = _docksBuildingRef(item);
                if (building == null) continue;
                if (building.hasCity() && pActor.getHomeBuilding() != building && building.isUsable() && !building.isAbandoned()
                    && building.city != null && _cityKingdomRef != null && _cityKingdomRef(building.city) != null && !_cityKingdomRef(building.city).isEnemy(pActor.kingdom)
                    && (item.isDockGood() || item.hasOceanTiles()))
                {
                    __result = item;
                    return false;
                }
            }
            __result = null;
            return false;
        }

        // ==========================================
        // 45. Civ City Migration Candidate Cities
        // ==========================================
        private static bool GetPotentialCityNearbyPrefix(WorldTile pFromTile, Actor pActor, ref City __result)
        {
            if (!LockstepClock.Active) return true;
            if (pFromTile == null || pActor == null) { __result = null; return false; }
            // was World.world.cities.list.LoopRandom(): the first matching city (and the one a nomad
            // joins) depended on the shuffled draw order; iterate cities sorted by city id instead
            List<City> cities = new List<City>(World.world.cities.list.Count);
            foreach (City c in World.world.cities.list) if (c != null) cities.Add(c);
            cities.Sort((a, b) => a.id.CompareTo(b.id));
            City best = null;
            foreach (City item in cities)
            {
                WorldTile tile = item.getTile();
                if (tile == null || item.isNeutral()) continue;
                float range = SimGlobals.m.nomad_check_far_city_range;
                if (!pActor.isNomad() && _cityKingdomRef != null && _cityKingdomRef(item) != pActor.kingdom) range *= 3f;
                if (Toolbox.DistVec3(pFromTile.posV, _cityCenterRef != null ? _cityCenterRef(item) : tile.posV) > range
                    || !item.isWelcomedToJoin(pActor) || !tile.isSameIsland(pFromTile)) continue;
                if (best != null)
                {
                    if (_cityKingdomRef != null && _cityKingdomRef(item) == pActor.kingdom && _cityKingdomRef(best) != pActor.kingdom) best = item;
                }
                else
                {
                    best = item;
                }
            }
            __result = best;
            return false;
        }

        // ==========================================
        // 46. Building Placement Candidate Tiles
        // ==========================================
        private static bool TryToPlaceBuildingPrefix(BuildingAsset pAsset, List<WorldTile> pList, ref bool __result)
        {
            if (!LockstepClock.Active) return true;
            if (pAsset == null || pList == null || pList.Count == 0) { __result = false; return false; }
            if (_canBuildFromFunc == null || _addBuildingFunc == null) return true;
            // was pList.LoopRandom(): the first buildable tile depended on the shuffled draw order
            List<WorldTile> tiles = new List<WorldTile>(pList.Count);
            foreach (WorldTile t in pList) if (t != null) tiles.Add(t);
            tiles.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));
            foreach (WorldTile item in tiles)
            {
                if (_canBuildFromFunc(World.world.buildings, item, pAsset, null, BuildPlacingType.New, false))
                {
                    if (_addBuildingFunc(World.world.buildings, pAsset, item, false, false, BuildPlacingType.New) != null)
                    {
                        __result = true;
                        return false;
                    }
                    break;
                }
            }
            __result = false;
            return false;
        }

        // ==========================================
        // 47. Newborn Trait Inheritance Candidate Order
        // ==========================================
        private static bool TraitsInheritPrefix(Actor pActorTarget, Actor pParent1, Actor pParent2)
        {
            if (!LockstepClock.Active) return true;
            if (pActorTarget == null || pParent1 == null) return true;
            // was getTraits() iteration order (an IReadOnlyCollection whose order can differ per PC)
            // feeding GetRandom() draws: sort the inherited trait pool by trait id instead
            using ListPool<ActorTrait> pool = new ListPool<ActorTrait>(128);
            int counter = AddParentTraitsSorted(pParent1, pool);
            int counter2 = pParent2 != null ? AddParentTraitsSorted(pParent2, pool) : 0;
            if (pool.Count != 0)
            {
                int amount = Mathf.Max(1, (int)((counter + counter2) * 0.25f));
                for (int i = 0; i < amount; i++)
                {
                    ActorTrait random = pool.GetRandom();
                    pActorTarget.addTrait(random.id);
                }
            }
            return false;
        }

        private static int AddParentTraitsSorted(Actor pParent, ListPool<ActorTrait> pList)
        {
            List<ActorTrait> traits = new List<ActorTrait>();
            foreach (ActorTrait trait in pParent.getTraits())
            {
                if (trait != null && (trait.rate_inherit != 0 || trait.rate_birth != 0)) traits.Add(trait);
            }
            traits.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            int num = 0;
            foreach (ActorTrait trait in traits)
            {
                num++;
                pList.AddTimes(trait.rate_birth, trait);
                pList.AddTimes(trait.rate_inherit, trait);
            }
            return num;
        }

        // ==========================================
        // 48. Subspecies Mutation Trait Selection
        // ==========================================
        private static bool GetRandomMutationTraitToAddPrefix(SubspeciesTraitLibrary __instance, ref SubspeciesTrait __result)
        {
            if (!LockstepClock.Active) return true;
            __result = PickMutationTraitSorted(__instance, _potMutationAddRef);
            return __result != null;
        }

        private static bool GetRandomMutationTraitToRemovePrefix(SubspeciesTraitLibrary __instance, ref SubspeciesTrait __result)
        {
            if (!LockstepClock.Active) return true;
            __result = PickMutationTraitSorted(__instance, _potMutationRemoveRef);
            return __result != null;
        }

        private static SubspeciesTrait PickMutationTraitSorted(SubspeciesTraitLibrary pLibrary, AccessTools.FieldRef<SubspeciesTraitLibrary, List<SubspeciesTrait>> pPoolRef)
        {
            if (pLibrary == null || pPoolRef == null) return null;
            List<SubspeciesTrait> pool = pPoolRef(pLibrary);
            if (pool == null || pool.Count == 0) return null; // let the original run (it fails the same way on an empty pot)
            // the pot's order comes from the library list's build order: sort a copy by trait id so the
            // GetRandom() index picks the same trait on every PC
            List<SubspeciesTrait> candidates = new List<SubspeciesTrait>(pool.Count);
            foreach (SubspeciesTrait t in pool) if (t != null) candidates.Add(t);
            candidates.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            return candidates.GetRandom();
        }
    }
}
