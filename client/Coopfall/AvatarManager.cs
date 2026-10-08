using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Remote players in your world.
    ///
    /// * While you POSSESS a unit (vanilla possession / Worldfall's first-person mode), your unit's
    ///   id, species, position, facing and actions are broadcast. Other players drive the SAME unit
    ///   (same id - everyone loaded the same snapshot) as a puppet: its AI is cancelled every frame,
    ///   it walks to your position with the game's own movement code (so walk animations and
    ///   Worldfall's 3D rendering just work). Your own game decides if you live or die: damage
    ///   it takes on their side is undone, except hits from their possessed creature, which
    ///   CombatSync sends to you. When it dies in your game, it dies in theirs too (same cause,
    ///   same killer). If that unit doesn't exist on their side, a tagged stand-in of the same
    ///   species is spawned (and removed again later).
    /// * While you are NOT possessing, your god cursor (+ selected power) is broadcast instead.
    /// </summary>
    public class AvatarManager
    {
        public class Remote
        {
            public string id, name, color = "#ffffff";
            public Color col = Color.white;
            // possession avatar
            public bool on;
            public long aid;
            public string asset = "human";
            public Vector2 target;
            public bool flip;
            public int hp, mhp;
            public Vector2? roomPos; // where they stand inside Worldfall's house room
            public string weapon;  // weapon asset in their hand ("": none, null: not told)
            public long bld;      // building they are inside (0: outside)
            public int localHp;   // health we hold the puppet at (CombatSync undoes other damage)
            public float yaw = float.NaN;   // where their first-person view looks (NaN: not in first person)
            public bool lookSet;
            public float tagAt = -999f;     // last time CoopUI drew their name tag
            public float lastAvatar;
            public Actor actor;
            public bool standin;
            public float nextStatus, nextResolve;
            public Vector2 velocity;
            public bool walking;
            // god cursor
            public bool hasCursor;
            public Vector2 cursor, cursorShown;
            public string power;
            public float lastCursor;
            // flair
            public string bubble;
            public float bubbleUntil;
            public float powerFlashUntil;
            public Vector2 powerFlashAt;
        }

        public readonly Dictionary<string, Remote> Remotes = new Dictionary<string, Remote>();
        private readonly CoopSession _s;

        private float _nextSend, _nextCursor;
        private bool _wasOn;
        private Actor _mine;
        public Actor Mine => _mine;
        private long _mineId;

        /// <summary>If the creature I possessed died, everybody's copy dies the same way (cause, killer).</summary>
        private bool SendDeathIfDead()
        {
            Actor last = _mine;
            if (last == null) return false;
            bool same = false;
            try { same = last.getData() != null && last.getID() == _mineId; } catch { }
            if (same && last.isAlive()) return false;
            var off = new JObject { ["on"] = false, ["dead"] = true, ["aid"] = _mineId.ToString(CultureInfo.InvariantCulture) };
            if (same)
            {
                Actor killer = CombatSync.LastAttacker(last);
                off["at"] = (int)CombatSync.LastAttackType(last);
                if (killer != null) off["by"] = killer.getID().ToString(CultureInfo.InvariantCulture);
            }
            _s.Net.Send("avatar", off);
            Log.Info("my creature #" + _mineId + " died");
            _mine = null;
            return true;
        }
        private Vector2 _lastCursorSent = new Vector2(-999, -999);
        private string _lastPowerSent;
        private readonly List<string> _pendingActs = new List<string>();

        /// <summary>Actions replayed from other players, by kind (scripted tests check these).</summary>
        public readonly Dictionary<string, int> ActsSeen = new Dictionary<string, int>();

        /// <summary>Scripted tests: relay an action as if my creature did it (jump, work, ...).</summary>
        public void SendAct(string act, JObject extra = null)
        {
            var m = extra ?? new JObject();
            m["a"] = act;
            if (m["x"] == null) { m["x"] = 0; m["y"] = 0; }
            _s.Net.Send("act", m);
        }

        public AvatarManager(CoopSession s)
        {
            _s = s;
            _s.ChatFrom += (id, text) =>
            {
                if (id == null || !Remotes.TryGetValue(id, out Remote r)) return;
                r.bubble = text.Length > 60 ? text.Substring(0, 57) + "..." : text;
                r.bubbleUntil = Time.unscaledTime + 7f;
            };
        }

        /// <summary>True if this unit is being driven as a remote player's avatar (live sync leaves it alone).</summary>
        public bool IsPuppet(Actor a)
        {
            if (a == null) return false;
            foreach (Remote r in Remotes.Values) if (r.on && r.actor == a) return true;
            return false;
        }

        /// <summary>The remote player whose possessed creature this unit is, or null.</summary>
        public Remote PuppetOwner(Actor a)
        {
            if (a == null) return null;
            foreach (Remote r in Remotes.Values) if (r.on && r.actor == a) return r;
            return null;
        }

        public Remote Get(string id)
        {
            if (id == null) return null;
            if (!Remotes.TryGetValue(id, out Remote r))
            {
                r = new Remote { id = id };
                Remotes[id] = r;
            }
            return r;
        }

        // ================================================================ incoming

        public void OnPacket(string t, JObject p)
        {
            string id = (string)p["id"];
            if (id == null || id == _s.MyId) return;
            Remote r = Get(id);
            r.name = (string)p["name"] ?? r.name ?? "?";
            string color = (string)p["color"];
            if (color != null && color != r.color) { r.color = color; r.col = CoopConfig.ParseColor(color); }

            if (t == "avatar")
            {
                bool on = (bool?)p["on"] ?? false;
                if (!on)
                {
                    if ((bool?)p["dead"] ?? false) KillPuppet(r, p);
                    r.on = false;
                    Release(r);
                    return;
                }
                Vector2 pos = new Vector2(F(p["x"]), F(p["y"]));
                float dt = Time.unscaledTime - r.lastAvatar;
                if (r.on && dt > 0.01f && dt < 1f)
                    r.velocity = Vector2.Lerp(r.velocity, (pos - r.target) / dt, 0.5f);
                else r.velocity = Vector2.zero;
                r.on = true;
                r.lastAvatar = Time.unscaledTime;
                r.target = pos;
                r.flip = (bool?)p["flip"] ?? false;
                r.yaw = p["yaw"] != null ? (float)p["yaw"] : float.NaN;
                r.hp = (int?)p["hp"] ?? 0;
                r.mhp = (int?)p["mhp"] ?? 0;
                r.weapon = (string)p["wpn"];
                r.roomPos = p["rx"] != null ? new Vector2(F(p["rx"]), F(p["ry"])) : (Vector2?)null;
                long.TryParse((string)p["bld"] ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out r.bld);
                long aid = 0;
                long.TryParse((string)p["aid"] ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out aid);
                string asset = (string)p["asset"] ?? "human";
                if (aid != r.aid || asset != r.asset) { Release(r); r.aid = aid; r.asset = asset; }
                r.hasCursor = false;
            }
            else if (t == "cursor")
            {
                r.cursor = new Vector2(F(p["x"]), F(p["y"]));
                if (!r.hasCursor) r.cursorShown = r.cursor;
                r.hasCursor = true;
                r.power = (string)p["p"];
                r.lastCursor = Time.unscaledTime;
            }
            else if (t == "act")
            {
                Actor a = r.actor;
                if (a == null || !a.isAlive()) return;
                Vector2 at = new Vector2(F(p["x"]), F(p["y"]));
                string act = (string)p["a"] ?? "?";
                ActsSeen.TryGetValue(act, out int seen); ActsSeen[act] = seen + 1;
                try
                {
                    switch ((string)p["a"])
                    {
                        case "attack": WorldBoxApi.Punch(a, at); break;
                        case "talk": a.spawnSlashTalk(at); break;
                        case "swear": a.spawnSlashYell(at); break;
                        case "steal": a.spawnSlashSteal(at); break;
                        case "jump": WorldBoxApi.AddStatus(a, "jump", 0f); break;   // the game's own hop (Worldfall lifts it in 3D)
                        case "ability": WorldfallBridge.UseAbility(a, (string)p["ab"], at); break;   // their species ability (X)
                        case "work":                                   // they felled a tree / mined a rock / gathered in Worldfall
                            long bid;
                            long.TryParse((string)p["bid"] ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out bid);
                            Building b = World.world.buildings.get(bid);
                            if (b != null && b.isAlive() && !b.isOnRemove() && !WorldBoxApi.Chopped(b))
                                R.Call(b, "extractResources", new[] { typeof(Actor) }, a);
                            break;
                    }
                }
                catch (Exception e) { Log.Warn("act replay: " + e.Message); }
            }
        }

        private static float F(JToken t) { return t == null ? 0f : (float)t; }

        /// <summary>Their possessed creature died in their game: it dies here too, the same way.</summary>
        private void KillPuppet(Remote r, JObject p)
        {
            Actor a = r.actor;
            if (a == null || !a.isAlive()) return;
            if (r.standin) return;   // Release removes stand-ins
            long by = 0;
            long.TryParse((string)p["by"] ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out by);
            var type = (AttackType)((int?)p["at"] ?? (int)AttackType.Other);
            try { if (r.walking) a.setPossessedMovement(false); } catch { }
            r.walking = false;
            r.actor = null;
            CoopMod.Instance?.Combat.Kill(a, type, WorldBoxApi.FindActor(by));
            Log.Info(r.name + "'s " + a.asset?.id + " #" + a.getID() + " died (" + type + ")");
        }

        public void NotePowerUse(string playerId, WorldTile tile, string powerId)
        {
            if (playerId == null || !Remotes.TryGetValue(playerId, out Remote r)) return;
            r.powerFlashAt = new Vector2(tile.posV3.x, tile.posV3.y);
            r.powerFlashUntil = Time.unscaledTime + 0.6f;
            r.power = powerId;
        }

        /// <summary>Called when the player list changes: forget players that left our room.</summary>
        public void SyncWithPlayers()
        {
            List<string> gone = null;
            foreach (var kv in Remotes)
            {
                PlayerInfo pi = _s.Player(kv.Key);
                if (pi == null || pi.room != _s.RoomId || _s.RoomId == null)
                    (gone ?? (gone = new List<string>())).Add(kv.Key);
                else
                {
                    kv.Value.name = pi.name;
                    if (kv.Value.color != pi.color) { kv.Value.color = pi.color; kv.Value.col = CoopConfig.ParseColor(pi.color); }
                }
            }
            if (gone != null)
                foreach (string id in gone) { Release(Remotes[id]); Remotes.Remove(id); }
        }

        /// <summary>
        /// A world reload replaces my creature with the host's copy (same id, new object): stop
        /// tracking the old one without reporting it dead, or the others kill my creature.
        /// </summary>
        public void ForgetMine()
        {
            if (_wasOn) _s.Net.Send("avatar", new JObject { ["on"] = false });
            _wasOn = false;
            _mine = null;
            _pendingActs.Clear();
        }

        public void ReleaseAll()
        {
            foreach (Remote r in Remotes.Values) Release(r);
            Remotes.Clear();
        }

        private void Release(Remote r)
        {
            Actor a = r.actor;
            if (a != null && WorldfallBridge.Present) WorldfallBridge.RemoveFromRoom(a);
            r.actor = null;
            bool wasWalking = r.walking;
            r.walking = false;
            r.velocity = Vector2.zero;
            if (a == null) return;
            if (r.lookSet) { WorldfallBridge.ClearLookAt(a); r.lookSet = false; }
            try
            {
                if (!a.isAlive()) return;
                Log.Info("released " + r.name + "'s avatar" + (r.standin ? " (stand-in removed)" : ""));
                if (wasWalking) a.setPossessedMovement(false);
                if (r.standin) WorldBoxApi.RemoveActor(a);
                else
                {
                    a.finishStatusEffect("invincible");
                    a.stopMovement();
                }
            }
            catch (Exception e) { Log.Warn("release avatar: " + e.Message); }
            r.standin = false;
        }

        // ================================================================ per-frame (LateUpdate: after the sim)

        public void LateTick()
        {
            if (!_s.InWorld || !WorldBoxApi.WorldReady) return;
            float now = Time.unscaledTime;
            SendMine(now);

            foreach (Remote r in Remotes.Values)
            {
                if (r.hasCursor) r.cursorShown = Vector2.Lerp(r.cursorShown, r.cursor, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
                if (r.hasCursor && now - r.lastCursor > 10f) r.hasCursor = false;
                if (!r.on) continue;
                if (now - r.lastAvatar > 4f) { r.on = false; Release(r); continue; }
                try { Drive(r, now); }
                catch (Exception e) { Log.Warn("drive avatar " + r.name + ": " + e.Message); Release(r); }
            }
        }

        private void Drive(Remote r, float now)
        {
            Actor a = r.actor;
            if (a != null && !a.isAlive()) { r.actor = null; a = null; }
            if (a != null && r.standin && now >= r.nextResolve)
            {
                // Live sync may have brought their real unit into our world since: swap the stand-in for it.
                r.nextResolve = now + 1f;
                Actor real = WorldBoxApi.FindActor(r.aid);
                if (real != null && real.isAlive() && real.asset != null && real.asset.id == r.asset && !ControllableUnit.isControllingUnit(real))
                {
                    Release(r);
                    a = null;
                }
            }
            if (a == null)
            {
                a = Resolve(r);
                if (a == null) return;
            }

            a.cancelAllBeh();
            WorldSync.SyncInside(a, r.bld);   // inside the same building as in their game
            SyncWeapon(a, r.weapon);
            if (WorldfallBridge.Present)
            {
                // Same house as me in Worldfall's room view: stand where they stand in theirs.
                Building myHouse = WorldfallBridge.InsideHouse;
                if (myHouse != null && r.bld != 0 && myHouse.getID() == r.bld && r.roomPos.HasValue)
                    WorldfallBridge.ShowInRoom(a, r.roomPos.Value, r.yaw);
                else if (myHouse != null) WorldfallBridge.RemoveFromRoom(a);
            }
            if (now >= r.nextStatus)
            {
                r.nextStatus = now + 0.5f;
                // Their game decides their health: mirror it, so every copy (and the next save) agrees.
                int want = r.hp > 0 ? Mathf.Min(r.hp, a.getMaxHealth()) : a.getHealth();
                r.localHp = Mathf.Max(1, want);
                if (a.getHealth() != r.localHp) try { a.setHealth(r.localHp); } catch { }
            }

            // We place the unit ourselves every frame (smoothly), instead of letting the game's
            // pathing chase the target: the remote player may move faster than this unit's walk
            // speed, and the local AI keeps interrupting paths. SetPosition also updates
            // current_tile, which the game only re-derives by itself while following a path.
            // The "possessed movement" flag is what makes is_moving true for a possessed unit,
            // so the walk animation plays (also in Worldfall's 3D view).
            if (a.is_moving && !r.walking) a.stopMovement();   // drop any path the AI started
            Vector2 cur = a.current_position;
            Vector2 goal = r.target + r.velocity * Mathf.Min(now - r.lastAvatar, 0.25f); // tiny extrapolation
            float d = Vector2.Distance(cur, goal);
            Vector2 next;
            if (d > 10f) next = goal;                                              // far behind: snap
            else next = Vector2.Lerp(cur, goal, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            if (Toolbox.getTileAt(next.x, next.y) != null) WorldBoxApi.SetPosition(a, next);
            bool walking = r.velocity.sqrMagnitude > 0.04f || d > 0.15f;
            if (walking != r.walking)
            {
                r.walking = walking;
                a.setPossessedMovement(walking);
            }
            if (walking)
            {
                // Worldfall turns a walking body towards next_step_position.
                Vector2 dir = r.velocity.sqrMagnitude > 0.01f ? r.velocity.normalized : (goal - cur).normalized;
                if (dir.sqrMagnitude > 0.5f) a.next_step_position = a.current_position + dir;
            }
            if (!float.IsNaN(r.yaw))
            {
                // They look around in first person: face the same way here (2D sprite and Worldfall's 3D body).
                var dir = new Vector2(Mathf.Cos(r.yaw), Mathf.Sin(r.yaw));
                WorldBoxApi.SetFlip(a, dir.x > 0f);
                // Their own Worldfall body follows the view, walking or not (BeastView.BodyYaw).
                WorldfallBridge.SetLookAt(a, a.current_position + dir * 3f);
                r.lookSet = true;
            }
            else
            {
                WorldBoxApi.SetFlip(a, r.flip);
                if (r.lookSet) { WorldfallBridge.ClearLookAt(a); r.lookSet = false; }
            }
        }

        /// <summary>Puts the weapon they hold in their game into their creature's hand here.</summary>
        private static void SyncWeapon(Actor a, string want)
        {
            if (want == null || a.equipment == null) return;
            try
            {
                ActorEquipmentSlot slot = a.equipment.weapon;
                Item it = slot.getItem();
                string have = it != null && !it.shouldbe_removed ? it.getAsset()?.id : null;
                if ((have ?? "") == want) return;
                if (it != null) WorldSync.Unequip(slot);
                if (want.Length > 0)
                {
                    EquipmentAsset ea = AssetManager.items.get(want);
                    if (ea != null) a.equipment.setItem(World.world.items.generateItem(ea, null, null, 1, a), a);
                }
                a.setStatsDirty();
            }
            catch (Exception e) { Log.Warn("weapon sync: " + e.Message); }
        }

        /// <summary>Find the remote player's unit in our world, or spawn a stand-in.</summary>
        private Actor Resolve(Remote r)
        {
            Actor real = WorldBoxApi.FindActor(r.aid);
            if (real != null && real.isAlive() && real.asset != null && real.asset.id == r.asset
                && !ControllableUnit.isControllingUnit(real)
                && (Vector2.Distance(real.current_position, r.target) < 12f || WorldBoxApi.InsideBuilding(real) != null || r.standin))
            {
                // Same id and species is their creature, even if it's somewhere else here (e.g. still
                // inside a house it entered, or after a re-sync): it walks to them. A stricter check
                // here than in Drive's stand-in swap made a release/stand-in loop every second.
                if (WorldBoxApi.InsideBuilding(real) != null && r.bld == 0) WorldBoxApi.ExitBuilding(real);
                r.actor = real;
                r.standin = false;
                r.localHp = Mathf.Max(1, r.hp > 0 ? r.hp : real.getHealth());
                r.nextStatus = 0f;
                Log.Info("puppeting " + r.name + "'s unit #" + r.aid + " (" + r.asset + ")");
                return real;
            }
            Actor s = WorldBoxApi.SpawnStandin(r.asset, r.target, r.name, r.id);
            if (s == null) return null;
            r.actor = s;
            r.standin = true;
            r.localHp = s.getHealth();
            r.nextStatus = 0f;
            Log.Info("spawned stand-in for " + r.name + " (" + r.asset + ")");
            return s;
        }

        // ================================================================ outgoing

        /// <summary>Called from LateUpdate after the game processed possession input this frame.</summary>
        public void CaptureActions()
        {
            if (!ControllableUnit.isControllingUnit()) return;
            if (ControllableUnit.isAttackJustPressedLeft() || ControllableUnit.isAttackJustPressedRight()) _pendingActs.Add("attack");
            if (ControllableUnit.isActionPressedTalk()) _pendingActs.Add("talk");
            if (ControllableUnit.isActionPressedSwear()) _pendingActs.Add("swear");
            if (ControllableUnit.isActionPressedSteal()) _pendingActs.Add("steal");
            if (ControllableUnit.isActionPressedJump()) _pendingActs.Add("jump");
            if (WorldfallBridge.Present && WorldfallBridge.PollWorkDone(out Building worked))
                _s.Net.Send("act", new JObject { ["a"] = "work", ["bid"] = worked.getID().ToString(CultureInfo.InvariantCulture),
                    ["x"] = worked.current_position.x, ["y"] = worked.current_position.y });
            if (WorldfallBridge.Present && WorldfallBridge.PollMyAbility(out string ab, out Vector2 abAt))
                _s.Net.Send("act", new JObject { ["a"] = "ability", ["ab"] = ab, ["x"] = abAt.x, ["y"] = abAt.y });
        }

        private void SendMine(float now)
        {
            if (!_s.Online) return;
            Actor me = ControllableUnit.getControllableUnit();
            bool on = me != null && me.isAlive();
            if (on)
            {
                // Worldfall can move you straight into another creature (e.g. your heir) when yours dies.
                if (_mine != null && _mine != me) SendDeathIfDead();
                _mine = me;
                _mineId = me.getID();
            }

            if (on)
            {
                Vector2 click = ControllableUnit.getClickVector();
                foreach (string act in _pendingActs)
                    _s.Net.Send("act", new JObject { ["a"] = act, ["x"] = click.x, ["y"] = click.y });
                _pendingActs.Clear();

                if (now >= _nextSend)
                {
                    _nextSend = now + 1f / Mathf.Clamp(_s.Cfg.avatarSendHz, 2f, 30f);
                    float yaw = WorldfallBridge.FirstPerson ? WorldfallBridge.ViewYaw : float.NaN;
                    var msg = new JObject
                    {
                        ["on"] = true,
                        ["aid"] = me.getID().ToString(CultureInfo.InvariantCulture),
                        ["asset"] = me.asset != null ? me.asset.id : "human",
                        ["x"] = Math.Round(me.current_position.x, 3),
                        ["y"] = Math.Round(me.current_position.y, 3),
                        ["flip"] = WorldBoxApi.GetFlip(me),
                        ["hp"] = me.getHealth(),
                        ["mhp"] = me.getMaxHealth(),
                    };
                    if (!float.IsNaN(yaw)) msg["yaw"] = Math.Round(yaw, 3);
                    Building house = WorldBoxApi.InsideBuilding(me) ?? (WorldfallBridge.Present ? WorldfallBridge.InsideHouse : null);
                    if (house != null) msg["bld"] = house.getID().ToString(CultureInfo.InvariantCulture);
                    if (house != null && WorldfallBridge.Present && WorldfallBridge.RoomEye(out Vector2 eye)) { msg["rx"] = Math.Round(eye.x, 3); msg["ry"] = Math.Round(eye.y, 3); }
                    msg["wpn"] = WorldBoxApi.WeaponId(me) ?? "";
                    _s.Net.Send("avatar", msg);
                }
                _wasOn = true;
                return;
            }

            _pendingActs.Clear();
            if (_wasOn)
            {
                _wasOn = false;
                if (!SendDeathIfDead()) _s.Net.Send("avatar", new JObject { ["on"] = false });
                _mine = null;
            }

            if (now >= _nextCursor && _s.Cfg.showCursors)
            {
                _nextCursor = now + 0.1f;
                Vector2 m = World.world.getMousePos();
                string power = World.world.selected_power?.id;
                if ((m - _lastCursorSent).sqrMagnitude > 0.04f || power != _lastPowerSent)
                {
                    _lastCursorSent = m;
                    _lastPowerSent = power;
                    var msg = new JObject { ["x"] = Math.Round(m.x, 2), ["y"] = Math.Round(m.y, 2) };
                    if (power != null) msg["p"] = power;
                    _s.Net.Send("cursor", msg);
                }
            }
        }
    }
}
