using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// "-coopfall-scenario patched": checks the sync fixes of 2026-10-07 with two games on one PC.
    /// Starts like "meet" (two humans, the host possesses one, the guest the other, face to face),
    /// then runs one step per fix. Each step does something in one game and checks it in the
    /// game that should see it ("TEST CHECK name: PASS|FAIL detail"), followed by a paired
    /// diagnostic snapshot with screenshots of both games (DIAG lines in the host's log).
    /// (Scenario notes are kept locally, not in the repository.)
    /// </summary>
    public partial class TestDriver
    {
        private int _pStep;
        private bool _pChecking;
        private string _hostId;
        private long _houseId, _treeId, _kingId, _innerId;
        private int _baseCount;
        private float _floodUntil, _nextFlood;

        private static void Check(string name, bool pass, string detail)
        {
            Log.Info("TEST CHECK " + name + ": " + (pass ? "PASS" : "FAIL") + " - " + detail);
        }

        private void ToGuest(string cmd, JObject extra = null)
        {
            var m = extra ?? new JObject();
            m["cmd"] = cmd; m["for"] = _guestId;
            _s.Net.Send("diag", m);
        }

        private AvatarManager.Remote Other()
        {
            foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values) if (r.on && r.actor != null && r.actor.isAlive()) return r;
            return null;
        }

        private static BuildingAsset BAsset(Building b) { try { return AssetManager.buildings.get(((BuildingData)b.getData())?.asset_id); } catch { return null; } }

        private static Actor Me() { try { return ControllableUnit.isControllingUnit() ? ControllableUnit.getControllableUnit() : null; } catch { return null; } }

        private int Seen(string act) { CoopMod.Instance.Avatars.ActsSeen.TryGetValue(act, out int n); return n; }

        private Building Nearest(Vector2 at, Func<Building, bool> ok, float max = 40f)
        {
            Building best = null; float bd = max;
            foreach (Building b in World.world.buildings)
            {
                if (b == null || !b.isAlive() || b.isOnRemove() || BAsset(b) == null || !ok(b)) continue;
                float d = Vector2.Distance(b.current_position, at);
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        private Actor Walker()
        {
            Actor best = null;
            foreach (Actor a in World.world.units.getSimpleList())
                if (a != null && a.isAlive() && !ControllableUnit.isControllingUnit(a) && CoopMod.Instance.Avatars.PuppetOwner(a) == null
                    && a.asset != null && a.asset.id == "human" && (best == null || Vector2.Distance(a.current_position, _spot) < Vector2.Distance(best.current_position, _spot)))
                    best = a;
            return best;
        }

        // ---------------------------------------------------------------- host: the steps

        /// <summary>Each step: act (phase A), then check after its wait (phase B), then a paired DIAG.</summary>
        private void Patched(float now)
        {
            if (_floodUntil > 0f) Flood(now);
            if (!_pChecking)
            {
                float wait = PStep(_pStep, now);
                if (wait < 0f) { Log.Info("TEST meet: done"); _meetPhase = 99; return; }
                _pChecking = true; _meetAt = now + wait;
                return;
            }
            try { PCheck(_pStep); } catch (Exception e) { Log.Warn("TEST check " + _pStep + ": " + e.Message); }
            _pChecking = false;
            CoopMod.Instance.Diag.Request("patched: after step " + _pStep + " " + StepName(_pStep));
            _pStep++;
            _meetAt = now + 6f;   // the DIAG capture (screenshots) needs a moment
        }

        private static readonly string[] Steps =
        {
            "jump", "ability", "weapon", "shots", "npc-hit", "chop", "inside-building", "kingdom-color",
            "room-view", "king-kill", "resync-keeps-creature", "guest-death", "flood", "population", "nameplate-godview",
        };

        private static string StepName(int i) { return i < Steps.Length ? Steps[i] : "?"; }

        /// <returns>Seconds to wait before the check, or -1 when done.</returns>
        private float PStep(int i, float now)
        {
            if (i >= Steps.Length) return -1f;
            Log.Info("TEST step " + i + ": " + Steps[i]);
            Actor me = Me();
            AvatarManager.Remote g = Other();
            foreach (PlayerInfo pl in _s.Players) if (pl.id != _s.MyId && pl.room == _s.RoomId) _guestId = pl.id;
            switch (Steps[i])
            {
                case "jump":
                    _baseCount = Seen("jump");
                    ToGuest("jump");
                    return 3f;
                case "ability":
                    _baseCount = Seen("ability");
                    ToGuest("ability");
                    return 4f;
                case "weapon":
                    ToGuest("weapon", new JObject { ["w"] = "sword_iron" });
                    return 4f;
                case "shots":
                {
                    _baseCount = CoopMod.Instance.Combat.ShotsShown;
                    Actor w = Walker();
                    if (w != null && g != null)
                        World.world.projectiles.spawn(w, null, "arrow", w.current_position, g.actor.current_position, 0f, 0.5f);
                    ToGuest("shots", new JObject { ["base"] = 0 });
                    return 4f;
                }
                case "npc-hit":
                {
                    Actor w = Walker();
                    if (w == null || g == null) { Check("npc-hit", false, "no walker or no guest creature"); return 1f; }
                    ToGuest("markhp");
                    _npcHitAt = now + 1.5f;
                    _npcHitBy = w;
                    return 5f;
                }
                case "chop":
                {
                    Building t = Nearest(_spot, b => BAsset(b).building_type == BuildingType.Building_Tree && !WorldBoxApi.Chopped(b), 60f);
                    if (t == null) { Check("chop", false, "no tree near the meeting spot"); return 1f; }
                    _treeId = t.getID();
                    ToGuest("chop", new JObject { ["bid"] = _treeId.ToString(CultureInfo.InvariantCulture) });
                    return 4f;
                }
                case "inside-building":
                {
                    Building h = Nearest(_spot, b => WorldfallBridge.IsEnterable(b), 80f);
                    Actor w = Walker();
                    if (h == null || w == null) { Check("inside-building", false, "no enterable house or walker"); return 1f; }
                    _houseId = h.getID(); _innerId = w.getID();
                    WorldBoxApi.StayInBuilding(w, h);
                    Log.Info("TEST walker #" + _innerId + " goes into house #" + _houseId + " (" + BAsset(h)?.id + ")");
                    return 5f;
                }
                case "kingdom-color":
                {
                    Kingdom k = AnyKingdom(0);
                    if (k == null) { Check("kingdom-color", false, "no kingdom"); return 1f; }
                    var lib = AssetManager.kingdom_colors_library.list;
                    int next = (k.data.color_id + 1) % lib.Count;
                    k.updateColor(lib[next]);
                    Log.Info("TEST kingdom #" + k.data.id + " color -> " + next);
                    _kingdomColor = next; _kingdomColorId = k.data.id;
                    return 8f;
                }
                case "room-view":
                {
                    Building h = World.world.buildings.get(_houseId);
                    if (h == null || !h.isAlive()) h = Nearest(me != null ? me.current_position : _spot, b => WorldfallBridge.IsEnterable(b), 80f);
                    if (h == null || me == null) { Check("room-view", false, "no house or not possessing"); return 1f; }
                    _houseId = h.getID();
                    WorldfallBridge.TestCall("EnterHouseForTest", h);
                    ToGuest("enterhouse", new JObject { ["bid"] = _houseId.ToString(CultureInfo.InvariantCulture) });
                    return 6f;
                }
                case "king-kill":
                {
                    WorldfallBridge.TestCall("LeaveRoomForTest");
                    ToGuest("leavehouse");
                    Kingdom k = null;
                    foreach (Kingdom x in World.world.kingdoms) if (x != null && x.isAlive() && x.king != null && x.king.isAlive()) { k = x; break; }
                    if (k == null) { Check("king-kill", false, "no kingdom with a king"); return 1f; }
                    _kingId = k.king.getID();
                    Log.Info("TEST guest asked to kill king #" + _kingId + " of " + k.name);
                    ToGuest("killking", new JObject { ["aid"] = _kingId.ToString(CultureInfo.InvariantCulture) });
                    return 5f;
                }
                case "resync-keeps-creature":
                    ToGuest("resync");
                    return 25f;
                case "guest-death":
                    ToGuest("die");
                    return 5f;
                case "flood":
                    _floodUntil = now + 12f; _nextFlood = now;
                    ToGuest("flood", new JObject { ["x"] = (int)_spot.x + 12, ["y"] = (int)_spot.y });
                    return 50f;   // let the live sync catch up afterwards
                case "population":
                    ToGuest("pop");
                    if (me == null)
                    {
                        // The flood can kill the host's creature (fire, skeletons): the name tag step needs one.
                        WorldTile t = World.world.GetTile((int)_spot.x, (int)_spot.y);
                        Actor fresh = t == null ? null : World.world.units.spawnNewUnit("human", t, false, false, 0f);
                        if (fresh != null)
                        {
                            fresh.setNutrition(fresh.getMaxNutrition());
                            ControllableUnit.setControllableCreature(fresh);
                            if (WorldfallBridge.Present) WorldfallBridge.ViewEnabled = true;
                            Log.Info("TEST host's creature died in the flood: possessing new human #" + fresh.getID());
                        }
                    }
                    return 4f;
                case "nameplate-godview":
                    ToGuest("godview", new JObject { ["lx"] = me != null ? me.current_position.x : _spot.x, ["ly"] = me != null ? me.current_position.y : _spot.y });
                    return 6f;
            }
            return 1f;
        }

        private float _npcHitAt;
        private Actor _npcHitBy;
        private int _kingdomColor;
        private long _kingdomColorId;

        private void Flood(float now)
        {
            if (now > _floodUntil) { _floodUntil = 0f; return; }
            if (now < _nextFlood) return;
            _nextFlood = now + 0.25f;
            WorldTile t = World.world.GetTile((int)_spot.x - 12 + UnityEngine.Random.Range(-4, 5), (int)_spot.y + UnityEngine.Random.Range(-4, 5));
            CoopMod.Instance.Powers.UseLocal(UnityEngine.Random.value < 0.5f ? "human" : "skeleton", t);
        }

        /// <summary>Host-side checks (what the host should now see), and asks the guest for its side.</summary>
        private void PCheck(int i)
        {
            AvatarManager.Remote g = Other();
            switch (Steps[i])
            {
                case "jump": Check("jump", Seen("jump") > _baseCount, "host replayed " + (Seen("jump") - _baseCount) + " jump(s) from the guest"); break;
                case "ability": Check("ability", Seen("ability") > _baseCount, "host replayed " + (Seen("ability") - _baseCount) + " ability use(s)"); break;
                case "weapon":
                {
                    string w = g?.actor != null ? WorldBoxApi.WeaponId(g.actor) : null;
                    Check("weapon", w == "sword_iron", "guest's creature here holds " + (w ?? "nothing") + " (guest said " + (g?.weapon ?? "?") + ")");
                    break;
                }
                case "shots":
                    Check("shots-guest-to-host", CoopMod.Instance.Combat.ShotsShown > _baseCount, "host showed " + (CoopMod.Instance.Combat.ShotsShown - _baseCount) + " shot(s) from the guest");
                    ToGuest("checkshots");
                    break;
                case "npc-hit": ToGuest("checkhp"); break;
                case "chop":
                {
                    Building t = World.world.buildings.get(_treeId);
                    bool fell = t == null || !t.isAlive() || t.isOnRemove() || WorldBoxApi.Chopped(t);
                    Check("chop", fell, "tree #" + _treeId + (t == null ? " gone" : " chopped=" + WorldBoxApi.Chopped(t)));
                    break;
                }
                case "inside-building":
                {
                    // Compare with where the walker is on the host now (its own AI may have walked it out again).
                    Building now = WorldBoxApi.InsideBuilding(WorldBoxApi.FindActor(_innerId));
                    Log.Info("TEST walker #" + _innerId + " on the host is " + (now == null ? "outside" : "inside #" + now.getID()) + " (was put into #" + _houseId + ")");
                    ToGuest("checkinside", new JObject { ["aid"] = _innerId.ToString(CultureInfo.InvariantCulture), ["bid"] = (now?.getID() ?? 0).ToString(CultureInfo.InvariantCulture) });
                    break;
                }
                case "kingdom-color":
                    ToGuest("checkcolor", new JObject { ["kid"] = _kingdomColorId.ToString(CultureInfo.InvariantCulture), ["c"] = _kingdomColor });
                    break;
                case "room-view":
                {
                    bool inside = WorldfallBridge.InsideHouse != null;
                    Check("room-view-host", inside && g != null && WorldfallBridge.RoomHas(g.actor), "host inside=" + inside + ", guest in host's room=" + (g != null && WorldfallBridge.RoomHas(g.actor)));
                    float tagAge = g != null ? Time.unscaledTime - g.tagAt : 999f;
                    Check("room-nameplate-host", tagAge < 1f, "guest's tag in the room drawn " + tagAge.ToString("0.0", CultureInfo.InvariantCulture) + " s ago");
                    Shot("room-host");
                    ToGuest("checkroom");
                    break;
                }
                case "king-kill":
                {
                    Actor k = WorldBoxApi.FindActor(_kingId);
                    Check("king-kill", k == null || !k.isAlive(), "king #" + _kingId + (k == null || !k.isAlive() ? " died here (look for 'the king of' in the log)" : " still alive here"));
                    break;
                }
                case "resync-keeps-creature":
                    Check("resync-keeps-creature", g != null && g.actor != null && g.actor.isAlive() && g.aid == _bId && !g.standin && g.actor.getID() == _bId,
                          "guest's creature here: " + (g?.actor == null ? "none" : "#" + g.actor.getID() + " (guest says #" + g.aid + ") alive=" + g.actor.isAlive() + (g.standin ? " STAND-IN" : "")) + " (was #" + _bId + ")");
                    break;
                case "guest-death":
                {
                    Actor b = WorldBoxApi.FindActor(_bId);
                    Check("guest-death", b == null || !b.isAlive(), "guest's creature #" + _bId + (b == null || !b.isAlive() ? " died here too" : " still alive here"));
                    break;
                }
                case "flood":
                    Log.Info("TEST flood done: host has " + World.world.units.Count + " creatures, " + World.world.cities.Count + " villages, " + World.world.kingdoms.Count + " kingdoms");
                    ToGuest("checkflood", new JObject { ["u"] = World.world.units.Count, ["c"] = World.world.cities.Count, ["k"] = World.world.kingdoms.Count });
                    break;
                case "population": break;   // the guest logs it
                case "nameplate-godview": ToGuest("checktag"); break;
            }
        }

        /// <summary>Host, every frame of the npc-hit step: the walker hits the guest's creature once.</summary>
        private void PatchedFrame(float now)
        {
            if (_npcHitAt > 0f && now >= _npcHitAt)
            {
                _npcHitAt = 0f;
                AvatarManager.Remote g = Other();
                if (g?.actor != null && _npcHitBy != null && _npcHitBy.isAlive())
                {
                    R.Method(typeof(Actor), "getHit", typeof(float), typeof(bool), typeof(AttackType), typeof(BaseSimObject), typeof(bool), typeof(bool), typeof(bool))
                     ?.Invoke(g.actor, new object[] { 10f, true, AttackType.Weapon, _npcHitBy, false, false, false });
                    Log.Info("TEST walker #" + _npcHitBy.getID() + " hits the guest's creature for 10");
                }
            }
        }

        // ---------------------------------------------------------------- guest: commands

        private int _guestBase, _guestHp;
        private bool _guestFlood;
        private float _gFloodUntil, _gNextFlood;
        private Vector2 _gFloodAt;

        /// <summary>Guest side of the "patched" scenario. True if the command was one of these.</summary>
        private bool GuestCommand(string cmd, JObject p)
        {
            Actor me = Me();
            CombatSync combat = CoopMod.Instance.Combat;
            switch (cmd)
            {
                case "jump":
                    if (me != null) WorldBoxApi.AddStatus(me, "jump", 0f);
                    CoopMod.Instance.Avatars.SendAct("jump");
                    Log.Info("TEST guest jumps");
                    return true;
                case "ability":
                {
                    bool pressed = WorldfallBridge.TestCall("PressAbilityForTest", true);
                    Log.Info("TEST guest presses the ability key (" + (pressed ? "Worldfall hook" : "no hook") + ")");
                    return true;
                }
                case "weapon":
                {
                    if (me == null) { Check("weapon", false, "guest isn't possessing"); return true; }
                    EquipmentAsset ea = AssetManager.items.get((string)p["w"]);
                    if (ea != null) { if (me.equipment.weapon.getItem() != null) WorldSync.Unequip(me.equipment.weapon); me.equipment.setItem(World.world.items.generateItem(ea, null, null, 1, me), me); me.setStatsDirty(); }
                    Log.Info("TEST guest equips " + p["w"] + " -> holds " + WorldBoxApi.WeaponId(me));
                    return true;
                }
                case "shots":
                {
                    _guestBase = combat.ShotsShown;
                    AvatarManager.Remote h = Other();
                    if (me != null && h != null) World.world.projectiles.spawn(me, null, "arrow", me.current_position, h.actor.current_position, 0f, 0.5f);
                    Log.Info("TEST guest shoots an arrow at the host");
                    return true;
                }
                case "checkshots":
                    Check("shots-host-to-guest", combat.ShotsShown > _guestBase, "guest showed " + (combat.ShotsShown - _guestBase) + " shot(s) from the host");
                    return true;
                case "markhp":
                    _guestHp = me != null ? me.getHealth() : -1;
                    _guestBase = combat.HitsApplied;
                    return true;
                case "checkhp":
                    Check("npc-hit", me != null && combat.HitsApplied > _guestBase && me.getHealth() < _guestHp,
                          "guest hp " + _guestHp + " -> " + (me != null ? me.getHealth() : -1) + ", hits applied +" + (combat.HitsApplied - _guestBase));
                    return true;
                case "chop":
                {
                    long.TryParse((string)p["bid"] ?? "0", out long bid);
                    Building t = World.world.buildings.get(bid);
                    if (t == null) { Check("chop", false, "guest doesn't have tree #" + bid); return true; }
                    R.Call(t, "extractResources", new[] { typeof(Actor) }, me);
                    CoopMod.Instance.Avatars.SendAct("work", new JObject { ["bid"] = bid.ToString(CultureInfo.InvariantCulture), ["x"] = t.current_position.x, ["y"] = t.current_position.y });
                    Log.Info("TEST guest chops tree #" + bid);
                    return true;
                }
                case "checkinside":
                {
                    long.TryParse((string)p["aid"] ?? "0", out long aid);
                    long.TryParse((string)p["bid"] ?? "0", out long bid);
                    Building inside = WorldBoxApi.InsideBuilding(WorldBoxApi.FindActor(aid));
                    Check("inside-building", (inside?.getID() ?? 0) == bid, "walker #" + aid + " inside here: " + (inside == null ? "none" : "#" + inside.getID()) + " (host: " + (bid == 0 ? "outside" : "#" + bid) + ")");
                    return true;
                }
                case "checkcolor":
                {
                    long.TryParse((string)p["kid"] ?? "0", out long kid);
                    Kingdom k = World.world.kingdoms.get(kid);
                    int want = (int?)p["c"] ?? -1;
                    int shown = k == null ? -1 : AssetManager.kingdom_colors_library.list.IndexOf(k.getColor());
                    Check("kingdom-color", k != null && k.data.color_id == want && shown == want, "guest data " + k?.data.color_id + ", drawn " + shown + ", host " + want);
                    return true;
                }
                case "enterhouse":
                {
                    long.TryParse((string)p["bid"] ?? "0", out long bid);
                    Building h = World.world.buildings.get(bid);
                    bool ok = h != null && WorldfallBridge.TestCall("EnterHouseForTest", h);
                    _roomWalkAt = Time.unscaledTime + 1f;   // both start on the room's spawn spot: step away once inside
                    Log.Info("TEST guest enters house #" + bid + (ok ? "" : " (failed)"));
                    return true;
                }
                case "checkroom":
                {
                    AvatarManager.Remote h = Other();
                    bool inside = WorldfallBridge.InsideHouse != null;
                    Check("room-view-guest", inside && h != null && WorldfallBridge.RoomHas(h.actor), "guest inside=" + inside + ", host in guest's room=" + (h != null && WorldfallBridge.RoomHas(h.actor)));
                    float tagAge = h != null ? Time.unscaledTime - h.tagAt : 999f;
                    Check("room-nameplate-guest", tagAge < 1f, "host's tag in the room drawn " + tagAge.ToString("0.0", CultureInfo.InvariantCulture) + " s ago");
                    Shot("room-guest");
                    return true;
                }
                case "leavehouse": WorldfallBridge.TestCall("LeaveRoomForTest"); return true;
                case "killking":
                {
                    long.TryParse((string)p["aid"] ?? "0", out long aid);
                    Actor k = WorldBoxApi.FindActor(aid);
                    if (k == null || me == null) { Check("king-kill-guest", false, "guest has no king #" + aid + " or isn't possessing"); return true; }
                    R.Method(typeof(Actor), "getHit", typeof(float), typeof(bool), typeof(AttackType), typeof(BaseSimObject), typeof(bool), typeof(bool), typeof(bool))
                     ?.Invoke(k, new object[] { k.getHealth() + 100f, true, AttackType.Weapon, me, false, false, false });
                    Log.Info("TEST guest kills king #" + aid);
                    return true;
                }
                case "resync":
                    Log.Info("TEST guest re-syncs while possessing #" + (me != null ? me.getID() : 0));
                    _s.Resync();
                    return true;
                case "die":
                    if (me == null) { Check("guest-death", false, "guest isn't possessing"); return true; }
                    combat.ApplyHit(me, me.getHealth() + 100, AttackType.Weapon, null);
                    Check("guest-death-local", !me.isAlive() || !me.hasHealth(), "guest's creature alive=" + me.isAlive() + " hp=" + me.getHealth());
                    return true;
                case "flood":
                    _gFloodAt = new Vector2((float?)p["x"] ?? 0f, (float?)p["y"] ?? 0f);
                    _gFloodUntil = Time.unscaledTime + 12f; _guestFlood = true;
                    return true;
                case "checkflood":
                {
                    int u = (int?)p["u"] ?? 0, c = (int?)p["c"] ?? 0, k = (int?)p["k"] ?? 0;
                    int mu = World.world.units.Count, mc = World.world.cities.Count, mk = World.world.kingdoms.Count;
                    Check("flood", Math.Abs(mu - u) <= Math.Max(3, u / 50) && mc == c && mk == k,
                          "guest " + mu + " creatures / " + mc + " villages / " + mk + " kingdoms, host " + u + " / " + c + " / " + k);
                    return true;
                }
                case "pop":
                    Check("population", CoopMod.Instance.Meta.LastPop == "all villages match", CoopMod.Instance.Meta.LastPop + " (recounts " + CoopMod.Instance.Meta.PopRecounts + ", fixed " + CoopMod.Instance.Meta.PopFixed + ")");
                    return true;
                case "godview":
                {
                    try { ControllableUnit.clear(false); } catch { }
                    Camera cam = WorldBoxApi.MapCamera;
                    if (cam != null)
                    {
                        cam.transform.position = new Vector3((float?)p["lx"] ?? 0f, (float?)p["ly"] ?? 0f, cam.transform.position.z);
                        cam.orthographicSize = 6f;
                    }
                    Log.Info("TEST guest in god view near the host");
                    return true;
                }
                case "checktag":
                {
                    AvatarManager.Remote h = Other();
                    float age = h != null ? Time.unscaledTime - h.tagAt : 999f;
                    Check("nameplate-godview", WorldfallBridge.GodView && age < 1f, "god view=" + WorldfallBridge.GodView + ", host's tag drawn " + age.ToString("0.0", CultureInfo.InvariantCulture) + " s ago");
                    Shot("tag-guest");
                    return true;
                }
            }
            return false;
        }

        /// <summary>Every frame in the "patched" scenario (both games).</summary>
        private float _roomWalkAt;

        private void PatchedTick(float now)
        {
            if (_s.IsHost) { PatchedFrame(now); return; }
            if (_roomWalkAt > 0f && now >= _roomWalkAt)
            {
                _roomWalkAt = 0f;
                bool ok = WorldfallBridge.RoomStep(1f, 0f, 1.2f);
                float yaw = WorldfallBridge.ViewYaw;
                if (!float.IsNaN(yaw)) WorldfallBridge.SetViewYaw(yaw + Mathf.PI);   // turn back to face the host at the door
                Log.Info("TEST guest walks into the room and turns around" + (ok ? "" : " (failed)"));
            }
            if (_guestFlood)
            {
                if (now > _gFloodUntil) { _guestFlood = false; return; }
                if (now < _gNextFlood) return;
                _gNextFlood = now + 0.25f;
                WorldTile t = World.world.GetTile((int)_gFloodAt.x + UnityEngine.Random.Range(-4, 5), (int)_gFloodAt.y + UnityEngine.Random.Range(-4, 5));
                CoopMod.Instance.Powers.UseLocal(UnityEngine.Random.value < 0.5f ? "human" : "skeleton", t);
            }
        }
    }
}
