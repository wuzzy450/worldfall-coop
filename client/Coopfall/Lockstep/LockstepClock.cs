using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Takes over WorldBox's simulation step. While Active, the world only advances through
    /// RunTicks: a fixed step size, Randy reseeded from (Seed, Tick) before every tick (so
    /// randomness used by drawing/UI between ticks can't leak into the simulation), parallel jobs
    /// on one thread, and only up to the tick the session has granted.
    /// </summary>
    public static class LockstepClock
    {
        public static bool Active;
        /// <summary>Ticks done since Start.</summary>
        public static long Tick;
        /// <summary>The world may advance while Tick &lt; Granted.</summary>
        public static long Granted;
        public static int Seed = 1;
        /// <summary>Simulation time per tick, the same on every machine.</summary>
        public static float StepElapsed = DefaultStep;
        /// <summary>
        /// Unity's default fixed step. Never read Time.fixedDeltaTime for this: Worldfall changes
        /// it while the game runs, so it differs between PCs and from moment to moment.
        /// </summary>
        public const float DefaultStep = 0.02f;
        public static int MaxPerFrame = 5;
        /// <summary>Called after each tick with the new Tick.</summary>
        public static event Action<long> AfterTick;
        /// <summary>Called right before each tick with the current Tick.</summary>
        public static event Action<long> BeforeTick;

        public static bool Installed { get; private set; }
        /// <summary>True while a lockstep tick is running.</summary>
        public static bool InTick => _inTickAll;
        private static bool _inTickAll;

        private static Action<MapBox, float> _updateSimulation;
        private static Action<MapBox> _updateFinish;
        private static AccessTools.FieldRef<MapBox, bool> _isPaused;
        private static AccessTools.FieldRef<MapBox, ParallelOptions> _parallel;
        private static AccessTools.FieldRef<MapBox, float> _elapsed, _deltaTime, _fixedDeltaTime;
        private static AccessTools.FieldRef<MapBox, GameStats> _gameStats;
        private static AccessTools.FieldRef<GameStats, GameStatsData> _gameStatsData;
        private static AccessTools.FieldRef<Actor, double> _staminaStamp;
        /// <summary>
        /// "Session time" in WorldBox is the player's real play time (it adds Time.deltaTime every
        /// frame), yet creatures use it for eating/stamina/social cooldowns. During a tick it reads a
        /// shared clock instead. Starts above 0 so the game's "0 = never" timestamps read as long ago.
        /// </summary>
        public const double SessionBase = 1000.0;
        public static double SessionTime => SessionBase + Tick * (double)DefaultStep;
        private static bool _inTick;
        private static AccessTools.FieldRef<WorldBehaviour, float> _behTimer;
        private static AccessTools.FieldRef<WorldBehaviour, WorldBehaviourAsset> _behAsset;

        public static bool Install()
        {
            if (Installed) return true;
            try
            {
                var h = new Harmony("coopfall.lockstep");
                MethodInfo main = AccessTools.Method(typeof(MapBox), "checkMainSimulationUpdate");
                MethodInfo sim = AccessTools.Method(typeof(MapBox), "updateSimulation");
                MethodInfo fin = AccessTools.Method(typeof(MapBox), "updateFinish");
                MethodInfo delayed = AccessTools.Method(typeof(DelayedActionsManager), "update");
                MethodInfo cooldowns = AccessTools.Method(typeof(Actor), "setupRandomDecisionCooldowns");
                MethodInfo register = AccessTools.Method(typeof(Actor), "registerDecisions");
                MethodInfo worldBeh = AccessTools.Method(typeof(WorldBehaviour), "update");
                MethodInfo windowOnScreen = AccessTools.Method(typeof(MapBox), "isWindowOnScreen");
                MethodInfo chunks = AccessTools.Method(typeof(MapChunkManager), "update");
                MethodInfo layerDraw = AccessTools.Method(typeof(MapLayer), "draw");
                _updateDirty = AccessTools.Method(typeof(MapLayer), "UpdateDirty");
                MethodInfo firstLoad = AccessTools.Method(typeof(SaveManager), "loadSubspecies");
                MethodInfo actorCmp = AccessTools.Method(typeof(Actor), "CompareTo", new[] { typeof(Actor) });
                MethodInfo buildingCmp = AccessTools.Method(typeof(Building), "CompareTo", new[] { typeof(Building) });
                if (main == null || sim == null || fin == null || delayed == null)
                {
                    Log.Error("lockstep: game methods not found (WorldBox changed?) main=" + (main != null) + " sim=" + (sim != null) + " finish=" + (fin != null) + " delayed=" + (delayed != null));
                    return false;
                }
                _updateSimulation = AccessTools.MethodDelegate<Action<MapBox, float>>(sim);
                _updateFinish = AccessTools.MethodDelegate<Action<MapBox>>(fin);
                _isPaused = AccessTools.FieldRefAccess<MapBox, bool>("_is_paused");
                _parallel = AccessTools.FieldRefAccess<MapBox, ParallelOptions>("parallel_options");
                _elapsed = AccessTools.FieldRefAccess<MapBox, float>("elapsed");
                _deltaTime = AccessTools.FieldRefAccess<MapBox, float>("delta_time");
                _fixedDeltaTime = AccessTools.FieldRefAccess<MapBox, float>("fixed_delta_time");
                _gameStats = AccessTools.FieldRefAccess<MapBox, GameStats>("game_stats");
                _gameStatsData = AccessTools.FieldRefAccess<GameStats, GameStatsData>("data");
                _staminaStamp = AccessTools.FieldRefAccess<Actor, double>("_last_stamina_reduce_timestamp");
                h.Patch(main, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(MainPrefix)));
                h.Patch(delayed, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(DelayedPrefix)));
                if (cooldowns != null) h.Patch(cooldowns, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(CooldownsPrefix)));
                else Log.Error("lockstep: Actor.setupRandomDecisionCooldowns not found: loading will roll different timers on each PC");
                if (register != null) h.Patch(register, postfix: new HarmonyMethod(typeof(LockstepClock), nameof(RegisterPostfix)));
                else Log.Error("lockstep: Actor.registerDecisions not found: decision order may differ between PCs");
                _behTimer = AccessTools.FieldRefAccess<WorldBehaviour, float>("_timer");
                _behAsset = AccessTools.FieldRefAccess<WorldBehaviour, WorldBehaviourAsset>("_asset");
                if (worldBeh != null) h.Patch(worldBeh, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(WorldBehaviourPrefix)));
                else Log.Error("lockstep: WorldBehaviour.update not found: zooming out may change the world");
                MethodInfo actionHappening = AccessTools.Method(typeof(MapBox), "isActionHappening");
                if (actionHappening != null) h.Patch(actionHappening, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(WindowOnScreenPrefix)));
                else Log.Error("lockstep: MapBox.isActionHappening not found: a player's mouse may delay map updates");
                if (windowOnScreen != null) h.Patch(windowOnScreen, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(WindowOnScreenPrefix)));
                else Log.Error("lockstep: MapBox.isWindowOnScreen not found: an open window may change the world");
                if (layerDraw != null && _updateDirty != null) h.Patch(layerDraw, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(LayerDrawPrefix)));
                else Log.Error("lockstep: MapLayer.draw not found: explosions and tile flashes may follow what's on screen");
                if (chunks != null) h.Patch(chunks, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(ChunksPrefix)), postfix: new HarmonyMethod(typeof(LockstepClock), nameof(ChunksPostfix)));
                else Log.Error("lockstep: MapChunkManager.update not found: pathfinding may differ between PCs");
                if (firstLoad != null) h.Patch(firstLoad, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(FreshObjectsPrefix)));
                else Log.Error("lockstep: SaveManager.loadSubspecies not found: loaded objects may carry state from the previous world");
                // Objects compare by a hash handed out from a counter that runs across worlds (and
                // survives object reuse): sort by ID instead
                if (actorCmp != null) h.Patch(actorCmp, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(CompareByIdPrefix)));
                else Log.Error("lockstep: Actor.CompareTo not found: creature sorting may differ between PCs");
                if (buildingCmp != null) h.Patch(buildingCmp, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(CompareByIdPrefix)));
                else Log.Error("lockstep: Building.CompareTo not found: building sorting may differ between PCs");
                MethodInfo loadStep = AccessTools.Method(typeof(SmoothLoader), "doActions");
                _loaderIndex = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(SmoothLoader), "_index"));
                _loaderActions = AccessTools.Field(typeof(SmoothLoader), "_actions");
                _loaderId = AccessTools.Field(AccessTools.TypeByName("MapLoaderContainer"), "id");
                if (loadStep != null) h.Patch(loadStep, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(LoadStepPrefix)));
                else Log.Error("lockstep: SmoothLoader.doActions not found: loading may roll different dice on each PC");
                MethodInfo statsUpdate = AccessTools.Method(typeof(GameStats), "updateStats");
                if (statsUpdate != null) h.Patch(statsUpdate, prefix: new HarmonyMethod(typeof(LockstepClock), nameof(StatsUpdatePrefix)), postfix: new HarmonyMethod(typeof(LockstepClock), nameof(StatsUpdatePostfix)));
                else Log.Error("lockstep: GameStats.updateStats not found: play time may be saved wrong");
                VisualIsolation.Install(h);
                FrameClock.Install(h);
                Installed = true;
                Log.Info("lockstep: installed (Harmony " + typeof(Harmony).Assembly.GetName().Version + ")");
                return true;
            }
            catch (Exception e) { Log.Error("lockstep: install failed: " + e); return false; }
        }

        /// <summary>Freeze the world at tick 0; nothing advances until Granted is raised.</summary>
        /// <summary>The player's real play time (a statistic), kept aside while session time is shared.</summary>
        private static double _realPlayTime;
        private static bool _sessionSwapped;

        private static GameStatsData Stats() => World.world == null ? null : _gameStatsData(_gameStats(World.world));

        /// <summary>
        /// Session time is read by the simulation (cooldowns, building tweens, ...) inside and
        /// outside ticks: while lockstep runs it is the shared clock everywhere. The real play time
        /// keeps counting aside and is what the game saves to its statistics file.
        /// </summary>
        private static void SwapSessionTime(bool shared)
        {
            GameStatsData d = Stats();
            if (d == null || shared == _sessionSwapped) return;
            if (shared) { _realPlayTime = d.gameTime; d.gameTime = SessionTime; }
            else d.gameTime = _realPlayTime;
            _sessionSwapped = shared;
        }

        private static void StatsUpdatePrefix(GameStats __instance)
        {
            if (!_sessionSwapped) return;
            _gameStatsData(__instance).gameTime = _realPlayTime;   // add the frame's time to the real value, save that
        }

        private static void StatsUpdatePostfix(GameStats __instance)
        {
            if (!_sessionSwapped) return;
            _realPlayTime = _gameStatsData(__instance).gameTime;
            _gameStatsData(__instance).gameTime = SessionTime;
        }

        public static void Start(int seed)
        {
            Seed = seed;
            Tick = 0;
            Granted = 0;
            StepElapsed = DefaultStep;   // x1 speed; game speed becomes ticks per frame
            Active = true;
            SwapSessionTime(true);
        }

        public static void Stop()
        {
            Active = false;
            SwapSessionTime(false);
        }

        /// <summary>
        /// Run once a save has finished loading, before tick 0. Loading rolls creatures' decision
        /// cooldowns against the world time of whatever world was open before; roll them again,
        /// seeded per creature, now that the loaded world's time is in place.
        /// </summary>
        public static void NormalizeAfterLoad()
        {
            var sorted = new List<ActorTrait>();
            foreach (Actor a in World.world.units)
            {
                if (a == null) continue;
                // Trait sets iterate in an order that depends on the object's pooled history:
                // rebuild them in ID order so every PC starts from the same layout.
                sorted.Clear();
                sorted.AddRange(a.traits);
                sorted.Sort((x, y) => string.CompareOrdinal(x.id, y.id));
                a.traits.Clear();
                foreach (ActorTrait t in sorted) a.traits.Add(t);
                SortDecisions(a);
                a.setupRandomDecisionCooldowns();
                _staminaStamp(a) = 0;   // session-time stamp; not saved and not reset on reused objects
            }
            RebuildBatches(World.world.units);
            RebuildBatches(World.world.buildings);
            ResetJobSkips(World.world.units);
            ResetJobSkips(World.world.buildings);
            ResetStatics();
            // tiles are reused between worlds of the same size; their "walls around" cache isn't
            // reset by loading
            FieldInfo flashState = AccessTools.Field(typeof(WorldTile), "flash_state");
            foreach (WorldTile t in (WorldTile[])AccessTools.Field(typeof(MapBox), "tiles_list").GetValue(World.world))
            {
                t.wall_check_dirty = true;
                flashState.SetValue(t, 0);   // flash effect; the pending list is cleared too
            }
            SortTileSets();
            foreach (MapChunk c in ((MapChunkManager)AccessTools.Field(typeof(MapBox), "map_chunk_manager").GetValue(World.world)).chunks)
                SortChunkObjects(c.objects);
            // world behaviour timers aren't in the save and carry over from the previous world
            if (_behTimer != null)
                foreach (WorldBehaviourAsset b in AssetManager.world_behaviours.list)
                    if (b.manager != null) _behTimer(b.manager) = b.interval;
        }

        /// <summary>
        /// The game's version skips world behaviours (plant growth etc.) while the camera is zoomed
        /// out to the minimap, so each player's zoom would change the world. Same logic, no camera.
        /// </summary>
        private static bool WorldBehaviourPrefix(WorldBehaviour __instance, float pElapsed)
        {
            if (!Active) return true;
            WorldBehaviourAsset asset = _behAsset(__instance);
            ref float timer = ref _behTimer(__instance);
            if (timer > 0f)
            {
                timer -= pElapsed;
                if (timer > 0f) return false;
            }
            timer += asset.interval + Randy.randomFloat(0f, asset.interval_random);
            asset.action();
            SectionTrace.Mark("world behaviour " + asset.id);
            return false;
        }

        /// <summary>Static simulation timers that aren't saved and carry over from the previous world.</summary>
        private static void ResetStatics()
        {
            SetStatic("WorldBehaviourTilesTemperatureFreeze", "timer_freeze_summits", 0f);
            // timers on the map and its managers (fresh-game values)
            MapBox w = World.world;
            SetField(w, "timer_nutrition_decay", SimGlobals.m.interval_nutrition_decay);
            object explosions = AccessTools.Field(typeof(MapBox), "explosion_layer")?.GetValue(w);
            SetField(explosions, "timer", 0f);
            SetField(explosions, "timerExplosionQueue", 0f);
            object flash = AccessTools.Field(typeof(MapBox), "flash_effects")?.GetValue(w);
            SetField(flash, "_timer", 0f);
            object pending = AccessTools.Field(typeof(PixelFlashEffects), "pixels_to_update")?.GetValue(flash);
            if (pending != null) AccessTools.Method(pending.GetType(), "Clear").Invoke(pending, null);
            SetField(AccessTools.Field(typeof(MapBox), "map_chunk_manager")?.GetValue(w), "_timer", 0.4f);
            SetField(w.subspecies, "_timer_unstable_genome", 0f);
            SetField(AccessTools.Field(typeof(MapBox), "era_manager")?.GetValue(w), "_timer_special_action", 0f);
            SetStatic("TaxiManager", "timer_check", 0f);
            // cached "is kingdom A an enemy of B", keyed by hash codes, which restart every load
            Kingdom.cache_enemy_check.clear();
            ResetManagerTimers(w);
            // effect cooldowns (session-time stamps per effect type) carry over too
            FieldInfo fxCooldown = AccessTools.Field(typeof(EffectAsset), "_cooldown");
            if (fxCooldown != null) foreach (EffectAsset fx in AssetManager.effects_library.list) fxCooldown.SetValue(fx, 0.0);
            else Log.Error("lockstep: EffectAsset._cooldown not found (game changed?)");
            // the tile runners' shuffled order and position; rebuilt on first use
            SetStatic("WorldBehaviourTilesRunner", "_tiles_to_check", null);
            SetStatic("WorldBehaviourTilesRunner", "_tile_next_check", 0);
        }

        /// <summary>
        /// Managers on the map keep interval timers (rebuild the per-chunk creature lists every
        /// 0.1 s, ...) that aren't saved, so their phase carries over from the previous world.
        /// Zero every float "timer" field on the map's simulation helpers (not its Unity objects).
        /// </summary>
        private static void ResetManagerTimers(MapBox w)
        {
            var names = new List<string>();
            foreach (FieldInfo mf in typeof(MapBox).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type mt = mf.FieldType;
                if (mt.Assembly != typeof(MapBox).Assembly || mt.IsValueType || typeof(UnityEngine.Object).IsAssignableFrom(mt) || mt == typeof(PlayerControl)) continue;
                object m = mf.GetValue(w);
                if (m == null) continue;
                for (Type t = m.GetType(); t != null && t != typeof(object); t = t.BaseType)
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (f.FieldType == typeof(float) && f.Name.IndexOf("timer", StringComparison.OrdinalIgnoreCase) >= 0 && !f.IsInitOnly)
                        {
                            if ((float)f.GetValue(m) != 0f) names.Add(mf.Name + "." + f.Name);
                            f.SetValue(m, 0f);
                        }
            }
            if (names.Count > 0) Log.Info("lockstep: reset timers " + string.Join(", ", names));
        }

        private static void SetField(object target, string field, object value)
        {
            FieldInfo f = target == null ? null : AccessTools.Field(target.GetType(), field);
            if (f == null) { Log.Error("lockstep: " + (target?.GetType().Name ?? "null") + "." + field + " not found (game changed?)"); return; }
            f.SetValue(target, value);
        }

        private static void SetStatic(string type, string field, object value)
        {
            FieldInfo f = AccessTools.Field(AccessTools.TypeByName(type), field);
            if (f == null) { Log.Error("lockstep: " + type + "." + field + " not found (game changed?)"); return; }
            f.SetValue(null, value);
        }

        private static readonly AccessTools.FieldRef<ChunkObjectContainer, Dictionary<long, List<Actor>>> _chunkUnits = AccessTools.FieldRefAccess<ChunkObjectContainer, Dictionary<long, List<Actor>>>("_dict_units");
        private static readonly AccessTools.FieldRef<ChunkObjectContainer, Dictionary<long, List<Building>>> _chunkBuildings = AccessTools.FieldRefAccess<ChunkObjectContainer, Dictionary<long, List<Building>>>("_dict_buildings");
        private static readonly AccessTools.FieldRef<ChunkObjectContainer, HashSet<long>> _chunkKingdomSet = AccessTools.FieldRefAccess<ChunkObjectContainer, HashSet<long>>("_hash_kingdoms");

        /// <summary>
        /// Each chunk keeps its creatures and buildings per kingdom, in dictionaries that remember
        /// every kingdom ever seen there, previous worlds included (chunks are reused). Their key
        /// order is the order enemies are found in. Keep only kingdoms with something in the chunk,
        /// in ID order.
        /// </summary>
        private static void SortChunkObjects(ChunkObjectContainer o)
        {
            Dictionary<long, List<Actor>> units = _chunkUnits(o);
            Dictionary<long, List<Building>> buildings = _chunkBuildings(o);
            var keys = new List<long>();
            foreach (var kv in buildings) if (kv.Value.Count > 0 || (units.TryGetValue(kv.Key, out List<Actor> u) && u.Count > 0)) keys.Add(kv.Key);
            foreach (var kv in units) if (kv.Value.Count > 0 && !keys.Contains(kv.Key)) keys.Add(kv.Key);
            keys.Sort();
            var u2 = new List<KeyValuePair<long, List<Actor>>>();
            var b2 = new List<KeyValuePair<long, List<Building>>>();
            foreach (long k in keys)
            {
                u2.Add(new KeyValuePair<long, List<Actor>>(k, units.TryGetValue(k, out List<Actor> ul) ? ul : new List<Actor>()));
                b2.Add(new KeyValuePair<long, List<Building>>(k, buildings.TryGetValue(k, out List<Building> bl) ? bl : new List<Building>()));
            }
            units.Clear();
            buildings.Clear();
            foreach (var kv in u2) units.Add(kv.Key, kv.Value);
            foreach (var kv in b2) buildings.Add(kv.Key, kv.Value);
            o.kingdoms.Clear();
            o.kingdoms.AddRange(keys);
            HashSet<long> set = _chunkKingdomSet(o);
            set.Clear();
            set.UnionWith(keys);
        }

        /// <summary>
        /// Each tile type keeps a HashSet of its tiles that the simulation walks (summits to freeze,
        /// ...); its order depends on history. Rebuild them in tile order.
        /// </summary>
        private static void SortTileSets()
        {
            FieldInfo dirty = AccessTools.Field(typeof(TileTypeBase), "_hashset_dirty");
            var tmp = new List<WorldTile>();
            foreach (System.Collections.IEnumerable lib in new System.Collections.IEnumerable[] { AssetManager.tiles.list, AssetManager.top_tiles.list })
                foreach (TileTypeBase t in lib)
                {
                    if (t?.hashset == null || t.hashset.Count < 2) continue;
                    tmp.Clear();
                    tmp.AddRange(t.hashset);
                    tmp.Sort((x, y) => x.tile_id.CompareTo(y.tile_id));
                    t.hashset.Clear();
                    foreach (WorldTile w in tmp) t.hashset.Add(w);
                    dirty?.SetValue(t, true);
                }
        }

        /// <summary>
        /// Creatures and buildings are updated batch by batch, and batches are pooled: which object
        /// lands in which batch, and so the order they are updated in (and draw random numbers in),
        /// depends on the previous world. Drop every batch and re-add all objects in ID order.
        /// </summary>
        private static void RebuildBatches<T>(System.Collections.Generic.IEnumerable<T> manager) where T : BaseSimObject
        {
            object jobs = AccessTools.Field(manager.GetType(), "_job_manager")?.GetValue(manager);
            MethodInfo add = jobs == null ? null : AccessTools.Method(jobs.GetType(), "addNewObject");
            MethodInfo clear = jobs == null ? null : AccessTools.Method(jobs.GetType(), "clear");
            object active = jobs == null ? null : AccessTools.Field(jobs.GetType(), "_batches_active")?.GetValue(jobs);
            object free = jobs == null ? null : AccessTools.Field(jobs.GetType(), "_batches_free")?.GetValue(jobs);
            if (add == null || clear == null || active == null || free == null) { Log.Error("lockstep: can't rebuild batches of " + manager.GetType().Name); return; }
            var objs = new List<T>();
            foreach (T o in manager) if (o != null) objs.Add(o);
            objs.Sort((x, y) => x.getID().CompareTo(y.getID()));
            clear.Invoke(jobs, null);
            AccessTools.Method(active.GetType(), "Clear").Invoke(active, null);
            AccessTools.Method(free.GetType(), "Clear").Invoke(free, null);
            var args = new object[1];
            foreach (T o in objs) { args[0] = o; add.Invoke(jobs, args); }
        }

        /// <summary>
        /// Jobs that run every few ticks (plant spread, ...) keep a random skip counter that isn't
        /// saved and carries over from the previous world. Zero it in every batch, pooled ones too.
        /// </summary>
        private static void ResetJobSkips(object manager)
        {
            object jobs = AccessTools.Field(manager.GetType(), "_job_manager")?.GetValue(manager);
            if (jobs == null) { Log.Error("lockstep: no job manager on " + manager.GetType().Name); return; }
            int n = 0;
            foreach (string listName in new[] { "_batches_active", "_batches_free" })
            {
                if (!(AccessTools.Field(jobs.GetType(), listName)?.GetValue(jobs) is System.Collections.IEnumerable batches)) continue;
                foreach (object batch in batches)
                {
                    // per-batch interval timers (plant/fungi spread, ...) also aren't saved; a new
                    // game starts them at 0
                    for (Type t = batch.GetType(); t != null && t != typeof(object); t = t.BaseType)
                        foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (f.FieldType == typeof(float) && f.Name.Contains("timer")) f.SetValue(batch, 0f);
                    foreach (string jobList in new[] { "jobs_pre", "jobs_post" })
                        if (AccessTools.Field(batch.GetType(), jobList)?.GetValue(batch) is System.Collections.IEnumerable list)
                            foreach (object job in list)
                            {
                                FieldInfo f = AccessTools.Field(job.GetType(), "current_skips");
                                if (f != null) { f.SetValue(job, 0); n++; }
                            }
                }
            }
            if (n == 0) Log.Error("lockstep: no job skip counters found on " + manager.GetType().Name);
        }

        private static readonly Comparison<DecisionAsset> _byIndex = (x, y) => x.decision_index.CompareTo(y.decision_index);
        private static readonly IComparer<DecisionAsset> _byIndexCmp = Comparer<DecisionAsset>.Create(_byIndex);

        private static void SortDecisions(Actor a)
        {
            if (a.decisions_counter > 1) Array.Sort(a.decisions, 0, a.decisions_counter, _byIndexCmp);
        }

        private static void RegisterPostfix(Actor __instance)
        {
            if (Active) SortDecisions(__instance);
        }

        private static bool MainPrefix(MapBox __instance)
        {
            if (!Active) return true;
            RunTicks(__instance);
            return false;
        }

        private static bool DelayedPrefix() => !Active || _inTick;

        private static MethodInfo _updateDirty;
        private static readonly object[] _oneArg = new object[1];

        /// <summary>
        /// Some map layers run simulation in their draw step, which only happens while the layer is
        /// being drawn: explosions (and clearing the set of tiles that already exploded), Conway's
        /// life, and tile flashes (lava rolls dice based on them). In a tick, run them regardless.
        /// </summary>
        private static bool LayerDrawPrefix(MapLayer __instance, float pElapsed)
        {
            if (!_inTickAll || !(__instance is PixelFlashEffects || __instance is ExplosionsEffects || __instance is ConwayLife)) return true;
            _oneArg[0] = pElapsed;
            _updateDirty.Invoke(__instance, _oneArg);
            return false;
        }

        private static readonly Comparison<MapRegion> _regionOrder = (x, y) =>
        {
            int a = x.tiles.Count > 0 ? x.tiles[0].tile_id : int.MaxValue, b = y.tiles.Count > 0 ? y.tiles[0].tile_id : int.MaxValue;
            return a.CompareTo(b);
        };

        /// <summary>
        /// Pathfinding breaks ties between equally good regions by their order in each region's
        /// neighbour list, which comes from pooled objects and hash sets. Keep those lists sorted.
        /// </summary>
        /// <summary>
        /// Recalculating map regions shuffles their tiles with dice (and may run in parallel).
        /// Outside a tick (end of loading, world clean-up) seed the dice and run it on one thread.
        /// </summary>
        private static void ChunksPrefix()
        {
            if (!Active) return;
            ParallelOptions po = _parallel(World.world);
            if (po != null && po.MaxDegreeOfParallelism != 1) po.MaxDegreeOfParallelism = 1;
            if (!_inTickAll) Randy.resetSeed(TickSeed(Seed ^ 0x3C4F0C4F, Tick));
        }

        private static void ChunksPostfix(MapChunkManager __instance)
        {
            if (!Active) return;
            SortRegionNeighbours(__instance);
        }

        public static void SortRegionNeighbours(MapChunkManager m)
        {
            MapChunk[] all = m.chunks;
            for (int i = 0; i < all.Length; i++)
            {
                List<MapRegion> regions = all[i].regions;
                for (int j = 0; j < regions.Count; j++)
                    if (regions[j].neighbours.Count > 1) regions[j].neighbours.Sort(_regionOrder);
            }
        }

        /// <summary>
        /// Loading reuses objects from the previous world's dead-object pools, and many fields
        /// (cached speed, facing, ...) aren't reset on reuse. Empty the pools right before the save's
        /// objects are created, so every PC builds the world from brand-new objects.
        /// </summary>
        private static void FreshObjectsPrefix()
        {
            if (!Active) return;
            int n = 0;
            foreach (BaseSystemManager m in World.world.list_all_sim_managers)
            {
                object dead = m == null ? null : AccessTools.Field(m.GetType(), "_dead_objects")?.GetValue(m);
                if (dead == null) continue;
                n += (int)AccessTools.Property(dead.GetType(), "Count").GetValue(dead, null);
                AccessTools.Method(dead.GetType(), "Clear").Invoke(dead, null);
            }
            // hash codes (hash set order, sorting) come from a counter that runs across worlds:
            // restart it so the loaded objects get the same hashes on every PC
            SetStatic("BaseSystemManager", "_latest_hash", 1);
            Log.Info("lockstep: dropped " + n + " pooled objects before loading");
        }

        private static bool CompareByIdPrefix(BaseSimObject __instance, BaseSimObject __0, ref int __result)
        {
            if (!Active) return true;
            __result = __0 == null ? 1 : __instance.getID().CompareTo(__0.getID());
            return false;
        }

        /// <summary>The game delays destroying objects while a window is open: not in a shared world.</summary>
        private static bool WindowOnScreenPrefix(ref bool __result)
        {
            if (!_inTickAll) return true;
            __result = false;
            return false;
        }

        /// <summary>
        /// Loading a save rolls every creature's action timer and decision cooldowns. Outside a
        /// tick Randy's state differs per PC, so seed that roll from the creature instead.
        /// </summary>
        private static void CooldownsPrefix(Actor __instance)
        {
            if (Active && !_inTick) Randy.resetSeed(TickSeed(Seed ^ 0x10AD5EED, __instance.getID()));
        }

        private static AccessTools.FieldRef<int> _loaderIndex;
        private static FieldInfo _loaderActions, _loaderId;
        private static readonly Dictionary<string, int> _loadStepSeen = new Dictionary<string, int>();

        /// <summary>
        /// Loading runs one step per frame or so, and some steps roll dice (phenotype shades, ...)
        /// with whatever state drawing and UI left behind. Seed each step from its name (and how
        /// often that name came up in this load); the step's position in the list differs
        /// between the first load of a session and later ones.
        /// </summary>
        private static void LoadStepPrefix()
        {
            if (!Active || _inTickAll) return;
            string id = "";
            if (_loaderActions?.GetValue(null) is System.Collections.IList list && _loaderIndex() < list.Count)
                id = _loaderId?.GetValue(list[_loaderIndex()]) as string ?? "";
            if (_loaderIndex() == 0) _loadStepSeen.Clear();
            _loadStepSeen.TryGetValue(id, out int seen);
            _loadStepSeen[id] = seen + 1;
            uint h = 2166136261;
            foreach (char c in id) h = (h ^ c) * 16777619;
            Randy.resetSeed(TickSeed(Seed ^ 0x5A0E5EED ^ (int)h, seen));
        }

        private static List<Action> _flushers;

        /// <summary>
        /// Managers add new objects to their main set lazily, and the renderer flushes that every
        /// frame, so code in the next tick would see new creatures or not depending on whether a
        /// frame passed. Flush at the end of every tick, so the renderer never has anything to add.
        /// </summary>
        private static void FlushContainers(MapBox map)
        {
            if (_flushers == null)
            {
                _flushers = new List<Action>();
                foreach (BaseSystemManager m in map.list_all_sim_managers)
                {
                    MethodInfo check = m == null ? null : AccessTools.Method(m.GetType(), "checkContainer");
                    if (check != null && check.GetParameters().Length == 0) _flushers.Add((Action)Delegate.CreateDelegate(typeof(Action), m, check));
                }
                if (_flushers.Count == 0) Log.Error("lockstep: no object containers to flush (game changed?)");
            }
            foreach (Action f in _flushers) f();
        }

        private static void RunTicks(MapBox map)
        {
            ParallelOptions po = _parallel(map);
            if (po != null && po.MaxDegreeOfParallelism != 1) po.MaxDegreeOfParallelism = 1;
            bool paused = _isPaused(map);
            float elapsed = _elapsed(map), delta = _deltaTime(map), fixedDelta = _fixedDeltaTime(map);
            SwapSessionTime(true);
            GameStatsData stats = _gameStatsData(_gameStats(map));
            int n = 0;
            try
            {
                while (Active && Tick < Granted && n < MaxPerFrame)
                {
                    BeforeTick?.Invoke(Tick);
                    _inTickAll = true;
                    Randy.resetSeed(TickSeed(Seed, Tick));
                    _isPaused(map) = false;   // local windows/pause must not change the shared world
                    // much of the simulation reads these instead of its argument
                    _elapsed(map) = StepElapsed;
                    _deltaTime(map) = DefaultStep;
                    _fixedDeltaTime(map) = DefaultStep;
                    stats.gameTime = SessionTime;
                    // knockback (and throw start points) read the drawn position, which is only
                    // refreshed for creatures on screen: refresh it for all, from simulated state
                    foreach (Actor a in map.units) if (a != null) a.updatePos();
                    _updateSimulation(map, StepElapsed);
                    _inTick = true;
                    try { map.delayed_actions_manager.update(StepElapsed, DefaultStep); }
                    finally { _inTick = false; }
                    _updateFinish(map);
                    FlushContainers(map);
                    _inTickAll = false;
                    Tick++;
                    n++;
                    AfterTick?.Invoke(Tick);
                }
            }
            finally { _inTickAll = false; _isPaused(map) = paused; _elapsed(map) = elapsed; _deltaTime(map) = delta; _fixedDeltaTime(map) = fixedDelta; stats.gameTime = SessionTime; }
        }

        public static int TickSeed(int seed, long tick)
        {
            ulong x = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ (ulong)tick * 0xC2B2AE3D27D4EB4FUL;
            x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33;
            int r = (int)x;
            return r == 0 ? 1 : r;
        }
    }
}
