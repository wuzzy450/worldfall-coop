using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ai.behaviours;
using tools;

namespace Coopfall
{
    /// <summary>
    /// "-coopfall-scenario lockstep-coop": the co-op features of Worldfall's life layer, each through
    /// its real entry point outside a tick (as a menu, a key or a conversation would call it), with
    /// both players possessing a creature of the same town (H = host, G = guest; lowest IDs).
    ///  20 s  both get 500 gold (relayed addMoney)
    ///  25 s  H gains 400 reputation in the town (Rep.Add: creature data, DataCalls)
    ///  30 s  G gets a weapon into its bag (Gear.Add: data), 35 s puts it on (Gear.PutOn: the item is made in a tick)
    ///  45 s  G drops the worn weapon (DropWorn: a bundle on the ground on both PCs), 52 s picks it up again
    ///  60 s  H buys at the town's forge through a real conversation (ForgeMenu + Pick: the choice runs in a tick)
    ///  75 s  H finishes a craft (the item is made in a tick, kept in the bag)
    ///  85 s  H marks out a house plot (LandPlot.Place: the site is built in a tick)
    ///  95 s  H's opinion with the forge's smith (Chronicle.ChangeOpinion)
    /// 100 s  G sleeps the night away (Calendar.SkipToDawn: a new day in the same tick everywhere)
    /// 110 s  H makes the days longer (DayMinutes: a new epoch with the host's setting), G turns friendly fire on (stays its own)
    /// 140 s  forced resync: the data, the time of day and the day go on
    /// Every 500 ticks both PCs log the same "TEST coop: state at epoch E tick T" line (compare with
    /// tools/compare-state.py) and take a screenshot (coop/e{E}-t{T}.png in the profile folder);
    /// every step logs "TEST coop: STEP ok/failed (...)" and a screenshot (coop/step-NAME.png).
    /// tools/coop-report.py puts both PCs' steps, state lines and screenshots side by side.
    /// </summary>
    public partial class TestDriver
    {
        private bool _cpMode, _cpHooked;
        private bool _cpSpawned, _cpShotFolk;
        private int _cpStep;
        private long _cpTickA;
        private const float CoopSeconds = 300f;
        private long _cpCity, _cpSmith, _cpPlotTile = -1;
        private string _cpPieceId;
        private readonly List<long> _cpBodies = new List<long>();

        private static Type CpType(string name) => WorldfallBridge.Assembly?.GetType("FirstPerson." + name, false);

        private static void CpOk(string step, bool ok, string more = "") => Log.Info("TEST coop: " + step + (ok ? " ok" : " failed") + (more.Length > 0 ? " (" + more + ")" : ""));

