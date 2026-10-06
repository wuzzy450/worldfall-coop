using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// God-power passthrough. Every GodPower's top-level click delegate (the one
    /// PlayerControl.clickedFinal invokes first) is wrapped once at startup: when the LOCAL player
    /// uses a power on the map, we send {power id, tile, brush}; when a remote player's power
    /// arrives, we replay the original delegate on the same tile with their brush. Powers that
    /// open windows, pick targets or possess units are left local.
    /// Also syncs game speed and pause.
    /// </summary>
    public class PowerSync
    {
        private enum Slot { PowerBrush, Power, Brush, Action }

        private class Hook
        {
            public GodPower power;
            public Slot slot;
            public PowerAction origPower;
            public PowerActionWithID origId;
        }

        private static readonly string[] LocalOnlyHandlers =
        {
            "inspect", "select", "possess", "crabzilla", "magnet", "relation", "whisper", "unity",
            "follow", "copy", "favorite", "window", "tooltip", "debug",
        };

        private readonly CoopSession _s;
        private readonly Dictionary<string, Hook> _hooks = new Dictionary<string, Hook>();
        private bool _installed;
        private bool _replaying;

        // batching of local clicks (drag-painting produces one call per tile per frame)
        private readonly List<JObject> _outbox = new List<JObject>();
        private string _lastSpeed;
        private bool _lastPaused;
        private float _ignoreSpeedUntil;

        public int Relayed, Replayed;

        public PowerSync(CoopSession s) { _s = s; }

        /// <summary>A speed change we make ourselves (e.g. re-possessing after a re-sync) is not broadcast.</summary>
        public void SuppressSpeedEcho(float seconds = 0.5f) { _ignoreSpeedUntil = Time.unscaledTime + seconds; }

        public void TryInstall()
        {
            if (_installed || AssetManager.powers == null) return;
            _installed = true;
            int wrapped = 0, skipped = 0;
            var localIds = new List<string>();
            foreach (GodPower p in AssetManager.powers.list)
            {
                try
                {
                    if (p == null || p.click_special_action != null) { skipped++; continue; }
                    Hook h = new Hook { power = p };
                    if (p.click_power_brush_action != null) { h.slot = Slot.PowerBrush; h.origPower = p.click_power_brush_action; }
                    else if (p.click_power_action != null) { h.slot = Slot.Power; h.origPower = p.click_power_action; }
                    else if (p.click_brush_action != null) { h.slot = Slot.Brush; h.origId = p.click_brush_action; }
                    else if (p.click_action != null) { h.slot = Slot.Action; h.origId = p.click_action; }
                    else { skipped++; continue; }
                    if (IsLocalOnly(p, h)) { skipped++; localIds.Add(p.id); continue; }

                    GodPower power = p;
                    switch (h.slot)
                    {
                        case Slot.PowerBrush: p.click_power_brush_action = (tile, gp) => AfterLocal(power, tile, h.origPower(tile, gp)); break;
                        case Slot.Power: p.click_power_action = (tile, gp) => AfterLocal(power, tile, h.origPower(tile, gp)); break;
                        case Slot.Brush: p.click_brush_action = (tile, id) => AfterLocal(power, tile, h.origId(tile, id)); break;
                        case Slot.Action: p.click_action = (tile, id) => AfterLocal(power, tile, h.origId(tile, id)); break;
                    }
                    _hooks[p.id] = h;
                    wrapped++;
                }
                catch (Exception e) { Log.Warn("hook power " + p?.id + ": " + e.Message); }
            }
            Log.Info("power sync: " + wrapped + " powers relayed, " + skipped + " kept local (filtered: " + string.Join(", ", localIds.ToArray()) + ")");
        }

        private static bool IsLocalOnly(GodPower p, Hook h)
        {
            string id = (p.id ?? "").ToLowerInvariant();
            Delegate d = (Delegate)h.origPower ?? h.origId;
            foreach (string bad in LocalOnlyHandlers)
            {
                if (id.Contains(bad)) return true;
                foreach (Delegate part in d.GetInvocationList())
                    if (part.Method.Name.ToLowerInvariant().Contains(bad)) return true;
            }
            return false;
        }

        /// <summary>Runs after the original power action. Relays genuine local mouse use only.</summary>
        private bool AfterLocal(GodPower p, WorldTile tile, bool result)
        {
            try
            {
                if (_replaying || tile == null || !_s.InWorld) return result;
                GodPower sel = World.world.selected_power;
                if (sel == null || sel.id != p.id) return result;            // e.g. a god finger creature acting
                if (!(Input.GetMouseButton(0) || Input.GetMouseButtonUp(0) || Input.touchCount > 0)) return result;
                _outbox.Add(new JObject
                {
                    ["p"] = p.id,
                    ["x"] = tile.pos.x,
                    ["y"] = tile.pos.y,
                    ["brush"] = Config.current_brush,
                });
            }
            catch (Exception e) { Log.Warn("relay power: " + e.Message); }
            return result;
        }

        /// <summary>Main thread, each frame: flush batched local power clicks, watch speed/pause.</summary>
        public void Tick()
        {
            if (_outbox.Count > 0)
            {
                if (_s.Online && _s.InWorld)
                {
                    if (_outbox.Count == 1) _s.Net.Send("power", _outbox[0]);
                    else _s.Net.Send("power", new JObject { ["batch"] = new JArray(_outbox.ToArray()) });
                    Relayed += _outbox.Count;
                }
                _outbox.Clear();
            }

            if (!_s.Cfg.syncSpeed || !_s.InWorld || Config.time_scale_asset == null) return;
            string speed = Config.time_scale_asset.id;
            bool paused = Config.paused;
            if (speed != _lastSpeed || paused != _lastPaused)
            {
                bool first = _lastSpeed == null;
                _lastSpeed = speed;
                _lastPaused = paused;
                if (!first && Time.unscaledTime >= _ignoreSpeedUntil)
                    _s.Net.Send("speed", new JObject { ["s"] = speed, ["paused"] = paused });
            }
        }

        public void OnPacket(string t, JObject p)
        {
            if (t == "speed")
            {
                if (!_s.Cfg.syncSpeed) return;
                try
                {
                    string s = (string)p["s"];
                    bool paused = (bool?)p["paused"] ?? false;
                    _ignoreSpeedUntil = Time.unscaledTime + 0.25f;
                    if (s != null && AssetManager.time_scales.get(s) != null && Config.time_scale_asset?.id != s) Config.setWorldSpeed(s);
                    Config.paused = paused;
                    _lastSpeed = Config.time_scale_asset?.id;
                    _lastPaused = Config.paused;
                    string who = (string)p["name"] ?? "Someone";
                    CoopMod.Instance?.UI.ShowToast(who + (paused ? " paused the world" : " set speed " + s));
                }
                catch (Exception e) { Log.Warn("speed sync: " + e.Message); }
                return;
            }

            if (p["batch"] is JArray batch)
            {
                foreach (JToken it in batch) if (it is JObject o) Replay(o, p);
            }
            else Replay(p, p);
        }

        private void Replay(JObject ev, JObject envelope)
        {
            string id = (string)ev["p"];
            if (id == null || !_hooks.TryGetValue(id, out Hook h)) { Log.Warn("remote power '" + id + "' is not relayable here - ignored"); return; }
            WorldTile tile = World.world.GetTile((int?)ev["x"] ?? -1, (int?)ev["y"] ?? -1);
            if (tile == null) { Log.Warn("remote power '" + id + "' outside the map at " + ev["x"] + "," + ev["y"]); return; }
            string myBrush = Config.current_brush;
            string brush = (string)ev["brush"];
            _replaying = true;
            try
            {
                if (!string.IsNullOrEmpty(brush) && brush != myBrush) Config.current_brush = brush;
                switch (h.slot)
                {
                    case Slot.PowerBrush: case Slot.Power: h.origPower(tile, h.power); break;
                    case Slot.Brush: case Slot.Action: h.origId(tile, h.power.id); break;
                }
                Replayed++;
                if (Replayed <= 3 || Replayed % 100 == 0)
                    Log.Info("replayed " + (string)envelope["name"] + "'s " + id + " at " + tile.pos.x + "," + tile.pos.y + " (total " + Replayed + ")");
                CoopMod.Instance?.Avatars.NotePowerUse((string)envelope["id"], tile, id);
            }
            catch (Exception e) { Log.Warn("replay " + id + ": " + e.Message); }
            finally
            {
                if (Config.current_brush != myBrush) Config.current_brush = myBrush;
                _replaying = false;
            }
        }
    }
}
