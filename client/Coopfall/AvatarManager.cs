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
    ///   Worldfall's 3D rendering just work) and it is kept invincible on their side - your own
    ///   game decides if you live or die. If that unit doesn't exist on their side, a tagged
    ///   stand-in of the same species is spawned (and removed again later).
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
        private Vector2 _lastCursorSent = new Vector2(-999, -999);
        private string _lastPowerSent;
        private readonly List<string> _pendingActs = new List<string>();

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
                if (!on) { r.on = false; Release(r); return; }
                Vector2 pos = new Vector2(F(p["x"]), F(p["y"]));
                float dt = Time.unscaledTime - r.lastAvatar;
                if (r.on && dt > 0.01f && dt < 1f)
                    r.velocity = Vector2.Lerp(r.velocity, (pos - r.target) / dt, 0.5f);
                else r.velocity = Vector2.zero;
                r.on = true;
                r.lastAvatar = Time.unscaledTime;
                r.target = pos;
                r.flip = (bool?)p["flip"] ?? false;
                r.hp = (int?)p["hp"] ?? 0;
                r.mhp = (int?)p["mhp"] ?? 0;
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
                try
                {
                    switch ((string)p["a"])
                    {
                        case "attack": WorldBoxApi.Punch(a, at); break;
                        case "talk": a.spawnSlashTalk(at); break;
                        case "swear": a.spawnSlashYell(at); break;
                        case "steal": a.spawnSlashSteal(at); break;
                    }
                }
                catch (Exception e) { Log.Warn("act replay: " + e.Message); }
            }
        }

        private static float F(JToken t) { return t == null ? 0f : (float)t; }

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

        public void ReleaseAll()
        {
            foreach (Remote r in Remotes.Values) Release(r);
            Remotes.Clear();
        }

        private void Release(Remote r)
        {
            Actor a = r.actor;
            r.actor = null;
            bool wasWalking = r.walking;
            r.walking = false;
            r.velocity = Vector2.zero;
            if (a == null) return;
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
            if (now >= r.nextStatus)
            {
                WorldBoxApi.AddStatus(a, "invincible", 3f);
                r.nextStatus = now + 1f;
                // Their game decides their health: mirror it, so every copy (and the next save) agrees.
                if (!r.standin && r.hp > 0 && Mathf.Abs(a.getHealth() - r.hp) > 1)
                    try { a.setHealth(r.hp); } catch { }
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
            WorldBoxApi.SetFlip(a, r.flip);
        }

        /// <summary>Find the remote player's unit in our world, or spawn a stand-in.</summary>
        private Actor Resolve(Remote r)
        {
            Actor real = WorldBoxApi.FindActor(r.aid);
            if (real != null && real.isAlive() && real.asset != null && real.asset.id == r.asset
                && !ControllableUnit.isControllingUnit(real)
                && Vector2.Distance(real.current_position, r.target) < 12f)
            {
                r.actor = real;
                r.standin = false;
                Log.Info("puppeting " + r.name + "'s unit #" + r.aid + " (" + r.asset + ")");
                return real;
            }
            Actor s = WorldBoxApi.SpawnStandin(r.asset, r.target, r.name, r.id);
            if (s == null) return null;
            r.actor = s;
            r.standin = true;
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
        }

        private void SendMine(float now)
        {
            if (!_s.Online) return;
            Actor me = ControllableUnit.getControllableUnit();
            bool on = me != null && me.isAlive();

            if (on)
            {
                Vector2 click = ControllableUnit.getClickVector();
                foreach (string act in _pendingActs)
                    _s.Net.Send("act", new JObject { ["a"] = act, ["x"] = click.x, ["y"] = click.y });
                _pendingActs.Clear();

                if (now >= _nextSend)
                {
                    _nextSend = now + 1f / Mathf.Clamp(_s.Cfg.avatarSendHz, 2f, 30f);
                    _s.Net.Send("avatar", new JObject
                    {
                        ["on"] = true,
                        ["aid"] = me.getID().ToString(CultureInfo.InvariantCulture),
                        ["asset"] = me.asset != null ? me.asset.id : "human",
                        ["x"] = Math.Round(me.current_position.x, 3),
                        ["y"] = Math.Round(me.current_position.y, 3),
                        ["flip"] = WorldBoxApi.GetFlip(me),
                        ["hp"] = me.getHealth(),
                        ["mhp"] = me.getMaxHealth(),
                    });
                }
                _wasOn = true;
                return;
            }

            _pendingActs.Clear();
            if (_wasOn)
            {
                _wasOn = false;
                _s.Net.Send("avatar", new JObject { ["on"] = false });
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
