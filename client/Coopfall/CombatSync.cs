using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Damage and kills across games. Every ActorAsset's action_get_hit delegate (which the game
    /// calls after a creature took damage) is wrapped once at startup, like PowerSync does with
    /// god powers. Rules:
    /// * A possessed creature belongs to its player: only their game decides its health. When my
    ///   possessed creature hits a remote player's creature here, the damage is sent to them
    ///   ("hit"); any other damage to it here is undone (their own game simulates that fight too).
    /// * Everything else belongs to the host. When a guest's possessed creature hits something,
    ///   the guest sends it to the host ("whit") and the host applies it for real, so the kill
    ///   counts in the host's world and reaches everybody through live sync.
    /// * On guests, the host's creatures don't die from the guest's own simulation: the host
    ///   announces deaths (with cause and killer) and every guest kills the creature the same way.
    /// </summary>
    public class CombatSync
    {
        private readonly CoopSession _s;
        private bool _installed, _applying;
        public int HitsSent, HitsApplied, KillsApplied, DeathsPrevented;

        public CombatSync(CoopSession s) { _s = s; }

        public void TryInstall()
        {
            if (_installed || AssetManager.actor_library == null || AssetManager.actor_library.list == null) return;
            _installed = true;
            int n = 0;
            foreach (ActorAsset asset in AssetManager.actor_library.list)
            {
                if (asset == null) continue;
                GetHitAction orig = asset.action_get_hit;
                asset.action_get_hit = (self, by, tile) =>
                {
                    bool r = orig == null || orig(self, by, tile);
                    try { OnHit(self, by); } catch (Exception e) { Log.Warn("hit hook: " + e.Message); }
                    return r;
                };
                n++;
            }
            Log.Info("combat sync: watching hits on " + n + " creature types");
        }

        private static Actor Mine()
        {
            try { return ControllableUnit.isControllingUnit() ? ControllableUnit.getControllableUnit() : null; } catch { return null; }
        }

        private void OnHit(BaseSimObject self, BaseSimObject by)
        {
            Actor victim = self as Actor;
            if (_applying || !_s.Online || !_s.InWorld || victim == null) return;
            Actor mine = Mine();
            bool fromMe = mine != null && by != null && (by as Actor) == mine && victim != mine;
            WorldBoxMod mod = CoopMod.Instance;
            if (mod == null) return;

            AvatarManager.Remote owner = mod.Avatars.PuppetOwner(victim);
            if (owner != null)
            {
                int keep = owner.localHp > 0 ? owner.localHp : victim.getMaxHealth();
                int dmg = keep - victim.getHealth();
                SetHealth(victim, keep);                       // their game decides
                if (fromMe && dmg > 0)
                {
                    _s.Net.Send("hit", new JObject
                    {
                        ["to"] = owner.id, ["aid"] = Id(victim), ["dmg"] = dmg,
                        ["at"] = (int)LastAttackType(victim), ["by"] = Id(mine),
                    });
                    HitsSent++;
                }
                return;
            }

            if (_s.IsHost) return;                             // the host's own simulation is the real one
            if (fromMe)
            {
                _s.Net.Send("whit", new JObject
                {
                    ["vid"] = Id(victim), ["hp"] = victim.getHealth(), ["at"] = (int)LastAttackType(victim), ["by"] = Id(mine),
                });
                HitsSent++;
                return;                                        // shown dying here at once; the host confirms
            }
            if (!victim.hasHealth() && mod.Sync.IsHostUnit(victim.getID()))
            {
                SetHealth(victim, 1);                          // the host announces this creature's death
                DeathsPrevented++;
            }
        }

        // ================================================================ incoming

        public void OnPacket(string t, JObject p)
        {
            try
            {
                if (t == "hit") OnHitPacket(p);
                else if (t == "whit") OnHostHit(p);
                else if (t == "act") OnAct(p);
            }
            catch (Exception e) { Log.Warn("combat '" + t + "': " + e.Message); }
        }

        /// <summary>Someone hit my possessed creature in their game.</summary>
        private void OnHitPacket(JObject p)
        {
            if ((string)p["to"] != _s.MyId) return;
            Actor mine = Mine();
            if (mine == null || !mine.isAlive() || Id(mine) != (string)p["aid"]) return;
            Actor by = WorldBoxApi.FindActor(ParseId(p["by"]));
            float dmg = (float?)p["dmg"] ?? 0f;
            if (dmg <= 0f) return;
            ApplyHit(mine, dmg, (AttackType)((int?)p["at"] ?? (int)AttackType.Weapon), by);
            Log.Info("hit by " + (string)p["name"] + ": -" + dmg + " hp (now " + mine.getHealth() + ")");
        }

        /// <summary>Host: a guest's possessed creature hit one of the world's creatures.</summary>
        private void OnHostHit(JObject p)
        {
            if (!_s.IsHost) return;
            Actor v = WorldBoxApi.FindActor(ParseId(p["vid"]));   // "id" is the sender (added by the relay)
            if (v == null || !v.isAlive() || CoopMod.Instance.Avatars.PuppetOwner(v) != null) return;
            if (ControllableUnit.isControllingUnit(v)) return;  // my own creature arrives as "hit"
            int hp = (int?)p["hp"] ?? v.getHealth();
            int dmg = v.getHealth() - Mathf.Max(hp, 0);
            if (hp <= 0) dmg = Mathf.Max(dmg, v.getHealth());
            if (dmg <= 0) return;
            Actor by = WorldBoxApi.FindActor(ParseId(p["by"]));
            ApplyHit(v, dmg, (AttackType)((int?)p["at"] ?? (int)AttackType.Weapon), by);
            if (!v.hasHealth()) Log.Info((string)p["name"] + " killed " + v.asset?.id + " #" + v.getID());
        }

        /// <summary>Host: a guest's possessed creature attacked; buildings it hits only take damage here.</summary>
        private void OnAct(JObject p)
        {
            if (!_s.IsHost || (string)p["a"] != "attack") return;
            AvatarManager.Remote r;
            if (!CoopMod.Instance.Avatars.Remotes.TryGetValue((string)p["id"] ?? "", out r) || r.actor == null || !r.actor.isAlive()) return;
            WorldTile tile = Toolbox.getTileAt((float?)p["x"] ?? -1f, (float?)p["y"] ?? -1f);
            Building b = tile?.building;
            if (b == null || !b.isAlive() || b.isOnRemove()) return;
            if (Vector2.Distance(r.actor.current_position, tile.posV3) > 4f) return;
            float dmg = 10f;
            try
            {
                object stats = R.Get(r.actor, "stats");
                if (stats != null) dmg = Mathf.Max(1f, (float)R.Call(stats, "get_Item", new[] { typeof(string) }, "damage"));
            }
            catch { }
            try
            {
                _applying = true;
                R.Method(typeof(Building), "getHit", typeof(float), typeof(bool), typeof(AttackType), typeof(BaseSimObject), typeof(bool), typeof(bool), typeof(bool))
                 ?.Invoke(b, new object[] { dmg, true, AttackType.Weapon, r.actor, false, false, false });
            }
            finally { _applying = false; }
        }

        // ================================================================ applying

        /// <summary>Real damage through the game's own getHit (reactions, kill credit, death).</summary>
        public void ApplyHit(Actor v, float dmg, AttackType type, Actor by)
        {
            try
            {
                _applying = true;
                R.Method(typeof(Actor), "getHit", typeof(float), typeof(bool), typeof(AttackType), typeof(BaseSimObject), typeof(bool), typeof(bool), typeof(bool))
                 ?.Invoke(v, new object[] { dmg, true, type, by, false, false, false });
                HitsApplied++;
                if (v.isAlive() && !v.hasHealth()) v.checkDeath();
            }
            catch (Exception e) { Log.Warn("apply hit: " + (e.InnerException ?? e).Message); }
            finally { _applying = false; }
        }

        /// <summary>Kills a creature the way the host saw it die (cause and killer), at once.</summary>
        public void Kill(Actor a, AttackType type, Actor killer)
        {
            if (a == null || !a.isAlive()) return;
            try
            {
                _applying = true;
                if (type == AttackType.Metamorphosis) { WorldBoxApi.RemoveActor(a); return; }
                R.Set(a, "_last_attack_type", type);
                R.Set(a, "attackedBy", killer != null && killer != a ? killer : null);
                a.setHealth(0);
                a.checkDeath();
                if (a.isAlive()) a.dieSimpleNone();
                KillsApplied++;
            }
            catch (Exception e) { Log.Warn("kill: " + e.Message); }
            finally { _applying = false; }
        }

        public static AttackType LastAttackType(Actor a)
        {
            object v = R.Get(a, "_last_attack_type");
            return v is AttackType at ? at : AttackType.Other;
        }

        public static Actor LastAttacker(Actor a)
        {
            var by = R.Get(a, "attackedBy") as BaseSimObject;
            try { var actor = by as Actor; return actor != null && actor.isAlive() ? actor : null; } catch { return null; }
        }

        private void SetHealth(Actor a, int hp)
        {
            try { a.setHealth(Mathf.Max(1, hp)); } catch { }
        }

        private static string Id(Actor a) { return a.getID().ToString(CultureInfo.InvariantCulture); }

        private static long ParseId(JToken t)
        {
            if (t == null) return 0;
            long.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v);
            return v;
        }
    }
}
