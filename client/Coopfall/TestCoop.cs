using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

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
        private int _cpStep;
        private long _cpTickA;
        private const float CoopSeconds = 220f;
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

        /// <summary>Both PCs pick their creatures the same way: two adults of the town with the most houses.</summary>
        private Actor CpPick()
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
            if (best == null) return null;
            _cpCity = best.getID();
            var adults = new List<Actor>();
            foreach (Actor a in best.units) if (a != null && a.isAlive() && a.isAdult() && !a.isKing() && !a.isWarrior() && a.canBePossessed()) adults.Add(a);
            adults.Sort((x, y) => x.getID().CompareTo(y.getID()));
            if (adults.Count < 3) return null;
            _cpBodies.Clear();
            _cpBodies.Add(adults[0].getID());
            _cpBodies.Add(adults[1].getID());
            _cpSmith = adults[2].getID();
            Log.Info("TEST coop: town " + best.name + " (#" + best.getID() + ", " + most + " houses), host #" + adults[0].getID() + ", guest #" + adults[1].getID() + ", smith #" + _cpSmith);
            return _s.IsHost ? adults[0] : adults[1];
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
                if (_cpStep == 0 && t > 12f)
                {
                    _cpStep = 1;
                    Actor pick = CpPick();
                    if (pick == null) { CpOk("possess", false, "no town with 3 free adults"); _cpStep = 99; return; }
                    if (ControllableUnit.isControllingUnit()) ControllableUnit.clear(false);
                    ControllableUnit.setControllableCreature(pick);
                    _lsPossessions++;
                }
                if (_cpStep == 1 && t > 18f) { _cpStep = 2; CpOk("possess", me != null && me.isAlive(), me == null ? "none" : "#" + me.getID()); CpShot("step-possess"); }
                if (me == null || !me.isAlive()) return;
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
                        EquipmentAsset w = CpWeapon();
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
                if (_cpStep == 191 && t > 126f) { _cpStep = 192; CpOk("meteor look", Lockstep.EffectSeeds.Given.Count > 0, "seeds given " + string.Join(",", Lockstep.EffectSeeds.Given.ConvertAll(x => x.ToString()).ToArray()) + ", drawn with them here " + Lockstep.EffectSeeds.Drawn + ", drawing now: " + CpMeteorSeeds(mod, modT)); CpShot("step-meteor"); }
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
                    _cpStep = 19;
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
                    CpOk("guest pause", d <= 2 && Config.paused, "ticks in 5 s while paused: " + d + ", paused here " + Config.paused);
                    if (!_s.IsHost) { Log.Info("TEST coop: guest sets x5"); Config.paused = false; Config.setWorldSpeed("x5"); }
                }
                if (_cpStep == 24 && t > 178f) { _cpStep = 25; _cpTickA = Lockstep.LockstepClock.Tick; }
                if (_cpStep == 25 && t > 183f)
                {
                    _cpStep = 26;
                    long d = Lockstep.LockstepClock.Tick - _cpTickA;
                    CpOk("guest speed", Config.time_scale_asset?.id == "x5" && !Config.paused && d > 0, "ticks in 5 s at " + Config.time_scale_asset?.id + ": " + d + " (tick " + Lockstep.LockstepClock.Tick + ")");
                    if (!_s.IsHost) { Config.setWorldSpeed("x1"); }
                }
                // nameplates: both bodies walk to each other (scripted controls travel as inputs), face each
                // other, and each PC checks that it drew the other player's tag
                if (_cpStep == 26 && t > 186f)
                {
                    _cpStep = 27;
                    Lockstep.LockstepControl.BeforeSample = () =>
                    {
                        Actor mine = ControllableUnit.getControllableUnit(), other = CpOther()?.actor;
                        Vector2 d = mine != null && other != null ? other.current_position - mine.current_position : Vector2.zero;
                        Lockstep.LockstepControl.Script(d.magnitude > 2.5f ? d.normalized : Vector2.zero, false, false);
                    };
                }
                if (_cpStep == 27 && t > 206f)
                {
                    _cpStep = 28;
                    Lockstep.LockstepControl.BeforeSample = () => Lockstep.LockstepControl.Script(Vector2.zero, false, false);
                    AvatarManager.Remote o = CpOther();
                    if (o?.actor != null) { Vector2 d = o.actor.current_position - me.current_position; WorldfallBridge.SetViewYaw(Mathf.Atan2(d.y, d.x)); }
                }
                if (_cpStep == 28 && t > 210f)
                {
                    _cpStep = 29;
                    AvatarManager.Remote o = CpOther();
                    float dist = o?.actor != null ? Vector2.Distance(o.actor.current_position, me.current_position) : -1f;
                    float age = o != null ? Time.unscaledTime - o.tagAt : 999f;
                    CpOk("nameplate", o?.actor != null && age < 1f, "other player " + (o?.name ?? "none") + " #" + (o?.actor?.getID() ?? 0) + " at distance " + dist.ToString("F1") +
                        ", first person " + WorldfallBridge.FirstPerson + ", tag drawn " + age.ToString("F1") + " s ago; me #" + me.getID() + " at " + me.current_position + ", tick " + Lockstep.LockstepClock.Tick);
                    CpShot("step-nameplate");
                    Lockstep.LockstepControl.BeforeSample = null;
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

        private static EquipmentAsset CpWeapon()
        {
            EquipmentAsset best = null;
            foreach (EquipmentAsset a in AssetManager.items.list)
                if (a != null && a.equipment_type == EquipmentType.Weapon && a.id != null && a.id.StartsWith("sword", StringComparison.Ordinal) && (best == null || string.CompareOrdinal(a.id, best.id) < 0)) best = a;
            return best;
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