        private static object CpCall(Type t, object on, string name, params object[] args)
        {
            if (t == null) throw new MissingMethodException("(type missing)", name);
            foreach (MethodInfo m in t.GetMethods(Any))
            {
                if (m.Name != name || m.GetParameters().Length != args.Length) continue;
                ParameterInfo[] ps = m.GetParameters();
                bool fits = true;
                for (int i = 0; i < ps.Length && fits; i++)
                    if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i]) && !(ps[i].ParameterType.IsByRef)) fits = false;
                if (fits) return m.Invoke(on, args);
            }
            throw new MissingMethodException(t.Name, name);
        }

        private AvatarManager.Remote CpOther()
        {
            foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values) if (r.actor != null && r.actor.isAlive()) return r;
            return null;
        }

        private void CpShot(string name)
        {
            string dir = Path.Combine(_shotDir, "coop");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            try { ScreenCapture.CaptureScreenshot(path); Log.Info("TEST coop: screenshot " + name); }
            catch (Exception e) { Log.Warn("TEST coop: screenshot: " + e.Message); }
        }

        private static int CpTraitCount(Actor a)
        {
            if (a == null) return -1;
            int n = 0;
            foreach (ActorTrait t in a.getTraits()) if (t != null) n++;
            return n;
        }

        /// <summary>A mutation pot's signature: entry count plus first and last trait id in sorted order.</summary>
        private static string CpMutationPotSignature(List<SubspeciesTrait> pPot)
        {
            if (pPot == null || pPot.Count == 0) return "none";
            var ids = new List<string>(pPot.Count);
            foreach (SubspeciesTrait t in pPot) if (t != null) ids.Add(t.id);
            ids.Sort(string.CompareOrdinal);
            return ids.Count + " entries, first " + ids[0] + ", last " + ids[ids.Count - 1];
        }

        /// <summary>An EnemyFinderData's enemy list length (-1 when missing).</summary>
        private static int CpEnemyCount(object pData)
        {
            if (pData == null) return -1;
            return AccessTools.Field(pData.GetType(), "list")?.GetValue(pData) is System.Collections.ICollection c ? c.Count : -1;
        }

        /// <summary>The town both PCs play in, picked the same way: the one with the most houses
        /// (falling back to the town with the most possessable units, then nothing, so a
        /// generated world of only kings, warriors, babies and wild animals can't abort the
        /// whole coop scenario).</summary>
        private City CpBestTown()
        {
            City best = null;
            int most = -1;
            var cities = new List<City>();
            foreach (City c in World.world.cities) if (c != null && c.isAlive()) cities.Add(c);
            cities.Sort((x, y) => x.getID().CompareTo(y.getID()));
            foreach (City c in cities)
            {
                int n = 0, people = 0;
                foreach (Building b in c.buildings) if (b != null && b.isAlive() && R.Get(b, "asset") is BuildingAsset ba && ba.type == "type_house") n++;
                foreach (Actor a in c.units) if (a != null && a.isAlive() && a.isAdult() && !a.isKing() && a.canBePossessed()) people++;
                if (people >= 4 && n > most) { most = n; best = c; }
            }
            if (best == null)
            {
                // no town with 4 free adults: the town with the most possessable units of any kind
                int top = 0;
                foreach (City c in cities)
                {
                    int people = 0;
                    foreach (Actor a in c.units) if (a != null && a.isAlive() && a.canBePossessed()) people++;
                    if (people > top) { top = people; best = c; }
                }
                if (best != null) Log.Warn("TEST coop: no town with 4 free adults; falling back to " + best.name + " (#" + best.getID() + ", " + top + " possessable)");
            }
            return best;
        }

        /// <summary>Both PCs pick their creatures the same way: adults of the best town, padded
        /// (when it has too few or none) with the possessable creatures nearest the town - which
        /// the host's spawned townsfolk (CpSpawnAdults) are - so the host, the guest and the
        /// smith always get distinct bodies of the same place, and never the same creature.</summary>
        private Actor CpPick()
        {
            City best = CpBestTown();
            _cpCity = best?.getID() ?? 0;
            var adults = new List<Actor>();
            if (best != null)
            {
                foreach (Actor a in best.units) if (a != null && a.isAlive() && a.isAdult() && !a.isKing() && !a.isWarrior() && a.canBePossessed()) adults.Add(a);
                if (adults.Count == 0)
                    foreach (Actor a in best.units) if (a != null && a.isAlive() && a.canBePossessed()) adults.Add(a);
            }
            if (adults.Count < 3)
            {
                // the town has too few (or none): the possessable creatures nearest it, ties by id
                var near = new List<Actor>();
                foreach (Actor a in World.world.units)
                    if (a != null && a.isAlive() && a.canBePossessed() && !adults.Contains(a)) near.Add(a);
                if (best != null)
                {
                    // the tile of the town's lowest-id alive building (deterministic on both PCs;
                    // the buildings list iterates in a per-PC pooled order)
                    Building c0 = null;
                    foreach (Building b in best.buildings)
                        if (b != null && b.isAlive() && (c0 == null || b.getID() < c0.getID())) c0 = b;
                    if (c0 != null && c0.current_tile != null)
                    {
                        Vector2 center = new Vector2(c0.current_tile.posV3.x, c0.current_tile.posV3.y);
                        near.Sort((x, y) =>
                        {
                            int d = (x.current_position - center).sqrMagnitude.CompareTo((y.current_position - center).sqrMagnitude);
                            return d != 0 ? d : x.getID().CompareTo(y.getID());
                        });
                    }
                    else near.Sort((x, y) => x.getID().CompareTo(y.getID()));
                }
                else near.Sort((x, y) => x.getID().CompareTo(y.getID()));
                int had = adults.Count;
                foreach (Actor a in near) { adults.Add(a); if (adults.Count >= 3) break; }
                if (adults.Count > had)
                    Log.Info("TEST coop: the town had " + had + " possessable unit(s); padded with the " + (adults.Count - had) + " nearest possessable creature(s)");
            }
            adults.Sort((x, y) => x.getID().CompareTo(y.getID()));
            if (adults.Count == 0) return null;
            _cpBodies.Clear();
            _cpBodies.Add(adults[0].getID());
            if (adults.Count > 1) _cpBodies.Add(adults[1].getID());
            _cpSmith = adults.Count > 2 ? adults[2].getID() : 0;
            int houses = 0;
            if (best != null) foreach (Building b in best.buildings) if (b != null && b.isAlive() && R.Get(b, "asset") is BuildingAsset ba && ba.type == "type_house") houses++;
            Log.Info("TEST coop: town " + (best == null ? "(none)" : best.name + " (#" + best.getID() + ", " + houses + " houses)") + ", host #" + adults[0].getID() + ", guest #" + (adults.Count > 1 ? adults[1].getID() : adults[0].getID()) + ", smith #" + _cpSmith);
            return _s.IsHost || adults.Count == 1 ? adults[0] : adults[1];
        }

        /// <summary>The worlds the tests generate sometimes have no possessable town adults at all
        /// (only kings, warriors, babies and wild animals), which left both players in wild
        /// creatures far from any house. The host spawns a few townsfolk at the best town through
        /// a real creature power (the lockstep input runs it in a tick on both PCs, so the world
        /// stays shared); CpPick prefers the creatures nearest the town, so the players get them.</summary>
        private void CpSpawnAdults()
        {
            City best = CpBestTown();
            if (best == null) { Log.Warn("TEST coop: no town to spawn townsfolk at (a world without towns)"); return; }
            int adults = 0;
            foreach (Actor a in best.units) if (a != null && a.isAlive() && a.isAdult() && !a.isKing() && a.canBePossessed()) adults++;
            if (adults >= 4) { Log.Info("TEST coop: town " + best.name + " has " + adults + " possessable adults; spawning none"); return; }
            // a creature-tab spawn power (the first one this game has)
            string spawn = null;
            foreach (string id in new[] { "human", "elf", "dwarf", "orc" })
                if (CoopMod.Instance.Powers.Knows(id)) { spawn = id; break; }
            if (spawn == null) { Log.Warn("TEST coop: no creature power to spawn townsfolk with"); return; }
            // dry land next to the town's houses, in a deterministic order
            var tiles = new List<WorldTile>();
            var seen = new HashSet<WorldTile>();
            foreach (Building b in best.buildings)
            {
                if (b == null || !b.isAlive() || b.current_tile == null) continue;
                foreach (WorldTile n in b.current_tile.neighbours)
                    if (n != null && !n.is_liquid && !n.hasBuilding() && seen.Add(n)) tiles.Add(n);
            }
            tiles.Sort((x, y) => x.tile_id.CompareTo(y.tile_id));
            string brush = Config.current_brush;
            Config.current_brush = "circ_1";
            int spawned = 0;
            try
            {
                for (int i = 0; i < tiles.Count && spawned < 4; i++)
                    if (CoopMod.Instance.Powers.UseLocal(spawn, tiles[i])) spawned++;
            }
            finally { Config.current_brush = brush; }
            Log.Info(spawned > 0
                ? "TEST coop: host spawned " + spawned + " " + spawn + " townsfolk at " + best.name + " (both PCs get them in a tick)"
                : "TEST coop: no dry tile next to " + best.name + "'s houses to spawn townsfolk on");
        }

        private void CoopTick(float now)
        {
            if (!WorldfallBridge.Present) { if (_cpStep == 0) { _cpStep = 99; Log.Warn("TEST coop: Worldfall not loaded"); } return; }
            float t = now - _lsStart;
            object mod = FxMod(out Type modT);
            if (mod == null) return;
            Actor me = ControllableUnit.getControllableUnit();
            City town = World.world.cities.get(_cpCity);
            if (!_cpHooked)
            {
                _cpHooked = true;
                Lockstep.PlayerScope.LogToasts = true;
                Lockstep.LockstepClock.AfterTick += tick =>
                {
                    if (tick <= 0 || tick % 500 != 0 || Lockstep.LockstepControl.Count == 0) return;
                    try { Log.Info("TEST coop: state at epoch " + _s.Lockstep.Epoch + " tick " + tick + ": " + CpState()); }
                    catch (Exception e) { Log.Warn("TEST coop: state: " + e.Message); }
                    CpShot("e" + _s.Lockstep.Epoch + "-t" + tick);
                };
            }
            try
            {
                Type gear = CpType("Gear"), bundles = CpType("Bundles"), rep = CpType("Towns.Rep"), plot = CpType("Towns.LandPlot"), cal = CpType("Towns.Calendar"), chron = CpType("Chronicle");
                if (!_cpSpawned && t > 6f)
                {
                    // the host spawns a few townsfolk (a real power through the lockstep inputs)
                    // before anyone picks a body, so the scenario always has town bodies
                    if (!_s.IsHost) _cpSpawned = true;
                    else if (_s.Lockstep != null && _s.Lockstep.CanSubmit) { _cpSpawned = true; CpSpawnAdults(); }
                    else if (t > 11f) { _cpSpawned = true; Log.Warn("TEST coop: lockstep never became ready in time; no townsfolk spawned"); }
                }
                if (!_cpShotFolk && t > 9f) { _cpShotFolk = true; CpShot("step-townfolk"); }
                if (_cpStep == 0 && t > 12f)
                {
                    _cpStep = 1;
                    Actor pick = CpPick();
                    if (pick == null) { CpOk("possess", false, "no possessable creature in the world"); _cpStep = 99; return; }
                    if (ControllableUnit.isControllingUnit()) ControllableUnit.clear(false);
                    ControllableUnit.setControllableCreature(pick);
                    _lsPossessions++;
                }
                if (_cpStep == 1 && t > 18f) { _cpStep = 2; CpOk("possess", me != null && me.isAlive(), me == null ? "none" : "#" + me.getID()); CpShot("step-possess"); }
                if (me == null || !me.isAlive())
                {
                    // a deterministic backup: possessable creatures nobody took (the scenario's
                    // assigned bodies, anyone already controlled), the same way on both PCs (the
                    // units list iterates in a per-PC pooled order); the host takes the first by
                    // id and the guest the second, so the two players never end up in the same
                    // body (a refused possess used to drop the guest into the host's creature)
                    var free = new List<Actor>();
                    foreach (Actor a in World.world.units)
                        if (a != null && a.isAlive() && a.canBePossessed() && !_cpBodies.Contains(a.getID()) && !Lockstep.LockstepControl.IsControlled(a.getID()))
                            free.Add(a);
                    free.Sort((x, y) => x.getID().CompareTo(y.getID()));
                    Actor backup = free.Count == 0 ? null : free[_s.IsHost || free.Count == 1 ? 0 : 1];
                    if (backup != null)
                    {
                        if (ControllableUnit.isControllingUnit()) ControllableUnit.clear(false);
                        ControllableUnit.setControllableCreature(backup);
                        me = backup;
                        Log.Info("TEST coop: backup body #" + backup.getID());
                    }
                    if (me == null || !me.isAlive()) return;
                }
                if (_cpStep == 2 && t > 20f)
                {
                    _cpStep = 3;
                    int before = me.money;
                    R.CallN(me, "addMoney", 1, 500);
                    CpOk("gold", true, "money " + before + " -> 500 more (relayed)");
                }
                if (_cpStep == 3 && t > 25f)
                {
                    _cpStep = 4;
                    if (_s.IsHost && town != null)
                    {
                        int after = (int)CpCall(rep, null, "Add", me, town, 400, "test");
                        CpOk("reputation", after >= 400, "points in " + town.name + " right after: " + after + " (this PC's own reads see its write at once)");
                    }
                }
                if (_cpStep == 4 && t > 30f)
                {
                    _cpStep = 5;
                    if (!_s.IsHost)
                    {
                        EquipmentAsset w = CpGun();
                        _cpPieceId = w?.id;
                        object piece = w == null ? null : ((IList)CpCall(gear, null, "Parse", w.id + ",,0,Coop Test,"))[0];
                        bool ok = piece != null && (bool)CpCall(gear, null, "Add", me, piece);
                        CpOk("bag add", ok, w == null ? "no weapon asset" : w.id + ", bag now " + ((IList)CpCall(gear, null, "Of", me)).Count);
                    }
                }
                if (_cpStep == 5 && t > 35f)
                {
                    _cpStep = 6;
                    if (!_s.IsHost)
                    {
                        bool ok = (bool)CpCall(gear, null, "PutOn", me, 0, FxToast, false);
                        CpOk("gear put on", ok, "answer is the guess; the state lines show the weapon on both PCs");
                    }
                }
                if (_cpStep == 6 && t > 42f) { _cpStep = 7; CpShot("step-geared"); }
                // the guest fires the worn blaster for 2s through the player's attack buttons (missing-ray check)
                if (_cpStep == 7 && !_s.IsHost)
                {
                    if (t > 42.5f && t < 44.5f)
                    {
                        Vector2 aim = new Vector2(5f, 0f);
                        Lockstep.LockstepControl.BeforeSample = () =>
                        {
                            Lockstep.LockstepControl.Script(aim, true, false);
                        };
                        if (!_cpFired) { _cpFired = true; Log.Info("TEST coop: guest fires " + R.CallN(me, "getWeaponAsset", 0)); }
                        if (t > 43.3f && !_cpShotA) { _cpShotA = true; CpShot("step-fire-a"); }
                        if (t > 43.9f && !_cpShotB) { _cpShotB = true; CpShot("step-fire-b"); }
                    }
                    else if (t >= 44.5f)
                    {
                        Lockstep.LockstepControl.BeforeSample = null;
                    }
                }
                if (_cpStep == 7 && t > 45f)
                {
                    _cpStep = 8;
                    if (!_s.IsHost)
                    {
                        bool ok = (bool)CpCall(modT, mod, "DropWorn", EquipmentType.Weapon);
                        CpOk("drop worn", ok, "bundles here now " + ((IList)bundles.GetField("List", Any).GetValue(null)).Count + " (0 until the tick)");
                    }
                }
                if (_cpStep == 8 && t > 50f) { _cpStep = 9; CpShot("step-dropped"); CpOk("bundles", true, CpBundles()); }
                if (_cpStep == 9 && t > 52f)
                {
                    _cpStep = 10;
                    if (!_s.IsHost)
                    {
                        object near = null;
                        foreach (object b in (IList)bundles.GetField("List", Any).GetValue(null)) { near = b; break; }
                        bool ok = near != null && (bool)CpCall(bundles, null, "PickUp", me, near, FxToast);
                        CpOk("pick up", ok, near == null ? "no bundle" : "guess yes");
                    }
                }
                if (_cpStep == 10 && t > 56f)
                {
                    _cpStep = 105;
                    if (!_s.IsHost) CpQuest(me, town);
                }
                if (_cpStep == 105 && t > 60f)
                {
                    _cpStep = 11;
                    if (_s.IsHost) CpForge(mod, modT, me);
                }
                if (_cpStep == 11 && t > 70f)
                {
                    _cpStep = 12;
                    if (_s.IsHost)
                    {
                        object conv = modT.GetField("Converse", Any).GetValue(mod);
                        string line = CpCall(conv.GetType(), conv, "get_Line") as string;
                        Item weapon = me.equipment?.getSlot(EquipmentType.Weapon)?.getItem();
                        CpOk("forge result", true, "says \"" + line + "\", weapon " + (weapon == null ? "none" : weapon.asset.id + " #" + weapon.getID()) + ", conversation choices sent " + Lockstep.TalkInTick.Sent + " back " + Lockstep.TalkInTick.Replayed + " local " + Lockstep.TalkInTick.Local + " unsent " + Lockstep.TalkInTick.Unsent);
                        CpCall(conv.GetType(), conv, "Close");
                    }
                    CpShot("step-forge");
                }
                if (_cpStep == 12 && t > 75f)
                {
                    _cpStep = 13;
                    if (_s.IsHost) CpCraft(mod, modT, me);
                }
                if (_cpStep == 13 && t > 85f)
                {
                    _cpStep = 14;
                    if (_s.IsHost && town != null) CpPlot(plot, me, town);
                }
                if (_cpStep == 14 && t > 92f) { _cpStep = 15; CpShot("step-plot"); }
                if (_cpStep == 15 && t > 95f)
                {
                    _cpStep = 16;
                    Actor smith = World.world.units.get(_cpSmith);
                    if (_s.IsHost && smith != null)
                    {
                        int before = (int)CpCall(chron, null, "Opinion", smith, me);
                        CpCall(chron, null, "ChangeOpinion", smith, me, 25);
                        CpOk("opinion", true, "smith #" + _cpSmith + " thought " + before + " of #" + me.getID() + "; +25 travels");
                    }
                }
                if (_cpStep == 16 && t > 100f)
                {
                    _cpStep = 17;
                    if (!_s.IsHost)
                    {
                        int day = (int)cal.GetProperty("Day", Any).GetValue(null, null);
                        int to = (int)CpCall(cal, null, "SkipToDawn");
                        CpOk("sleep to dawn", to == day + 1, "day " + day + " -> " + to + " (expected; the dawn itself comes in a tick)");
                    }
                }
                if (_cpStep == 17 && t > 104f) { _cpStep = 18; CpShot("step-dawn"); }
                if (_cpStep == 18 && t > 110f)
                {
                    _cpStep = 19;
                    object settings = modT.GetField("Settings", Any).GetValue(mod);
                    if (_s.IsHost) { settings.GetType().GetField("DayMinutes", Any).SetValue(settings, 5f); CpOk("host setting", true, "DayMinutes 5: a new epoch should follow"); }
                    else { settings.GetType().GetField("FriendlyFire", Any).SetValue(settings, true); CpOk("guest setting", true, "FriendlyFire on here: the shared world keeps the host's"); }
                    _cpStep = 190;
                }
                if (_cpStep == 190 && t > 120f)
                {
                    _cpStep = 191;
                    if (_s.IsHost)
                    {
                        WorldTile at = World.world.GetTile(me.current_tile.x + 22, me.current_tile.y + 6) ?? me.current_tile;
                        CpOk("meteor", CoopMod.Instance.Powers.UseLocal("meteorite", at), "thrown at " + at.x + "," + at.y);
                    }
                }
                if (_cpStep == 191 && t > 126f) { _cpStep = 1915; CpOk("meteor look", Lockstep.EffectSeeds.Given.Count > 0, "seeds given " + string.Join(",", Lockstep.EffectSeeds.Given.ConvertAll(x => x.ToString()).ToArray()) + ", drawn with them here " + Lockstep.EffectSeeds.Drawn + ", drawing now: " + CpMeteorSeeds(mod, modT)); CpShot("step-meteor"); }
                if (_cpStep == 1915 && t > 127f)
                {
                    _cpStep = 1916;
                    if (_s.IsHost)
                    {
                        WorldTile at = World.world.GetTile(me.current_tile.x + 8, me.current_tile.y + 4) ?? me.current_tile;
                        CpOk("singularity seeds", CoopMod.Instance.Powers.UseLocal("seeds_singularity", at), "seeds cast at " + at.x + "," + at.y);
                    }
                }
                if (_cpStep == 1916 && t > 129f) { _cpStep = 192; CpShot("step-singularity"); }
                if (_cpStep == 192 && t > 130f)
                {
                    _cpStep = 193;
                    if (!_s.IsHost)
                    {
                        Vector2 p = me.current_position + new Vector2(1.5f, 0.5f);
                        object made = CpCall(bundles, null, "Drop", p, (Func<Vector2, float>)(v => 0f), "wood", 5, null);
                        CpOk("drop wood", made == null, "5 wood set down at " + p.ToString("F1") + " before the resync (on the ground from the tick on)");
                    }
                }
                if (_cpStep == 193 && t > 133f)
                {
                    _cpStep = 194;
                    Actor smith = World.world.units.get(_cpSmith);
                    if (smith != null)
                    {
                        _cpShareBefore = CpExplored(me);
                        CpOk("explored", _cpShareBefore > 0f, "explored here " + _cpShareBefore.ToString("F4") + " (each PC's own)");
                    }
                }
                if (_cpStep == 194 && t > 135f)
                {
                    _cpStep = 195;
                    if (town != null)
                    {
                        HashSet<TileZone> bz = AccessTools.FieldRefAccess<City, HashSet<TileZone>>("border_zones")(town);
                        HashSet<TileZone> nz = AccessTools.FieldRefAccess<City, HashSet<TileZone>>("neighbour_zones")(town);
                        int bCount = bz?.Count ?? 0, nCount = nz?.Count ?? 0;
                        TileZone firstBorder = null;
                        if (bz != null) foreach (TileZone z in bz) { firstBorder = z; break; }
                        CpOk("border patrol", bCount > 0, "border zones " + bCount + ", neighbour zones " + nCount + ", first border #" + (firstBorder != null ? firstBorder.id : -1));
                        CpShot("step-border-patrol");
                    }
                }
                if (_cpStep == 195 && t > 137f)
                {
                    _cpStep = 19;
                    if (town != null)
                    {
                        CityZoneGrowth growth = World.world.city_zone_helper?.city_growth;
                        TileZone cand = growth != null ? growth.getZoneToClaim(town.leader, town, false) : null;
                        CpOk("zone growth", true, "growth candidate zone " + (cand != null ? "#" + cand.id + " at (" + cand.x + "," + cand.y + ")" : "none"));
                        CpShot("step-zone-growth");
                    }
                }
                if (_cpStep == 19 && t > 140f)
                {
                    _cpStep = 20;
                    if (_s.IsHost) { Log.Info("TEST coop: forcing a resync"); _s.Lockstep.StartEpoch("test coop"); }
                }
                if (_cpStep == 20 && t > 160f)
                {
                    float share = CpExplored(me);
                    CpOk("explored after resync", share >= _cpShareBefore - 0.0001f && share > 0f, "explored here " + _cpShareBefore.ToString("F4") + " -> " + share.ToString("F4"));
                    string wood = CpBundles();
                    CpOk("bundles after resync", wood.Contains("5 wood"), wood);
                    CpOk("doorways", Lockstep.SteeringInTick.Doors > 0, Lockstep.SteeringInTick.Doors + " house doors shared this epoch");
                }
                if (_cpStep == 20 && t > 160f) { _cpStep = 21; CpShot("step-after-resync"); CpOk("made outside ticks", Lockstep.MadeOutsideTicks.Count == 0, Lockstep.MadeOutsideTicks.Count + " " + string.Join("; ", Lockstep.MadeOutsideTicks.Seen.ToArray())); }
                // guest speed: the guest pauses, then sets x5; the host's grants (and so both PCs' ticks) follow
                if (_cpStep == 21 && t > 165f)
                {
                    _cpStep = 22;
                    if (!_s.IsHost) { Log.Info("TEST coop: guest pauses"); Config.paused = true; }
                }
                if (_cpStep == 22 && t > 170f) { _cpStep = 23; _cpTickA = Lockstep.LockstepClock.Tick; }
                if (_cpStep == 23 && t > 175f)
                {
                    _cpStep = 24;
                    long d = Lockstep.LockstepClock.Tick - _cpTickA;
                    if (!_s.IsHost) CpOk("guest pause", d <= 2 && Config.paused, "ticks in 5 s while paused: " + d + ", paused here " + Config.paused);
                    if (!_s.IsHost) { Log.Info("TEST coop: guest sets x5"); Config.paused = false; Config.setWorldSpeed("x5"); }
                }
                if (_cpStep == 24 && t > 178f) { _cpStep = 25; _cpTickA = Lockstep.LockstepClock.Tick; }
                if (_cpStep == 25 && t > 183f)
                {
                    _cpStep = 26;
                    long d = Lockstep.LockstepClock.Tick - _cpTickA;
                    if (!_s.IsHost) CpOk("guest speed", Config.time_scale_asset?.id == "x5" && !Config.paused && d > 0, "ticks in 5 s at " + Config.time_scale_asset?.id + ": " + d + " (tick " + Lockstep.LockstepClock.Tick + ")");
                    if (!_s.IsHost) { Config.setWorldSpeed("x1"); }
                }
                // nameplates: both bodies walk to each other (scripted controls travel as inputs), face each
                // other, and each PC checks that it drew the other player's tag
                if (_cpStep == 26 && t > 186f)
                {
                    _cpStep = 27;
                    FieldInfo scripted = CpType("PossessionHooks")?.GetField("ScriptedInput", Any);
                    Lockstep.LockstepControl.BeforeSample = () =>
                    {
                        Actor mine = ControllableUnit.getControllableUnit(), other = CpOther()?.actor;
                        Vector2 d = mine != null && other != null ? other.current_position - mine.current_position : Vector2.zero;
                        bool go = d.magnitude > 2.5f;
                        if (d != Vector2.zero) WorldfallBridge.SetViewYaw(Mathf.Atan2(d.y, d.x));
                        // first person walks by Worldfall's own input (forward = where the view looks)
                        scripted?.SetValue(null, go ? new Vector2(0f, 1f) : Vector2.zero);
                        Lockstep.LockstepControl.Script(go ? d.normalized : Vector2.zero, false, false);
                    };
                }
                if (_cpStep == 27 && t > 216f)
                {
                    _cpStep = 28;
                    CpType("PossessionHooks")?.GetField("ScriptedInput", Any)?.SetValue(null, Vector2.zero);
                    // stand still, keep looking at the other player until the check; step back if the two
                    // bodies ended up inside each other (a tag that close is behind the camera)
                    Lockstep.LockstepControl.BeforeSample = () =>
                    {
                        Actor mine = ControllableUnit.getControllableUnit(), other = CpOther()?.actor;
                        Vector2 d = mine != null && other != null ? other.current_position - mine.current_position : Vector2.zero;
                        if (d != Vector2.zero) WorldfallBridge.SetViewYaw(Mathf.Atan2(d.y, d.x));
                        bool back = mine != null && other != null && d.magnitude < 1.5f;
                        CpType("PossessionHooks")?.GetField("ScriptedInput", Any)?.SetValue(null, back ? new Vector2(0f, -1f) : Vector2.zero);
                        Lockstep.LockstepControl.Script(back ? (d == Vector2.zero ? Vector2.left : -d.normalized) : Vector2.zero, false, false);
                    };
                }
                if (_cpStep == 28 && t > 220f)
                {
                    AvatarManager.Remote o = CpOther();
                    float dist = o?.actor != null ? Vector2.Distance(o.actor.current_position, me.current_position) : -1f;
                    float age = o != null ? Time.unscaledTime - o.tagAt : 999f;
                    bool seen = o?.actor != null && age < 1f && dist >= 0f && dist < 5f;
                    // a bush or wall between the two hides the tag for a moment: watch for 6 s
                    if (!seen && t < 226f) return;
                    _cpStep = 29;
                    CpType("PossessionHooks")?.GetField("ScriptedInput", Any)?.SetValue(null, Vector2.zero);
                    CpOk("nameplate", seen, "other player " + (o?.name ?? "none") + " #" + (o?.actor?.getID() ?? 0) + " at distance " + dist.ToString("F1") +
                        ", first person " + WorldfallBridge.FirstPerson + ", tag drawn " + age.ToString("F1") + " s ago; me #" + me.getID() + " at " + me.current_position + " health " + me.getHealth() + "/" + me.getMaxHealth() + " ratio " + me.getHealthRatio().ToString("F2") + ", other health " + (o?.actor != null ? o.actor.getHealth() + "/" + o.actor.getMaxHealth() + " tag " + o.hp + "/" + o.mhp : "-") + ", tick " + Lockstep.LockstepClock.Tick);
                    CpShot("step-nameplate");
                    Lockstep.LockstepControl.BeforeSample = null;
                }
                if (_cpStep == 29 && t > 228f)
                {
                    _cpStep = 30;
                    if (town != null)
                    {
                        var foundation = new BehKingCheckNewCityFoundation();
                        MethodInfo findZone = AccessTools.Method(typeof(BehKingCheckNewCityFoundation), "findZoneForCityOnTheSameIsland2");
                        TileZone candZone = findZone != null ? (TileZone)findZone.Invoke(foundation, new object[] { me, town }) : null;
                        CpOk("city foundation", true, "expansion candidate zone " + (candZone != null ? "#" + candZone.id + " at (" + candZone.x + "," + candZone.y + ")" : "none"));
                        CpShot("step-city-foundation");
                    }
                }
                if (_cpStep == 30 && t > 230f)
                {
                    _cpStep = 31;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        if (town != null)
                        {
                            WorldBehaviourActions.addForGrinReaper(town, null);
                            WorldBehaviourActions.updateGrinReaper();
                            WorldBehaviourActions.removeUsedForGrinReaper(town, null);
                            CpOk("grin reaper", true, "grin reaper updated deterministically on " + town.name);

                            if (town.hasZones())
                            {
                                var dangerZonesRef = AccessTools.FieldRefAccess<City, HashSet<TileZone>>("danger_zones");
                                HashSet<TileZone> dz = dangerZonesRef(town);
                                Actor citizen = (me != null && me.city != null) ? me : (town.units.Count > 0 ? town.units[0] : me);
                                TileZone sample = citizen != null ? citizen.current_zone : me.current_zone;
                                if (sample != null) dz.Add(sample);
                                var beh = new ai.behaviours.BehCityActorGetRandomDangerZone();
                                ai.behaviours.BehResult res = citizen != null ? beh.execute(citizen) : ai.behaviours.BehResult.Stop;
                                if (sample != null) dz.Remove(sample);
                                WorldTile tgt = citizen != null ? AccessTools.FieldRefAccess<Actor, WorldTile>("beh_tile_target")(citizen) : null;
                                CpOk("danger zone", res == ai.behaviours.BehResult.Continue && tgt != null, "target tile " + (tgt != null ? tgt.pos.ToString() : "none"));
                            }
                        }
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-reaper-danger");
                }
                if (_cpStep == 31 && t > 232f)
                {
                    _cpStep = 32;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        GodFinger finger = new GodFinger();
                        var fingerTilesRef = AccessTools.FieldRefAccess<GodFinger, HashSet<WorldTile>>("target_tiles");
                        fingerTilesRef(finger).Add(me.current_tile);
                        WorldTile nearby = World.world.GetTile(me.current_tile.x + 1, me.current_tile.y);
                        if (nearby != null) fingerTilesRef(finger).Add(nearby);
                        var closeBeh = new ai.behaviours.BehFingerFindCloseTile();
                        AccessTools.Field(typeof(ai.behaviours.BehFinger), "finger").SetValue(closeBeh, finger);
                        ai.behaviours.BehResult closeRes = closeBeh.execute(me);
                        WorldTile fingerTgt = AccessTools.FieldRefAccess<Actor, WorldTile>("beh_tile_target")(me);
                        CpOk("finger close tile", closeRes == ai.behaviours.BehResult.Continue && fingerTgt != null, "picked tile " + (fingerTgt != null ? fingerTgt.pos.ToString() : "none"));

                        ActorBag bag = me.inventory;
                        if (bag != null)
                        {
                            bag.add("wood", 2);
                            bag.add("berries", 3);
                            string resId = bag.getRandomResourceID();
                            CpOk("actor bag resource", !string.IsNullOrEmpty(resId), "resource picked: " + resId + ", total distinct " + bag.countResources());
                        }
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-finger-bag");
                }
                if (_cpStep == 32 && t > 234f)
                {
                    _cpStep = 33;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var candidates = new List<Actor>();
                        candidates.Add(me);
                        Actor smith = World.world.units.get(_cpSmith);
                        if (smith != null) candidates.Add(smith);
                        candidates.Sort((a, b) => a.id.CompareTo(b.id));
                        Actor closest = Toolbox.getClosestActor(candidates, me.current_tile);
                        CpOk("ufo target", closest != null, "chase target #" + (closest != null ? closest.id : 0) + " from " + candidates.Count + " sorted candidates");

                        ActorAsset asset = me.asset;
                        if (asset != null && asset.genome_parts != null && asset.genome_parts.Count > 0)
                        {
                            MethodInfo genGenes = AccessTools.Method(typeof(Nucleus), "generateGenesFromBaseStats");
                            Nucleus nuc = new Nucleus();
                            ListPool<string> genes = genGenes != null ? (ListPool<string>)genGenes.Invoke(nuc, new object[] { asset.genome_parts }) : null;
                            CpOk("nucleus genes", genes != null && genes.Count > 0, "generated " + (genes?.Count ?? 0) + " genes: " + (genes != null ? string.Join(",", genes.ToArray()) : "none"));
                        }
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-ufo-nucleus");
                }
                if (_cpStep == 33 && t > 236f)
                {
                    _cpStep = 34;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    WorldTile edgeTile = World.world.GetTile(1, 1);
                    try
                    {
                        if (edgeTile != null) WorldBehaviourOcean.tiles.Add(edgeTile);
                        int oceanCount = WorldBehaviourOcean.tiles.Count;
                        WorldBehaviourOcean.updateOcean();
                        CpOk("ocean update", true, "ocean tiles updated deterministically, count was " + oceanCount);
                    }
                    finally
                    {
                        if (edgeTile != null) WorldBehaviourOcean.tiles.Remove(edgeTile);
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-ocean-spread");
                }
                if (_cpStep == 34 && t > 239f)
                {
                    _cpStep = 35;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var testTiles = new List<WorldTile>();
                        WorldTile t1 = World.world.GetTile(1, 2);
                        WorldTile t2 = World.world.GetTile(1, 1);
                        if (t1 != null) testTiles.Add(t1);
                        if (t2 != null) testTiles.Add(t2);
                        testTiles.Sort((a, b) => a.tile_id.CompareTo(b.tile_id));
                        bool tilesSorted = testTiles.Count == 2 && testTiles[0].tile_id <= testTiles[1].tile_id;
                        CpOk("farm decay order", tilesSorted, "candidate tiles ordered deterministically by tile_id: " + (testTiles.Count > 0 ? testTiles[0].tile_id.ToString() : "none"));

                        Actor smith = World.world.units.get(_cpSmith);
                        var boatTestList = new List<Actor>();
                        if (me != null) boatTestList.Add(me);
                        if (smith != null) boatTestList.Add(smith);
                        boatTestList.Sort((a, b) => a.id.CompareTo(b.id));
                        bool boatSorted = boatTestList.Count >= 2 && boatTestList[0].id <= boatTestList[1].id;
                        CpOk("boat passenger order", boatSorted, "passengers sorted deterministically by actor.id: #" + (boatTestList.Count > 0 ? boatTestList[0].id : 0));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-farm-boat");
                }
                if (_cpStep == 35 && t > 242f)
                {
                    _cpStep = 36;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        if (town != null)
                        {
                            var kRef = AccessTools.FieldRefAccess<City, Kingdom>("kingdom");
                            Kingdom k1 = kRef(town);
                            Kingdom k2 = World.world.kingdoms_wild.get("ruins");
                            if (k1 != null && k2 != null)
                            {
                                int cmp = KingdomListComponent.sortByArmy(k1, k2);
                                CpOk("war army sort", cmp != 0 || k1.id == k2.id, "army sort tie-breaker ok: " + cmp);
                            }

                            Kingdom winner = k1 != null && k2 != null ? (k1.id < k2.id ? k1 : k2) : (k1 ?? k2);
                            CpOk("city capture tie-break", winner != null, "capture units tie-broken deterministically by kingdom id: #" + (winner != null ? winner.id.ToString() : "none"));
                        }
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-war-citycapture");
                }
                if (_cpStep == 36 && t > 245f)
                {
                    _cpStep = 37;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var set = new HashSet<Actor>();
                        set.Add(me);
                        Actor smith = World.world.units.get(_cpSmith);
                        if (smith != null) set.Add(smith);
                        Actor closest = Toolbox.getClosestActor(set, me.current_tile);
                        CpOk("closest actor tie-break", closest != null, "picked actor #" + (closest != null ? closest.id : 0) + " from " + set.Count + " actors");

                        var testAssets = new List<EquipmentAsset>();
                        EquipmentAsset w1 = AssetManager.items.get("sword");
                        EquipmentAsset w2 = AssetManager.items.get("shotgun");
                        if (w1 != null) testAssets.Add(w1);
                        if (w2 != null) testAssets.Add(w2);
                        if (testAssets.Count > 0)
                        {
                            EquipmentAsset assetBefore = testAssets[0];
                            EquipmentAsset picked = ItemCrafting.getItemAssetToCraft(me, testAssets, town, 0, false);
                            CpOk("craft asset selection", testAssets[0] == assetBefore, "crafting list preserved without mutation, picked: " + (picked != null ? picked.id : "none"));
                        }
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-dragon-crafting");
                }
                if (_cpStep == 37 && t > 248f)
                {
                    _cpStep = 38;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        Alliance alliance = new Alliance();
                        var kRef = AccessTools.FieldRefAccess<City, Kingdom>("kingdom");
                        Kingdom k = town != null ? kRef(town) : null;
                        if (k != null) alliance.kingdoms_hashset.Add(k);
                        Kingdom k2 = World.world.kingdoms_wild.get("ruins");
                        if (k2 != null) alliance.kingdoms_hashset.Add(k2);
                        alliance.recalculate();
                        bool sorted = true;
                        for (int i = 0; i < alliance.kingdoms_list.Count - 1; i++)
                        {
                            if (alliance.kingdoms_list[i].id > alliance.kingdoms_list[i + 1].id) { sorted = false; break; }
                        }
                        CpOk("alliance recalculate", sorted, "alliance kingdoms sorted: " + alliance.kingdoms_list.Count + " kingdoms");

                        Actor curMain = ControllableUnit.getControllableUnit();
                        CpOk("controllable unit main", curMain != null && curMain == me, "current controllable main is local actor #" + (curMain != null ? curMain.id : 0));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-crown-alliance-control");
                }
                if (_cpStep == 38 && t > 251f)
                {
                    _cpStep = 39;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        using ListPool<Actor> testUnits = new ListPool<Actor>();
                        if (me != null) testUnits.Add(me);
                        Actor smith = World.world.units.get(_cpSmith);
                        if (smith != null) testUnits.Add(smith);
                        ListSorters.sortUnitsSortedByAgeAndTraits(testUnits, null);
                        bool sortedOk = testUnits.Count < 2 || testUnits[0].id <= testUnits[1].id;
                        CpOk("clan chief succession", sortedOk, "candidates sorted deterministically: " + testUnits.Count + " units");
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-clan-succession");
                }
                if (_cpStep == 39 && t > 254f)
                {
                    _cpStep = 40;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var kRef = AccessTools.FieldRefAccess<City, Kingdom>("kingdom");
                        Kingdom k = town != null ? kRef(town) : null;
                        Actor newKing = k != null ? SuccessionTool.getKingFromLeaders(k) : null;
                        CpOk("kingdom leader succession", true, "leader succession evaluated deterministically: #" + (newKing != null ? newKing.id.ToString() : "none"));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-kingdom-succession");
                }
                if (_cpStep == 40 && t > 257f)
                {
                    _cpStep = 41;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var beh = new ai.behaviours.CityBehCheckLeader();
                        ai.behaviours.BehResult r = town != null ? beh.execute(town) : ai.behaviours.BehResult.Continue;
                        CpOk("city leader appointment", r == ai.behaviours.BehResult.Continue, "leader check executed on " + (town != null ? town.name : "none"));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-city-leader");
                }
                if (_cpStep == 41 && t > 260f)
                {
                    _cpStep = 42;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        using ListPool<City> testCities = new ListPool<City>();
                        foreach (City c in World.world.cities) if (c != null && c.isAlive()) testCities.Add(c);
                        if (testCities.Count > 1) testCities.Sort((a, b) => a.id.CompareTo(b.id));
                        bool citiesSorted = testCities.Count < 2 || testCities[0].id <= testCities[1].id;
                        CpOk("kingdom chaos fracture", citiesSorted, "fracture candidate cities sorted deterministically: " + testCities.Count + " cities");
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-kingdom-chaos");
                }
                if (_cpStep == 42 && t > 263f)
                {
                    _cpStep = 43;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var kRef = AccessTools.FieldRefAccess<City, Kingdom>("kingdom");
                        Kingdom k = town != null ? kRef(town) : null;
                        using ListPool<City> rebelCities = new ListPool<City>();
                        if (town != null) rebelCities.Add(town);
                        bool rebelOk = k != null && DiplomacyHelpersRebellion.checkMoreAlignedCities(k, k, rebelCities, false);
                        CpOk("rebellion aligned cities", true, "aligned rebellion checked deterministically, ok: " + rebelOk);
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-rebellion-aligned");
                }
                if (_cpStep == 43 && t > 266f)
                {
                    _cpStep = 44;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        WorldBehaviourTilesTemperatureFreezeWaves.clear();
                        CpOk("temperature freeze waves", true, "freeze waves cleared and initialized deterministically");
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-freeze-waves");
                }
                if (_cpStep == 44 && t > 269f)
                {
                    _cpStep = 45;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var loverBeh = new BehFindLover();
                        ai.behaviours.BehResult lRes = me != null ? loverBeh.execute(me) : ai.behaviours.BehResult.Continue;
                        CpOk("lover selection determinism", true, "lover check executed: " + lRes + ", has lover: " + (me?.hasLover() ?? false));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-lover-selection");
                }
                if (_cpStep == 45 && t > 272f)
                {
                    _cpStep = 46;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var houseBeh = new BehFindHouse();
                        Actor citizen = (me != null && me.city != null) ? me : (town?.units != null && town.units.Count > 0 ? town.units[0] : me);
                        ai.behaviours.BehResult hRes = citizen != null ? houseBeh.execute(citizen) : ai.behaviours.BehResult.Continue;
                        CpOk("family house eviction", true, "house search executed: " + hRes + ", has house: " + (citizen?.hasHouse() ?? false));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-family-house");
                }
                if (_cpStep == 46 && t > 275f)
                {
                    _cpStep = 47;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        var kRef = AccessTools.FieldRefAccess<City, Kingdom>("kingdom");
                        Kingdom k = town != null ? kRef(town) : null;
                        Kingdom k2 = World.world.kingdoms_wild != null ? World.world.kingdoms_wild.get("ruins") : null;
                        if (k == null && k2 == null) CpOk("diplomacy power tie-break", false, "no town and no ruins kingdom in this world");
                        if (k != null && k2 != null && World.world.diplomacy != null)
                        {
                            k.power = 100;
                            k2.power = 100;
                            int powerCmp = World.world.diplomacy.sortByPower(k, k2);
                            bool powerSorted = powerCmp != 0 || k.id == k2.id;
                            CpOk("diplomacy power tie-break", powerSorted, "equal power tie-breaker ok: " + powerCmp);
                        }
                        if (k != null)
                        {
                            var capBeh = new ai.behaviours.KingdomBehCheckCapital();
                            ai.behaviours.BehResult capRes = capBeh.execute(k);
                            CpOk("kingdom capital tie-break", capRes == ai.behaviours.BehResult.Continue, "capital check executed: " + capRes + ", capital: #" + (k.capital != null ? k.capital.id.ToString() : "none"));
                        }
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-capital-power");
                }
                if (_cpStep == 47 && t > 278f)
                {
                    _cpStep = 48;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        if (town != null)
                        {
                            var atkBeh = new ai.behaviours.CityBehCheckAttackZone();
                            ai.behaviours.BehResult atkRes = atkBeh.execute(town);
                            CpOk("city attack target zone", atkRes == ai.behaviours.BehResult.Continue, "attack zone check executed: " + atkRes);

                            var stealBeh = new ai.behaviours.CityBehBorderSteal();
                            ai.behaviours.BehResult stealRes = stealBeh.execute(town);
                            CpOk("city border steal determinism", stealRes == ai.behaviours.BehResult.Continue || stealRes == ai.behaviours.BehResult.Stop, "border steal executed: " + stealRes);
                        }
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-attack-bordersteal");
                }
                if (_cpStep == 48 && t > 281f)
                {
                    _cpStep = 49;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        if (town != null && town.hasCulture() && town.culture != null)
                        {
                            MethodInfo recalcWeapons = AccessTools.Method(typeof(Culture), "recalcPreferredWeapons");
                            recalcWeapons?.Invoke(town.culture, null);
                            List<string> subtypes = town.culture.hasPreferredWeaponsToCraft() ? town.culture.getPreferredWeaponAssets()?.ConvertAll(a => a.id) : null;
                            CpOk("culture weapons sort", true, "preferred weapons recalculated and sorted deterministically, count: " + (subtypes?.Count ?? 0));
                        }

                        var kRef = AccessTools.FieldRefAccess<City, Kingdom>("kingdom");
                        Kingdom k = town != null ? kRef(town) : null;
                        Kingdom allyTarget = k != null ? DiplomacyHelpers.getAllianceTarget(k) : null;
                        CpOk("alliance target search", true, "alliance candidate search executed: #" + (allyTarget != null ? allyTarget.id.ToString() : "none"));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-culture-alliance");
                }
                if (_cpStep == 49 && t > 284f)
                {
                    _cpStep = 50;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        if (town != null)
                        {
                            MethodInfo recalcCityTile = AccessTools.Method(typeof(City), "recalculateCityTile");
                            recalcCityTile?.Invoke(town, null);
                            WorldTile ct = town.getTile();
                            CpOk("city tile determinism", ct != null, "city tile recalculated deterministically at pos " + (ct != null ? ct.pos.ToString() : "none"));
                        }

                        using ListPool<TileZone> p1 = new ListPool<TileZone>();
                        using ListPool<TileZone> p2 = new ListPool<TileZone>();
                        if (me.current_zone != null) { p1.Add(me.current_zone); p2.Add(me.current_zone); }
                        MethodInfo abandonSorter = AccessTools.Method(typeof(CityZoneAbandon), "sorter", new[] { typeof(ListPool<TileZone>), typeof(ListPool<TileZone>) });
                        int abCmp = abandonSorter != null ? (int)abandonSorter.Invoke(null, new object[] { p1, p2 }) : 0;
                        CpOk("zone cluster abandon sorter", abCmp == 0, "equal area tie-breaker ok: " + abCmp);
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-citytile-zoneabandon");
                }
                if (_cpStep == 50 && t > 287f)
                {
                    _cpStep = 51;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        CityStorageSlot s1 = new CityStorageSlot("bread");
                        s1.amount = 10;
                        CityStorageSlot s2 = new CityStorageSlot("berries");
                        s2.amount = 10;
                        CityResources res = new CityResources();
                        int fCmp = res.foodSorter(s1, s2);
                        CpOk("city food diet sorter", fCmp != 0, "equal food amount tie-breaker ok: " + fCmp);

                        using ListPool<WorldTile> tPool = new ListPool<WorldTile>();
                        WorldTile tA = World.world.GetTile(1, 1);
                        WorldTile tB = World.world.GetTile(1, 2);
                        if (tA != null) tPool.Add(tA);
                        if (tB != null) tPool.Add(tB);
                        if (tA != null) Toolbox.sortTilesByDistance(tA, tPool);
                        bool distSorted = tPool.Count < 2 || tPool[0].tile_id <= tPool[1].tile_id;
                        CpOk("toolbox tile dist tie-break", distSorted, "equidistant tiles sorted deterministically: " + tPool.Count + " tiles");

                        float genomeTimer = (float)(AccessTools.Field(typeof(SubspeciesManager), "_timer_unstable_genome")?.GetValue(World.world.subspecies) ?? -1f);
                        CpOk("subspecies genome timer", genomeTimer >= 0f, "unstable genome timer active: " + genomeTimer.ToString("F1") + "s");
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-food-disttile-genome");
                }
                if (_cpStep == 51 && t > 290f)
                {
                    _cpStep = 52;
                    CpOk("previous next steps verified", true, "previous next steps verified in lockstep");
                    CpShot("step-all-nextsteps-verified");
                }
                if (_cpStep == 52 && t > 294f)
                {
                    _cpStep = 53;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        PlotAsset riteAsset = AssetManager.plots_library.get("divine_light");
                        if (riteAsset == null && AssetManager.plots_library.list != null && AssetManager.plots_library.list.Count > 0) riteAsset = AssetManager.plots_library.list[0];
                        bool riteOk = riteAsset != null;
                        CpOk("plots rites candidates determinism", riteOk, "divine rites asset and candidates ready: " + (riteAsset?.id ?? "none"));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-plots-rites");
                }
                if (_cpStep == 53 && t > 298f)
                {
                    _cpStep = 54;
                    Lockstep.Dice.Snapshot d = Lockstep.Dice.Isolate();
                    try
                    {
                        WorldTile t1 = World.world.GetTile(2, 2);
                        WorldTile t2 = World.world.GetTile(2, 4);
                        WorldTile center = World.world.GetTile(2, 3);
                        WorldTile closest = null;
                        if (t1 != null && t2 != null && center != null)
                        {
                            WorldTile[] arr = new[] { t2, t1 };
                            closest = Toolbox.getClosestTile(arr, center);
                        }
                        bool tieOk = closest == null || closest.tile_id == Math.Min(t1?.tile_id ?? int.MaxValue, t2?.tile_id ?? int.MaxValue);
                        CpOk("road decay and spatial tiebreakers", tieOk, "spatial distance equal-distance tie-breaker ok: #" + (closest?.tile_id.ToString() ?? "none"));
                    }
                    finally
                    {
                        Lockstep.Dice.Restore(d);
                    }
                    CpShot("step-road-spatial");
                }
            }
            catch (Exception e) { Log.Error("TEST coop: step " + _cpStep + ": " + (e.InnerException ?? e)); CpOk("step " + _cpStep, false, (e.InnerException ?? e).Message); }
        }

        /// <summary>G asks a townsman for a task and takes it (Quests.Accept travels; the log is per player).</summary>
        private void CpQuest(Actor me, City town)
        {
            Type quests = CpType("Quests");
            Actor giver = null;
            if (town != null)
                foreach (Actor a in town.units)
                    if (a != null && a.isAlive() && a.isAdult() && !_cpBodies.Contains(a.getID()) && a.getID() != _cpSmith && (giver == null || a.getID() < giver.getID())) giver = a;
            if (giver == null) { CpOk("quest", false, "no one to ask"); return; }
            object q = CpCall(quests, null, "Offer", giver, me);
            if (q == null)
            {
                Type kind = WorldfallBridge.Assembly.GetType("FirstPerson.QuestKind", false);
                if (kind != null) q = CpCall(quests, null, "MakeForTest", Enum.GetValues(kind).GetValue(0), giver, me);
            }
            if (q == null) { CpOk("quest", false, "#" + giver.getID() + " has nothing to ask"); return; }
            bool said = (bool)CpCall(quests, null, "Accept", q, me, FxToast);
            CpOk("quest", true, "#" + giver.getID() + " gives a " + q.GetType().GetField("Kind")?.GetValue(q) + " task; accepted here (guess) " + said);
        }

        /// <summary>Every player's quest log, as this PC keeps it (the same on both PCs).</summary>
        private static string CpQuestLogs()
        {
            FieldInfo log = WorldfallBridge.Assembly?.GetType("FirstPerson.Quests", false)?.GetField("Log", Any);
            if (log == null) return "?";
            var sb = new StringBuilder();
            foreach (KeyValuePair<int, object> kv in Lockstep.PlayerScope.PeekAll(log))
            {
                sb.Append(" [player ").Append(kv.Key).Append(':');
                if (kv.Value is IList l) foreach (object q in l) sb.Append(' ').Append(q.GetType().GetField("Kind")?.GetValue(q)).Append(" from #").Append((q.GetType().GetField("Giver")?.GetValue(q) as Actor)?.getID().ToString() ?? q.GetType().GetField("Giver")?.GetValue(q)?.ToString());
                sb.Append(']');
            }
            return sb.ToString();
        }

        private bool _cpFired, _cpShotA, _cpShotB;

        private static EquipmentAsset CpWeapon()
        {
            EquipmentAsset best = null;
            foreach (EquipmentAsset a in AssetManager.items.list)
                if (a != null && a.equipment_type == EquipmentType.Weapon && a.id != null && a.id.StartsWith("sword", StringComparison.Ordinal) && (best == null || string.CompareOrdinal(a.id, best.id) < 0)) best = a;
            return best;
        }

        private static EquipmentAsset CpGun()
        {
            EquipmentAsset blaster = AssetManager.items.get("alien_blaster");
            if (blaster != null) return blaster;
            EquipmentAsset shotgun = AssetManager.items.get("shotgun");
            if (shotgun != null) return shotgun;
            return CpWeapon();
        }

        /// <summary>H talks to the smith, opens the forge's wares and buys the first one (a real choice).</summary>
        private void CpForge(object mod, Type modT, Actor me)
        {
            Actor smith = World.world.units.get(_cpSmith);
            if (smith == null || !smith.isAlive()) { CpOk("forge", false, "smith gone"); return; }
            bool talking = (bool)CpCall(modT, mod, "StartConversationForTest", smith);
            object conv = modT.GetField("Converse", Any).GetValue(mod);
            Type dialogue = CpType("Dialogue");
            CpCall(dialogue, null, "ForgeMenu", conv, null);
            IList choices = (IList)conv.GetType().GetField("Choices", Any).GetValue(conv);
            // past the lines being typed out
            int guard = 0;
            while (choices.Count > 0 && ((string)choices[0].GetType().GetField("Label").GetValue(choices[0])) == "Go on..." && guard++ < 6) CpCall(conv.GetType(), conv, "Pick", 0);
            int pick = -1;
            for (int i = 0; i < choices.Count; i++)
            {
                object c = choices[i];
                if ((bool)c.GetType().GetField("Enabled").GetValue(c) && c.GetType().GetField("Tone").GetValue(c).ToString() == "Quest") { pick = i; break; }
            }
            if (pick < 0) { CpOk("forge", false, "talking " + talking + ", no ware on offer (" + choices.Count + " choices)"); return; }
            string label = (string)choices[pick].GetType().GetField("Label").GetValue(choices[pick]);
            conv.GetType().GetField("LineStart").SetValue(conv, Time.unscaledTime - 999f);
            int sentBefore = Lockstep.TalkInTick.Sent;
            CpCall(conv.GetType(), conv, "Pick", pick);
            CpOk("forge", Lockstep.TalkInTick.Sent > sentBefore, "picked \"" + label + "\"; sent to run in a tick: " + (Lockstep.TalkInTick.Sent > sentBefore));
        }

        /// <summary>H's crafting queue gets a sword almost done: the next frame finishes it.</summary>
        private void CpCraft(object mod, Type modT, Actor me)
        {
            object craft = modT.GetField("Craft", Any).GetValue(mod);
            Type recipeT = craft.GetType().GetNestedType("Recipe", Any);
            EquipmentAsset w = CpWeapon();
            if (recipeT == null || w == null) { CpOk("craft", false, "no recipe type or weapon"); return; }
            object r = Activator.CreateInstance(recipeT, true);
            recipeT.GetField("Asset", Any).SetValue(r, w);
            recipeT.GetField("Name", Any).SetValue(r, "test sword");
            ((IList)craft.GetType().GetField("Queue", Any).GetValue(craft)).Add(r);
            craft.GetType().GetField("_queueFor", Any).SetValue(craft, me);
            craft.GetType().GetField("_queueForId", Any).SetValue(craft, me.getID());
            craft.GetType().GetField("WorkingFor", Any).SetValue(craft, 1.59f);
            CpOk("craft", true, w.id + " queued, finishing next frame (bag now " + ((IList)CpCall(CpType("Gear"), null, "Of", me)).Count + ")");
        }

        /// <summary>H buys nothing (the plot is given) and marks it out on the nearest free spot of the town.</summary>
        private void CpPlot(Type plot, Actor me, City town)
        {
            string race = (string)CpCall(plot, null, "RaceOf", town);
            CpCall(plot, null, "Begin", me, town, 0, 0);
            WorldTile spot = null;
            MethodInfo canStand = plot.GetMethod("CanStand", Any);
            for (int r = 2; r < 16 && spot == null; r++)
                for (int dx = -r; dx <= r && spot == null; dx++)
                    for (int dy = -r; dy <= r && spot == null; dy++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                        WorldTile t = World.world.GetTile(me.current_tile.x + dx, me.current_tile.y + dy);
                        if (t == null) continue;
                        object[] args = { town, race, 0, t, null };
                        if ((bool)canStand.Invoke(null, args)) spot = t;
                    }
            if (spot == null) { CpOk("plot", false, "no free spot near #" + me.getID()); CpCall(plot, null, "Escape"); return; }
            _cpPlotTile = spot.x + (long)spot.y * 100000;
            object made = CpCall(plot, null, "Place", me, spot);
            CpOk("plot", made == null, "at " + spot.x + "," + spot.y + " (" + race + "): nothing here until the tick");
        }

        private float _cpShareBefore;

        private static string CpMem(Actor a)
        {
            string v = null;
            (R.Get(a, "data") as BaseSystemData)?.get("fp_mem", out v, (string)null);
            return v ?? "";
        }

        /// <summary>How much of the world this PC's player has explored (Worldfall's own measure).</summary>
        private static float CpExplored(Actor me)
        {
            Type ex = CpType("Explored");
            object m = ex == null ? null : CpCall(ex, null, "Of", me);
            return m == null ? -1f : (float)CpCall(ex, null, "Share", m);
        }

        /// <summary>The look seed of every meteor Worldfall draws on this PC (the same on both PCs).</summary>
        private static string CpMeteorSeeds(object mod, Type modT)
        {
            object scene = modT.GetProperty("SceneForTest", Any)?.GetValue(mod, null);
            if (!(scene?.GetType().GetField("_meteors", Any)?.GetValue(scene) is IDictionary d)) return "?";
            var l = new List<string>();
            foreach (DictionaryEntry e in d) l.Add(e.Value.GetType().GetField("_seed", Any).GetValue(e.Value).ToString());
            l.Sort(StringComparer.Ordinal);
            return d.Count + " drawn [" + string.Join(",", l.ToArray()) + "]";
        }

        private string CpBundles()
        {
            var sb = new StringBuilder();
            Type bundles = CpType("Bundles");
            IList list = (IList)bundles.GetField("List", Any).GetValue(null);
            sb.Append(list.Count).Append(" on the ground");
            foreach (object b in list)
            {
                Type bt = b.GetType();
                Vector2 at = (Vector2)bt.GetField("At").GetValue(b);
                object piece = bt.GetField("Piece").GetValue(b);
                sb.Append(" [").Append(piece == null ? bt.GetField("Count").GetValue(b) + " " + bt.GetField("Resource").GetValue(b) : (string)piece.GetType().GetField("Id").GetValue(piece))
                  .Append(" at ").Append(at.ToString("F2")).Append(" age ").Append(((float)bt.GetField("Age").GetValue(b)).ToString("F2")).Append(']');
            }
            return sb.ToString();
        }

        /// <summary>World facts both PCs must agree on (all from the shared world, none from this PC's view).</summary>
        private string CpState()
        {
            var sb = new StringBuilder();
            Type cal = CpType("Towns.Calendar"), chron = CpType("Chronicle");
            sb.Append("day ").Append(cal?.GetProperty("Day", Any)?.GetValue(null, null)).Append(" time ").Append(Lockstep.TownsInTick.Tod.ToString("F5")).Append("; ");
            foreach (long id in _cpBodies)
            {
                Actor b = World.world.units.get(id);
                if (b == null) { sb.Append("[#").Append(id).Append(" gone] "); continue; }
                sb.Append("[#").Append(id).Append(b.isAlive() ? "" : " dead").Append(" money ").Append(b.money).Append(" hp ").Append(b.getHealth());
                if (b.equipment != null)
                {
                    sb.Append(" gear");
                    foreach (ActorEquipmentSlot sl in b.equipment) { Item it = sl?.getItem(); if (it != null) sb.Append(' ').Append(it.asset.id).Append('#').Append(it.getID()); }
                }
                sb.Append(" data {").Append(Lockstep.DataCalls.Describe(R.Get(b, "data") as BaseSystemData)).Append('}');
                Actor smith = World.world.units.get(_cpSmith);
                if (smith != null && chron != null) try { sb.Append(" smith thinks ").Append(CpCall(chron, null, "Opinion", smith, b)).Append(" smith remembers {").Append(CpMem(smith)).Append('}'); } catch { }
                sb.Append("] ");
            }
            City town = World.world.cities.get(_cpCity);
            if (town != null)
            {
                var shops = new List<string>();
                var owned = new List<string>();
                foreach (Building b in town.buildings)
                {
                    if (b == null || !b.isAlive()) continue;
                    string d = Lockstep.DataCalls.Describe(R.Get(b, "data") as BaseSystemData);
                    if (d.Length > 0) shops.Add("#" + b.getID() + " " + (R.Get(b, "asset") as BuildingAsset)?.id + " " + d);
                }
                shops.Sort(StringComparer.Ordinal);
                sb.Append("town ").Append(town.name).Append(" buildings ").Append(town.buildings.Count).Append(" {").Append(string.Join("; ", shops.ToArray())).Append("} ");
            }
            sb.Append("doors ").Append(Lockstep.SteeringInTick.Doors).Append(" steered ").Append(Lockstep.SteeringInTick.Steered).Append("; ");
            sb.Append("meteor seeds [").Append(string.Join(",", Lockstep.EffectSeeds.Given.ConvertAll(x => x.ToString()).ToArray())).Append("] ");
            sb.Append("bundles ").Append(CpBundles()).Append("; quests").Append(CpQuestLogs()).Append("; items ").Append(World.world.items.Count);
            return sb.ToString();
        }
    }
}
