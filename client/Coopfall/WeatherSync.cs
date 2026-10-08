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
    /// Weather: WorldBox's clouds (rain, snow, acid, lava, lightning, ...) and Worldfall's wind gusts.
    /// Each game rolls its own clouds (where, which sprite, how fast) and its own gusts, so the host
    /// sends its clouds and its gust state about once a second ("wc"). Guests don't spawn clouds of
    /// their own: they keep copies of the host's (same type, sprite, speed and place) and take over
    /// the host's gust timing, so the same gust blows through everybody's first person at once.
    /// Worldfall's 3D storms are drawn from these clouds, and its rain/snow follows the world age
    /// (synced with the world's time), so they match too.
    /// </summary>
    public class WeatherSync
    {
        private const float Every = 1f;
        private readonly CoopSession _s;
        private float _next;

        // host: an id per live cloud (clouds are pooled, so a reused one gets a new id)
        private readonly Dictionary<Cloud, int> _ids = new Dictionary<Cloud, int>();
        private readonly Dictionary<Cloud, float> _lastAge = new Dictionary<Cloud, float>();
        private int _nextId = 1;

        // guest: host cloud id -> our copy
        private readonly Dictionary<int, Cloud> _copies = new Dictionary<int, Cloud>();
        public int CloudsMade, GustsTaken;

        private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static FieldInfo _fSpeed, _fAlive, _fLifespan, _fState, _fSprites, _fList;
        private static MethodInfo _mDie;

        public WeatherSync(CoopSession s) { _s = s; }

        private bool Ready { get { WorldSync w = CoopMod.Instance?.Sync; return w != null && !w.Disabled && _s.Cfg.liveSync && _s.Online && _s.InWorld && WorldBoxApi.WorldReady; } }

        public void Reset()
        {
            _ids.Clear(); _lastAge.Clear(); _copies.Clear(); _next = 0f;
        }

        private static void Lookup()
        {
            if (_fSpeed != null) return;
            _fSpeed = typeof(Cloud).GetField("speed", Any);
            _fAlive = typeof(Cloud).GetField("alive_time", Any);
            _fLifespan = typeof(Cloud).GetField("_lifespan", Any);
            _fState = typeof(BaseEffect).GetField("state", Any);
            _fSprites = typeof(CloudAsset).GetField("cached_sprites", Any);
            _fList = typeof(BaseEffectController).GetField("_list", Any);
            _mDie = typeof(BaseEffect).GetMethod("startToDie", Any, null, Type.EmptyTypes, null);
        }

        /// <summary>Clouds on the map that are alive (not fading out).</summary>
        private static List<Cloud> Clouds()
        {
            Lookup();
            var list = new List<Cloud>();
            object stack = R.Get(World.world, "stack_effects");
            if (!(R.Get(stack, "list") is System.Collections.IEnumerable controllers)) return list;
            foreach (object o in controllers)
            {
                if (!(o is BaseEffectController c) || c.asset == null || c.asset.id != "fx_cloud") continue;
                if (!(_fList?.GetValue(c) is List<BaseEffect> effects)) continue;
                foreach (BaseEffect e in effects)
                    if (e is Cloud cl && Active(cl) && cl.asset != null && State(cl) < 2) list.Add(cl);
            }
            return list;
        }

        private static FieldInfo _fActive, _fRenderer;

        private static bool Active(BaseEffect e)
        {
            if (_fActive == null) _fActive = typeof(BaseEffect).GetField("active", Any) ?? typeof(BaseMapObject).GetField("active", Any);
            return e != null && (_fActive == null ? e.gameObject.activeSelf : (bool)_fActive.GetValue(e));
        }

        private static SpriteRenderer Sr(BaseEffect e)
        {
            if (_fRenderer == null) _fRenderer = typeof(BaseEffect).GetField("sprite_renderer", Any);
            return (_fRenderer?.GetValue(e) as SpriteRenderer) ?? e.GetComponent<SpriteRenderer>();
        }

        private static int State(BaseEffect e) { return _fState == null ? 1 : (int)_fState.GetValue(e); }

        /// <summary>For sync reports: every cloud (type, x, y) and the gust state.</summary>
        public static JObject DiagState()
        {
            var clouds = new JArray();
            foreach (Cloud c in Clouds())
            {
                Vector3 p = c.transform.localPosition;
                clouds.Add(new JArray(c.asset.id, Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f));
            }
            var o = new JObject { ["clouds"] = clouds };
            float[] g = WorldfallBridge.GustState();
            if (g != null) o["gust"] = new JArray(g[0], g[1], g[2], g[4]);
            return o;
        }

        private static float F(FieldInfo f, object o) { return f == null ? 0f : (float)f.GetValue(o); }

        public void Tick()
        {
            if (!Ready) return;
            if (!_s.IsHost) { HoldClouds(true); return; }
            HoldClouds(false);
            float now = Time.unscaledTime;
            if (_s.OthersInRoom() == 0 || now < _next) return;
            _next = now + Every;
            try { Send(); }
            catch (Exception e) { Log.Warn("weather sync: " + e.Message); _next = now + 10f; }
        }

        private void Send()
        {
            List<Cloud> clouds = Clouds();
            var live = new HashSet<Cloud>(clouds);
            foreach (Cloud c in new List<Cloud>(_ids.Keys)) if (!live.Contains(c)) { _ids.Remove(c); _lastAge.Remove(c); }
            var sb = new StringBuilder(2048);
            sb.Append("{\"t\":\"wc\",\"room\":").Append(JsonConvert.ToString(_s.RoomId)).Append(",\"c\":[");
            bool first = true;
            foreach (Cloud c in clouds)
            {
                float age = F(_fAlive, c);
                if (!_ids.TryGetValue(c, out int id) || (_lastAge.TryGetValue(c, out float was) && age < was)) _ids[c] = id = _nextId++;
                _lastAge[c] = age;
                Sprite[] sprites = _fSprites?.GetValue(c.asset) as Sprite[];
                int sprite = sprites == null ? 0 : Math.Max(0, Array.IndexOf(sprites, Sr(c).sprite));
                Vector3 p = c.transform.localPosition;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('[').Append(id).Append(',').Append(JsonConvert.ToString(c.asset.id)).Append(',')
                  .Append(N(p.x)).Append(',').Append(N(p.y)).Append(',').Append(N(F(_fSpeed, c))).Append(',')
                  .Append(sprite).Append(',').Append(Sr(c).flipX ? 1 : 0).Append(',')
                  .Append(N(age)).Append(',').Append(N(F(_fLifespan, c))).Append(']');
            }
            sb.Append(']');
            float[] g = WorldfallBridge.GustState();
            if (g != null)
            {
                sb.Append(",\"g\":[");
                for (int i = 0; i < g.Length; i++) { if (i > 0) sb.Append(','); sb.Append(N(g[i])); }
                sb.Append(']');
            }
            sb.Append('}');
            _s.Net.SendRaw(sb.ToString(), false);
        }

        private static string N(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        // Guests: WorldBox's own cloud spawning is off (the host's clouds are copied instead).
        private static bool _held;
        private static WorldBehaviourAction _origClouds;

        private static void HoldClouds(bool hold)
        {
            if (hold == _held || AssetManager.world_behaviours == null) return;
            WorldBehaviourAsset a = AssetManager.world_behaviours.get("clouds");
            if (a == null) return;
            if (hold) { _origClouds = a.action; a.action = () => { }; }
            else if (_origClouds != null) a.action = _origClouds;
            _held = hold;
            Log.Info("weather sync: clouds " + (hold ? "come from the host" : "spawned here again"));
        }

        public void OnPacket(JObject p)
        {
            if (!Ready || _s.IsHost) return;
            try
            {
                if (p["c"] is JArray c) ApplyClouds(c);
                if (p["g"] is JArray g && WorldfallBridge.Present)
                {
                    var v = new float[g.Count];
                    for (int i = 0; i < v.Length; i++) v[i] = (float)g[i];
                    if (WorldfallBridge.SetGustState(v)) GustsTaken++;
                }
            }
            catch (Exception e) { Log.Warn("weather sync: " + e.Message); }
        }

        private void ApplyClouds(JArray arr)
        {
            Lookup();
            var seen = new HashSet<int>();
            foreach (JToken t in arr)
            {
                if (!(t is JArray a) || a.Count < 9) continue;
                int id = (int)a[0];
                seen.Add(id);
                string type = (string)a[1];
                var pos = new Vector3((float)a[2], (float)a[3], 0f);
                if (!_copies.TryGetValue(id, out Cloud c) || c == null || !Active(c) || State(c) >= 2 || c.asset?.id != type)
                {
                    CloudAsset asset = AssetManager.clouds.get(type);
                    if (asset == null) continue;
                    c = EffectsLibrary.spawn("fx_cloud", null, type) as Cloud;
                    if (c == null) continue;
                    _copies[id] = c;
                    CloudsMade++;
                    Sprite[] sprites = _fSprites?.GetValue(asset) as Sprite[];
                    int si = (int)a[5];
                    if (sprites != null && si >= 0 && si < sprites.Length) Sr(c).sprite = sprites[si];
                    Sr(c).flipX = (int)a[6] == 1;
                    _fSpeed?.SetValue(c, (float)a[4]);
                    _fLifespan?.SetValue(c, (float)a[8]);
                    _fAlive?.SetValue(c, (float)a[7]);
                    c.transform.localPosition = pos;
                    continue;
                }
                // same speed here, so it only drifts by frame timing: correct when visibly off
                if ((c.transform.localPosition - pos).sqrMagnitude > 0.25f) c.transform.localPosition = pos;
                _fAlive?.SetValue(c, (float)a[7]);
            }
            foreach (int id in new List<int>(_copies.Keys))
                if (!seen.Contains(id)) { Die(_copies[id]); _copies.Remove(id); }
            // clouds this game made itself (before the host's arrived, or replaying a cloud power):
            // the host's copy of that cloud is in the list already
            var mine = new HashSet<Cloud>(_copies.Values);
            foreach (Cloud c in Clouds()) if (!mine.Contains(c)) Die(c);
        }

        private static void Die(Cloud c)
        {
            if (c == null || !Active(c) || State(c) >= 2) return;
            try { _mDie?.Invoke(c, null); } catch { }
        }
    }
}
