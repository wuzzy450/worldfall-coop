using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// "-coopfall-scenario lockstep": host and guest both run lockstep; once the world runs, each
    /// uses a god power every few seconds (alternating), through the lockstep inputs. Both log
    /// progress; the host compares checksums (any drift is logged and re-synced by the session).
    /// </summary>
    public partial class TestDriver
    {
        private static readonly string[] LockstepPowers =
        {
            "meteorite", "lightning", "fire", "bomb", "evil_mage", "white_mage", "tornado", "acid", "rain",
            "plague", "curse", "dragon", "grenade", "lava", "earthquake", "madness", "blessing", "necromancer",
        };
        private float _lsStart = -1f, _lsNextPower, _lsNextLog;
        private int _lsUsed;
        private bool _lsDone;
        private const float LockstepSeconds = 300f;
        private float _lsPossessAt = -1f, _lsSteerAt;
        private Vector2 _lsMove;
        private bool _lsAttack, _lsJump;
        private int _lsPossessions, _lsNudged;
        private static readonly bool ForceDesync = System.Array.Exists(System.Environment.GetCommandLineArgs(), x => x == "-coopfall-test-desync");
        private static readonly bool NoPossess = System.Array.Exists(System.Environment.GetCommandLineArgs(), x => x == "-coopfall-test-nopossess");

        /// <summary>
        /// From 20 s on, each player takes over a creature and steers it: a new direction every
        /// 2 s, attacking now and then, a jump now and then; after 60 s it lets go and takes
        /// another one (powers keep going meanwhile).
        /// </summary>
        private void LockstepPossessTick(float now)
        {
            if (now - _lsStart < 20f || NoPossess) return;
            if (Lockstep.LockstepControl.BeforeSample == null)
                Lockstep.LockstepControl.BeforeSample = () => { if (ControllableUnit.isControllingUnit()) Lockstep.LockstepControl.Script(_lsMove, _lsAttack, _lsJump); _lsJump = false; };
            bool possessing = ControllableUnit.isControllingUnit();
            if (!possessing || now - _lsPossessAt > 60f)
            {
                if (possessing) { ControllableUnit.clear(false); Log.Info("TEST lockstep: let go of my creature"); _lsPossessAt = now; return; }
                if (_lsPossessAt > 0f && now - _lsPossessAt < 3f) return;
                var units = World.world.units.getSimpleList();
                for (int k = 0; k < 20 && units.Count > 0; k++)
                {
                    Actor a = units[Random.Range(0, units.Count)];
                    if (a == null || !a.isAlive() || !a.canBePossessed() || Lockstep.LockstepControl.IsControlled(a.getID())) continue;
                    ControllableUnit.setControllableCreature(a);
                    _lsPossessAt = now;
                    _lsPossessions++;
                    Log.Info("TEST lockstep: possessing #" + a.getID() + " (" + a.asset.id + ")");
                    break;
                }
                return;
            }
            if (now < _lsSteerAt) return;
            _lsSteerAt = now + 2f;
            _lsMove = new Vector2(Random.Range(-1, 2), Random.Range(-1, 2));
            _lsAttack = Random.value < 0.3f;
            _lsJump = Random.value < 0.2f;
        }

        /// <summary>Name tags (the others' creatures as this PC labels them), who is indoors, the gusts.</summary>
        private string ExtrasLine()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(", tags [");
            foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values)
                if (r.on && r.actor != null && r.actor.isAlive()) sb.Append(r.name).Append("=#").Append(r.actor.getID()).Append(r.mhp > 0 ? " hp " + r.hp + "/" + r.mhp : "").Append(' ');
            sb.Append(']');
            foreach (Actor a in World.world.units)
                if (a != null && a.isAlive() && Lockstep.LockstepControl.IsControlled(a.getID()))
                {
                    Building b = WorldBoxApi.InsideBuilding(a);
                    sb.Append(", #").Append(a.getID()).Append(b != null ? " inside #" + b.getID() : " outside");
                }
            float[] g = WorldfallBridge.GustState();
            if (g != null) sb.Append(", gust ").Append(g[0].ToString("0.000")).Append(" dir ").Append(g[1].ToString("0.00"));
            sb.Append(", ").Append(Lockstep.SharedWeather.State());
            return sb.ToString();
        }

        private static System.Reflection.MethodInfo _hold;
        private Building _lsHouse;
        private bool _lsHouseDone, _lsInRoom;
        private float _lsHouseLog;

        /// <summary>
        /// Both players go into one house through Worldfall's own room view: the guest at 90 s
        /// (nearest house), the host at 95 s (the house the guest's creature is in, as the shared
        /// world has it). Until 120 s both log every 2 s what they see; then both leave.
        /// </summary>
        private void LockstepHouseTick(float now)
        {
            if (NoPossess || _lsHouseDone || now - _lsStart < (_s.IsHost ? 95f : 90f) || !WorldfallBridge.Present) return;
            Actor me = ControllableUnit.getControllableUnit();
            if (me == null || !me.isAlive()) return;
            if (_lsHouse == null)
            {
                if (_s.IsHost)
                {
                    foreach (Actor a in World.world.units)
                        if (a != null && a != me && a.isAlive() && Lockstep.LockstepControl.IsControlled(a.getID()) && WorldBoxApi.InsideBuilding(a) != null) { _lsHouse = WorldBoxApi.InsideBuilding(a); break; }
                    if (_lsHouse == null) { if (now - _lsStart > 105f) { _lsHouseDone = true; Log.Warn("TEST lockstep: house: the guest's creature never went inside"); } return; }
                }
                else
                {
                    float best = float.MaxValue;
                    foreach (Building b in World.world.buildings)
                    {
                        if (b == null || !b.isAlive() || !WorldfallBridge.IsEnterable(b)) continue;
                        float d = (b.current_position - (Vector2)me.current_position).sqrMagnitude;
                        if (d < best) { best = d; _lsHouse = b; }
                    }
                    if (_lsHouse == null) { _lsHouseDone = true; Log.Warn("TEST lockstep: house: no house to enter"); return; }
                }
                bool ok = WorldfallBridge.TestCall("EnterHouseForTest", _lsHouse);
                _lsInRoom = ok && WorldfallBridge.InsideHouse == _lsHouse;
                Log.Info("TEST lockstep: house: #" + me.getID() + " goes into #" + _lsHouse.getID() + (_lsInRoom ? " (Worldfall's room view)" : " (no room view here: holding it indoors directly)") + ", hp " + me.getHealth());
            }
            if (!_lsInRoom)
            {
                if (_hold == null) _hold = WorldfallBridge.Assembly.GetType("FirstPerson.HouseInterior", false)?.GetMethod("HoldIndoors", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                _hold?.Invoke(null, new object[] { me, _lsHouse });
            }
            if (now >= _lsHouseLog)
            {
                _lsHouseLog = now + 2f;
                var sb = new System.Text.StringBuilder("TEST lockstep: house: ");
                foreach (Actor a in World.world.units)
                {
                    if (a == null || !a.isAlive() || !Lockstep.LockstepControl.IsControlled(a.getID())) continue;
                    Building b = WorldBoxApi.InsideBuilding(a);
                    sb.Append('#').Append(a.getID()).Append(b != null ? " in #" + b.getID() : " OUTSIDE")
                      .Append(b != null && ((Vector2)a.current_position - b.current_position).sqrMagnitude < 0.01f ? " at the house" : " at " + ((Vector2)a.current_position).ToString("F1"))
                      .Append(" hp ").Append(a.getHealth()).Append(" visible ").Append(R.Get(a, "is_visible")).Append("; ");
                }
                Building mine = WorldfallBridge.InsideHouse;
                sb.Append("my room ").Append(mine != null ? "#" + mine.getID() : "none");
                if (WorldfallBridge.RoomEye(out Vector2 eye)) sb.Append(" eye ").Append(eye.ToString("F2"));
                foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values)
                    sb.Append("; ").Append(r.name).Append(" says house #").Append(r.bld).Append(r.roomPos.HasValue ? " spot " + r.roomPos.Value.ToString("F2") : "")
                      .Append(r.actor != null && WorldfallBridge.RoomHas(r.actor) ? ", standing in my room" : ", not in my room");
                Log.Info(sb.ToString());
            }
            if (now - _lsStart < 120f) return;
            if (_lsInRoom) WorldfallBridge.TestCall("LeaveRoomForTest");
            else WorldfallBridge.Assembly.GetType("FirstPerson.HouseInterior", false)?.GetMethod("LetOut", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object[] { me, _lsHouse });
            _lsHouseDone = true;
            Log.Info("TEST lockstep: house: left #" + _lsHouse.getID());
        }

        private long _lsTree, _lsTreeBy;
        private int _lsCallStep;

        /// <summary>
        /// Worldfall-style world changes from frame code: at 130 s the guest chops the nearest tree
        /// and gets 7 gold (outside a tick, as Worldfall does); at 140 s both log the tree and the gold.
        /// </summary>
        private void LockstepCallsTick(float now)
        {
            float t = now - _lsStart;
            if (_lsCallStep == 0 && t > 130f && !_s.IsHost)
            {
                Actor me = ControllableUnit.getControllableUnit();
                if (me == null || !me.isAlive()) return;
                Building tree = null;
                float best = float.MaxValue;
                foreach (Building b in World.world.buildings)
                {
                    if (b == null || !b.isAlive() || !(R.Get(b, "asset") is BuildingAsset ba) || ba.type != "type_tree") continue;
                    float d = (b.current_position - (Vector2)me.current_position).sqrMagnitude;
                    if (d < best) { best = d; tree = b; }
                }
                _lsCallStep = 1;
                if (tree == null) { Log.Warn("TEST lockstep: calls: no tree"); return; }
                Log.Info("TEST lockstep: calls: #" + me.getID() + " chops tree #" + tree.getID() + " and gets 7 gold (money " + R.CallN(me, "getMoney", 0) + ")");
                R.Call(tree, "extractResources", new[] { typeof(Actor) }, me);
                R.CallN(me, "addMoney", 1, 7);
                _lsTree = tree.getID(); _lsTreeBy = me.getID();
                _s.Net.Send("chat", new Newtonsoft.Json.Linq.JObject { ["text"] = "lstree " + tree.getID() + " " + me.getID() });
            }
            if (_lsCallStep <= 1 && t > 140f)
            {
                _lsCallStep = 2;
                Actor by = World.world.units.get(_lsTreeBy);
                Building tree = World.world.buildings.get(_lsTree);
                Log.Info("TEST lockstep: calls: tree #" + _lsTree + (tree == null ? " gone" : " alive " + tree.isAlive() + " chopped " + WorldBoxApi.Chopped(tree))
                    + ", #" + _lsTreeBy + (by == null ? " gone" : " money " + R.CallN(by, "getMoney", 0) + " carrying " + R.CallN(by, "hasResources", 0))
                    + ", relayed sent " + Lockstep.WorldCalls.Sent + " replayed " + Lockstep.WorldCalls.Replayed + " refused " + Lockstep.WorldCalls.Refused);
            }
        }

        private int _lsAbilityStep;
        private long _lsAbilityBy;

        /// <summary>
        /// At 150 s each player uses its creature's X ability (Worldfall) at the nearest creature, as
        /// a release of the ability key would (relayed, used in a tick); at 160 s both log mana/stamina.
        /// </summary>
        private void LockstepAbilityTick(float now)
        {
            float t = now - _lsStart;
            if (_lsAbilityStep == 0 && t > 150f)
            {
                _lsAbilityStep = 1;
                Actor me = ControllableUnit.getControllableUnit();
                if (me == null || !me.isAlive()) { Log.Warn("TEST lockstep: ability: not controlling a creature"); return; }
                string name = Lockstep.WorldfallInTick.AbilityName(me);
                if (name == null) { Log.Info("TEST lockstep: ability: #" + me.getID() + " (" + me.asset.id + ") has no X ability"); return; }
                Actor near = null;
                float best = float.MaxValue;
                foreach (Actor a in World.world.units)
                {
                    if (a == null || a == me || !a.isAlive()) continue;
                    float d = (a.current_position - me.current_position).sqrMagnitude;
                    if (d < best) { best = d; near = a; }
                }
                Vector2 at = near != null ? near.current_position : me.current_position + Vector2.right;
                _lsAbilityBy = me.getID();
                _lsAbilityUsers.Add(_lsAbilityBy);
                Log.Info("TEST lockstep: ability: #" + me.getID() + " (" + me.asset.id + ") uses " + name + " at " + (near != null ? "#" + near.getID() : "nothing") + ", mana " + me.getMana() + " stamina " + me.getStamina());
                Lockstep.WorldfallInTick.UseAbility(me, at, near, (at - me.current_position).normalized);
                _s.Net.Send("chat", new Newtonsoft.Json.Linq.JObject { ["text"] = "lsability " + me.getID() });
            }
            if (_lsAbilityStep <= 1 && t > 160f)
            {
                _lsAbilityStep = 2;
                foreach (long id in _lsAbilityUsers)
                {
                    Actor a = World.world.units.get(id);
                    Log.Info("TEST lockstep: ability: #" + id + (a == null ? " gone" : " alive " + a.isAlive() + " mana " + a.getMana() + " stamina " + a.getStamina() + " hp " + a.getHealth()));
                }
            }
        }

        private readonly System.Collections.Generic.SortedSet<long> _lsAbilityUsers = new System.Collections.Generic.SortedSet<long>();

        /// <summary>The guest tells which tree it chopped (chat line "lstree tree creature").</summary>
        internal void LockstepNote(string text)
        {
            string[] p = text.Split(' ');
            if (p.Length == 2 && p[0] == "lsability" && long.TryParse(p[1], out long ab)) _lsAbilityUsers.Add(ab);
            if (p.Length == 3 && p[0] == "lstree") { long.TryParse(p[1], out _lsTree); long.TryParse(p[2], out _lsTreeBy); }
        }

        private void LockstepTick()
        {
            Lockstep.LockstepSession ls = _s.Lockstep;
            float now = Time.unscaledTime;
            if (now >= _lsNextLog)
            {
                _lsNextLog = now + 10f;
                Log.Info("TEST lockstep: " + ls.StatusLine() + ", players " + (_s.OthersInRoom() + 1) + ", powers used " + _lsUsed + ", possessions " + _lsPossessions + " (controlled now " + Lockstep.LockstepControl.Count + "), checks ok " + ls.Checked + ", desyncs " + ls.Desyncs + ", late inputs " + Lockstep.LockstepInput.Late + ExtrasLine());
            }
            if (_lsDone || !_s.Online || !_s.InWorld || _s.OthersInRoom() == 0 || !ls.Active || Lockstep.LockstepClock.Granted < 100) return;
            if (_lsStart < 0f) { _s.ChatFrom += (id, text) => LockstepNote(text); _lsStart = now; _lsNextPower = now + (_s.IsHost ? 5f : 6.5f); Log.Info("TEST lockstep: running as " + (_s.IsHost ? "host" : "guest")); }
            if (now - _lsStart > LockstepSeconds)
            {
                _lsDone = true;
                Log.Info("TEST lockstep: done - " + ls.StatusLine() + ", powers used " + _lsUsed + ", checks ok " + ls.Checked + ", desyncs " + ls.Desyncs + (ls.LastDesync != null ? " (last: " + ls.LastDesync + ")" : ""));
                return;
            }
            LockstepPossessTick(now);
            LockstepHouseTick(now);
            LockstepCallsTick(now);
            LockstepAbilityTick(now);
            // "-coopfall-test-desync": the guest nudges one creature after 60 s and again after 150 s (times resyncs)
            if (ForceDesync && !_s.IsHost && (_lsNudged == 0 && now - _lsStart > 60f || _lsNudged == 1 && now - _lsStart > 150f))
            {
                var all = World.world.units.getSimpleList();
                if (all.Count > 0) { all[0].current_position += new Vector2(0.25f, 0f); _lsNudged++; Log.Info("TEST lockstep: nudged creature #" + all[0].getID() + " to force a resync"); }
            }
            if (now < _lsNextPower) return;
            _lsNextPower = now + 3f;
            // a creature's tile picked with the local dice (each player clicks somewhere different)
            var units = World.world.units.getSimpleList();
            if (units.Count == 0) return;
            Actor a = units[Random.Range(0, units.Count)];
            if (a == null || a.current_tile == null) return;
            string p = LockstepPowers[(_lsUsed * 2 + (_s.IsHost ? 0 : 1)) % LockstepPowers.Length];
            if (CoopMod.Instance.Powers.UseLocal(p, a.current_tile)) _lsUsed++;
        }
    }
}
