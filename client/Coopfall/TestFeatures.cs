using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// "-coopfall-scenario lockstep-features": the Worldfall features the "lockstep" scenario doesn't
    /// reach, each through its real entry point, both players possessing a creature (no god powers).
    /// The host takes the king of the realm with the most ships (lowest ID on a tie), the guest the
    /// lowest-ID citizen of another realm's city.
    ///  20 s  family follow on (both PCs' setting), host gives its family Follow, shares food (40 s), settles (45 s)
    ///  25 s  guest assaults the nearest creature of another realm (Law.OnHit): guards should come
    ///  60 s  guest enlists (Service), 62 s 5 kills, 64 s commands its army to march, 70 s moves its site, 90 s discharged
    /// 100 s  host touches the obelisk and calls a storm on a rival's city (Royal)
    /// 120 s  host's ships: sail to open water, 130 s bombard the nearest foreign creature, 145 s cease fire;
    ///        a war flag on the coast and a ferry of 4 warriors to it
    /// 170 s  forced resync; everything must go on after it
    /// Every 10 s both PCs log the same "state" line (world facts about both bodies, ships, councils).
    /// Each step logs "TEST features: step ok/failed".
    /// </summary>
    public partial class TestDriver
    {
        private bool _fxMode;
        private int _fxStep;
        private bool _fxHooked;
        private long _fxRealm, _fxChestAt = -1, _fxStormCity;
        private float _fxStormAt = -1f;
        private bool _fxStormAgain;

        /// <summary>The host names a coming tick (chat "fxchest tick"); both PCs fill the realm's war chest in it.</summary>
        internal void FeaturesNote(string text)
        {
            string[] p = text.Split(' ');
            if (p.Length == 2 && p[0] == "fxchest" && long.TryParse(p[1], out long at)) _fxChestAt = at;
        }
        private const float FeaturesSeconds = 210f;
        private readonly List<long> _fxShips = new List<long>();
        private static readonly Action<string> FxToast = s => Log.Info("TEST features: toast: " + s);

        private static object FxMod(out Type modT)
        {
            modT = WorldfallBridge.Assembly?.GetType("FirstPerson.WorldBoxMod", false);
            return modT?.GetProperty("Instance", Any)?.GetValue(null, null);
        }

        private static Type FxType(string name) => WorldfallBridge.Assembly?.GetType("FirstPerson." + name, false);

        private static void FxOk(string step, bool ok, string more = "") => Log.Info("TEST features: " + step + (ok ? " ok" : " failed") + (more.Length > 0 ? " (" + more + ")" : ""));

        private static object FxCall(Type t, object on, string name, params object[] args)
        {
            foreach (MethodInfo m in t.GetMethods(Any))
                if (m.Name == name && m.GetParameters().Length == args.Length) return m.Invoke(on, args);
            throw new MissingMethodException(t.Name, name);
        }

        private static bool IsShip(Actor a) => a != null && a.isAlive() && a.asset != null && a.asset.is_boat;

        /// <summary>Both PCs pick the same creatures (lowest IDs), each possesses its own.</summary>
        private Actor FxPick()
        {
            var ships = new Dictionary<long, int>();
            foreach (Actor a in World.world.units)
                if (IsShip(a) && a.kingdom != null) { ships.TryGetValue(a.kingdom.getID(), out int n); ships[a.kingdom.getID()] = n + 1; }
            Kingdom best = null;
            int most = -1;
            var kingdoms = new List<Kingdom>();
            foreach (Kingdom k in World.world.kingdoms)
                if (k != null && k.isAlive() && k.isCiv() && k.king != null && k.king.isAlive()) kingdoms.Add(k);
            kingdoms.Sort((x, y) => x.getID().CompareTo(y.getID()));
            foreach (Kingdom k in kingdoms)
            {
                ships.TryGetValue(k.getID(), out int n);
                if (n > most) { most = n; best = k; }
            }
            if (best == null) return null;
            Log.Info("TEST features: host realm " + best.name + " with " + most + " ships");
            _fxRealm = best.getID();
            if (_s.IsHost) return best.king;
            Actor pick = null;
            foreach (Actor a in World.world.units)
                if (a != null && a.isAlive() && a.city != null && a.kingdom != null && a.kingdom != best && !a.isKing() && a.isAdult() && a.canBePossessed()
                    && (pick == null || a.getID() < pick.getID())) pick = a;
            return pick;
        }

        private void FeaturesTick(float now)
        {
            if (!WorldfallBridge.Present) { if (_fxStep == 0) { _fxStep = 99; Log.Warn("TEST features: Worldfall not loaded"); } return; }
            float t = now - _lsStart;
            object mod = FxMod(out Type modT);
            if (mod == null) return;
            Actor me = ControllableUnit.getControllableUnit();
            if (!_fxHooked)
            {
                // family follow on, on both PCs, before anyone controls a creature (it only acts for a
                // controlled one): switched on in different ticks on each PC it would split the world
                object settings = modT.GetField("Settings", Any)?.GetValue(mod);
                settings?.GetType().GetField("FamilyFollows", Any)?.SetValue(settings, true);
                // at the same ticks on both PCs (a slow PC logs later in real time, not at another tick)
                _fxHooked = true;
                Lockstep.PlayerScope.LogToasts = true;
                Lockstep.LockstepClock.AfterTick += tick =>
                {
                    if (tick == _fxChestAt)
                    {
                        Kingdom k = World.world.kingdoms.get(_fxRealm);
                        object data = k == null ? null : R.Get(k, "data");
                        if (data != null) R.Call(data, "set", new[] { typeof(string), typeof(long) }, "fp_kills_spent", -500L);
                        Log.Info("TEST features: war chest of " + k?.name + " filled at tick " + tick);
                    }
                    if (tick > 0 && tick % 500 == 0 && Lockstep.LockstepControl.Count > 0)
                        try { Log.Info("TEST features: state at epoch " + _s.Lockstep.Epoch + " tick " + tick + ": " + FxState()); }
                        catch (Exception e) { Log.Warn("TEST features: state: " + e.Message); }
                };
            }
            try
            {
                if (_fxStep == 0 && t > 15f)
                {
                    _fxStep = 1;
                    Actor pick = FxPick();
                    if (pick == null) { FxOk("possess", false, "nothing to pick"); _fxStep = 99; return; }
                    if (ControllableUnit.isControllingUnit()) ControllableUnit.clear(false);
                    ControllableUnit.setControllableCreature(pick);
                    _lsPossessions++;
                    Log.Info("TEST features: possessing #" + pick.getID() + " (" + pick.asset.id + ") of " + pick.kingdom?.name + ", city " + pick.city?.name);
                }
                if (_fxStep == 1 && t > 20f)
                {
                    _fxStep = 2;
                    FxOk("possess", me != null && me.isAlive(), me == null ? "none" : "#" + me.getID());
                    if (_s.IsHost && me != null)
                    {
                        Type ff = FxType("FamilyFollow");
                        var members = new List<Actor>((IEnumerable<Actor>)ff.GetProperty("Members", Any).GetValue(null, null));
                        object follow = Enum.Parse(ff.GetNestedType("Orders", Any), "Follow");
                        int n = members.Count == 0 ? 0 : (int)FxCall(ff, null, "Give", me, members, follow, me.current_tile);
                        FxOk("family give", members.Count > 0, members.Count + " members, " + n + " follow");
                    }
                }
                if (_fxStep == 2 && t > 25f)
                {
                    _fxStep = 3;
                    if (!_s.IsHost && me != null)
                    {
                        Actor victim = null;
                        float best = float.MaxValue;
                        foreach (Actor a in World.world.units)
                        {
                            if (a == null || a == me || !a.isAlive() || a.kingdom == me.kingdom || a.kingdom == null || !a.kingdom.isCiv()) continue;
                            float d = (a.current_position - me.current_position).sqrMagnitude;
                            if (d < best || d == best && a.getID() < victim.getID()) { best = d; victim = a; }
                        }
                        if (victim != null)
                        {
                            FxCall(FxType("Law"), null, "OnHit", victim);
                            // a bounty high enough that any grown subject nearby comes, not only warriors
                            FxCall(FxType("Law"), null, "SetBounty", victim.kingdom, me, 250);
                            FxOk("crime", true, "#" + me.getID() + " hits #" + victim.getID() + " of " + victim.kingdom.name + " at " + Mathf.Sqrt(best).ToString("F1") + " tiles");
                        }
                        else FxOk("crime", false, "no victim");
                    }
                }
                if (_fxStep == 3 && t > 40f)
                {
                    _fxStep = 4;
                    if (_s.IsHost && me != null)
                    {
                        Type ff = FxType("FamilyFollow");
                        var members = new List<Actor>((IEnumerable<Actor>)ff.GetProperty("Members", Any).GetValue(null, null));
                        object said = FxCall(ff, null, "ShareFood", me, members);
                        FxOk("family share food", true, members.Count + " members: " + said);
                    }
                }
                if (_fxStep == 4 && t > 45f)
                {
                    _fxStep = 5;
                    if (_s.IsHost && me != null)
                    {
                        object city = FxCall(FxType("FamilyFollow"), null, "SettleHere", me, FxToast);
                        FxOk("family settle", true, city == null ? "no new village (returns null when it is relayed)" : "village " + ((City)city).name);
                    }
                }
                if (!_s.IsHost && me != null && _fxStep >= 5 && _fxStep < 10)
                {
                    Type sv = FxType("Service");
                    if (_fxStep == 5 && t > 60f) { _fxStep = 6; FxCall(sv, null, "Enlist", me); FxOk("service enlist", true, "city " + me.city?.name); }
                    if (_fxStep == 6 && t > 62f) { _fxStep = 7; FxCall(sv, null, "AddKills", me, 5); FxOk("service kills", true); }
                    if (_fxStep == 7 && t > 64f)
                    {
                        _fxStep = 8;
                        WorldTile to = World.world.GetTile(me.current_tile.x + 6, me.current_tile.y) ?? me.current_tile;
                        object march = Enum.Parse(sv.GetNestedType("CommandKind", Any) ?? FxType("Service+CommandKind") ?? WorldfallBridge.Assembly.GetType("FirstPerson.Service+CommandKind"), "March");
                        FxCall(sv, null, "CommandArmy", me, march, to, "test march");
                        FxOk("service command", true, "march to " + to.x + "," + to.y);
                    }
                    if (_fxStep == 8 && t > 70f) { _fxStep = 9; FxCall(sv, null, "MoveSite", me, me.current_tile.zone); FxOk("service move site", true); }
                    if (_fxStep == 9 && t > 90f) { _fxStep = 10; FxCall(sv, null, "Discharge", me); FxOk("service discharge", true); }
                }
                if (!_s.IsHost && _fxStep == 5 && t > 60f && (me == null || !me.isAlive())) { _fxStep = 10; FxOk("service", false, "skipped: no living creature (the guards may have killed it)"); }
                if (_s.IsHost && _fxStep >= 5 && _fxStep < 10 && t > 90f) _fxStep = 10;
                if (_fxStep == 10 && t > 95f)
                {
                    _fxStep = 105;
                    if (_s.IsHost)
                    {
                        _fxChestAt = Lockstep.LockstepClock.Granted + 150;
                        _s.Net.Send("chat", new Newtonsoft.Json.Linq.JObject { ["text"] = "fxchest " + _fxChestAt });
                    }
                }
                if (_fxStep == 105 && t > 103f && (!_s.IsHost || Lockstep.LockstepClock.Tick > _fxChestAt))
                {
                    _fxStep = 11;
                    if (_s.IsHost && me != null)
                    {
                        object royal = modT.GetField("Royal", Any).GetValue(mod);
                        Type royalT = royal.GetType();
                        FxCall(royalT, royal, "TouchObelisk", me, FxToast);
                        City target = null;
                        foreach (City c in World.world.cities)
                            if (c != null && c.isAlive() && R.Get(c, "kingdom") is Kingdom ck && ck != me.kingdom && ck.isCiv() && (target == null || c.getID() < target.getID())) target = c;
                        // enough gold for it
                        if (me.kingdom?.capital != null) R.CallN(me.kingdom.capital, "addResourcesToRandomStockpile", 2, "gold", 500);
                        object kind = Enum.Parse(FxType("Storm") ?? royalT.GetNestedType("Storm", Any), "Lightning");
                        if (target != null) FxCall(royalT, royal, "CallStorm", me, target, kind, FxToast);
                        _fxStormCity = target == null ? 0 : target.getID();
                        _fxStormAt = now;
                        FxOk("storm", target != null, target == null ? "no foreign city" : "lightning on " + target.name);
                    }
                }
                // striking a realm at peace asks for a second call within 5 s
                if (_fxStep == 11 && _s.IsHost && _fxStormCity != 0 && !_fxStormAgain && now - _fxStormAt > 1.5f && _fxStormAt > 0f)
                {
                    _fxStormAgain = true;
                    object royal = modT.GetField("Royal", Any).GetValue(mod);
                    City target = World.world.cities.get(_fxStormCity);
                    object kind = Enum.Parse(FxType("Storm") ?? royal.GetType().GetNestedType("Storm", Any), "Lightning");
                    if (me != null && target != null) FxCall(royal.GetType(), royal, "CallStorm", me, target, kind, FxToast);
                    Log.Info("TEST features: storm called again (confirming)");
                }
                if (_fxStep == 11 && t > 120f)
                {
                    _fxStep = 12;
                    if (_s.IsHost && me != null && me.kingdom != null)
                    {
                        object map = modT.GetField("WarMap", Any).GetValue(mod);
                        Type mapT = map.GetType();
                        var ships = new List<Actor>();
                        foreach (Actor a in World.world.units) if (IsShip(a) && a.kingdom == me.kingdom) ships.Add(a);
                        ships.Sort((x, y) => x.getID().CompareTo(y.getID()));
                        _fxShips.Clear();
                        foreach (Actor a in ships) _fxShips.Add(a.getID());
                        WorldTile water = ships.Count == 0 ? null : FxWaterNear(ships[0].current_tile, 12);
                        if (water != null)
                        {
                            object[] args = { me, ships, water, 0 };
                            int n = (int)mapT.GetMethod("SailTo", Any).Invoke(map, args);
                            FxOk("fleet sail", true, ships.Count + " ships to " + water.x + "," + water.y + ", " + n + " sent");
                        }
                        else FxOk("fleet sail", false, ships.Count + " ships, no water found");
                        // a flag on the coast and a ferry
                        WorldTile shore = ships.Count == 0 ? null : FxShoreNear(ships[0].current_tile, 20);
                        object flag = shore == null ? null : mapT.GetMethod("Plant", Any).Invoke(map, new object[] { shore, me.kingdom, 0 });
                        if (flag != null)
                        {
                            object royal = modT.GetField("Royal", Any).GetValue(mod);
                            var troops = new List<Actor>();
                            foreach (Actor a in World.world.units) if (a != null && a != me && a.isAlive() && a.kingdom == me.kingdom && a.isWarrior() && troops.Count < 4) troops.Add(a);
                            object[] fargs = { royal, me, troops, flag, null, 0, 0 };
                            int n = (int)mapT.GetMethod("Ferry", Any).Invoke(map, fargs);
                            FxOk("fleet ferry", true, troops.Count + " warriors, " + n + " ferried, no ship " + fargs[5] + ", ships " + fargs[6]);
                        }
                        else FxOk("fleet ferry", false, "no flag (shore " + (shore != null) + ")");
                    }
                }
                if (_fxStep == 12 && t > 130f)
                {
                    _fxStep = 13;
                    if (_s.IsHost && me != null)
                    {
                        object map = modT.GetField("WarMap", Any).GetValue(mod);
                        var ships = FxShipList();
                        Actor target = null;
                        float best = float.MaxValue;
                        if (ships.Count > 0)
                            foreach (Actor a in World.world.units)
                            {
                                if (a == null || !a.isAlive() || a.kingdom == me.kingdom || IsShip(a)) continue;
                                float d = (a.current_position - ships[0].current_position).sqrMagnitude;
                                if (d < best) { best = d; target = a; }
                            }
                        if (target != null)
                        {
                            object[] args = { me, ships, (Vector2)target.current_position, target, null, "test", 0 };
                            int n = (int)map.GetType().GetMethod("Bombard", Any).Invoke(map, args);
                            FxOk("fleet bombard", true, "#" + target.getID() + " at " + Mathf.Sqrt(best).ToString("F1") + " tiles, " + n + " firing, unarmed " + args[6]);
                        }
                        else FxOk("fleet bombard", false, ships.Count + " ships, no target");
                    }
                }
                if (_fxStep == 13 && t > 145f)
                {
                    _fxStep = 14;
                    if (_s.IsHost && me != null)
                    {
                        object map = modT.GetField("WarMap", Any).GetValue(mod);
                        var ships = FxShipList();
                        int n = (int)FxCall(map.GetType(), map, "CeaseFire", ships);
                        FxOk("fleet cease fire", ships.Count > 0, n + " of " + ships.Count);
                    }
                }
                if (_fxStep == 14 && t > 170f)
                {
                    _fxStep = 15;
                    if (_s.IsHost) { Log.Info("TEST features: forcing a resync"); _s.Lockstep.StartEpoch("test features"); }
                }
            }
            catch (Exception e) { Log.Error("TEST features: step " + _fxStep + ": " + (e.InnerException ?? e)); FxOk("step " + _fxStep, false, (e.InnerException ?? e).Message); }
        }

        private List<Actor> FxShipList()
        {
            var ships = new List<Actor>();
            foreach (long id in _fxShips) { Actor a = World.world.units.get(id); if (IsShip(a)) ships.Add(a); }
            return ships;
        }

        private static WorldTile FxWaterNear(WorldTile from, int r)
        {
            if (from == null) return null;
            for (int d = r; d >= 3; d--)
                foreach (Vector2Int o in new[] { new Vector2Int(d, 0), new Vector2Int(-d, 0), new Vector2Int(0, d), new Vector2Int(0, -d) })
                {
                    WorldTile t = World.world.GetTile(from.x + o.x, from.y + o.y);
                    if (t != null && t.Type.ocean) return t;
                }
            return null;
        }

        private static WorldTile FxShoreNear(WorldTile from, int r)
        {
            if (from == null) return null;
            for (int d = 1; d <= r; d++)
                foreach (Vector2Int o in new[] { new Vector2Int(d, 0), new Vector2Int(-d, 0), new Vector2Int(0, d), new Vector2Int(0, -d) })
                {
                    WorldTile t = World.world.GetTile(from.x + o.x, from.y + o.y);
                    if (t != null && t.Type.ground) return t;
                }
            return null;
        }

        /// <summary>World facts both PCs must agree on: the bodies, who attacks them, their followers, the host's ships, the councils.</summary>
        private string FxState()
        {
            var sb = new StringBuilder();
            var bodies = new List<Actor>();
            foreach (long id in Lockstep.LockstepControl.ControlledIds()) { Actor a = World.world.units.get(id); if (a != null) bodies.Add(a); }
            bodies.Sort((x, y) => x.getID().CompareTo(y.getID()));
            foreach (Actor b in bodies)
            {
                sb.Append("[#").Append(b.getID()).Append(b.isAlive() ? "" : " dead").Append(" at ").Append(((Vector2)b.current_position).ToString("F2")).Append(" hp ").Append(b.getHealth())
                  .Append(" warrior ").Append(b.isWarrior()).Append(" army ").Append(b.army?.getID().ToString() ?? "-");
                int kills = -1;
                try { object[] ka = { "fp_kills", 0, -1 }; HarmonyLib.AccessTools.Method(typeof(ActorData), "get", new[] { typeof(string), typeof(int).MakeByRefType(), typeof(int) })?.Invoke(R.Get(b, "data"), ka); kills = (int)ka[1]; } catch { }
                sb.Append(" kills ").Append(kills);
                try
                {
                    int bounty = 0;
                    foreach (object kv in (IEnumerable)FxCall(FxType("Law"), null, "WantedIn", b)) bounty += (int)kv.GetType().GetProperty("Value").GetValue(kv, null);
                    sb.Append(" bounty ").Append(bounty);
                }
                catch { sb.Append(" bounty ?"); }
                int attackers = 0, followers = 0;
                float closest = float.MaxValue;
                var near = new List<long>();
                foreach (Actor a in World.world.units)
                {
                    if (a == null || a == b || !a.isAlive()) continue;
                    float d = (a.current_position - b.current_position).sqrMagnitude;
                    if (R.Get(a, "attack_target") == (object)b) { attackers++; closest = Mathf.Min(closest, d); }
                    object ai = R.Get(a, "ai");
                    if ((ai == null ? null : R.Get(ai, "task")) is Asset task && task.id == "fp_family_follow" && d < 400f) followers++;
                    if (d < 16f) near.Add(a.getID());
                }
                near.Sort();
                sb.Append(" attackers ").Append(attackers).Append(attackers > 0 ? " closest " + Mathf.Sqrt(closest).ToString("F2") : "").Append(" following ").Append(followers)
                  .Append(" near ").Append(near.Count).Append(" {").Append(string.Join(",", near.GetRange(0, Math.Min(8, near.Count)).ConvertAll(x => x.ToString()).ToArray())).Append("}] ");
            }
            {
                sb.Append("ships");
                var all = new List<Actor>();
                foreach (Actor a in World.world.units) if (IsShip(a)) all.Add(a);
                all.Sort((x, y) => x.getID().CompareTo(y.getID()));
                int shown = 0;
                foreach (Actor a in all)
                {
                    if (bodies.Count == 0 || a.kingdom != bodies[0].kingdom || shown++ >= 4) continue;
                    sb.Append(" #").Append(a.getID()).Append(' ').Append(((Vector2)a.current_position).ToString("F1"));
                }
                sb.Append("; ");
            }
            sb.Append("steered ").Append(Lockstep.SteeringInTick.Steered).Append("; ");
            sb.Append(Lockstep.PlayerScope.Describe());
            return sb.ToString();
        }
    }
}
