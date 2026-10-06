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
    ///   died or never existed on the host). Deaths are announced in the next tick with their
    ///   cause and killer, so guests see the same death (and kill counts) at once.
    /// * "wb": every building/tree as {id, asset, state}. Changes every 2 s, complete list every 10 s.
    /// * "wdata": the full save data (ActorData / BuildingData, exactly what a save file holds) of
    ///   newborn creatures and new buildings, so guests load the very same object with the same id.
    /// GUESTS reconcile:
    /// * the host's creatures are driven by the host only: their own AI is cancelled here, and
    ///   every frame they are placed on the host's (extrapolated) position, facing the host's way,
    ///   with the walk animation on while they walk there; health is copied; creatures the host
    ///   doesn't have die (or vanish if they only ever existed locally), creatures they lack are
    ///   requested with "wneed" and loaded from "wdata";
    /// * buildings likewise (added, removed, finished, ruined).
    /// Guests allocate ids for anything their own simulation creates far above the host's ids,
    /// so a local-only creature can never be mistaken for one of the host's.
    /// The remote players' own possessed units are left to AvatarManager. Everything else
    /// (cities, kingdoms, wars, borders, terrain, ...) is MetaSync's and TileSync's.
    /// </summary>
    public class WorldSync
    {
        private const long GuestIdOffset = 50000000L;
        private const int UnitsPerPart = 1500, BuildingsPerPart = 4000, DataPerTick = 32;
        private const float UnitFullEvery = 5f, BuildTick = 2f, BuildFullEvery = 10f;
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
        public int UnitsCorrected, UnitsLoaded, UnitsRemoved, UnitsKilled, BuildingsLoaded, BuildingsRemoved;
        public int ErrSamples, ErrFar, FacingWrong;
        public float ErrSum, ErrMax;

        /// <summary>Accuracy since the last call: "avg/max tiles off, % over half a tile, % facing wrong".</summary>
        public string TakeAccuracy()
        {
            if (ErrSamples == 0) return "no samples";
            string r = (ErrSum / ErrSamples).ToString("0.000", CultureInfo.InvariantCulture) + " avg / " + ErrMax.ToString("0.00", CultureInfo.InvariantCulture) +
                       " max tiles, " + (100f * ErrFar / ErrSamples).ToString("0.0", CultureInfo.InvariantCulture) + "% over 0.5, " +
                       (100f * FacingWrong / ErrSamples).ToString("0.0", CultureInfo.InvariantCulture) + "% facing wrong (" + ErrSamples + " samples)";
            ErrSamples = ErrFar = FacingWrong = 0; ErrSum = ErrMax = 0f;
            return r;
        }

        public WorldSync(CoopSession s) { _s = s; }

        private bool Ready { get { return !Disabled && _s.Cfg.liveSync && _s.Online && _s.InWorld && WorldBoxApi.WorldReady; } }

        /// <summary>Forget everything (world loaded, role changed, room changed).</summary>
        public void Reset()
        {
            _sentUnits.Clear(); _sentBuild.Clear(); _bornU.Clear(); _bornB.Clear(); _bornUSet.Clear(); _bornBSet.Clear();
            _primed = false; _nextUnitTick = _nextUnitFull = _nextBuildTick = _nextBuildFull = 0f;
            ReleaseDriven();
            _tracks.Clear(); _missingU.Clear(); _missingB.Clear(); _attempts.Clear();
            _hostUnitsPrev.Clear(); _motion.Clear(); _forced.Clear(); _loadedAt.Clear(); _localSince.Clear(); _sentRefs.Clear(); _deaths.Clear();
            _ucSeq = _bcSeq = -1;
            _driftSince = -1f;
            CoopMod.Instance?.Meta?.Reset();
            CoopMod.Instance?.Tiles?.Reset();
        }

        /// <summary>Someone joined: send complete lists soon (their snapshot may be a few seconds old).</summary>
        public void ForceFull()
        {
            _nextUnitFull = Mathf.Min(_nextUnitFull, Time.unscaledTime + 1f);
            _nextBuildFull = Mathf.Min(_nextBuildFull, Time.unscaledTime + 2f);
            CoopMod.Instance?.Meta?.ForceFull();
            CoopMod.Instance?.Tiles?.ForceFull();
        }

        public void Tick()
        {
            if (!Ready) return;
            if (_s.IsHost) { HostTick(); FinishRemovals(); }
        }

        public void LateTick()
        {
            if (!Ready || _s.IsHost) return;
            Steer();
            FinishRemovals();
        }

        // ================================================================ host

        private class Sent { public string asset; public int x, y, hp, f, h = -1, z; }
        private readonly Dictionary<long, int> _heading = new Dictionary<long, int>();   // last direction each creature walked (0-255)
        private class Motion { public Vector2 vel; public readonly List<KeyValuePair<float, Vector2>> samples = new List<KeyValuePair<float, Vector2>>(); }
        private readonly Dictionary<long, Motion> _motion = new Dictionary<long, Motion>();   // real velocity of each creature (tiles/s)

        private readonly Dictionary<long, float> _flipAt = new Dictionary<long, float>();   // when each creature last turned around (2D)

        /// <summary>Host, diagnostics: seconds since this creature last turned around in 2D (as seen by the live sync).</summary>
        public float FlipAge(Actor a)
        {
            long id = a.getID();
            if (_sentUnits.TryGetValue(id, out Sent s) && ((s.f & 1) != 0) != GetFlip(a)) return 0f;   // turned after the last update
            return _flipAt.TryGetValue(id, out float t) ? Time.unscaledTime - t : 999f;
        }

        private readonly Dictionary<long, Sent> _sentUnits = new Dictionary<long, Sent>();
        private readonly Dictionary<long, Actor> _sentRefs = new Dictionary<long, Actor>();
        private readonly List<string> _deaths = new List<string>();   // "id,attackType,killer" since the last send
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
            else if (_primed) ForcedTick(now);
        }

        private struct UnitRow { public long id; public string asset; public int x, y, hp, f, h, vx, vy, z; }   // vx, vy: velocity in tiles/s *10   // f: 1 facing right (flip), 2 walking, 4 pushed by forces; h: heading 0-255 or -1
        private struct BuildRow { public long id; public string asset; public int st; }

        /// <summary>The direction a creature last walked in (what Worldfall turns its 3D body to), 0-255, or -1 if unknown.</summary>
        private int Heading(Actor a, long id)
        {
            // What Worldfall actually draws here (includes its own turning, LookAt and stale next steps).
            if (WorldfallBridge.Heading(a, out float drawnAng, out Vector2 _))
            {
                int w = Mathf.RoundToInt(Mathf.Repeat(drawnAng * Mathf.Rad2Deg, 360f) / 1.40625f) & 255;
                _heading[id] = w;
                return w;
            }
            if (a.is_moving)
            {
                Vector2 d = a.next_step_position - a.current_position;
                if (d.sqrMagnitude > 0.0004f)
                {
                    int h = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, 360f) / 1.40625f) & 255;
                    _heading[id] = h;
                    return h;
                }
            }
            return _heading.TryGetValue(id, out int last) ? last : -1;
        }

        /// <summary>
        /// The creature's real velocity (guests extrapolate with it). Walkers: net movement over the
        /// last half second or more, so a creature wobbling on the spot doesn't look fast. Creatures
        /// pushed around: the latest step only, they change direction quickly.
        /// </summary>
        private Vector2 Velocity(Actor a, long id, Vector2 p, float now)
        {
            if (!_motion.TryGetValue(id, out Motion m)) { _motion[id] = m = new Motion(); }
            var h = m.samples;
            if (h.Count > 0 && now - h[h.Count - 1].Key < 0.02f) return m.vel;
            h.Add(new KeyValuePair<float, Vector2>(now, p));
            while (h.Count > 2 && now - h[1].Key >= 0.5f) h.RemoveAt(0);   // keep the newest sample at least 0.5 s old
            if (h.Count < 2) return m.vel = Vector2.zero;
            bool pushed = UnderForces(a);
            var from = pushed ? h[h.Count - 2] : h[0];
            float dt = now - from.Key;
            Vector2 v = dt > 0.02f && dt < 3f ? (p - from.Value) / dt : Vector2.zero;
            // Teleported, or standing (creatures carried by a tornado move without walking or forces).
            if (v.sqrMagnitude > 900f || (!(a.is_moving || pushed) && v.sqrMagnitude < 1f)) v = Vector2.zero;
            if (!pushed && a.is_moving && v.sqrMagnitude > 0f)
            {
                // Blocked walkers get put back where they were: movement against the way it walks isn't real.
                Vector2 step = a.next_step_position - p;
                if (step.sqrMagnitude > 0.0004f && Vector2.Dot(v, step) < 0f) v = Vector2.zero;
            }
            return m.vel = v;
        }

        private UnitRow MakeRow(Actor a, long id, float now)
        {
            Vector2 p = a.current_position;
            Vector2 vel = Velocity(a, id, p, now);
            bool forced = UnderForces(a) || (!a.is_moving && vel.sqrMagnitude >= 1f);   // pushed, or carried
            return new UnitRow
            {
                id = id, asset = a.asset.id, x = Mathf.RoundToInt(p.x * 10f), y = Mathf.RoundToInt(p.y * 10f), hp = a.getHealth(),
                f = (GetFlip(a) ? 1 : 0) | (a.is_moving || forced ? 2 : 0) | (forced ? 4 : 0),
                h = Heading(a, id),
                vx = Mathf.RoundToInt(vel.x * 10f), vy = Mathf.RoundToInt(vel.y * 10f), z = Mathf.RoundToInt(Mathf.Max(0f, a.position_height) * 10f),
            };
        }

        // Creatures thrown around (tornado, earthquake, blasts) move fast and erratically: they're
        // sent ForcedHz times a second between the regular ticks, so guests needn't guess.
        private const float ForcedHz = 15f;
        private readonly HashSet<long> _forced = new HashSet<long>();
        private float _nextForcedTick;

        private void ForcedTick(float now)
        {
            if (_forced.Count == 0 || now < _nextForcedTick) return;
            _nextForcedTick = now + 1f / ForcedHz;
            var rows = new List<UnitRow>();
            var still = new List<long>();
            foreach (long id in _forced)
            {
                Actor a = WorldBoxApi.FindActor(id);
                if (a == null || !a.isAlive() || a.asset == null || !_sentUnits.TryGetValue(id, out Sent s)) continue;
                UnitRow row = MakeRow(a, id, now);
                if ((row.f & 4) != 0) still.Add(id);
                s.asset = row.asset; s.x = row.x; s.y = row.y; s.hp = row.hp; s.f = row.f; s.h = row.h; s.z = row.z;
                rows.Add(row);
            }
            _forced.Clear();
            foreach (long id in still) _forced.Add(id);
            rows.Sort((x, y) => x.id.CompareTo(y.id));
            if (rows.Count > 0) SendUnits(rows, false);
        }

        private List<long> CollectUnits(bool full, out List<UnitRow> rows)
        {
            _forced.Clear();
            rows = new List<UnitRow>();
            var fresh = new List<long>();
            var alive = new HashSet<long>();
            float now = Time.unscaledTime;
            foreach (Actor a in World.world.units.getSimpleList())
            {
                if (a == null || !a.isAlive() || a.asset == null || WorldBoxApi.IsStandin(a)) continue;
                long id = a.getID();
                alive.Add(id);
                UnitRow row = MakeRow(a, id, now);
                if ((row.f & 4) != 0) _forced.Add(id);
                if (!_sentUnits.TryGetValue(id, out Sent s))
                {
                    fresh.Add(id);
                    s = new Sent();
                    _sentUnits[id] = s;
                    _sentRefs[id] = a;
                }
                else if (!full && s.asset == row.asset && s.hp == row.hp && s.f == row.f && Mathf.Abs(s.z - row.z) < 2 && Mathf.Abs(s.x - row.x) < 2 && Mathf.Abs(s.y - row.y) < 2
                         && (s.h == row.h || (s.h >= 0 && row.h >= 0 && Mathf.Abs(Mathf.DeltaAngle(s.h * 1.40625f, row.h * 1.40625f)) < 12f)))
                    continue;                                // hasn't moved a fifth of a tile or turned: skip in delta ticks
                if (((s.f ^ row.f) & 1) != 0) _flipAt[id] = now;
                s.asset = row.asset; s.x = row.x; s.y = row.y; s.hp = row.hp; s.f = row.f; s.h = row.h; s.z = row.z;
                rows.Add(row);
            }
            // Creatures gone since the last tick: announce how they died.
            var dead = new List<long>();
            foreach (long id in _sentUnits.Keys) if (!alive.Contains(id)) dead.Add(id);
            foreach (long id in dead)
            {
                _sentUnits.Remove(id);
                string cause = "0,0";
                try
                {
                    // The object may already be recycled (another creature) or disposed (no data).
                    if (_sentRefs.TryGetValue(id, out Actor da) && da != null && da.getData() != null && da.getID() == id && !da.isAlive())
                    {
                        Actor killer = CombatSync.LastAttacker(da);
                        cause = (int)CombatSync.LastAttackType(da) + "," + (killer != null && killer.getData() != null ? killer.getID().ToString(CultureInfo.InvariantCulture) : "0");
                    }
                }
                catch { }
                _deaths.Add(id.ToString(CultureInfo.InvariantCulture) + "," + cause);
                _sentRefs.Remove(id);
                _heading.Remove(id);
                _motion.Remove(id);
                _flipAt.Remove(id);
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
                // WorldBox's removal fade-out sometimes never ends (the building stays half-removed):
                // finish it, as guests do, so everybody's map agrees.
                if (bd != null && b.isAlive() && b.isOnRemove() && !_removing.ContainsKey(b)) _removing[b] = Time.unscaledTime;
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
            if (!full && rows.Count == 0 && _deaths.Count == 0) return;
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
                     .Append(r.x).Append(',').Append(r.y).Append(',').Append(r.hp).Append(',').Append(r.f).Append(',').Append(r.h)
                     .Append(',').Append(r.vx).Append(',').Append(r.vy).Append(',').Append(r.z);
                    prev = r.id;
                }
                var sb = Begin("wu");
                sb.Append(",\"seq\":").Append(seq).Append(",\"part\":").Append(part).Append(",\"parts\":").Append(parts)
                  .Append(",\"st\":10,\"ts\":").Append(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).Append(",\"full\":").Append(full ? "true" : "false").Append(",\"idu\":").Append(idu)
                  .Append(",\"ck\":[").Append(cities).Append(',').Append(kingdoms).Append("]")
                  .Append(",\"a\":").Append(JsonConvert.SerializeObject(assets)).Append(",\"u\":[").Append(u).Append("]");
                if (part == 0 && _deaths.Count > 0)
                {
                    sb.Append(",\"d\":[").Append(string.Join(",", _deaths.ToArray())).Append("]");
                    _deaths.Clear();
                }
                if (part == 0 && full) sb.Append(",\"ids\":").Append(JsonConvert.SerializeObject(IdCounters()));
                sb.Append("}");
                _s.Net.SendRaw(sb.ToString(), false);
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

        private class Track
        {
            public Vector2 pos, vel;
            public float at;
            public bool flip, walking, forced, animOn, lookSet;
            public float height;
            public float ahead = 0.6f;          // how far ahead of the last sample we may extrapolate (s)
            public float heading = float.NaN;   // radians, the way the host's creature last walked
            public Actor actor;
        }

        private readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();
        private readonly List<long> _done = new List<long>();

        private static Func<Actor, bool> _getFlip, _getMoving;
        private static Action<Actor, bool> _setFlip, _setMovingUnused;

        private static Func<Actor, bool> _getForces;
        private static Action<Actor, bool> _setForces;
        private static Func<Actor, Vector3> _getVel;
        private static Action<Actor, Vector3> _setVel;

        private static bool UnderForces(Actor a)
        {
            if (_getForces == null) R.FastField("under_forces", out _getForces, out _setForces);
            return _getForces(a);
        }

        private static void DropForces(Actor a)
        {
            if (_setVel == null) R.FastField("velocity", out _getVel, out _setVel);
            _setVel(a, Vector3.zero);
            _setForces(a, false);
        }

        private static bool GetFlip(Actor a)
        {
            if (_getFlip == null) R.FastField("flip", out _getFlip, out _setFlip);
            return _getFlip(a);
        }

        private static void SetFlip(Actor a, bool v)
        {
            if (_setFlip == null) R.FastField("flip", out _getFlip, out _setFlip);
            _setFlip(a, v);
        }

        /// <summary>Moving along a path of its own (not the "possessed movement" flag we set).</summary>
        private static bool OwnPath(Actor a)
        {
            if (_getMoving == null) R.FastField("_is_moving", out _getMoving, out _setMovingUnused);
            return _getMoving(a);
        }
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
            // How old this packet really is: it may have waited behind bulk traffic (tiles, buildings).
            long ts = (long?)p["ts"] ?? 0;
            _timed = ts > 0;
            _lastDelay = PacketDelay(ts);
            float now = Time.unscaledTime - _lastDelay;
            RaiseIds("id_unit", (long?)p["idu"] ?? 0);
            if (p["ids"] is JObject ids) foreach (var kv in ids) RaiseIds(kv.Key, (long)kv.Value);
            if (p["d"] is JArray d) ApplyDeaths(d);
            var assets = p["a"] as JArray;
            var u = p["u"] as JArray;
            if (assets == null || u == null) return;
            bool full = (bool?)p["full"] ?? false;
            int seq = (int?)p["seq"] ?? 0, part = (int?)p["part"] ?? 0, parts = (int?)p["parts"] ?? 1;
            if (full && seq != _ucSeq) { _ucSeq = seq; _ucGot = 0; _ucSeen = new HashSet<long>(); }

            int stride = Mathf.Max(5, (int?)p["st"] ?? 5);
            long id = 0;
            for (int i = 0; i + stride - 1 < u.Count; i += stride)
            {
                id += (long)u[i];
                string asset = (string)assets[(int)u[i + 1]];
                var pos = new Vector2((int)u[i + 2] / 10f, (int)u[i + 3] / 10f);
                int hp = (int)u[i + 4];
                int f = stride > 5 ? (int)u[i + 5] : 2;
                int h = stride > 6 ? (int)u[i + 6] : -1;
                Vector2? vel = stride > 8 ? new Vector2((int)u[i + 7] / 10f, (int)u[i + 8] / 10f) : (Vector2?)null;
                float z = stride > 9 ? (int)u[i + 9] / 10f : 0f;
                if (full) _ucSeen.Add(id);
                ApplyUnit(id, asset, pos, hp, f, h, vel, z, now);
            }

            if (full && ++_ucGot == parts && part == parts - 1)
            {
                FinishUnitCycle(now);
                CheckDrift(p["ck"] as JArray, now);
            }
            else RequestMissing(now);
        }

        /// <summary>Host deaths: [id, attackType, killerId, ...]. The creature dies here the same way.</summary>
        private int _loadErrors;
        private float _lastDelay;   // how long the message being applied waited in queues
        private bool _timed;   // the host stamps its messages: sample times already include the transit delay
        private double _minOffset = double.MaxValue;
        private float _minOffsetAt;

        /// <summary>
        /// Seconds this packet spent queued beyond the quickest one seen lately (host and guest
        /// clocks may differ, so only the difference to the best case counts).
        /// </summary>
        private float PacketDelay(long ts)
        {
            if (ts <= 0) return 0f;
            double off = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond - ts;
            float t = Time.unscaledTime;
            // Let the best case age out slowly (clock drift, route changes).
            if (off < _minOffset || t - _minOffsetAt > 30f) { _minOffset = off; _minOffsetAt = t; }
            return Mathf.Clamp((float)(off - _minOffset) / 1000f, 0f, 3f);
        }

        private void ApplyDeaths(JArray d)
        {
            CombatSync combat = CoopMod.Instance?.Combat;
            for (int i = 0; i + 2 < d.Count; i += 3)
            {
                long id = (long)d[i];
                _missingU.Remove(id);
                _hostUnitsPrev.Remove(id);
                Untrack(id);
                Actor a = WorldBoxApi.FindActor(id);
                if (a == null || !a.isAlive() || Exempt(a)) continue;
                combat?.Kill(a, (AttackType)(int)d[i + 1], WorldBoxApi.FindActor((long)d[i + 2]));
                UnitsKilled++;
            }
        }

        /// <summary>True for creatures the host has told us about (their deaths are the host's call).</summary>
        public bool IsHostUnit(long id) { return _hostUnitsPrev.Contains(id) || _tracks.ContainsKey(id) || _loadedAt.ContainsKey(id); }

        private void ApplyUnit(long id, string asset, Vector2 pos, int hp, int flags, int heading, Vector2? hostVel, float height, float now)
        {
            Actor a = WorldBoxApi.FindActor(id);
            if (a == null || !a.isAlive())
            {
                if (!_missingU.ContainsKey(id)) _missingU[id] = now;
                return;
            }
            if (Exempt(a)) { Untrack(id); return; }
            if (a.asset == null || a.asset.id != asset)
            {
                // Same id, different creature (e.g. it transformed on the host): replace it.
                WorldBoxApi.RemoveActor(a);
                UnitsRemoved++;
                _missingU[id] = now - 10f;
                return;
            }
            _missingU.Remove(id);

            bool walking = (flags & 2) != 0;
            if (_tracks.TryGetValue(id, out Track seen) && seen.actor == a)
            {
                // How close our copy was when the host's state arrived (shown in the Co-op menu / test log).
                float e = Vector2.Distance(a.current_position, pos);
                ErrSamples++; ErrSum += e; if (e > ErrMax) ErrMax = e; if (e > 0.5f) ErrFar++;
                if (a.asset != null && a.asset.can_flip && GetFlip(a) != ((flags & 1) != 0)) FacingWrong++;
            }
            if (!_tracks.TryGetValue(id, out Track tr)) { tr = new Track { pos = pos, at = now, vel = walking && hostVel.HasValue ? hostVel.Value : Vector2.zero }; _tracks[id] = tr; }
            else if (hostVel.HasValue)
            {
                // The host measured the real velocity: no guessing from samples 0.2+ s apart.
                tr.vel = walking ? hostVel.Value : Vector2.zero;
                tr.pos = pos;
                tr.at = now;
            }
            else
            {
                float dt = now - tr.at;
                Vector2 v = dt > 0.05f && dt < 3f ? (pos - tr.pos) / dt : Vector2.zero;
                if (v.sqrMagnitude > 900f) v = Vector2.zero;   // teleported on the host
                tr.vel = walking ? Vector2.Lerp(tr.vel, v, 0.7f) : Vector2.zero;
                tr.pos = pos;
                tr.at = now;
            }
            tr.actor = a;
            tr.flip = (flags & 1) != 0;
            tr.walking = walking;
            tr.forced = (flags & 4) != 0;
            // Make up for the time the message spent in queues, but don't run on when the host simply
            // hasn't said anything new (it may have hitched: its creatures stood still meanwhile).
            tr.ahead = _timed ? Mathf.Min(_lastDelay + 0.3f, 1.5f) : 0.6f;
            tr.height = height;
            tr.heading = heading < 0 ? float.NaN : heading * 1.40625f * Mathf.Deg2Rad;

            int localHp = a.getHealth();
            if (Mathf.Abs(localHp - hp) > 1) { try { a.setHealth(hp); } catch { } }

            // The host decides what this creature does: stop whatever our own AI started.
            try { a.cancelAllBeh(); } catch { }
        }

        /// <summary>Guest, diagnostics: what the host last said about a creature (heading in degrees, velocity) and how we drive it.</summary>
        public bool TrackInfo(long id, out float headingDeg, out Vector2 vel, out float age, out bool anim, out bool look)
        {
            headingDeg = float.NaN; vel = Vector2.zero; age = 0f; anim = look = false;
            if (!_tracks.TryGetValue(id, out Track tr)) return false;
            headingDeg = float.IsNaN(tr.heading) ? float.NaN : tr.heading * Mathf.Rad2Deg;
            vel = tr.vel; age = Time.unscaledTime - tr.at; anim = tr.animOn; look = tr.lookSet;
            return true;
        }

        private void Untrack(long id)
        {
            if (_tracks.TryGetValue(id, out Track tr))
            {
                StopAnim(tr);
                _tracks.Remove(id);
            }
        }

        private static void StopAnim(Track tr)
        {
            if (tr.lookSet) { tr.lookSet = false; try { WorldfallBridge.ClearLookAt(tr.actor); } catch { } }
            if (!tr.animOn) return;
            tr.animOn = false;
            try { if (tr.actor != null && tr.actor.isAlive()) tr.actor.setPossessedMovement(false); } catch { }
        }

        /// <summary>Hands creatures back to the local game (role change, world change, live sync off).</summary>
        private void ReleaseDriven()
        {
            foreach (Track tr in _tracks.Values) StopAnim(tr);
        }

        /// <summary>
        /// LateUpdate: every creature the host told us about follows the host's stream: its own path is
        /// dropped, it is placed on the host's position (looking ahead by its speed for the time the
        /// data took to arrive), faces the host's way and plays its walk animation while walking there.
        /// </summary>
        private void Steer()
        {
            if (_tracks.Count == 0) return;
            float now = Time.unscaledTime;
            float k = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
            // The host's positions are about half the round trip old when they arrive: look that far ahead.
            float lag = _timed ? 0.02f : (_s.PingMs > 0 ? Mathf.Min(_s.PingMs / 2000f, 0.4f) : 0.05f);
            _done.Clear();
            foreach (var kv in _tracks)
            {
                Track tr = kv.Value;
                Actor a = tr.actor;
                if (a == null || !a.isAlive() || a.getID() != kv.Key || Exempt(a)) { StopAnim(tr); _done.Add(kv.Key); continue; }
                try
                {
                    if (OwnPath(a)) a.stopMovement();          // our AI started a walk of its own
                    // Local tornadoes, explosions and knockback push our copy too: only the host's physics count.
                    if (UnderForces(a)) DropForces(a);
                    a.position_height = Mathf.Lerp(a.position_height, tr.height, k);
                    // Walkers keep their line for a while; thrown creatures (tornado, blasts) curve, so look ahead only briefly.
                    Vector2 goal = tr.pos + tr.vel * Mathf.Min(now - tr.at + lag, tr.forced ? 0.07f : tr.ahead);
                    Vector2 cur = a.current_position;
                    float d = Vector2.Distance(cur, goal);
                    if (d > 6f) { WorldBoxApi.Teleport(a, goal); UnitsCorrected++; }
                    else if (d > 0.01f)
                    {
                        Vector2 next = Vector2.Lerp(cur, goal, k);
                        if (Toolbox.getTileAt(next.x, next.y) != null) WorldBoxApi.SetPosition(a, next);
                    }
                    if (a.asset != null && a.asset.can_flip && GetFlip(a) != tr.flip) SetFlip(a, tr.flip);
                    bool anim = tr.walking || d > 0.15f;
                    if (anim != tr.animOn) { tr.animOn = anim; a.setPossessedMovement(anim); }
                    Vector2 hd = float.IsNaN(tr.heading) ? Vector2.zero : new Vector2(Mathf.Cos(tr.heading), Mathf.Sin(tr.heading));
                    Vector2 dir = hd.sqrMagnitude > 0.5f ? hd : (tr.vel.sqrMagnitude > 0.01f ? tr.vel.normalized : (goal - cur).normalized);
                    // Worldfall routes a walking body towards next_step_position.
                    if (anim && dir.sqrMagnitude > 0.5f) a.next_step_position = a.current_position + dir;
                    if (hd.sqrMagnitude > 0.5f)
                    {
                        // LookAt overrides Worldfall's own turning (walking or standing): face exactly as drawn on the host.
                        WorldfallBridge.SetLookAt(a, a.current_position + hd * 3f);
                        tr.lookSet = true;
                        WorldfallBridge.SetHeading(a, tr.heading);   // no lag behind the host's own (already smoothed) turning
                    }
                    else if (tr.lookSet) { WorldfallBridge.ClearLookAt(a); tr.lookSet = false; }
                }
                catch (Exception e) { Log.Warn("live sync steer " + kv.Key + ": " + e.Message); StopAnim(tr); _done.Add(kv.Key); }
            }
            foreach (long id in _done) _tracks.Remove(id);
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
            foreach (long id in oldTracks) Untrack(id);

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

            DropLocalBuildings(now);

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
                    if (lb == null || !lb.isAlive()) continue;
                    long lid = lb.getID();
                    if (_bcSeen.Contains(lid)) continue;
                    if (lb.isOnRemove()) { if (!_removing.ContainsKey(lb)) _removing[lb] = now; continue; }
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

        private readonly Dictionary<long, float> _localB = new Dictionary<long, float>();

        /// <summary>Buildings our own simulation made (guest-range ids) can never be the host's: gone after 3 s.</summary>
        private void DropLocalBuildings(float now)
        {
            var seen = new HashSet<long>();
            var doomed = new List<Building>();
            foreach (Building lb in World.world.buildings.getSimpleList())
            {
                if (lb == null || !lb.isAlive() || lb.isOnRemove()) continue;
                long lid = lb.getID();
                if (lid < GuestIdOffset) continue;
                seen.Add(lid);
                if (!_localB.TryGetValue(lid, out float since)) _localB[lid] = now;
                else if (now - since > 3f) doomed.Add(lb);
            }
            var gone = new List<long>();
            foreach (long lid in _localB.Keys) if (!seen.Contains(lid)) gone.Add(lid);
            foreach (long lid in gone) _localB.Remove(lid);
            foreach (Building lb in doomed) { RemoveBuilding(lb); BuildingsRemoved++; }
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
                if (st != 2 && ruin)
                {
                    // Ruined only here (our own lightning, fire, ...): replace it with the host's intact one.
                    RemoveBuilding(lb);
                    BuildingsRemoved++;
                    _missingB[id] = now - 10f;
                    return;
                }
                if (st == 0 && lb.isUnderConstruction()) lb.completeConstruction();
                else if (st == 2 && !ruin) Invoke(lb, "startMakingRuins");   // not startDestroyBuilding: that also removes it
            }
            catch (Exception e) { Log.Warn("live sync building state: " + e.Message); }
        }

        private static FieldInfo _fiLawCached;

        private static Building LoadIgnoringBiome(BuildingData data)
        {
            WorldLawAsset law = WorldLawLibrary.world_law_roots_without_borders;
            if (law == null) return null;
            if (_fiLawCached == null) _fiLawCached = typeof(WorldLawAsset).GetField("_cached_enabled", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_fiLawCached == null) return null;
            bool was = (bool)_fiLawCached.GetValue(law);
            try
            {
                _fiLawCached.SetValue(law, true);
                return World.world.buildings.loadObject(data);
            }
            catch { return null; }
            finally { _fiLawCached.SetValue(law, was); }
        }

        /// <summary>Removes (at once) every other building standing on the tiles this one needs.</summary>
        private int ClearFootprint(BuildingData data)
        {
            BuildingAsset asset = AssetManager.buildings.get(data.asset_id);
            if (asset == null) return 0;
            BuildingFundament f = asset.fundament;
            var found = new HashSet<Building>();
            for (int i = 0; i < f.width; i++)
                for (int j = 0; j < f.height; j++)
                {
                    WorldTile t = World.world.GetTile(data.mainX - f.left + i, data.mainY - f.bottom + j);
                    Building b = t?.building;
                    if (b != null && b.getID() != data.id) found.Add(b);
                }
            foreach (Building b in found)
            {
                Invoke(b, "removeBuildingFinal");   // right away: the new one needs the tiles now
                BuildingsRemoved++;
            }
            return found.Count;
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
                        if (have != null)
                        {
                            // Never load over an id the game still knows (even one being removed): its loader
                            // would take a recycled object, fail on the duplicate id and leave it half-built.
                            bool usable = have.isAlive() && !have.isOnRemove();
                            if (usable && BData(have).asset_id == data.asset_id) continue;
                            if (usable) RemoveBuilding(have);
                            _missingB[data.id] = now;
                            continue;
                        }
                        Building loaded = World.world.buildings.loadObject(data);
                        if (loaded == null)
                        {
                            // Usually a local-only building (tree, plant, house) stands on its footprint: clear it and retry.
                            if (ClearFootprint(data) > 0) loaded = World.world.buildings.loadObject(data);
                            // Plants that spread onto a biome edge exist on the host, but loading checks the
                            // biome strictly: load it the way the "roots without borders" world law allows.
                            if (loaded == null) loaded = LoadIgnoringBiome(data);
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
                        if (have != null)
                        {
                            if (have.isAlive() && have.asset != null && have.asset.id == data.asset_id) continue;
                            if (have.isAlive()) WorldBoxApi.RemoveActor(have);
                            _missingU[data.id] = now;
                            continue;                                // loaded on a later request, once the id is free
                        }
                        Actor a = World.world.units.loadObject(data);
                        if (a != null) { units++; _loadedAt[data.id] = now; AdoptLocal(a); }
                    }
                    catch (Exception e) { Log.Warn("live sync: creature load: " + (_loadErrors++ < 2 ? e.ToString() : e.Message)); }
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

        // Buildings we started removing -> when. WorldBox finishes a removal with a scale tween,
        // which can get stuck (e.g. behind another tween), leaving the building standing for good.
        private static readonly Dictionary<Building, float> _removing = new Dictionary<Building, float>();

        private static void RemoveBuilding(Building b)
        {
            if (b == null) return;
            if (!Invoke(b, "startRemove")) Invoke(b, "startDestroyBuilding");
            if (!_removing.ContainsKey(b)) _removing[b] = Time.unscaledTime;
        }

        /// <summary>Finishes removals whose fade-out didn't complete within 2 s.</summary>
        private static void FinishRemovals()
        {
            if (_removing.Count == 0) return;
            float now = Time.unscaledTime;
            var done = new List<Building>();
            foreach (var kv in _removing)
            {
                Building b = kv.Key;
                if (b == null || !b.isAlive()) { done.Add(b); continue; }
                if (now - kv.Value < 2f) continue;
                done.Add(b);
                Invoke(b, "removeBuildingFinal");
            }
            foreach (Building b in done) _removing.Remove(b);
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

        internal static long MapStatsId(string field)
        {
            try
            {
                object ms = MapStats();
                FieldInfo f = ms?.GetType().GetField(field);
                return f != null ? (long)f.GetValue(ms) : 0;
            }
            catch { return 0; }
        }

        private static readonly string[] IdFields =
        {
            "id_unit", "id_building", "id_kingdom", "id_city", "id_culture", "id_clan", "id_alliance", "id_war", "id_plot",
            "id_book", "id_subspecies", "id_family", "id_army", "id_language", "id_religion", "id_item", "id_diplomacy",
        };

        private static Dictionary<string, long> IdCounters()
        {
            var d = new Dictionary<string, long>();
            foreach (string f in IdFields) { long v = MapStatsId(f); if (v > 0) d[f] = v; }
            return d;
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
