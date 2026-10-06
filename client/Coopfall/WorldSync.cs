using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Live world sync: keeps guests' worlds on the host's simulation all the time, so drift never
    /// builds up and full re-downloads are rarely needed.
    ///
    /// HOST streams (to the other players in its world):
    /// * "wu": every creature as {id, species, position, health}. Moved/changed creatures at
    ///   liveSyncHz, the complete list every few seconds (so guests can also find units that
    ///   died or never existed on the host).
    /// * "wb": every building/tree as {id, asset, state}. Changes every 2 s, complete list every 30 s.
    /// * "wdata": the full save data (ActorData / BuildingData, exactly what a save file holds) of
    ///   newborn creatures and new buildings, so guests load the very same object with the same id.
    /// GUESTS reconcile:
    /// * creatures are steered smoothly onto the host's positions (with velocity extrapolation),
    ///   health is copied, creatures the host doesn't have die (or vanish if they only ever
    ///   existed locally), creatures they lack are requested with "wneed" and loaded from "wdata";
    /// * buildings likewise (added, removed, finished, ruined).
    /// Guests allocate ids for anything their own simulation creates far above the host's ids,
    /// so a local-only creature can never be mistaken for one of the host's.
    /// The remote players' own possessed units are left to AvatarManager.
    /// </summary>
    public class WorldSync
    {
        private const long GuestIdOffset = 50000000L;
        private const int UnitsPerPart = 1500, BuildingsPerPart = 4000, DataPerTick = 32;
        private const float UnitFullEvery = 5f, BuildTick = 2f, BuildFullEvery = 30f;
        private const float DriftResyncAfter = 90f;
        // A creature only this guest has (e.g. one it just spawned with a god power) is kept this
        // long: the host spawns its own copy from the relayed power, which then takes its place.
        private const float LocalGrace = 8f;
        private const float AdoptRadius = 6f;

        private static readonly JsonSerializerSettings WriteSettings = new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate,
            Formatting = Formatting.None,
        };

        private readonly CoopSession _s;
        public bool Disabled;            // server doesn't know the live sync messages
        public int UnitsCorrected, UnitsLoaded, UnitsRemoved, BuildingsLoaded, BuildingsRemoved;

        public WorldSync(CoopSession s) { _s = s; }

        private bool Ready { get { return !Disabled && _s.Cfg.liveSync && _s.Online && _s.InWorld && WorldBoxApi.WorldReady; } }

        /// <summary>Forget everything (world loaded, role changed, room changed).</summary>
        public void Reset()
        {
            _sentUnits.Clear(); _sentBuild.Clear(); _bornU.Clear(); _bornB.Clear(); _bornUSet.Clear(); _bornBSet.Clear();
            _primed = false; _nextUnitTick = _nextUnitFull = _nextBuildTick = _nextBuildFull = 0f;
            _tracks.Clear(); _correcting.Clear(); _missingU.Clear(); _missingB.Clear(); _attempts.Clear();
            _hostUnitsPrev.Clear(); _loadedAt.Clear(); _localSince.Clear();
            _ucSeq = _bcSeq = -1;
            _driftSince = -1f;
        }

        /// <summary>Someone joined: send complete lists soon (their snapshot may be a few seconds old).</summary>
        public void ForceFull()
        {
            _nextUnitFull = Mathf.Min(_nextUnitFull, Time.unscaledTime + 1f);
            _nextBuildFull = Mathf.Min(_nextBuildFull, Time.unscaledTime + 2f);
        }

        public void Tick()
        {
            if (!Ready) return;
            if (_s.IsHost) HostTick();
        }

        public void LateTick()
        {
            if (!Ready || _s.IsHost) return;
            Steer();
        }

        // ================================================================ host

        private class Sent { public string asset; public int x, y, hp; }

        private readonly Dictionary<long, Sent> _sentUnits = new Dictionary<long, Sent>();
        private readonly Dictionary<long, string> _sentBuild = new Dictionary<long, string>();
        private readonly Queue<long> _bornU = new Queue<long>(), _bornB = new Queue<long>();
        private readonly HashSet<long> _bornUSet = new HashSet<long>(), _bornBSet = new HashSet<long>();
        private bool _primed;
        private float _nextUnitTick, _nextUnitFull, _nextBuildTick, _nextBuildFull;
        private int _seq;

        private void HostTick()
        {
            if (_s.OthersInRoom() == 0) { if (_primed) Reset(); return; }
            if (_s.Net.BulkBytesQueued > 512 * 1024) return;   // a snapshot upload is in progress
            float now = Time.unscaledTime;

            if (now >= _nextUnitTick)
            {
                _nextUnitTick = now + 1f / Mathf.Clamp(_s.Cfg.liveSyncHz, 0.5f, 10f);
                bool full = now >= _nextUnitFull || !_primed;
                if (full) _nextUnitFull = now + UnitFullEvery;
                bool buildFull = false, buildTick = false;
                if (now >= _nextBuildTick) { _nextBuildTick = now + BuildTick; buildTick = true; }
                if (now >= _nextBuildFull || !_primed) { _nextBuildFull = now + BuildFullEvery; buildFull = buildTick = true; }

                List<long> newUnits = CollectUnits(full, out List<UnitRow> rows);
                List<long> newBuild = null, goneBuild = null;
                List<BuildRow> brows = null;
                if (buildTick) newBuild = CollectBuildings(buildFull, out brows, out goneBuild);
                if (_primed)
                {
                    foreach (long id in newUnits) QueueBorn(id, false);
                    if (newBuild != null) foreach (long id in newBuild) QueueBorn(id, true);
                }
                SendData();                                  // births first, so guests have them when the list arrives
                SendUnits(rows, full);
                if (buildTick) SendBuildings(brows, goneBuild, buildFull);
                _primed = true;
            }
        }

        private struct UnitRow { public long id; public string asset; public int x, y, hp; }
        private struct BuildRow { public long id; public string asset; public int st; }

        private List<long> CollectUnits(bool full, out List<UnitRow> rows)
        {
            rows = new List<UnitRow>();
            var fresh = new List<long>();
            var alive = new HashSet<long>();
            foreach (Actor a in World.world.units.getSimpleList())
            {
                if (a == null || !a.isAlive() || a.asset == null || WorldBoxApi.IsStandin(a)) continue;
                long id = a.getID();
                alive.Add(id);
                Vector2 p = a.current_position;
                var row = new UnitRow { id = id, asset = a.asset.id, x = Mathf.RoundToInt(p.x * 10f), y = Mathf.RoundToInt(p.y * 10f), hp = a.getHealth() };
                if (!_sentUnits.TryGetValue(id, out Sent s))
                {
                    fresh.Add(id);
                    s = new Sent();
                    _sentUnits[id] = s;
                }
                else if (!full && s.asset == row.asset && s.hp == row.hp && Mathf.Abs(s.x - row.x) < 5 && Mathf.Abs(s.y - row.y) < 5)
                    continue;                                // hasn't moved half a tile: skip in delta ticks
                s.asset = row.asset; s.x = row.x; s.y = row.y; s.hp = row.hp;
                rows.Add(row);
            }
            if (full)
            {
                var dead = new List<long>();
                foreach (long id in _sentUnits.Keys) if (!alive.Contains(id)) dead.Add(id);
                foreach (long id in dead) _sentUnits.Remove(id);
            }
            rows.Sort((a, b) => a.id.CompareTo(b.id));
            return fresh;
        }

        private List<long> CollectBuildings(bool full, out List<BuildRow> rows, out List<long> gone)
        {
            rows = new List<BuildRow>();
            gone = new List<long>();
            var fresh = new List<long>();
            var alive = new HashSet<long>();
            foreach (Building b in World.world.buildings.getSimpleList())
            {
                BuildingData bd = b == null ? null : BData(b);
                if (bd == null || !b.isAlive() || bd.asset_id == null || b.isOnRemove() || bd.state == BuildingState.Removed) continue;
                long id = b.getID();
                alive.Add(id);
                int st = bd.state == BuildingState.Ruins ? 2 : (b.isUnderConstruction() ? 1 : 0);
                string key = bd.asset_id + "|" + st;
                if (!_sentBuild.TryGetValue(id, out string old)) fresh.Add(id);
                else if (!full && old == key) continue;
                _sentBuild[id] = key;
                rows.Add(new BuildRow { id = id, asset = bd.asset_id, st = st });
            }
            foreach (long id in _sentBuild.Keys) if (!alive.Contains(id)) gone.Add(id);
            foreach (long id in gone) _sentBuild.Remove(id);
            rows.Sort((a, b) => a.id.CompareTo(b.id));
            return fresh;
        }

        private void QueueBorn(long id, bool building)
        {
            if (building) { if (_bornBSet.Add(id)) _bornB.Enqueue(id); }
            else if (_bornUSet.Add(id)) _bornU.Enqueue(id);
        }

        private void SendData()
        {
            if (_bornU.Count == 0 && _bornB.Count == 0) return;
            var units = new List<string>();
            var builds = new List<string>();
            while (_bornU.Count > 0 && units.Count < DataPerTick)
            {
                long id = _bornU.Dequeue();
                _bornUSet.Remove(id);
                Actor a = WorldBoxApi.FindActor(id);
                if (a == null || !a.isAlive() || WorldBoxApi.IsStandin(a)) continue;
                try
                {
                    a.prepareForSave();
                    units.Add(JsonConvert.SerializeObject(a.getData(), WriteSettings));
                }
                catch (Exception e) { Log.Warn("live sync: unit " + id + " data: " + e.Message); }
            }
            while (_bornB.Count > 0 && builds.Count < DataPerTick)
            {
                long id = _bornB.Dequeue();
                _bornBSet.Remove(id);
                Building b = FindBuilding(id);
                if (b == null || !b.isAlive() || b.isOnRemove()) continue;
                try
                {
                    b.prepareForSave();
                    builds.Add(JsonConvert.SerializeObject(b.getData(), WriteSettings));
                }
                catch (Exception e) { Log.Warn("live sync: building " + id + " data: " + e.Message); }
            }
            if (units.Count == 0 && builds.Count == 0) return;
            var sb = Begin("wdata");
            sb.Append(",\"u\":[").Append(string.Join(",", units.ToArray())).Append("]");
            sb.Append(",\"b\":[").Append(string.Join(",", builds.ToArray())).Append("]}");
            _s.Net.SendRaw(sb.ToString(), true);
        }

        private void SendUnits(List<UnitRow> rows, bool full)
        {
            if (!full && rows.Count == 0) return;
            int seq = ++_seq;
            int parts = Mathf.Max(1, (rows.Count + UnitsPerPart - 1) / UnitsPerPart);
            long idu = MapStatsId("id_unit");
            int cities = SafeCount(() => World.world.cities.Count), kingdoms = SafeCount(() => World.world.kingdoms.Count);
            for (int part = 0; part < parts; part++)
            {
                var assets = new List<string>();
                var index = new Dictionary<string, int>();
                var u = new StringBuilder();
                long prev = 0;
                int end = Mathf.Min(rows.Count, (part + 1) * UnitsPerPart);
                for (int i = part * UnitsPerPart; i < end; i++)
                {
                    UnitRow r = rows[i];
                    if (!index.TryGetValue(r.asset, out int ai)) { ai = assets.Count; assets.Add(r.asset); index[r.asset] = ai; }
                    if (u.Length > 0) u.Append(',');
                    u.Append((r.id - prev).ToString(CultureInfo.InvariantCulture)).Append(',').Append(ai).Append(',')
                     .Append(r.x).Append(',').Append(r.y).Append(',').Append(r.hp);
                    prev = r.id;
                }
                var sb = Begin("wu");
                sb.Append(",\"seq\":").Append(seq).Append(",\"part\":").Append(part).Append(",\"parts\":").Append(parts)
                  .Append(",\"full\":").Append(full ? "true" : "false").Append(",\"idu\":").Append(idu)
                  .Append(",\"ck\":[").Append(cities).Append(',').Append(kingdoms).Append("]")
                  .Append(",\"a\":").Append(JsonConvert.SerializeObject(assets)).Append(",\"u\":[").Append(u).Append("]}");
                _s.Net.SendRaw(sb.ToString(), full);
            }
        }

        private void SendBuildings(List<BuildRow> rows, List<long> gone, bool full)
        {
            if (!full && rows.Count == 0 && gone.Count == 0) return;
            int seq = ++_seq;
            int parts = Mathf.Max(1, (rows.Count + BuildingsPerPart - 1) / BuildingsPerPart);
            long idb = MapStatsId("id_building");
            for (int part = 0; part < parts; part++)
            {
                var assets = new List<string>();
                var index = new Dictionary<string, int>();
                var b = new StringBuilder();
                long prev = 0;
                int end = Mathf.Min(rows.Count, (part + 1) * BuildingsPerPart);
                for (int i = part * BuildingsPerPart; i < end; i++)
                {
                    BuildRow r = rows[i];
                    if (!index.TryGetValue(r.asset, out int ai)) { ai = assets.Count; assets.Add(r.asset); index[r.asset] = ai; }
                    if (b.Length > 0) b.Append(',');
                    b.Append((r.id - prev).ToString(CultureInfo.InvariantCulture)).Append(',').Append(ai).Append(',').Append(r.st);
                    prev = r.id;
                }
                var sb = Begin("wb");
                sb.Append(",\"seq\":").Append(seq).Append(",\"part\":").Append(part).Append(",\"parts\":").Append(parts)
                  .Append(",\"full\":").Append(full ? "true" : "false").Append(",\"idb\":").Append(idb)
                  .Append(",\"a\":").Append(JsonConvert.SerializeObject(assets)).Append(",\"b\":[").Append(b).Append("]");
                if (part == 0 && gone.Count > 0)
                {
                    sb.Append(",\"gone\":[");
                    for (int i = 0; i < gone.Count; i++) { if (i > 0) sb.Append(','); sb.Append(gone[i].ToString(CultureInfo.InvariantCulture)); }
                    sb.Append("]");
                }
                sb.Append("}");
                _s.Net.SendRaw(sb.ToString(), true);
            }
        }

        /// <summary>Host: a guest is missing these objects.</summary>
        private void OnNeed(JObject p)
        {
            if (!_s.IsHost) return;
            if (p["u"] is JArray u) foreach (JToken t in u) QueueBorn((long)t, false);
            if (p["b"] is JArray b) foreach (JToken t in b) QueueBorn((long)t, true);
        }

        private StringBuilder Begin(string type)
        {
            // "t" must come first: the relay recognizes live sync lines by their prefix without parsing them.
            var sb = new StringBuilder(4096);
            sb.Append("{\"t\":\"").Append(type).Append("\",\"room\":").Append(JsonConvert.ToString(_s.RoomId));
            return sb;
        }

        // ================================================================ guest

        private class Track { public Vector2 pos, vel; public float at; }

        private readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();
        private readonly HashSet<long> _correcting = new HashSet<long>();
        private readonly List<long> _done = new List<long>();
        private readonly Dictionary<long, float> _missingU = new Dictionary<long, float>(), _missingB = new Dictionary<long, float>();
        private readonly Dictionary<long, int> _attempts = new Dictionary<long, int>();
        private readonly Dictionary<long, float> _loadedAt = new Dictionary<long, float>();
        private readonly Dictionary<long, float> _localSince = new Dictionary<long, float>();   // local-only creature -> first noticed
        private HashSet<long> _hostUnitsPrev = new HashSet<long>();
        private int _ucSeq = -1, _ucGot, _bcSeq = -1, _bcGot;
        private HashSet<long> _ucSeen = new HashSet<long>(), _bcSeen = new HashSet<long>();
        private float _nextNeed, _driftSince = -1f;

        public void OnPacket(string t, JObject p)
        {
            if (!Ready) return;
            try
            {
                switch (t)
                {
                    case "wneed": OnNeed(p); break;
                    case "wu": if (!_s.IsHost) OnUnits(p); break;
                    case "wb": if (!_s.IsHost) OnBuildings(p); break;
                    case "wdata": if (!_s.IsHost) OnData(p); break;
                }
            }
            catch (Exception e) { Log.Warn("live sync '" + t + "': " + e); }
        }

        private void OnUnits(JObject p)
        {
            float now = Time.unscaledTime;
            RaiseIds("id_unit", (long?)p["idu"] ?? 0);
            var assets = p["a"] as JArray;
            var u = p["u"] as JArray;
            if (assets == null || u == null) return;
            bool full = (bool?)p["full"] ?? false;
            int seq = (int?)p["seq"] ?? 0, part = (int?)p["part"] ?? 0, parts = (int?)p["parts"] ?? 1;
            if (full && seq != _ucSeq) { _ucSeq = seq; _ucGot = 0; _ucSeen = new HashSet<long>(); }

            long id = 0;
            for (int i = 0; i + 4 < u.Count; i += 5)
            {
                id += (long)u[i];
                string asset = (string)assets[(int)u[i + 1]];
                var pos = new Vector2((int)u[i + 2] / 10f, (int)u[i + 3] / 10f);
                int hp = (int)u[i + 4];
                if (full) _ucSeen.Add(id);
                ApplyUnit(id, asset, pos, hp, now);
            }

            if (full && ++_ucGot == parts && part == parts - 1)
            {
                FinishUnitCycle(now);
                CheckDrift(p["ck"] as JArray, now);
            }
            else RequestMissing(now);
        }

        private void ApplyUnit(long id, string asset, Vector2 pos, int hp, float now)
        {
            Actor a = WorldBoxApi.FindActor(id);
            if (a == null || !a.isAlive())
            {
                if (!_missingU.ContainsKey(id)) _missingU[id] = now;
                return;
            }
            if (Exempt(a)) return;
            if (a.asset == null || a.asset.id != asset)
            {
                // Same id, different creature (e.g. it transformed on the host): replace it.
                WorldBoxApi.RemoveActor(a);
                UnitsRemoved++;
                _missingU[id] = now - 10f;
                return;
            }
            _missingU.Remove(id);

            if (!_tracks.TryGetValue(id, out Track tr)) { tr = new Track { pos = pos, at = now }; _tracks[id] = tr; }
            else
            {
                float dt = now - tr.at;
                Vector2 v = dt > 0.05f && dt < 3f ? (pos - tr.pos) / dt : Vector2.zero;
                if (v.sqrMagnitude > 900f) v = Vector2.zero;   // teleported on the host
                tr.vel = Vector2.Lerp(tr.vel, v, 0.6f);
                tr.pos = pos;
                tr.at = now;
            }

            int localHp = a.getHealth();
            if (Mathf.Abs(localHp - hp) > 1) { try { a.setHealth(hp); } catch { } }

            float err = Vector2.Distance(a.current_position, pos);
            if (err > 0.4f)
            {
                _correcting.Add(id);
                if (err > 3f) { try { a.stopMovement(); } catch { } }   // its local path leads elsewhere
            }
        }

        /// <summary>LateUpdate: glide drifting creatures onto the host's (extrapolated) positions.</summary>
        private void Steer()
        {
            if (_correcting.Count == 0) return;
            float now = Time.unscaledTime;
            float k = 1f - Mathf.Exp(-5f * Time.unscaledDeltaTime);
            _done.Clear();
            foreach (long id in _correcting)
            {
                Actor a = WorldBoxApi.FindActor(id);
                if (a == null || !a.isAlive() || Exempt(a) || !_tracks.TryGetValue(id, out Track tr)) { _done.Add(id); continue; }
                Vector2 goal = tr.pos + tr.vel * Mathf.Min(now - tr.at, 0.75f);
                Vector2 cur = a.current_position;
                float d = Vector2.Distance(cur, goal);
                if (d < 0.15f || now - tr.at > 4f) { _done.Add(id); continue; }
                try
                {
                    if (d > 12f) { WorldBoxApi.Teleport(a, goal); UnitsCorrected++; _done.Add(id); continue; }
                    Vector2 next = Vector2.Lerp(cur, goal, k);
                    if (Toolbox.getTileAt(next.x, next.y) != null) WorldBoxApi.SetPosition(a, next);
                }
                catch (Exception e) { Log.Warn("live sync steer " + id + ": " + e.Message); _done.Add(id); }
            }
            foreach (long id in _done) _correcting.Remove(id);
        }

        private void FinishUnitCycle(float now)
        {
            // Creatures the host doesn't have: died there (shown dying here) or only ever existed here (removed).
            var doomed = new List<Actor>();
            foreach (Actor a in World.world.units.getSimpleList())
            {
                if (a == null || !a.isAlive()) continue;
                long id = a.getID();
                if (_ucSeen.Contains(id) || Exempt(a)) continue;
                if (_loadedAt.TryGetValue(id, out float at) && now - at < 3f) continue;
                if (!_hostUnitsPrev.Contains(id))
                {
                    // Only ever existed here: give the host's copy time to arrive and replace it.
                    if (!_localSince.TryGetValue(id, out float since)) { _localSince[id] = now; continue; }
                    if (now - since < LocalGrace) continue;
                }
                doomed.Add(a);
            }
            foreach (Actor a in doomed)
            {
                try
                {
                    if (_hostUnitsPrev.Contains(a.getID())) a.dieSimpleNone();
                    else WorldBoxApi.RemoveActor(a);
                    UnitsRemoved++;
                }
                catch (Exception e) { Log.Warn("live sync remove: " + e.Message); }
            }
            _hostUnitsPrev = _ucSeen;
            _ucSeen = new HashSet<long>();
            _ucSeq = -1;
            var settled = new List<long>();
            foreach (long id in _localSince.Keys)
            {
                Actor la = WorldBoxApi.FindActor(id);
                if (la == null || !la.isAlive() || _hostUnitsPrev.Contains(id)) settled.Add(id);
            }
            foreach (long id in settled) _localSince.Remove(id);

            // Forget missing ids the host no longer has.
            var stale = new List<long>();
            foreach (long id in _missingU.Keys) if (!_hostUnitsPrev.Contains(id)) stale.Add(id);
            foreach (long id in stale) _missingU.Remove(id);
            var oldLoads = new List<long>();
            foreach (var kv in _loadedAt) if (now - kv.Value > 10f) oldLoads.Add(kv.Key);
            foreach (long id in oldLoads) _loadedAt.Remove(id);
            var oldTracks = new List<long>();
            foreach (var kv in _tracks) if (now - kv.Value.at > 30f) oldTracks.Add(kv.Key);
            foreach (long id in oldTracks) _tracks.Remove(id);

            if (doomed.Count > 0) Log.Info("live sync: removed " + doomed.Count + " creature(s) the host doesn't have");
            RequestMissing(now);
        }

        private void OnBuildings(JObject p)
        {
            float now = Time.unscaledTime;
            RaiseIds("id_building", (long?)p["idb"] ?? 0);
            var assets = p["a"] as JArray;
            var b = p["b"] as JArray;
            if (assets == null || b == null) return;
            bool full = (bool?)p["full"] ?? false;
            int seq = (int?)p["seq"] ?? 0, part = (int?)p["part"] ?? 0, parts = (int?)p["parts"] ?? 1;
            if (full && seq != _bcSeq) { _bcSeq = seq; _bcGot = 0; _bcSeen = new HashSet<long>(); }

            if (p["gone"] is JArray gone)
                foreach (JToken t in gone)
                {
                    long gid = (long)t;
                    _missingB.Remove(gid);
                    Building lb = FindBuilding(gid);
                    if (lb != null && lb.isAlive() && !lb.isOnRemove()) { RemoveBuilding(lb); BuildingsRemoved++; }
                }

            long id = 0;
            for (int i = 0; i + 2 < b.Count; i += 3)
            {
                id += (long)b[i];
                string asset = (string)assets[(int)b[i + 1]];
                int st = (int)b[i + 2];
                if (full) _bcSeen.Add(id);
                ApplyBuilding(id, asset, st, now);
            }

            if (full && ++_bcGot == parts && part == parts - 1)
            {
                var doomed = new List<Building>();
                foreach (Building lb in World.world.buildings.getSimpleList())
                {
                    if (lb == null || !lb.isAlive() || lb.isOnRemove()) continue;
                    long lid = lb.getID();
                    if (_bcSeen.Contains(lid)) continue;
                    if (_loadedAt.TryGetValue(-lid, out float at) && now - at < 5f) continue;
                    doomed.Add(lb);
                }
                foreach (Building lb in doomed) RemoveBuilding(lb);
                BuildingsRemoved += doomed.Count;
                var stale = new List<long>();
                foreach (long mid in _missingB.Keys) if (!_bcSeen.Contains(mid)) stale.Add(mid);
                foreach (long mid in stale) _missingB.Remove(mid);
                if (doomed.Count > 0) Log.Info("live sync: removed " + doomed.Count + " building(s) the host doesn't have");
                _attempts.Clear();
                _bcSeen = new HashSet<long>();
                _bcSeq = -1;
            }
        }

        private void ApplyBuilding(long id, string asset, int st, float now)
        {
            Building lb = FindBuilding(id);
            if (lb == null || !lb.isAlive() || lb.isOnRemove())
            {
                if (!_missingB.ContainsKey(id)) _missingB[id] = now;
                return;
            }
            if (BData(lb).asset_id != asset)
            {
                RemoveBuilding(lb);
                BuildingsRemoved++;
                _missingB[id] = now;
                return;
            }
            _missingB.Remove(id);
            try
            {
                bool ruin = BData(lb).state == BuildingState.Ruins;
                if (st == 0 && lb.isUnderConstruction()) lb.completeConstruction();
                else if (st == 2 && !ruin) Invoke(lb, "startDestroyBuilding");
            }
            catch (Exception e) { Log.Warn("live sync building state: " + e.Message); }
        }

        private void OnData(JObject p)
        {
            float now = Time.unscaledTime;
            int units = 0, builds = 0;
            if (p["b"] is JArray b)
                foreach (JToken t in b)
                {
                    try
                    {
                        var data = t.ToObject<BuildingData>(JsonHelper.reader);
                        if (data == null) continue;
                        _missingB.Remove(data.id);
                        Building have = FindBuilding(data.id);
                        if (have != null && have.isAlive() && !have.isOnRemove())
                        {
                            if (BData(have).asset_id == data.asset_id) continue;
                            RemoveBuilding(have);
                        }
                        Building loaded = World.world.buildings.loadObject(data);
                        if (loaded == null)
                        {
                            // Usually a local-only building (tree, house) stands on that spot: clear it and retry.
                            WorldTile tile = World.world.GetTileSimple(data.mainX, data.mainY);
                            Building occupant = tile?.building;
                            if (occupant != null && occupant.getID() != data.id)
                            {
                                RemoveBuilding(occupant);
                                BuildingsRemoved++;
                                loaded = World.world.buildings.loadObject(data);
                            }
                        }
                        if (loaded != null) { builds++; _loadedAt[-data.id] = now; }
                    }
                    catch (Exception e) { Log.Warn("live sync: building load: " + e.Message); }
                }
            if (p["u"] is JArray u)
                foreach (JToken t in u)
                {
                    try
                    {
                        var data = t.ToObject<ActorData>(JsonHelper.reader);
                        if (data == null) continue;
                        _missingU.Remove(data.id);
                        Actor have = WorldBoxApi.FindActor(data.id);
                        if (have != null && have.isAlive())
                        {
                            if (have.asset != null && have.asset.id == data.asset_id) continue;
                            WorldBoxApi.RemoveActor(have);
                        }
                        Actor a = World.world.units.loadObject(data);
                        if (a != null) { units++; _loadedAt[data.id] = now; AdoptLocal(a); }
                    }
                    catch (Exception e) { Log.Warn("live sync: creature load: " + e.Message); }
                }
            UnitsLoaded += units;
            BuildingsLoaded += builds;
            if (units + builds > 0) Log.Info("live sync: loaded " + units + " creature(s), " + builds + " building(s) from the host");
        }

        /// <summary>
        /// The host's copy of a creature arrived: if this guest has its own just-spawned creature of
        /// the same species nearby (made by the same god power, but with a guest-only id), that one
        /// steps aside so there's exactly one, the host's.
        /// </summary>
        private void AdoptLocal(Actor hostCopy)
        {
            if (hostCopy.asset == null) return;
            Actor best = null;
            float bestDist = AdoptRadius;
            long hostId = hostCopy.getID();
            foreach (Actor la in World.world.units.getSimpleList())
            {
                if (la == null || la == hostCopy || !la.isAlive() || la.asset == null || la.asset.id != hostCopy.asset.id) continue;
                long id = la.getID();
                // only creatures the host has never told us about
                if (_hostUnitsPrev.Contains(id) || _tracks.ContainsKey(id) || _loadedAt.ContainsKey(id) || Exempt(la)) continue;
                float d = Vector2.Distance(la.current_position, hostCopy.current_position);
                if (d < bestDist) { bestDist = d; best = la; }
            }
            if (best == null) return;
            long localId = best.getID();
            _localSince.Remove(localId);
            WorldBoxApi.RemoveActor(best);
            Log.Info("live sync: your " + hostCopy.asset.id + " #" + localId + " is now the host's #" + hostId);
        }

        private void RequestMissing(float now)
        {
            if (now < _nextNeed) return;
            _nextNeed = now + 1f;
            var u = new List<long>();
            var b = new List<long>();
            foreach (var kv in _missingU)
                if (now - kv.Value > 1.5f && u.Count < 64 && (!_attempts.TryGetValue(kv.Key, out int n) || n < 3)) u.Add(kv.Key);
            foreach (var kv in _missingB)
                if (now - kv.Value > 2.5f && b.Count < 64 && (!_attempts.TryGetValue(-kv.Key, out int n) || n < 3)) b.Add(kv.Key);
            if (u.Count == 0 && b.Count == 0) return;
            foreach (long id in u) { _missingU[id] = now; CountRequest(id); }
            foreach (long id in b) { _missingB[id] = now; CountRequest(-id); }
            var sb = Begin("wneed");
            sb.Append(",\"u\":[").Append(string.Join(",", u.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())).Append("]");
            sb.Append(",\"b\":[").Append(string.Join(",", b.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())).Append("]}");
            _s.Net.SendRaw(sb.ToString(), false);
        }

        /// <summary>Each id is requested at most 3 times (per full building cycle), so an object that can't load here isn't asked for forever.</summary>
        private void CountRequest(long key)
        {
            _attempts.TryGetValue(key, out int n);
            _attempts[key] = n + 1;
        }

        /// <summary>
        /// Cities/kingdoms (borders, wars, alliances) aren't streamed. If their counts keep differing
        /// from the host's for a while, a full re-sync fixes them.
        /// </summary>
        private void CheckDrift(JArray ck, float now)
        {
            if (ck == null || ck.Count < 2 || !_s.Cfg.resyncOnDrift) return;
            int cities = SafeCount(() => World.world.cities.Count), kingdoms = SafeCount(() => World.world.kingdoms.Count);
            bool same = cities == (int)ck[0] && kingdoms == (int)ck[1];
            if (same) { _driftSince = -1f; return; }
            if (_driftSince < 0f) { _driftSince = now; return; }
            if (now - _driftSince > DriftResyncAfter && !CoopMod.UiBlocking)
            {
                Log.Info("live sync: cities/kingdoms differ from the host (" + cities + "/" + kingdoms + " vs " + ck[0] + "/" + ck[1] + ") - re-syncing");
                _driftSince = -1f;
                _s.Resync(true);
            }
        }

        // ================================================================ helpers

        /// <summary>Units the live sync must not touch: mine, other players' avatars, stand-ins.</summary>
        private static bool Exempt(Actor a)
        {
            if (ControllableUnit.isControllingUnit(a) || WorldBoxApi.IsStandin(a)) return true;
            AvatarManager av = CoopMod.Instance?.Avatars;
            return av != null && av.IsPuppet(a);
        }

        private static BuildingData BData(Building b) { return (BuildingData)b.getData(); }

        private static Building FindBuilding(long id)
        {
            try { return id > 0 && World.world != null ? World.world.buildings.get(id) : null; }
            catch { return null; }
        }

        private static void RemoveBuilding(Building b)
        {
            if (b == null) return;
            if (!Invoke(b, "startRemove")) Invoke(b, "startDestroyBuilding");
        }

        private static readonly Dictionary<string, MethodInfo> _methods = new Dictionary<string, MethodInfo>();

        private static bool Invoke(Building b, string name)
        {
            try
            {
                if (!_methods.TryGetValue(name, out MethodInfo m))
                {
                    m = typeof(Building).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    _methods[name] = m;
                }
                if (m == null) return false;
                m.Invoke(b, null);
                return true;
            }
            catch (Exception e) { Log.Warn("live sync: " + name + ": " + e.Message); return false; }
        }

        private static FieldInfo _fiMapStats;

        private static object MapStats()
        {
            if (_fiMapStats == null) _fiMapStats = typeof(MapBox).GetField("map_stats", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return _fiMapStats?.GetValue(World.world);
        }

        private static long MapStatsId(string field)
        {
            try
            {
                object ms = MapStats();
                FieldInfo f = ms?.GetType().GetField(field);
                return f != null ? (long)f.GetValue(ms) : 0;
            }
            catch { return 0; }
        }

        /// <summary>Guests number their own new objects far above the host's, so ids never collide.</summary>
        private static void RaiseIds(string field, long hostNext)
        {
            if (hostNext <= 0) return;
            try
            {
                object ms = MapStats();
                FieldInfo f = ms?.GetType().GetField(field);
                if (f == null) return;
                long want = hostNext + GuestIdOffset;
                if ((long)f.GetValue(ms) < want) f.SetValue(ms, want);
            }
            catch { }
        }

        private static int SafeCount(Func<int> f)
        {
            try { return f(); } catch { return -1; }
        }
    }
}
