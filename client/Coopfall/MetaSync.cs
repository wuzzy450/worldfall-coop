using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Live sync of everything that isn't a creature's position or a building: kingdoms, cities
    /// (with their borders), wars, alliances, diplomacy, clans, cultures, religions, languages,
    /// families, subspecies, armies, plots, books and items ("wm"); each creature's name, traits,
    /// memberships (city, kingdom, clan, ...), job, level, kills and equipment ("wa"); and the
    /// world itself: time, age/era, world laws and statistics ("ww").
    ///
    /// The host fingerprints every object (its save data minus fields that tick constantly, like
    /// timers and statistics) and pushes the full save data of objects whose fingerprint changed.
    /// Every 15 s it also sends the complete fingerprint list, which guests compare with their own
    /// copies: whatever is missing or different is asked for ("wask"), whatever the host no longer
    /// has is removed. Guests apply changes in place, through the game's own setters, so there is
    /// no loading screen.
    /// </summary>
    public class MetaSync
    {
        private const float FullEvery = 15f, ActorEvery = 2f, ActorFullEvery = 20f, WorldEvery = 2f;
        private const int MaxMessageChars = 240 * 1024;

        private static readonly JsonSerializerSettings WriteSettings = new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate,
            Formatting = Formatting.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        };

        private static readonly JsonSerializerSettings SigSettings = new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate,
            Formatting = Formatting.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            ContractResolver = new SigResolver(),
        };

        private readonly CoopSession _s;
        private readonly List<Kind> _kinds = new List<Kind>();
        private readonly Dictionary<string, Kind> _byKey = new Dictionary<string, Kind>();
        public int ObjectsCreated, ObjectsUpdated, ObjectsRemoved, ActorsUpdated, Mismatches;
        public string LastCheck = "";

        public MetaSync(CoopSession s)
        {
            _s = s;
            BuildKinds();
        }

        private bool Ready { get { WorldSync w = CoopMod.Instance?.Sync; return w != null && !w.Disabled && _s.Cfg.liveSync && _s.Cfg.syncMeta && _s.Online && _s.InWorld && WorldBoxApi.WorldReady; } }

        public void Reset()
        {
            foreach (Kind k in _kinds) k.st = new KState();
            _sentRows.Clear(); _actorsPrimed = false; _nextActors = _nextActorsFull = _nextWorld = 0f;
            _askAt.Clear(); _expect.Clear(); _lawsSent = null; _nextTick = 0f;
        }

        public void ForceFull()
        {
            float soon = Time.unscaledTime + 2f;
            foreach (Kind k in _kinds) k.st.nextFull = Mathf.Min(k.st.nextFull, soon);
            _nextActorsFull = Mathf.Min(_nextActorsFull, soon);
            _lawsSent = null;
        }

        // ================================================================ kinds

        private class KState
        {
            public bool primed;
            public float next, nextFull;
            public readonly Dictionary<long, int> sent = new Dictionary<long, int>();
            public readonly HashSet<long> forced = new HashSet<long>();
            public HashSet<long> hostIds = new HashSet<long>();
            public readonly Dictionary<long, float> localSince = new Dictionary<long, float>();
        }

        private abstract class Kind
        {
            public string key;
            public float every;
            public KState st = new KState();
            public abstract IEnumerable All();
            public abstract object Find(long id);
            public abstract long IdOf(object o);
            public abstract object DataOf(object o);
            public abstract object Apply(string json, out bool created);
            public abstract void Remove(object o);
        }

        private class Kind<TObj, TData> : Kind where TObj : CoreSystemObject<TData>, new() where TData : BaseSystemData, new()
        {
            public Func<CoreSystemManager<TObj, TData>> mgr;
            public Action<TObj> afterCreate, afterUpdate;
            public Action<TObj, TData> beforeUpdate;
            public Action<TObj, TData> customUpdate;   // replaces beforeUpdate + loadData + afterUpdate
            public Action<TObj> remove;
            public Func<TData, bool> canCreate;
            public Action<TData> beforeCreate;

            public override IEnumerable All()
            {
                var list = new List<TObj>();
                foreach (TObj o in mgr()) if (o != null && o.isAlive()) list.Add(o);
                return list;
            }
            public override object Find(long id) { TObj o = mgr().get(id); return o != null && o.isAlive() ? o : null; }
            public override long IdOf(object o) { return ((TObj)o).data.id; }
            public override object DataOf(object o) { var t = (TObj)o; t.save(); return t.data; }

            public override object Apply(string json, out bool created)
            {
                created = false;
                TData d = Read<TData>(json);
                if (d == null) return null;
                TObj o = mgr().get(d.id);
                if (o != null && !o.isAlive()) return null;   // id still taken: the loader would fail half-way
                if (o == null)
                {
                    if (canCreate != null && !canCreate(d)) return null;
                    beforeCreate?.Invoke(d);
                    o = mgr().loadObject(d);
                    if (o == null) return null;
                    afterCreate?.Invoke(o);
                    created = true;
                    return o;
                }
                if (customUpdate != null) customUpdate(o, d);
                else
                {
                    beforeUpdate?.Invoke(o, d);
                    o.loadData(d);
                    afterUpdate?.Invoke(o);
                }
                return o;
            }

            public override void Remove(object o)
            {
                var t = (TObj)o;
                if (remove != null) remove(t); else mgr().removeObject(t);
            }
        }

        private void Add<TObj, TData>(string key, float every, Func<CoreSystemManager<TObj, TData>> mgr, Action<Kind<TObj, TData>> setup = null)
            where TObj : CoreSystemObject<TData>, new() where TData : BaseSystemData, new()
        {
            var k = new Kind<TObj, TData> { key = key, every = every, mgr = mgr };
            setup?.Invoke(k);
            _kinds.Add(k);
            _byKey[key] = k;
        }

        /// <summary>Same order as the game loads a save: everything an object refers to exists before it.</summary>
        private void BuildKinds()
        {
            Add<Subspecies, SubspeciesData>("subspecies", 6f, () => World.world.subspecies, k => k.beforeUpdate = (o, d) => ClearTraits(o));
            Add<Family, FamilyData>("family", 6f, () => World.world.families);
            Add<Language, LanguageData>("language", 6f, () => World.world.languages, k => k.beforeUpdate = (o, d) => ClearTraits(o));
            Add<Religion, ReligionData>("religion", 6f, () => World.world.religions, k => k.beforeUpdate = (o, d) => ClearTraits(o));
            Add<Item, ItemData>("item", 6f, () => World.world.items, k => k.remove = RemoveItem);
            Add<Book, BookData>("book", 6f, () => World.world.books);
            Add<Culture, CultureData>("culture", 6f, () => World.world.cultures, k => k.beforeUpdate = (o, d) => ClearTraits(o));
            Add<Clan, ClanData>("clan", 6f, () => World.world.clans, k => k.beforeUpdate = (o, d) => ClearTraits(o));
            Add<Kingdom, KingdomData>("kingdom", 2f, () => World.world.kingdoms, k =>
            {
                k.beforeUpdate = (o, d) => ClearTraits(o);
                k.afterCreate = o => { R.Call0(o, "load2"); BordersDirty(); };
                k.afterUpdate = o => { RelinkKingdom(o); BordersDirty(); };
            });
            Add<City, CityData>("city", 2f, () => World.world.cities, k =>
            {
                k.afterCreate = o => { o.loadLeader(); BordersDirty(); };
                k.customUpdate = UpdateCity;
            });
            Add<War, WarData>("war", 2f, () => World.world.wars, k =>
            {
                k.beforeUpdate = (o, d) =>
                {
                    if (d.died_time > 0 && !o.hasEnded()) World.world.wars.endWar(o, d.winner);
                };
            });
            Add<Army, ArmyData>("army", 2f, () => World.world.armies, k =>
            {
                k.afterCreate = o => o.loadDataCaptains();
                k.afterUpdate = o => o.loadDataCaptains();
                // Unlink cities first: a city still pointing at a removed army throws in setArmy every tick.
                k.remove = o =>
                {
                    foreach (City c in World.world.cities.list) if (c != null && c.getArmy() == o) c.setArmy(null);
                    World.world.armies.removeObject(o);
                };
            });
            Add<Alliance, AllianceData>("alliance", 2f, () => World.world.alliances, k =>
            {
                k.beforeUpdate = (o, d) => o.kingdoms_hashset.Clear();
                k.remove = o => World.world.alliances.dissolveAlliance(o);
            });
            Add<Plot, PlotData>("plot", 4f, () => World.world.plots, k =>
            {
                k.afterCreate = o => o.loadAuthors();
                k.afterUpdate = o => o.loadAuthors();
            });
            Add<DiplomacyRelation, DiplomacyRelationData>("diplomacy", 2f, () => World.world.diplomacy, k =>
            {
                k.canCreate = d => World.world.kingdoms.get(d.kingdom1_id) != null && World.world.kingdoms.get(d.kingdom2_id) != null;
                k.beforeCreate = d =>
                {
                    // Our own relation between the same two kingdoms (other id) would collide.
                    var dict = R.Get(World.world.diplomacy, "_dict") as Dictionary<string, DiplomacyRelation>;
                    if (dict != null && dict.TryGetValue(d.kingdom1_id + "_" + d.kingdom2_id, out DiplomacyRelation old) && old != null)
                        World.world.diplomacy.removeObject(old);
                };
            });
        }

        private static void ClearTraits(object o)
        {
            object traits = R.Get(o, "_traits");
            if (traits != null) R.Call0(traits, "Clear");
        }

        public static int BorderRedraws;

        private static void BordersDirty()
        {
            BorderRedraws++;
            try { R.Call0(R.Zones, "setDrawnZonesDirty"); } catch { }
        }

        private static void RelinkKingdom(Kingdom k)
        {
            City cap = World.world.cities.get(k.data.capitalID);
            if (cap != null && k.capital != cap) k.setCapital(cap);
            if (k.data.kingID.hasValue())
            {
                Actor king = World.world.units.get(k.data.kingID);
                if (king != null && king.isAlive() && k.king != king)
                {
                    k.setKing(king, true);
                    R.CallN(king, "setProfession", 2, UnitProfession.King, false);
                }
            }
            else if (k.king != null) R.Call0(k, "removeKing");
        }

        /// <summary>City: borders (zones), kingdom, leader and culture change through the game's setters.</summary>
        private static void UpdateCity(City c, CityData d)
        {
            var want = new HashSet<long>();
            if (d.zones != null) foreach (ZoneData z in d.zones) want.Add(((long)z.x << 32) | (uint)z.y);
            var zones = R.Get(c, "zones") as List<TileZone>;
            ZoneCalculator calc = R.Zones;
            if (zones != null && calc != null)
            {
                foreach (TileZone z in new List<TileZone>(zones))
                    if (!want.Remove(((long)z.x << 32) | (uint)z.y))
                        R.Call(c, "removeZone", new[] { typeof(TileZone) }, z);
                foreach (long key in want)
                {
                    TileZone z = calc.getZone((int)(key >> 32), (int)(uint)key);
                    if (z != null) R.Call(c, "addZone", new[] { typeof(TileZone) }, z);
                }
            }
            Kingdom k = d.kingdomID.hasValue() && d.kingdomID != 0 ? World.world.kingdoms.get(d.kingdomID) : WildKingdomsManager.neutral;
            if (k != null && R.Get(c, "kingdom") != k) R.Call(c, "setKingdom", new[] { typeof(Kingdom), typeof(bool) }, k, false);
            Culture cu = World.world.cultures.get(d.id_culture);
            if (cu != c.culture) R.Call(c, "setCulture", new[] { typeof(Culture) }, cu);
            Language la = World.world.languages.get(d.id_language);
            if (la != c.language) R.Call(c, "setLanguage", new[] { typeof(Language) }, la);
            Religion re = World.world.religions.get(d.id_religion);
            if (re != c.religion) R.Call(c, "setReligion", new[] { typeof(Religion) }, re);
            d.equipment = c.data.equipment;                  // item links are kept by the city itself
            d.zones = c.data.zones;
            c.setData(d);
            Actor leader = d.leaderID.hasValue() ? World.world.units.get(d.leaderID) : null;
            if (leader != null && leader.isAlive()) { if (c.leader != leader) c.setLeader(leader, false); }
            else if (!d.leaderID.hasValue() && c.leader != null) c.removeLeader();
        }

        // ================================================================ host

        private float _nextTick;

        public void Tick()
        {
            if (!Ready || !_s.IsHost) return;
            if (_s.OthersInRoom() == 0) { if (_kinds[0].st.primed) Reset(); return; }
            float now = Time.unscaledTime;
            if (now < _nextTick || _s.Net.BulkBytesQueued > 512 * 1024) return;
            _nextTick = now + 0.5f;
            foreach (Kind k in _kinds)
                if (now >= k.st.next || k.st.forced.Count > 0)
                {
                    k.st.next = now + k.every;
                    try { HostKind(k, now); }
                    catch (Exception e) { Log.Warn("meta sync " + k.key + ": " + e.Message); }
                }
            if (now >= _nextActors)
            {
                _nextActors = now + ActorEvery;
                try { HostActors(now); } catch (Exception e) { Log.Warn("meta sync creatures: " + e.Message); }
            }
            if (now >= _nextWorld)
            {
                _nextWorld = now + WorldEvery;
                try { HostWorld(); } catch (Exception e) { Log.Warn("meta sync world: " + e.Message); }
            }
        }

        private void HostKind(Kind k, float now)
        {
            KState st = k.st;
            bool full = now >= st.nextFull || !st.primed;
            if (full) st.nextFull = now + FullEvery;
            var hashes = full ? new StringBuilder() : null;
            var data = new List<string>();
            var sigs = new List<int>();
            var alive = new HashSet<long>();
            foreach (object o in k.All())
            {
                object d = k.DataOf(o);
                long id = k.IdOf(o);
                int sig = Sig(d);
                alive.Add(id);
                if (hashes != null) { if (hashes.Length > 0) hashes.Append(','); hashes.Append(id.ToString(CultureInfo.InvariantCulture)).Append(',').Append(sig); }
                bool want = st.forced.Remove(id) || (st.primed && (!st.sent.TryGetValue(id, out int old) || old != sig));
                st.sent[id] = sig;
                if (want) { data.Add(JsonConvert.SerializeObject(d, WriteSettings)); sigs.Add(sig); }
            }
            st.forced.Clear();
            var gone = new List<long>();
            foreach (long id in st.sent.Keys) if (!alive.Contains(id)) gone.Add(id);
            foreach (long id in gone) st.sent.Remove(id);
            if (!st.primed) gone.Clear();
            st.primed = true;

            // data first, then (in the last message) what's gone and the full list, so guests compare after loading
            int i = 0;
            while (i < data.Count)
            {
                var sb = Begin("wm", k.key);
                sb.Append(",\"d\":[");
                int start = i, len = 0;
                while (i < data.Count && (i == start || len + data[i].Length < MaxMessageChars)) { if (i > start) sb.Append(','); sb.Append(data[i]); len += data[i].Length; i++; }
                sb.Append("],\"s\":[");
                for (int j = start; j < i; j++) { if (j > start) sb.Append(','); sb.Append(sigs[j]); }
                sb.Append("]}");
                _s.Net.SendRaw(sb.ToString(), true);
            }
            if (gone.Count > 0 || hashes != null)
            {
                var sb = Begin("wm", k.key);
                if (gone.Count > 0) sb.Append(",\"gone\":[").Append(string.Join(",", gone.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())).Append("]");
                if (hashes != null) sb.Append(",\"full\":true,\"h\":[").Append(hashes).Append("]");
                sb.Append("}");
                _s.Net.SendRaw(sb.ToString(), true);
            }
        }

        private StringBuilder Begin(string type, string kind = null)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"t\":\"").Append(type).Append("\",\"room\":").Append(JsonConvert.ToString(_s.RoomId));
            if (kind != null) sb.Append(",\"k\":\"").Append(kind).Append('"');
            return sb;
        }

        /// <summary>A guest is missing (or has a different copy of) these objects.</summary>
        private void OnAsk(JObject p)
        {
            if (!_s.IsHost) return;
            if (p["m"] is JObject m)
                foreach (var kv in m)
                    if (_byKey.TryGetValue(kv.Key, out Kind k) && kv.Value is JArray ids)
                        foreach (JToken t in ids) k.st.forced.Add((long)t);
            if (p["a"] is JArray actors)
                foreach (JToken t in actors) _sentRows.Remove((long)t);
            CoopMod.Instance?.Tiles.OnAsk(p);
        }

        // ---------------------------------------------------------------- creatures' details

        private readonly Dictionary<long, string> _sentRows = new Dictionary<long, string>();
        private bool _actorsPrimed;
        private float _nextActors, _nextActorsFull, _nextWorld;

        private void HostActors(float now)
        {
            bool full = now >= _nextActorsFull;
            if (full) _nextActorsFull = now + ActorFullEvery;
            var rows = new List<string>();
            var alive = new HashSet<long>();
            foreach (Actor a in World.world.units.getSimpleList())
            {
                if (a == null || !a.isAlive() || WorldBoxApi.IsStandin(a)) continue;
                long id = a.getID();
                alive.Add(id);
                string row = Row(a);
                if (full || !_sentRows.TryGetValue(id, out string old) || old != row)
                    if (_actorsPrimed || full) rows.Add(row);
                _sentRows[id] = row;
            }
            var gone = new List<long>();
            foreach (long id in _sentRows.Keys) if (!alive.Contains(id)) gone.Add(id);
            foreach (long id in gone) _sentRows.Remove(id);
            _actorsPrimed = true;
            int i = 0;
            while (i < rows.Count)
            {
                var sb = Begin("wa");
                sb.Append(",\"r\":[");
                int start = i, len = 0;
                while (i < rows.Count && (i == start || len + rows[i].Length < MaxMessageChars)) { if (i > start) sb.Append(','); sb.Append(rows[i]); len += rows[i].Length; i++; }
                sb.Append("]}");
                _s.Net.SendRaw(sb.ToString(), true);
            }
        }

        /// <summary>One creature's details as a JSON array (also what guests compare their copy against).</summary>
        public static string Row(Actor a)
        {
            var traits = new List<string>();
            foreach (ActorTrait t in a.getTraits()) if (t != null) traits.Add(t.id);
            traits.Sort(StringComparer.Ordinal);
            var items = new List<string>();
            try
            {
                if (a.equipment != null)
                    foreach (Item it in a.equipment.getItems()) if (it != null) items.Add(it.data.id.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
            items.Sort(StringComparer.Ordinal);
            ActorData d = (ActorData)a.getData();
            var sb = new StringBuilder(160);
            sb.Append('[').Append(a.getID().ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(JsonConvert.ToString(d.name ?? ""))
              .Append(',').Append(Id(a.city))
              .Append(',').Append(JsonConvert.ToString(KingdomKey(a.kingdom)))
              .Append(',').Append(Id(a.clan)).Append(',').Append(Id(a.family)).Append(',').Append(Id(a.culture))
              .Append(',').Append(Id(a.religion)).Append(',').Append(Id(a.language)).Append(',').Append(Id(a.subspecies))
              .Append(',').Append(Id(a.army)).Append(',').Append(Id(a.plot))
              .Append(',').Append(a.lover != null && a.lover.isAlive() ? a.lover.getID().ToString(CultureInfo.InvariantCulture) : "-1")
              .Append(',').Append((int)a.getProfession())
              .Append(',').Append(d.level).Append(',').Append(d.experience).Append(',').Append(d.kills).Append(',').Append(d.renown)
              .Append(',').Append(JsonConvert.ToString(string.Join(",", traits.ToArray())))
              .Append(',').Append(JsonConvert.ToString(string.Join(",", items.ToArray())))
              .Append(']');
            return sb.ToString();
        }

        private static string Id<T>(CoreSystemObject<T> o) where T : BaseSystemData
        {
            return o != null && o.isAlive() ? o.data.id.ToString(CultureInfo.InvariantCulture) : "-1";
        }

        private static string KingdomKey(Kingdom k)
        {
            if (k == null) return "";
            if (k.wild) return "w:" + (k.asset != null ? k.asset.id : "");
            return k.data.id.ToString(CultureInfo.InvariantCulture);
        }

        private static Kingdom ResolveKingdom(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.StartsWith("w:")) return World.world.kingdoms_wild.get(key.Substring(2));
            return long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? World.world.kingdoms.get(id) : null;
        }

        // ---------------------------------------------------------------- world

        private string _lawsSent;

        private void HostWorld()
        {
            MapStats ms = R.MapStats;
            if (ms == null) return;
            var sb = Begin("ww");
            sb.Append(",\"time\":").Append(ms.world_time.ToString("R", CultureInfo.InvariantCulture))
              .Append(",\"age\":").Append(JsonConvert.ToString(ms.world_age_id ?? ""))
              .Append(",\"slot\":").Append(ms.world_age_slot_index)
              .Append(",\"prog\":").Append(ms.current_age_progress.ToString("R", CultureInfo.InvariantCulture))
              .Append(",\"dur\":").Append(ms.current_world_ages_duration.ToString("R", CultureInfo.InvariantCulture))
              .Append(",\"apause\":").Append(ms.is_world_ages_paused ? "true" : "false")
              .Append(",\"amul\":").Append(ms.world_ages_speed_multiplier.ToString("R", CultureInfo.InvariantCulture))
              .Append(",\"slots\":").Append(JsonConvert.SerializeObject(ms.world_ages_slots));
            var stats = new JObject();
            foreach (FieldInfo f in typeof(MapStats).GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (f.FieldType == typeof(long) && !f.Name.StartsWith("id_")) stats[f.Name] = (long)f.GetValue(ms);
            sb.Append(",\"stats\":").Append(stats.ToString(Formatting.None));
            string laws = LawsJson();
            if (laws != null && laws != _lawsSent) { sb.Append(",\"laws\":").Append(laws); _lawsSent = laws; }
            sb.Append('}');
            _s.Net.SendRaw(sb.ToString(), false);
        }

        private static string LawsJson()
        {
            WorldLaws wl = R.WorldLaws;
            if (wl?.list == null) return null;
            var arr = new JArray();
            foreach (PlayerOptionData o in wl.list)
                if (o != null) arr.Add(new JArray(o.name, o.boolVal, o.intVal, o.stringVal ?? ""));
            return arr.ToString(Formatting.None);
        }

        // ================================================================ guest

        private readonly Dictionary<string, float> _askAt = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _expect = new Dictionary<string, int>();   // kind:id -> host fingerprint we couldn't reach
        private readonly Dictionary<string, List<long>> _ask = new Dictionary<string, List<long>>();
        private readonly List<long> _askActors = new List<long>();
        private int _diffLogs;

        public void OnPacket(string t, JObject p)
        {
            if (!Ready) return;
            try
            {
                switch (t)
                {
                    case "wask": OnAsk(p); break;
                    case "wm": if (!_s.IsHost) OnMeta(p); break;
                    case "wa": if (!_s.IsHost) OnActors(p); break;
                    case "ww": if (!_s.IsHost) OnWorld(p); break;
                }
            }
            catch (Exception e) { Log.Warn("meta sync '" + t + "': " + e); }
            SendAsk();
        }

        private void OnMeta(JObject p)
        {
            if (!_byKey.TryGetValue((string)p["k"] ?? "", out Kind k)) return;
            float now = Time.unscaledTime;
            if (p["d"] is JArray data)
            {
                var sigs = p["s"] as JArray;
                for (int i = 0; i < data.Count; i++)
                {
                    string json = data[i].ToString(Formatting.None);
                    try
                    {
                        object o = k.Apply(json, out bool created);
                        if (o == null) continue;
                        if (created) ObjectsCreated++; else ObjectsUpdated++;
                        long id = k.IdOf(o);
                        k.st.localSince.Remove(id);
                        if (sigs != null && i < sigs.Count)
                        {
                            int want = (int)sigs[i];
                            object mine = k.DataOf(o);
                            if (Sig(mine) != want)
                            {
                                _expect[k.key + ":" + id] = want;
                                if (_diffLogs < 40) { _diffLogs++; Log.Info("meta sync: " + k.key + " #" + id + " still differs after loading: " + Diff(json, mine)); }
                            }
                            else _expect.Remove(k.key + ":" + id);
                        }
                    }
                    catch (Exception e) { Log.Warn("meta sync: load " + k.key + ": " + e.Message); }
                }
            }
            if (p["gone"] is JArray gone)
                foreach (JToken t in gone)
                {
                    object o = k.Find((long)t);
                    if (o != null) Remove(k, o);
                }
            if (p["h"] is JArray h) Compare(k, h, now);
        }

        private void Remove(Kind k, object o)
        {
            try { k.Remove(o); ObjectsRemoved++; if (k.key == "city" || k.key == "kingdom") BordersDirty(); }
            catch (Exception e) { Log.Warn("meta sync: remove " + k.key + ": " + e.Message); }
        }

        /// <summary>Full fingerprint list from the host: find what's missing, different or extra here.</summary>
        private void Compare(Kind k, JArray h, float now)
        {
            var host = new HashSet<long>();
            int missing = 0, differ = 0, removed = 0;
            for (int i = 0; i + 1 < h.Count; i += 2)
            {
                long id = (long)h[i];
                int sig = (int)h[i + 1];
                host.Add(id);
                object o = k.Find(id);
                if (o == null) { missing++; Ask(k.key, id, now); continue; }
                if (Sig(k.DataOf(o)) == sig) { _expect.Remove(k.key + ":" + id); continue; }
                if (_expect.TryGetValue(k.key + ":" + id, out int exp) && exp == sig) continue;   // known unreachable difference
                differ++;
                Ask(k.key, id, now);
            }
            var extra = new List<object>();
            foreach (object o in k.All())
            {
                long id = k.IdOf(o);
                if (host.Contains(id)) { k.st.localSince.Remove(id); continue; }
                // Something the host had before: it's gone there. Something only we made: give the host's own
                // version a moment to arrive (it usually makes the same city/war/family with another id).
                if (!k.st.hostIds.Contains(id))
                {
                    if (!k.st.localSince.TryGetValue(id, out float since)) { k.st.localSince[id] = now; continue; }
                    if (now - since < 10f) continue;
                }
                extra.Add(o);
            }
            foreach (object o in extra) { k.st.localSince.Remove(k.IdOf(o)); Remove(k, o); removed++; }
            k.st.hostIds = host;
            Mismatches += missing + differ;
            if (missing + differ + removed > 0)
                Log.Info("meta check " + k.key + ": host " + host.Count + ", missing " + missing + ", different " + differ + ", removed " + removed);
            LastCheck = k.key + " " + host.Count + "/" + missing + "/" + differ;
        }

        private void Ask(string kind, long id, float now)
        {
            string key = kind + ":" + id;
            if (_askAt.TryGetValue(key, out float at) && now - at < 6f) return;
            _askAt[key] = now;
            if (!_ask.TryGetValue(kind, out List<long> l)) _ask[kind] = l = new List<long>();
            if (l.Count < 200) l.Add(id);
        }

        private void SendAsk()
        {
            if (_ask.Count == 0 && _askActors.Count == 0) return;
            var m = new JObject();
            foreach (var kv in _ask) if (kv.Value.Count > 0) m[kv.Key] = new JArray(kv.Value.ConvertAll(x => (object)x).ToArray());
            var sb = Begin("wask");
            sb.Append(",\"m\":").Append(m.ToString(Formatting.None));
            if (_askActors.Count > 0) sb.Append(",\"a\":[").Append(string.Join(",", _askActors.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())).Append("]");
            sb.Append('}');
            _s.Net.SendRaw(sb.ToString(), false);
            _ask.Clear();
            _askActors.Clear();
        }

        // ---------------------------------------------------------------- creatures' details

        private void OnActors(JObject p)
        {
            if (!(p["r"] is JArray rows)) return;
            foreach (JToken t in rows)
            {
                if (!(t is JArray r) || r.Count < 20) continue;
                Actor a = WorldBoxApi.FindActor((long)r[0]);
                if (a == null || !a.isAlive() || WorldBoxApi.IsStandin(a)) continue;
                try
                {
                    if (Row(a) == r.ToString(Formatting.None)) continue;
                    ApplyRow(a, r);
                    ActorsUpdated++;
                }
                catch (Exception e) { Log.Warn("meta sync: creature #" + a.getID() + ": " + e.Message); }
            }
        }

        private static long L(JToken t) { return t == null ? -1 : (long)t; }

        private static void ApplyRow(Actor a, JArray r)
        {
            ActorData d = (ActorData)a.getData();
            string name = (string)r[1];
            if (!string.IsNullOrEmpty(name) && name != d.name) a.setName(name, false);

            City city = L(r[2]) > 0 ? World.world.cities.get(L(r[2])) : null;
            if (a.city != city && (city != null || L(r[2]) <= 0)) R.Call(a, "setCity", new[] { typeof(City) }, city);
            Kingdom k = ResolveKingdom((string)r[3]);
            if (k != null && a.kingdom != k) R.Call(a, "setKingdom", new[] { typeof(Kingdom) }, k);

            Link(L(r[4]), a.clan, World.world.clans.get, x => a.setClan(x));
            Link(L(r[5]), a.family, World.world.families.get, x => a.setFamily(x));
            Link(L(r[6]), a.culture, World.world.cultures.get, x => a.setCulture(x));
            Link(L(r[7]), a.religion, World.world.religions.get, x => a.setReligion(x));
            Link(L(r[8]), a.language, World.world.languages.get, x => a.setLanguage(x));
            Link(L(r[9]), a.subspecies, World.world.subspecies.get, x => a.setSubspecies(x));
            Link(L(r[10]), a.army, World.world.armies.get, x => a.setArmy(x));
            if (a.army != null && a.army.data.id_captain == a.getID()) a.army.loadDataCaptains();
            Link(L(r[11]), a.plot, World.world.plots.get, x => a.setPlot(x));
            long lover = L(r[12]);
            Actor la = lover > 0 ? WorldBoxApi.FindActor(lover) : null;
            if (a.lover != la && (la != null || lover <= 0)) a.setLover(la);

            var prof = (UnitProfession)(int)r[13];
            if (a.getProfession() != prof) R.CallN(a, "setProfession", 2, prof, false);
            d.level = (int)r[14];
            d.experience = (int)r[15];
            d.kills = (int)r[16];
            d.renown = (int)r[17];

            var want = new HashSet<string>(((string)r[18]).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var drop = new List<string>();
            foreach (ActorTrait t in a.getTraits()) if (t != null && !want.Remove(t.id)) drop.Add(t.id);
            foreach (string id in drop) a.removeTrait(id);
            foreach (string id in want) a.addTrait(id);

            ApplyItems(a, (string)r[19]);
            R.Call0(a, "setStatsDirty");
        }

        private static void Link<T>(long id, T current, Func<long, T> get, Action<T> set) where T : class
        {
            T want = id > 0 ? get(id) : null;
            if (want == current || (want == null && id > 0)) return;   // not here (yet): the next full list retries
            set(want);
        }

        /// <summary>
        /// Items removed while still equipped stay in the slot: every slot check then logs an error
        /// with a stack trace (many per frame), and the recycled object later turns up as another
        /// item, e.g. boots in a weapon slot. Unequip first.
        /// </summary>
        private static Func<Item, Actor> _itemActor;
        private static Action<Item, Actor> _itemActorSet;

        private static void RemoveItem(Item it)
        {
            // The holder may be dying (not in the unit list any more) but still drawn and checked.
            if (_itemActor == null) R.FastField("_actor", out _itemActor, out _itemActorSet);
            Actor holder = it.unit_has_it && _itemActor != null ? _itemActor(it) : null;
            if (holder?.equipment != null)
                foreach (ActorEquipmentSlot slot in holder.equipment)
                    if (slot.getItem() == it) { WorldSync.Unequip(slot); holder.setStatsDirty(); }
            foreach (Actor a in World.world.units.getSimpleList())
            {
                if (a?.equipment == null) continue;
                foreach (ActorEquipmentSlot slot in a.equipment)
                    if (slot.getItem() == it) { WorldSync.Unequip(slot); a.setStatsDirty(); }
            }
            World.world.items.removeObject(it);
        }

        private static void ApplyItems(Actor a, string ids)
        {
            if (a.equipment == null) return;
            var want = new HashSet<long>();
            foreach (string s in ids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) want.Add(id);
            foreach (ActorEquipmentSlot slot in a.equipment)
            {
                Item it = slot.getItem();
                if (it != null && (it.shouldbe_removed || it.data == null || !want.Remove(it.data.id))) WorldSync.Unequip(slot);
            }
            foreach (long id in want)
            {
                Item it = World.world.items.get(id);
                if (it != null && it.getAsset() != null) a.equipment.setItem(it, a);
            }
        }

        // ---------------------------------------------------------------- world

        private void OnWorld(JObject p)
        {
            MapStats ms = R.MapStats;
            if (ms == null) return;
            double time = (double?)p["time"] ?? ms.world_time;
            if (Math.Abs(ms.world_time - time) > 1.0) ms.world_time = time;

            string age = (string)p["age"];
            if (p["slots"] is JArray slots && ms.world_ages_slots != null && slots.Count == ms.world_ages_slots.Length)
                for (int i = 0; i < slots.Count; i++) ms.world_ages_slots[i] = (string)slots[i];
            ms.is_world_ages_paused = (bool?)p["apause"] ?? ms.is_world_ages_paused;
            ms.world_ages_speed_multiplier = (float?)p["amul"] ?? ms.world_ages_speed_multiplier;
            if (!string.IsNullOrEmpty(age) && age != ms.world_age_id && AssetManager.era_library.get(age) != null)
            {
                ms.world_age_id = age;
                ms.world_age_slot_index = (int?)p["slot"] ?? ms.world_age_slot_index;
                R.Call0(R.Get(World.world, "era_manager"), "loadAge");
                Log.Info("world sync: the age is now " + age);
            }
            ms.current_age_progress = (float?)p["prog"] ?? ms.current_age_progress;
            ms.current_world_ages_duration = (float?)p["dur"] ?? ms.current_world_ages_duration;

            if (p["stats"] is JObject stats)
                foreach (var kv in stats)
                {
                    FieldInfo f = typeof(MapStats).GetField(kv.Key, BindingFlags.Instance | BindingFlags.Public);
                    if (f != null && f.FieldType == typeof(long)) f.SetValue(ms, (long)kv.Value);
                }

            if (p["laws"] is JArray laws) ApplyLaws(laws);
        }

        private static void ApplyLaws(JArray laws)
        {
            WorldLaws wl = R.WorldLaws;
            if (wl?.dict == null) return;
            int changed = 0;
            foreach (JToken t in laws)
            {
                if (!(t is JArray l) || l.Count < 4) continue;
                if (!wl.dict.TryGetValue((string)l[0], out PlayerOptionData o) || o == null) continue;
                bool b = (bool)l[1];
                int iv = (int)l[2];
                string sv = (string)l[3];
                if (o.boolVal == b && o.intVal == iv && (o.stringVal ?? "") == sv) continue;
                o.boolVal = b; o.intVal = iv; o.stringVal = sv;
                try { o.on_switch?.Invoke(o); } catch (Exception e) { Log.Warn("law " + o.name + ": " + e.Message); }
                changed++;
            }
            if (changed > 0)
            {
                try { wl.updateCaches(); } catch { }
                Log.Info("world sync: " + changed + " world law(s) changed");
            }
        }

        // ================================================================ fingerprints

        private static T Read<T>(string json) where T : class
        {
            using (var sr = new StringReader(json))
            using (var jr = new JsonTextReader(sr))
                return JsonHelper.reader.Deserialize<T>(jr);
        }

        private static int Sig(object data)
        {
            return (int)Fnv(JsonConvert.SerializeObject(data, SigSettings));
        }

        public static uint Fnv(string s)
        {
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h;
        }

        /// <summary>Top-level fields that differ (for the log, to see what can't be synced).</summary>
        private static string Diff(string hostJson, object mine)
        {
            try
            {
                JObject a = JObject.Parse(JsonConvert.SerializeObject(JsonConvert.DeserializeObject(hostJson), Formatting.None));
                JObject b = JObject.Parse(JsonConvert.SerializeObject(mine, SigSettings));
                var names = new List<string>();
                foreach (var kv in b) if (!JToken.DeepEquals(kv.Value, a[kv.Key])) names.Add(kv.Key);
                foreach (var kv in a) if (b[kv.Key] == null && !SigResolver.Volatile(kv.Key)) names.Add(kv.Key + "(missing here)");
                return string.Join(", ", names.ToArray());
            }
            catch (Exception e) { return "?" + e.Message; }
        }

        /// <summary>Fingerprints skip what ticks on its own in every game (timers, statistics, ...) and sort id lists.</summary>
        private class SigResolver : DefaultContractResolver
        {
            private static readonly string[] Prefixes = { "timer", "timestamp", "total_", "deaths_", "past_", "last_", "custom_data", "cached", "_" };
            private static readonly HashSet<string> Names = new HashSet<string>
            {
                "renown", "metamorphosis", "evolutions", "left", "joined", "moved", "migrated", "equipment", "age", "births",
                "kills", "created_time", "from_db", "money", "loot", "pollen", "food_consumed", "total_food_consumed",
                "happiness", "nutrition", "stamina", "mana", "health", "x", "y", "experience", "level", "storage", "points", "durability",
            };

            public static bool Volatile(string name)
            {
                if (name == null) return true;
                string n = name.ToLowerInvariant();
                if (Names.Contains(n)) return true;
                foreach (string p in Prefixes) if (n.StartsWith(p)) return true;
                return false;
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization ms)
            {
                JsonProperty p = base.CreateProperty(member, ms);
                if (Volatile(p.PropertyName)) { p.ShouldSerialize = _ => false; return p; }
                if (p.PropertyType == typeof(List<long>)) p.ValueProvider = new Sorted<long>(p.ValueProvider, null);
                else if (p.PropertyType == typeof(List<string>)) p.ValueProvider = new Sorted<string>(p.ValueProvider, StringComparer.Ordinal);
                else if (p.PropertyType == typeof(List<ZoneData>))
                    p.ValueProvider = new Sorted<ZoneData>(p.ValueProvider, Comparer<ZoneData>.Create((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y)));
                return p;
            }
        }

        private class Sorted<T> : IValueProvider
        {
            private readonly IValueProvider _inner;
            private readonly IComparer<T> _cmp;
            public Sorted(IValueProvider inner, IComparer<T> cmp) { _inner = inner; _cmp = cmp; }
            public void SetValue(object target, object value) { _inner.SetValue(target, value); }
            public object GetValue(object target)
            {
                var l = _inner.GetValue(target) as List<T>;
                if (l == null) return null;
                var copy = new List<T>(l);
                copy.Sort(_cmp);
                return copy;
            }
        }
    }
}
