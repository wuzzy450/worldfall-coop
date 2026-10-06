using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Live terrain sync: ground and top tile types (roads, farm fields, lava, water, ice, ...),
    /// fires and burn marks. The map is split into WorldBox's 8x8-tile zones; the host
    /// fingerprints each zone and sends the tiles of zones that changed ("wt"), plus every 5 s
    /// the full fingerprint list, which guests compare with their own map to ask ("wask") for
    /// zones that differ.
    /// </summary>
    public class TileSync
    {
        private const float Every = 0.5f, FullEvery = 5f;
        private const int ZonesPerMessage = 300;

        private readonly CoopSession _s;
        private int[] _sent;
        private bool _primed;
        private float _next, _nextFull;
        private readonly HashSet<int> _forced = new HashSet<int>();
        private readonly Dictionary<int, float> _askedAt = new Dictionary<int, float>();
        private readonly Dictionary<object, uint> _typeHash = new Dictionary<object, uint>();
        public int ZonesApplied, TilesChanged;
        private int _logged;

        public TileSync(CoopSession s) { _s = s; }

        private bool Ready { get { WorldSync w = CoopMod.Instance?.Sync; return w != null && !w.Disabled && _s.Cfg.liveSync && _s.Cfg.syncTerrain && _s.Online && _s.InWorld && WorldBoxApi.WorldReady; } }

        public void Reset()
        {
            _sent = null; _primed = false; _next = _nextFull = 0f; _ordered = null;
            _forced.Clear(); _askedAt.Clear(); _hostHash = null;
        }

        public void ForceFull() { _nextFull = Mathf.Min(_nextFull, Time.unscaledTime + 2f); }

        private List<TileZone> _zonesSrc;
        private int _zonesCount;
        private List<TileZone> _ordered;

        /// <summary>
        /// All zones sorted by map position. The game's own zone list isn't in the same order in
        /// every game, so a zone's index here (its id on the wire) is the same for everybody.
        /// </summary>
        private List<TileZone> Zones()
        {
            List<TileZone> src = R.Zones?.zones;
            if (src == null) return null;
            if (src != _zonesSrc || src.Count != _zonesCount || _ordered == null)
            {
                _zonesSrc = src;
                _zonesCount = src.Count;
                _ordered = new List<TileZone>(src);
                _ordered.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            }
            return _ordered;
        }

        // ---------------------------------------------------------------- fingerprints

        private uint TypeHash(object t, string id)
        {
            if (t == null) return 1;
            if (!_typeHash.TryGetValue(t, out uint h)) _typeHash[t] = h = MetaSync.Fnv(id ?? "");
            return h;
        }

        private int ZoneHash(TileZone z)
        {
            uint h = 2166136261;
            foreach (WorldTile t in z.tiles)
            {
                if (t == null) continue;
                h = (h ^ TypeHash(t.main_type, t.main_type?.id)) * 16777619;
                h = (h ^ TypeHash(t.top_type, t.top_type?.id)) * 16777619;
                h = (h ^ (uint)((t.isOnFire() ? 1 : 0) | (t.burned_stages << 1))) * 16777619;
            }
            return (int)h;
        }

        // ---------------------------------------------------------------- host

        public void Tick()
        {
            QuakeMode(Ready && !_s.IsHost);
            WorldMode(Ready && !_s.IsHost);
            if (Ready && !_s.IsHost)
            {
                GuestTick();
                return;
            }
            if (!Ready) return;
            if (_s.OthersInRoom() == 0) { if (_primed) Reset(); return; }
            float now = Time.unscaledTime;
            if (now < _next || _s.Net.BulkBytesQueued > 512 * 1024) return;
            _next = now + Every;
            List<TileZone> zones = Zones();
            if (zones == null || zones.Count == 0) return;
            bool full = now >= _nextFull || !_primed;
            if (full) _nextFull = now + FullEvery;
            if (_sent == null || _sent.Length != zones.Count) { _sent = new int[zones.Count]; _primed = false; }

            var changed = new List<int>();
            var hashes = full ? new StringBuilder(zones.Count * 12) : null;
            for (int i = 0; i < zones.Count; i++)
            {
                int h = ZoneHash(zones[i]);
                if (hashes != null) { if (i > 0) hashes.Append(','); hashes.Append(h); }
                if ((_primed && h != _sent[i]) || _forced.Contains(i)) changed.Add(i);
                _sent[i] = h;
            }
            _forced.Clear();
            _primed = true;
            for (int i = 0; i < changed.Count; i += ZonesPerMessage)
                SendZones(zones, changed.GetRange(i, Mathf.Min(ZonesPerMessage, changed.Count - i)));
            if (hashes != null)
            {
                var sb = Begin();
                sb.Append(",\"n\":").Append(zones.Count).Append(",\"zh\":[").Append(hashes).Append("]}");
                _s.Net.SendRaw(sb.ToString(), true);
            }
        }

        private void SendZones(List<TileZone> zones, List<int> ids)
        {
            var keys = new List<string>();
            var index = new Dictionary<string, int>();
            var z = new StringBuilder();
            foreach (int id in ids)
            {
                if (z.Length > 0) z.Append(',');
                z.Append('[').Append(id);
                foreach (WorldTile t in zones[id].tiles)
                {
                    string key = t == null ? "" : (t.main_type?.id ?? "") + "|" + (t.top_type?.id ?? "") + "|" + (t.isOnFire() ? 1 : 0) + "|" + t.burned_stages;
                    if (!index.TryGetValue(key, out int k)) { k = keys.Count; keys.Add(key); index[key] = k; }
                    z.Append(',').Append(k);
                }
                z.Append(']');
            }
            var sb = Begin();
            sb.Append(",\"k\":").Append(JsonConvert.SerializeObject(keys)).Append(",\"z\":[").Append(z).Append("]}");
            _s.Net.SendRaw(sb.ToString(), true);
        }

        private StringBuilder Begin()
        {
            var sb = new StringBuilder(8192);
            sb.Append("{\"t\":\"wt\",\"room\":").Append(JsonConvert.ToString(_s.RoomId));
            return sb;
        }

        /// <summary>Host: a guest asks for zones ("z" in "wask").</summary>
        public void OnAsk(JObject p)
        {
            if (!_s.IsHost || !(p["z"] is JArray z)) return;
            foreach (JToken t in z) _forced.Add((int)t);
            _next = 0f;
        }

        // ---------------------------------------------------------------- guest

        // Earthquakes pick "raise" or "lower" and their tile order at random, separately in every
        // game. On guests every quake print points off the map: the ground still shakes, but the
        // tiles only change as the host's quake changes them (streamed like any terrain change).
        private static readonly Dictionary<object, Array> _quakeSteps = new Dictionary<object, Array>();
        private static bool _quakeMuted;

        // WorldBox's own random world changes. Each game rolls its own dice, so on guests they're
        // off: the host's results arrive through the terrain, building and creature sync.
        private static readonly string[] HostOnlyBehaviours =
        {
            "disasters", "migrants", "decay_roads", "decay_farms", "spawn_random_vegetation", "spawn_minerals",
            "unit_spawn", "ruins_rat_spawn", "creep_decay", "ocean", "tiles_freeze", "tiles_unfreeze", "biomes",
            "lava", "fire", "burned_tiles", "erosion", "biome_inferno_fires", "creep_biomass",
        };
        private static readonly Dictionary<WorldBehaviourAsset, WorldBehaviourAction> _behaviours = new Dictionary<WorldBehaviourAsset, WorldBehaviourAction>();
        private static bool _worldMuted;

        private static void WorldMode(bool mute)
        {
            if (mute == _worldMuted || AssetManager.world_behaviours == null) return;
            _worldMuted = mute;
            try
            {
                foreach (string id in HostOnlyBehaviours)
                {
                    WorldBehaviourAsset a = AssetManager.world_behaviours.get(id);
                    if (a == null) continue;
                    if (mute) { if (!_behaviours.ContainsKey(a)) _behaviours[a] = a.action; a.action = () => { }; }
                    else if (_behaviours.TryGetValue(a, out WorldBehaviourAction orig)) a.action = orig;
                }
                if (!mute) _behaviours.Clear();
                Log.Info("tile sync: world changes " + (mute ? "left to the host" : "simulated here again"));
            }
            catch (Exception e) { Log.Warn("tile sync: world mode: " + e.Message); }
        }

        private static void QuakeMode(bool mute)
        {
            if (mute == _quakeMuted) return;
            try
            {
                var quakes = PrintLibrary.getQuakes();
                if (quakes == null) return;
                foreach (object pt in quakes)
                {
                    var f = pt.GetType().GetField("steps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (f == null) continue;
                    if (mute)
                    {
                        var steps = (PrintStep[])f.GetValue(pt);
                        if (steps == null) continue;
                        _quakeSteps[pt] = steps;
                        var off = new PrintStep[steps.Length];
                        for (int i = 0; i < off.Length; i++) off[i] = new PrintStep { x = 1000000, y = 1000000, action = steps[i].action };
                        f.SetValue(pt, off);
                    }
                    else if (_quakeSteps.TryGetValue(pt, out Array orig)) f.SetValue(pt, orig);
                }
                if (!mute) _quakeSteps.Clear();
                _quakeMuted = mute;
            }
            catch (Exception e) { Log.Warn("tile sync: quake mode: " + e.Message); _quakeMuted = mute; }
        }

        public void OnPacket(JObject p)
        {
            if (!Ready || _s.IsHost) return;
            try
            {
                if (p["z"] is JArray zs && p["k"] is JArray keys) ApplyZones(zs, keys);
                if (p["zh"] is JArray zh) Compare(zh, (int?)p["n"] ?? -1);
            }
            catch (Exception e) { Log.Warn("tile sync: " + e); }
        }

        // The host's fingerprint of every zone as far as we know (from its full lists and the zones it sent).
        private int[] _hostHash;
        private float _nextLocal;

        /// <summary>
        /// Guest, every second: zones our own simulation changed (grass spreading, fire, ...) differ
        /// from the host's last fingerprint: ask for them now instead of waiting for the next full list.
        /// </summary>
        private void GuestTick()
        {
            float now = Time.unscaledTime;
            if (now < _nextLocal || _hostHash == null) return;
            _nextLocal = now + Every;
            List<TileZone> zones = Zones();
            if (zones == null || zones.Count != _hostHash.Length) return;
            var ask = new List<int>();
            for (int i = 0; i < zones.Count && ask.Count < 200; i++)
            {
                if (ZoneHash(zones[i]) == _hostHash[i]) continue;
                if (_askedAt.TryGetValue(i, out float at) && now - at < 2f) continue;
                _askedAt[i] = now;
                ask.Add(i);
            }
            Ask(ask);
        }

        private void ApplyZones(JArray zs, JArray keys)
        {
            List<TileZone> zones = Zones();
            if (zones == null) return;
            var parsed = new string[keys.Count][];
            for (int i = 0; i < keys.Count; i++) parsed[i] = ((string)keys[i]).Split('|');
            int tiles = 0;
            foreach (JToken zt in zs)
            {
                if (!(zt is JArray za) || za.Count < 2) continue;
                int id = (int)za[0];
                if (id < 0 || id >= zones.Count) continue;
                WorldTile[] ts = zones[id].tiles;
                for (int i = 0; i < ts.Length && i + 1 < za.Count; i++)
                {
                    WorldTile t = ts[i];
                    string[] k = parsed[(int)za[i + 1]];
                    if (t == null || k.Length < 4) continue;
                    if (ApplyTile(t, k, _logged < 30)) { tiles++; _logged++; }
                }
                if (_hostHash != null && id < _hostHash.Length) _hostHash[id] = ZoneHash(zones[id]);
                ZonesApplied++;
            }
            TilesChanged += tiles;
        }

        private static bool ApplyTile(WorldTile t, string[] k, bool log)
        {
            if (log)
            {
                string mine = (t.main_type?.id ?? "") + "|" + (t.top_type?.id ?? "") + "|" + (t.isOnFire() ? 1 : 0) + "|" + t.burned_stages;
                string host = string.Join("|", k);
                if (mine != host) Log.Info("tile " + t.pos.x + "," + t.pos.y + ": " + mine + " -> " + host);
            }
            bool changed = false;
            TileType main = string.IsNullOrEmpty(k[0]) ? null : AssetManager.tiles.get(k[0]);
            TopTileType top = string.IsNullOrEmpty(k[1]) ? null : AssetManager.top_tiles.get(k[1]);
            if (main != null && (t.main_type != main || t.top_type != top))
            {
                t.setTileTypes(main, top, true);
                changed = true;
            }
            bool fire = k[2] == "1";
            if (fire != t.isOnFire())
            {
                if (fire) R.Call(t, "startFire", new[] { typeof(bool) }, true);
                else R.Call0(t, "stopFire");
                changed = true;
            }
            int burned = int.Parse(k[3], CultureInfo.InvariantCulture);
            if (burned != t.burned_stages)
            {
                R.Call(t, "setBurnedStage", new[] { typeof(int) }, burned);
                changed = true;
            }
            return changed;
        }

        private void Compare(JArray zh, int count)
        {
            List<TileZone> zones = Zones();
            if (zones == null || (count >= 0 && count != zones.Count)) return;
            float now = Time.unscaledTime;
            if (_hostHash == null || _hostHash.Length != zones.Count) _hostHash = new int[zones.Count];
            var ask = new List<int>();
            for (int i = 0; i < zh.Count && i < zones.Count; i++)
            {
                _hostHash[i] = (int)zh[i];
                if (ZoneHash(zones[i]) == (int)zh[i]) continue;
                if (_askedAt.TryGetValue(i, out float at) && now - at < 4f) continue;
                _askedAt[i] = now;
                if (ask.Count < 400) ask.Add(i);
            }
            if (ask.Count > 0) Log.Info("tile check: " + ask.Count + " zone(s) differ from the host");
            Ask(ask);
        }

        private void Ask(List<int> ask)
        {
            if (ask.Count == 0) return;
            var sb = new StringBuilder();
            sb.Append("{\"t\":\"wask\",\"room\":").Append(JsonConvert.ToString(_s.RoomId)).Append(",\"z\":[")
              .Append(string.Join(",", ask.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())).Append("]}");
            _s.Net.SendRaw(sb.ToString(), false);
        }
    }
}
