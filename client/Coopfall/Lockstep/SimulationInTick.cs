using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ai.behaviours;

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

                _cityTileRef = AccessTools.FieldRefAccess<City, WorldTile>("_city_tile");
                _targetAttackCityRef = AccessTools.FieldRefAccess<City, City>("target_attack_city");
                _targetAttackZoneRef = AccessTools.FieldRefAccess<City, TileZone>("target_attack_zone");
                _cityCenterRef = AccessTools.FieldRefAccess<City, Vector2>("city_center");
                _preferredSubtypesRef = AccessTools.FieldRefAccess<Culture, List<string>>("_preferred_weapons_craft_subtypes");
                _preferredAssetsRef = AccessTools.FieldRefAccess<Culture, List<EquipmentAsset>>("_preferred_weapons_craft_assets");
                _cityZonesRef = AccessTools.FieldRefAccess<City, List<TileZone>>("zones");
                _removeZoneMethod = AccessTools.Method(typeof(City), "removeZone", new[] { typeof(TileZone) });
                _addZoneMethod = AccessTools.Method(typeof(City), "addZone", new[] { typeof(TileZone) });
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
    }
}
