using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ai.behaviours;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Farmers pick the nearest field, crop or wheat tile out of a city's tile containers, keeping
    /// the first one found at the nearest distance. Those containers are HashSets (swapped and
    /// refilled on every farm check), so of two tiles at the same distance each PC could keep a
    /// different one (the farmer drifts at 232,263: t4237, t6620; the wheat pick at t3963). In
    /// lockstep those searches walk the container in tile ID order.
    /// </summary>
    public static class FarmsInTick
    {
        private static readonly MethodInfo _enumerate = AccessTools.Method(typeof(ObjectContainer<WorldTile>), nameof(ObjectContainer<WorldTile>.GetEnumerator));

        public static void Install(Harmony h)
        {
            // a city window refreshes its city (farm tiles, citizens' jobs, food) when it opens: on
            // one PC, between ticks. The world does the same checks in ticks when they're due.
            MethodInfo force = AccessTools.Method(typeof(City), "forceDoChecks");
            if (force != null) h.Patch(force, prefix: new HarmonyMethod(typeof(FarmsInTick), nameof(ForceDoChecksPrefix)));
            else Log.Warn("lockstep: City.forceDoChecks not found: opening a city window may change the world on one PC");
            // a creature gets its name on first ask (from its culture then): asked by frame code (a
            // naming box, a tag) it was named on one PC from a different culture state than the
            // tick that named it on the other (newborn #1527 "Jobaor" vs "Bebais", t7300)
            MethodInfo getName = AccessTools.Method(typeof(Actor), "getName");
            if (getName != null) {
                Type actorData = _data?.FieldType;
                _name = actorData == null ? null : AccessTools.Property(actorData, "name");
                _sex = actorData == null ? null : AccessTools.Property(actorData, "sex");
                if (_name == null || _sex == null) Log.Warn("lockstep: ActorData.name/sex not found: names may differ between PCs");
                h.Patch(getName, prefix: new HarmonyMethod(typeof(FarmsInTick), nameof(GetNamePrefix)));
                LockstepClock.AfterTick += NameNewcomers;
            }
            else Log.Warn("lockstep: Actor.getName not found: a newborn may get different names on each PC");
            // a culture picks its name lists (onomastics) on first use, with a dice roll: first used by
            // a PC's frame code (a tag, a window) or a tick, the pick differed; then every name its
            // people got differed too (#1544 "Taehar" vs "Deawald", t8040). Rolled from the culture now.
            MethodInfo ono = AccessTools.Method(typeof(Culture), "getOnomasticData");
            if (ono != null && _onoData != null) h.Patch(ono, prefix: new HarmonyMethod(typeof(FarmsInTick), nameof(OnoPrefix)), finalizer: new HarmonyMethod(typeof(FarmsInTick), nameof(OnoFinalizer)));
            else Log.Warn("lockstep: Culture.getOnomasticData not found: names may differ between PCs");
            // the culture's name set (data.name_template_set) is picked the same way on its first use, and
            // getNameTemplate is also asked directly (kingdom/city names, windows): kingdom "Montiolth" on the
            // host, "Kashiardia" on the guest (unlocker map, t1460)
            // a name from a template without onomastics ignores its seed and draws from the shared dice:
            // kingdom #3 'Vysuzan' on the host, 'Sotor' on the guest (same tick, same founder, no culture).
            // In lockstep every generated name rolls its own dice, seeded from its seed.
            MethodInfo gen = AccessTools.Method(typeof(NameGenerator), "generateName", new[] { typeof(Actor), typeof(MetaType), typeof(long), typeof(ActorSex) });
            if (gen != null) h.Patch(gen, prefix: new HarmonyMethod(typeof(FarmsInTick), nameof(GenNamePrefix)), finalizer: new HarmonyMethod(typeof(FarmsInTick), nameof(OnoFinalizer)));
            else Log.Warn("lockstep: NameGenerator.generateName not found: kingdom names may differ between PCs");
            MethodInfo nck = AccessTools.Method(typeof(Kingdom), "newCivKingdom");
            if (nck != null) h.Patch(nck, postfix: new HarmonyMethod(typeof(FarmsInTick), nameof(NewKingdomPostfix)));
            MethodInfo nct = AccessTools.Method(typeof(City), "generateName", new[] { typeof(Actor) });
            if (nct != null) h.Patch(nct, postfix: new HarmonyMethod(typeof(FarmsInTick), nameof(NewCityPostfix)));
            MethodInfo tpl = AccessTools.Method(typeof(Culture), "getNameTemplate");
            if (tpl != null) h.Patch(tpl, prefix: new HarmonyMethod(typeof(FarmsInTick), nameof(TemplatePrefix)), finalizer: new HarmonyMethod(typeof(FarmsInTick), nameof(OnoFinalizer)));
            else Log.Warn("lockstep: Culture.getNameTemplate not found: names may differ between PCs");
            foreach (Type t in new[] { typeof(BehFindFarmField), typeof(BehFindWheat), typeof(BehFindTileForFarm), typeof(BehCityActorFindUngrownCrop) })
            {
                try
                {
                    MethodInfo m = AccessTools.Method(t, "execute", new[] { typeof(Actor) });
                    if (_enumerate == null || m == null || !Enumerates(m)) { Log.Warn("lockstep: " + t.Name + " changed: farmers may pick different tiles on each PC"); continue; }
                    h.Patch(m, transpiler: new HarmonyMethod(typeof(FarmsInTick), nameof(Transpiler)));
                }
                catch (Exception e) { Log.Warn("lockstep: couldn't make " + t.Name + " pick the same tile on every PC: " + e.Message); }
            }
        }

        private static bool ForceDoChecksPrefix() => !LockstepClock.Active || LockstepClock.InTick || SmoothLoader.isLoading();

        private static readonly FieldInfo _data = AccessTools.Field(typeof(Actor), "data"), _stats = AccessTools.Field(typeof(MapBox), "map_stats");
        private static PropertyInfo _name, _sex;
        private static FieldInfo _dna;

        private static bool _naming;
        private static readonly HashSet<long> _named = new HashSet<long>();

        /// <summary>
        /// WorldBox names a creature the first time anything asks, and naming resets the world's dice
        /// (OnomasticsData.generateName reseeds Randy). Asked by code that runs on one PC only (in
        /// ticks too), creatures were named at different ticks and the dice split for everyone after
        /// (the "units, rng" drifts at ticks 70-120 after loads; the kin-name drifts). In lockstep
        /// names are only kept here, after every tick, for every creature still unnamed, in list
        /// order and on dice of their own; anything asking earlier gets the name without keeping it.
        /// </summary>
        private static void NameNewcomers(long tick)
        {
            if (!LockstepClock.Active || _data == null || _name == null) { _named.Clear(); return; }
            if (tick <= 1) _named.Clear();
            foreach (Actor a in World.world.units)
            {
                if (a == null || _named.Contains(a.getID())) continue;
                object data = _data.GetValue(a);
                if (data == null) continue;
                if (string.IsNullOrEmpty(_name.GetValue(data) as string))
                {
                    Dice.Snapshot d = Dice.Isolate();
                    _naming = true;
                    try { a.getName(); }
                    catch (Exception e) { Log.Warn("lockstep: couldn't name #" + a.getID() + ": " + e.Message); }
                    finally { _naming = false; Dice.Restore(d); }
                }
                _named.Add(a.getID());
            }
        }

        private static bool GetNamePrefix(Actor __instance, ref string __result)
        {
            if (!LockstepClock.Active || _naming || __instance == null || _data == null || _stats == null) return true;
            object data = _data.GetValue(__instance);
            if (data == null) return true;
            if (_name == null) { _name = AccessTools.Property(data.GetType(), "name"); _sex = AccessTools.Property(data.GetType(), "sex"); }
            if (_name == null || _sex == null || !string.IsNullOrEmpty(_name.GetValue(data) as string)) return true;
            // what it will be called, not kept: NameNewcomers names it after the tick, on every PC alike
            Dice.Snapshot d = Dice.Isolate();
            try
            {
                object stats = _stats.GetValue(World.world);
                if (_dna == null) _dna = AccessTools.Field(stats.GetType(), "life_dna");
                object dna = _dna != null ? _dna.GetValue(stats) : AccessTools.Property(stats.GetType(), "life_dna").GetValue(stats);
                long seed = Convert.ToInt64(dna) + __instance.getID() * 543;
                __result = NameGenerator.generateName(__instance, MetaType.Unit, seed, __instance.isSapient() ? (ActorSex)_sex.GetValue(data) : ActorSex.None);
            }
            catch { __result = ""; }
            finally { Dice.Restore(d); }
            return false;
        }

        private static readonly FieldInfo _onoData = AccessTools.Field(typeof(Culture), "_onomastics_data");

        private static void OnoPrefix(Culture __instance, MetaType pType, bool pReset, out Dice.Snapshot? __state)
        {
            __state = null;
            if (!LockstepClock.Active) return;
            if (!pReset && _onoData.GetValue(__instance) is System.Collections.IDictionary d && d.Contains(pType)) return;
            __state = Dice.Isolate();
            Randy.resetSeed(LockstepClock.TickSeed((int)__instance.getID() * 31 + (int)pType, 0x0A0A));
        }

        private static readonly FieldInfo _nameSet = AccessTools.Field(typeof(Culture), "_name_set_asset");

        /// <summary>
        /// After an epoch load: every culture's cached name lists and name set dropped, so they are
        /// rolled again from the culture (seeded) on every PC; caches kept from before the load (pooled
        /// objects, frame code) named a new kingdom differently on the guest (unlocker map, t1460/t2490).
        /// </summary>
        public static void ResetNameCaches()
        {
            int n = 0;
            try
            {
                foreach (Culture c in World.world.cultures.list)
                {
                    if (c == null) continue;
                    // rebuild from the save like Culture.loadData does: wiping them made every culture
                    // roll new name styles after a re-sync ("Framald" vs "Holy Aphaphe")
                    if (_onoData?.GetValue(c) is Dictionary<MetaType, OnomasticsData> d)
                    {
                        d.Clear();
                        if (c.data?.onomastics != null)
                            foreach (KeyValuePair<MetaType, string> kv in c.data.onomastics)
                            {
                                var o = new OnomasticsData();
                                o.loadFromShortTemplate(kv.Value);
                                d[kv.Key] = o;
                            }
                    }
                    _nameSet?.SetValue(c, null);
                    n++;
                }
            }
            catch (Exception e) { Log.Warn("lockstep: resetting culture name caches: " + e.Message); }
        }

        private static void GenNamePrefix(MetaType pType, long pSeed, out Dice.Snapshot? __state)
        {
            __state = null;
            if (!LockstepClock.Active) return;
            __state = Dice.Isolate();
            long s = pSeed + (long)pType.GetHashCode();
            Randy.resetSeed(LockstepClock.TickSeed((int)(s ^ (s >> 32)), 0x0C0C));
        }

        private static int _kingdomLogs, _cityLogs;

        /// <summary>Diagnostic: every new kingdom's name and what it was made from (compare between PCs).</summary>
        private static void NewKingdomPostfix(Kingdom __instance, Actor pActor)
        {
            if (!LockstepClock.Active || _kingdomLogs++ > 60) return;
            try
            {
                Culture c = pActor?.culture;
                string ono = "";
                if (c != null && _onoData.GetValue(c) is System.Collections.IDictionary d)
                    foreach (System.Collections.DictionaryEntry e in d) ono += e.Key + " ";
                Log.Info("lockstep: tick " + LockstepClock.Tick + (LockstepClock.InTick ? "" : " OUTSIDE") + " kingdom #" + __instance.getID() + " '" + __instance.data.name + "' by #" + pActor?.getID() + " " + pActor?.asset?.id +
                         " culture " + (c == null ? "none" : "#" + c.getID() + " set=" + c.data.name_template_set + " tpl=" + c.getNameTemplate(MetaType.Kingdom) + " cached " + ono) + " dna " + R.MapStats?.life_dna);
            }
            catch (Exception e) { Log.Warn("lockstep: kingdom log: " + e.Message); }
        }

        /// <summary>Diagnostic: every new city's name and founder (compare between PCs).</summary>
        private static void NewCityPostfix(City __instance, Actor pActor)
        {
            if (!LockstepClock.Active || _cityLogs++ > 60) return;
            try
            {
                Culture c = pActor?.culture;
                Log.Info("lockstep: tick " + LockstepClock.Tick + (LockstepClock.InTick ? "" : " OUTSIDE") + " city #" + __instance.getID() + " '" + __instance.data.name + "' by #" + pActor?.getID() + " " + pActor?.asset?.id +
                         " culture " + (c == null ? "none" : "#" + c.getID()) + " dna " + R.MapStats?.life_dna);
            }
            catch (Exception e) { Log.Warn("lockstep: city log: " + e.Message); }
        }

        private static void TemplatePrefix(Culture __instance, MetaType pType, out Dice.Snapshot? __state)
        {
            __state = null;
            if (!LockstepClock.Active || !string.IsNullOrEmpty(__instance.data?.name_template_set)) return;
            __state = Dice.Isolate();
            Randy.resetSeed(LockstepClock.TickSeed((int)__instance.getID() * 37 + 7, 0x0B0B));
        }

        private static Exception OnoFinalizer(Exception __exception, Dice.Snapshot? __state)
        {
            if (__state.HasValue) Dice.Restore(__state.Value);
            return __exception;
        }


        private static bool Enumerates(MethodInfo m)
        {
            foreach (KeyValuePair<OpCode, object> i in PatchProcessor.ReadMethodBody(m))
                if (i.Value is MethodInfo target && target == _enumerate) return true;
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo sorted = AccessTools.Method(typeof(FarmsInTick), nameof(Sorted));
            foreach (CodeInstruction c in list)
                if ((c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && c.operand is MethodInfo m && m == _enumerate)
                {
                    c.opcode = OpCodes.Call;
                    c.operand = sorted;
                }
            return list;
        }

        private static readonly Comparison<WorldTile> ById = (a, b) => a.data.tile_id.CompareTo(b.data.tile_id);

        public static IEnumerator<WorldTile> Sorted(ObjectContainer<WorldTile> c)
        {
            if (!LockstepClock.Active) return c.GetEnumerator();
            var l = new List<WorldTile>(c.Count);
            foreach (WorldTile t in c) l.Add(t);
            l.Sort(ById);
            return l.GetEnumerator();
        }
    }
}
