using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Sync diagnostics: one trigger makes every game in the world capture, at the same moment, a
    /// screenshot and a dump of what it believes (its own creature, its copies of the other players
    /// and their name tags, every creature and building nearby with position, facing, walking, 3D
    /// heading, task). The host collects everybody's dump, compares them and logs every
    /// disagreement ("DIAG ..." lines), and writes it all to coopfall/diag/.
    ///
    /// Triggers: "/report" in the chat, the report key (F9), a file named "report.now" in the
    /// coopfall folder (for scripts), the "meet" test scenario, and automatically when this game
    /// notices something off (a remote player's name tag not drawn although they stand in front of
    /// you, or their creature far from where they are), at most once a minute.
    /// </summary>
    public class DiagSync
    {
        private const float Radius = 25f, AutoEvery = 60f;
        private readonly CoopSession _s;
        private float _nextFile, _lastAuto = -999f;
        private readonly Dictionary<string, float> _suspectSince = new Dictionary<string, float>();
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        public int Captures;
        public string LastSummary = "";

        private class Pending { public string reason; public float at; public readonly Dictionary<string, JObject> states = new Dictionary<string, JObject>(); }

        public DiagSync(CoopSession s) { _s = s; }

        private string Dir { get { string d = Path.Combine(Log.Dir ?? Application.persistentDataPath, "diag"); Directory.CreateDirectory(d); return d; } }

        // ================================================================ triggers

        public void Request(string reason)
        {
            if (!_s.Online || !_s.InWorld) { Log.Info("diag: not in a world"); return; }
            int n = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 1000000);
            // Everybody captures at the same wall-clock moment (half a second from now), not on arrival.
            long at = UtcMs() + 500;
            _s.Net.Send("diag", new JObject { ["cmd"] = "capture", ["n"] = n, ["reason"] = reason, ["at"] = at });
            Schedule(n, reason, at);
        }

        private static long UtcMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

        private readonly List<KeyValuePair<long, JObject>> _scheduled = new List<KeyValuePair<long, JObject>>();

        private void Schedule(int n, string reason, long at)
        {
            _scheduled.Add(new KeyValuePair<long, JObject>(at, new JObject { ["n"] = n, ["reason"] = reason }));
        }

        public void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextFile)
            {
                _nextFile = now + 1f;
                try
                {
                    string f = Path.Combine(Log.Dir, "report.now");
                    if (File.Exists(f))
                    {
                        string why = File.ReadAllText(f).Trim();
                        File.Delete(f);
                        Request(string.IsNullOrEmpty(why) ? "report.now" : why);
                    }
                }
                catch { }
                Watch(now);
            }
            if (Input.GetKeyDown(CoopConfig.ParseKey(_s.Cfg.reportKey, KeyCode.F9)) && !(CoopMod.Instance?.UI.Typing ?? false)) Request("report key");
            // Compare once everybody answered (or after 6 s with whoever did).
            var done = new List<int>();
            foreach (var kv in _pending)
                if (kv.Value.states.Count >= _s.OthersInRoom() + 1 || now - kv.Value.at > 6f) done.Add(kv.Key);
            foreach (int n in done) { Pending p = _pending[n]; _pending.Remove(n); if (_s.IsHost) Compare(n, p); }
        }

        /// <summary>LateUpdate, after the live sync placed everything: capture what is about to be drawn.</summary>
        public void LateTick()
        {
            for (int i = _scheduled.Count - 1; i >= 0; i--)
                if (UtcMs() >= _scheduled[i].Key)
                {
                    JObject c = _scheduled[i].Value;
                    _scheduled.RemoveAt(i);
                    Capture((int)c["n"], (string)c["reason"]);
                }
        }

        /// <summary>Notices problems by itself and captures them.</summary>
        private void Watch(float now)
        {
            if (!_s.Cfg.autoDiag || !_s.InWorld || now - _lastAuto < AutoEvery) return;
            Actor me = Mine();
            foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values)
            {
                if (!r.on || r.actor == null || !r.actor.isAlive()) continue;
                string issue = null;
                float off = Vector2.Distance(r.actor.current_position, r.target);
                if (off > 2.5f) issue = r.name + "'s creature is " + off.ToString("0.0", CultureInfo.InvariantCulture) + " tiles from where they are";
                else if (WorldfallBridge.FirstPerson && me != null && _s.Cfg.showNameTags && Vector2.Distance(me.current_position, r.actor.current_position) < 12f
                         && now - r.tagAt > 2f && InFront(r.actor))
                    issue = r.name + "'s name tag isn't drawn though they're in front of me";
                string key = r.id;
                if (issue == null) { _suspectSince.Remove(key); continue; }
                if (!_suspectSince.TryGetValue(key, out float since)) { _suspectSince[key] = now; continue; }
                if (now - since < 3f) continue;
                _suspectSince.Remove(key);
                _lastAuto = now;
                Log.Warn("diag: " + issue + " - capturing a report");
                Request("auto: " + issue);
                return;
            }
        }

        private static bool InFront(Actor a)
        {
            float yaw = WorldfallBridge.ViewYaw;
            Actor me = Mine();
            if (float.IsNaN(yaw) || me == null) return false;
            Vector2 d = a.current_position - me.current_position;
            return Vector2.Dot(d.normalized, new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw))) > 0.7f;
        }

        private static Actor Mine()
        {
            try { return ControllableUnit.isControllingUnit() ? ControllableUnit.getControllableUnit() : null; } catch { return null; }
        }

        // ================================================================ packets

        public void OnPacket(JObject p)
        {
            string cmd = (string)p["cmd"];
            try
            {
                switch (cmd)
                {
                    case "capture": Schedule((int?)p["n"] ?? 0, (string)p["reason"] ?? "?", (long?)p["at"] ?? UtcMs()); break;
                    case "state":
                        if (!_s.IsHost) break;
                        int n = (int?)p["n"] ?? 0;
                        Get(n, (string)p["reason"]).states[(string)p["id"] ?? "?"] = p["s"] as JObject;
                        break;
                    default: CoopMod.Instance?.Test?.OnCommand(cmd, p); break;
                }
            }
            catch (Exception e) { Log.Warn("diag '" + cmd + "': " + e); }
        }

        private Pending Get(int n, string reason)
        {
            if (!_pending.TryGetValue(n, out Pending p)) _pending[n] = p = new Pending { reason = reason, at = Time.unscaledTime };
            return p;
        }

        private void Capture(int n, string reason)
        {
            Captures++;
            string role = _s.IsHost ? "host" : "guest";
            string baseName = n + "-" + role + "-" + CoopSession.Slug(_s.MyName ?? "me");
            JObject state = BuildState(n, reason);
            try
            {
                File.WriteAllText(Path.Combine(Dir, baseName + ".json"), state.ToString(Formatting.Indented));
                ScreenCapture.CaptureScreenshot(Path.Combine(Dir, baseName + ".png"));
            }
            catch (Exception e) { Log.Warn("diag write: " + e.Message); }
            Log.Info("diag #" + n + " captured (" + reason + ") -> " + Path.Combine(Dir, baseName + ".*"));
            if (_s.IsHost) Get(n, reason).states[_s.MyId ?? "host"] = state;
            else _s.Net.Send("diag", new JObject { ["cmd"] = "state", ["n"] = n, ["reason"] = reason, ["s"] = state });
        }

        // ================================================================ state

        private JObject BuildState(int n, string reason)
        {
            Actor me = Mine();
            Vector2 at = me != null ? me.current_position : CameraCentre();
            float yaw = WorldfallBridge.ViewYaw;
            var st = new JObject
            {
                ["n"] = n, ["reason"] = reason, ["player"] = _s.MyName, ["id"] = _s.MyId, ["host"] = _s.IsHost,
                ["time"] = Math.Round(Time.unscaledTime, 3), ["utc"] = UtcMs(), ["fp"] = WorldfallBridge.FirstPerson,
                ["yaw"] = float.IsNaN(yaw) ? null : (JToken)Math.Round(yaw, 3), ["ref"] = new JArray(R2(at.x), R2(at.y)),
                ["screen"] = new JArray(Screen.width, Screen.height),
            };
            if (me != null) st["me"] = Unit(me);
            var remotes = new JArray();
            foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values)
            {
                var o = new JObject
                {
                    ["player"] = r.name, ["id"] = r.id, ["on"] = r.on, ["aid"] = r.aid, ["asset"] = r.asset,
                    ["target"] = new JArray(R2(r.target.x), R2(r.target.y)), ["standin"] = r.standin,
                    ["yaw"] = float.IsNaN(r.yaw) ? null : (JToken)Math.Round(r.yaw, 3), ["hp"] = r.hp,
                    ["tagAge"] = Math.Round(Time.unscaledTime - r.tagAt, 2),
                };
                if (r.actor != null && r.actor.isAlive())
                {
                    o["actor"] = Unit(r.actor);
                    bool ok = WorldfallBridge.ProjectHead(r.actor, 0.35f, out Vector2 sp, out float depth);
                    o["project"] = new JObject { ["ok"] = ok, ["x"] = R2(sp.x), ["y"] = R2(sp.y), ["depth"] = R2(depth), ["raw"] = WorldfallBridge.LastProjection };
                }
                remotes.Add(o);
            }
            st["remotes"] = remotes;
            var units = new JArray();
            foreach (Actor a in World.world.units.getSimpleList())
                if (a != null && a.isAlive() && Vector2.Distance(a.current_position, at) <= Radius) units.Add(Unit(a));
            st["units"] = units;
            var builds = new JArray();
            foreach (Building b in World.world.buildings.getSimpleList())
            {
                if (b == null || !b.isAlive() || b.isOnRemove() || b.current_tile == null) continue;
                if (Vector2.Distance(b.current_tile.posV3, at) > Radius) continue;
                var bd = (BuildingData)b.getData();
                builds.Add(new JArray(b.getID(), bd.asset_id, bd.state.ToString(), b.current_tile.pos.x, b.current_tile.pos.y));
            }
            st["buildings"] = builds;
            var tiles = new JObject();
            for (int dy = -16; dy <= 16; dy++)
                for (int dx = -16; dx <= 16; dx++)
                {
                    WorldTile t = World.world.GetTile((int)at.x + dx, (int)at.y + dy);
                    if (t != null) tiles[t.pos.x + "," + t.pos.y] = (t.main_type?.id ?? "") + "/" + (t.top_type?.id ?? "") + (t.isOnFire() ? "!" : "");
                }
            st["tiles"] = tiles;
            return st;
        }

        private static JObject Unit(Actor a)
        {
            var o = new JObject
            {
                ["id"] = a.getID(), ["a"] = a.asset?.id, ["x"] = R2(a.current_position.x), ["y"] = R2(a.current_position.y),
                ["flip"] = (bool)(R.Get(a, "flip") ?? false), ["moving"] = a.is_moving, ["hp"] = a.getHealth(),
                ["name"] = ((ActorData)a.getData()).name,
            };
            try { object ai = R.Get(a, "ai"); object task = ai == null ? null : R.Get(ai, "task"); if (task is Asset ta) o["task"] = ta.id; } catch { }
            if (WorldfallBridge.Heading(a, out float ang, out Vector2 drawn))
                o["wf"] = new JArray(R2(ang), R2(drawn.x), R2(drawn.y));
            Vector2 step = a.next_step_position - a.current_position;
            if (step.sqrMagnitude > 0.0004f) o["ns"] = R2(Mathf.Repeat(Mathf.Atan2(step.y, step.x) * Mathf.Rad2Deg, 360f));   // next-step angle (deg)
            if (R.Get(a, "_possessed_movement") is bool pm && pm) o["pm"] = true;
            if (WorldfallBridge.GetLookAt(a, out Vector2 look)) o["look"] = R2(Mathf.Repeat(Mathf.Atan2(look.y - a.current_position.y, look.x - a.current_position.x) * Mathf.Rad2Deg, 360f));
            WorldSync ws = CoopMod.Instance.Sync;
            if (ws != null && ws.TrackInfo(a.getID(), out float th, out Vector2 tv, out float tage, out bool tanim, out bool tlook))
                o["tr"] = new JArray(float.IsNaN(th) ? -1 : R2(th), R2(tv.x), R2(tv.y), R2(tage), tanim, tlook);   // guest: host heading, velocity, sample age, walk anim, LookAt set
            if (ws != null && CoopMod.Instance.Session.IsHost) o["flipAge"] = R2(Mathf.Min(ws.FlipAge(a), 999f));
            if (ControllableUnit.isControllingUnit(a)) o["mine"] = true;
            AvatarManager.Remote owner = CoopMod.Instance.Avatars.PuppetOwner(a);
            if (owner != null) o["puppetOf"] = owner.name;
            if (WorldBoxApi.IsStandin(a)) o["standin"] = true;
            return o;
        }

        private static double R2(float v) { return Math.Round(v, 2); }

        private static Vector2 CameraCentre()
        {
            Camera c = WorldBoxApi.MapCamera;
            return c != null ? (Vector2)c.transform.position : new Vector2(MapBox.width / 2f, MapBox.height / 2f);
        }

        // ================================================================ compare (host)

        private void Compare(int n, Pending p)
        {
            if (!p.states.TryGetValue(_s.MyId ?? "host", out JObject host)) return;
            var sb = new StringBuilder();
            var summary = new List<string>();
            foreach (var kv in p.states)
            {
                if (kv.Key == (_s.MyId ?? "host") || kv.Value == null) continue;
                JObject g = kv.Value;
                string who = (string)g["player"] ?? kv.Key;
                CompareUnits(host, g, who, sb, summary);
                ComparePlayers(host, g, who, sb, summary);
                CompareTiles(host, g, who, sb, summary);
                CompareBuildings(host, g, who, sb, summary);
                long dt = Math.Abs(((long?)host["utc"] ?? 0) - ((long?)g["utc"] ?? 0));
                if (dt > 80) sb.AppendLine("  (snapshots " + dt + " ms apart)");
            }
            foreach (var kv in p.states) if (kv.Value != null) CheckTags(kv.Value, sb, summary);
            string line = "DIAG #" + n + " (" + p.reason + ", " + p.states.Count + " game(s)): " + (summary.Count == 0 ? "everything matches" : string.Join("; ", summary.ToArray()));
            LastSummary = line;
            Log.Info(line);
            if (sb.Length > 0) Log.Info("DIAG #" + n + " details:\n" + sb);
            try
            {
                var all = new JObject { ["n"] = n, ["reason"] = p.reason, ["summary"] = line, ["details"] = sb.ToString() };
                foreach (var kv in p.states) all[kv.Key] = kv.Value;
                File.WriteAllText(Path.Combine(Dir, n + "-compare.json"), all.ToString(Formatting.Indented));
            }
            catch { }
        }

        private static void CompareTiles(JObject host, JObject g, string who, StringBuilder sb, List<string> summary)
        {
            if (!(host["tiles"] is JObject a) || !(g["tiles"] is JObject b)) return;
            int common = 0, diff = 0;
            var ex = new List<string>();
            foreach (var kv in a)
            {
                JToken o = b[kv.Key];
                if (o == null) continue;
                common++;
                if ((string)o == (string)kv.Value) continue;
                diff++;
                if (ex.Count < 8) ex.Add(kv.Key + " " + (string)kv.Value + " vs " + (string)o);
            }
            if (diff == 0) return;
            summary.Add(diff + " of " + common + " tiles differ on " + who);
            sb.AppendLine("  terrain on " + who + ": " + string.Join("; ", ex.ToArray()));
        }

        private static void CompareBuildings(JObject host, JObject g, string who, StringBuilder sb, List<string> summary)
        {
            var a = new Dictionary<long, JArray>(); var b = new Dictionary<long, JArray>();
            if (host["buildings"] is JArray ha) foreach (JToken t in ha) if (t is JArray x) a[(long)x[0]] = x;
            if (g["buildings"] is JArray ga) foreach (JToken t in ga) if (t is JArray x) b[(long)x[0]] = x;
            var hr = host["ref"] as JArray; var gr = g["ref"] as JArray;
            Vector2 href = new Vector2((float)hr[0], (float)hr[1]), gref = new Vector2((float)gr[0], (float)gr[1]);
            int missing = 0, extra = 0, state = 0;
            var ex = new List<string>();
            foreach (var kv in a)
            {
                Vector2 p = new Vector2((float)kv.Value[3], (float)kv.Value[4]);
                if (!b.TryGetValue(kv.Key, out JArray o)) { if (Vector2.Distance(p, gref) < Radius - 3f) { missing++; if (ex.Count < 8) ex.Add("missing " + kv.Value.ToString(Formatting.None)); } continue; }
                if ((string)o[1] != (string)kv.Value[1] || (string)o[2] != (string)kv.Value[2]) { state++; if (ex.Count < 8) ex.Add(kv.Value.ToString(Formatting.None) + " vs " + o.ToString(Formatting.None)); }
            }
            foreach (var kv in b)
            {
                Vector2 p = new Vector2((float)kv.Value[3], (float)kv.Value[4]);
                if (!a.ContainsKey(kv.Key) && Vector2.Distance(p, href) < Radius - 3f) { extra++; if (ex.Count < 8) ex.Add("only on " + who + " " + kv.Value.ToString(Formatting.None)); }
            }
            if (missing + extra + state == 0) return;
            summary.Add("buildings on " + who + ": " + missing + " missing, " + extra + " extra, " + state + " different");
            sb.AppendLine("  buildings on " + who + ": " + string.Join("; ", ex.ToArray()));
        }

        private static bool Near(JObject a, JObject b, float d)
        {
            var ra = a["ref"] as JArray; var rb = b["ref"] as JArray;
            return ra != null && rb != null && Vector2.Distance(new Vector2((float)ra[0], (float)ra[1]), new Vector2((float)rb[0], (float)rb[1])) < d;
        }

        private static Dictionary<long, JObject> ById(JObject st)
        {
            var d = new Dictionary<long, JObject>();
            if (st["units"] is JArray u) foreach (JToken t in u) if (t is JObject o) d[(long)o["id"]] = o;
            return d;
        }

        private static Vector2 P(JObject u) { return new Vector2((float)u["x"], (float)u["y"]); }

        private static void CompareUnits(JObject host, JObject g, string who, StringBuilder sb, List<string> summary)
        {
            var h = ById(host); var o = ById(g);
            var hr = host["ref"] as JArray; var gr = g["ref"] as JArray;
            Vector2 href = new Vector2((float)hr[0], (float)hr[1]), gref = new Vector2((float)gr[0], (float)gr[1]);
            int common = 0, far = 0, flips = 0, turned = 0, missing = 0, extra = 0, asset = 0;
            float worst = 0f;
            var details = new List<string>();
            foreach (var kv in h)
            {
                JObject a = kv.Value;
                if (a["puppetOf"] != null || a["mine"] != null) continue;            // players: compared below
                if (!o.TryGetValue(kv.Key, out JObject b))
                {
                    if (Vector2.Distance(P(a), gref) < Radius - 3f) { missing++; if (details.Count < 40) details.Add("missing on " + who + ": " + Desc(a)); }
                    continue;
                }
                if (b["puppetOf"] != null || b["mine"] != null) continue;
                common++;
                if ((string)a["a"] != (string)b["a"]) { asset++; details.Add("species differs: " + Desc(a) + " vs " + (string)b["a"]); }
                float d = Vector2.Distance(P(a), P(b));
                if (d > worst) worst = d;
                if (d > 1f) { far++; if (details.Count < 40) details.Add("off by " + d.ToString("0.0", CultureInfo.InvariantCulture) + ": " + Desc(a) + " vs " + Desc(b)); }
                bool moving = (bool)a["moving"] || (bool)b["moving"];
                // A turn the host made after its last update can't be here yet (updates go out 5 times a second).
                bool inTransit = ((double?)a["flipAge"] ?? 999) < 0.4;
                if (!moving && !inTransit && (bool)a["flip"] != (bool)b["flip"]) { flips++; if (details.Count < 40) details.Add("2D facing differs (both standing): " + Desc(a)); }
                if (a["wf"] is JArray wa && b["wf"] is JArray wb)
                {
                    float diff = Mathf.Abs(Mathf.DeltaAngle((float)wa[0] * Mathf.Rad2Deg, (float)wb[0] * Mathf.Rad2Deg));
                    if (diff > 60f) { turned++; if (details.Count < 40) details.Add("3D facing differs by " + (int)diff + " deg: " + Desc(a) + " | host ns " + a["ns"] + " look " + a["look"] + " pm " + a["pm"] + " | " + who + " ns " + b["ns"] + " look " + b["look"] + " pm " + b["pm"] + " tr " + (b["tr"]?.ToString(Formatting.None) ?? "-")); }
                }
            }
            foreach (var kv in o)
            {
                JObject b = kv.Value;
                if (b["puppetOf"] != null || b["mine"] != null || b["standin"] != null || h.ContainsKey(kv.Key)) continue;
                if (Vector2.Distance(P(b), href) < Radius - 3f) { extra++; if (details.Count < 40) details.Add("only on " + who + ": " + Desc(b)); }
            }
            sb.AppendLine("  " + who + ": " + common + " creatures in both, worst position " + worst.ToString("0.00", CultureInfo.InvariantCulture) + " tiles");
            foreach (string s in details) sb.AppendLine("    " + s);
            if (far > 0) summary.Add(far + " creature(s) more than 1 tile off on " + who + " (worst " + worst.ToString("0.0", CultureInfo.InvariantCulture) + ")");
            if (flips > 0) summary.Add(flips + " standing creature(s) face the other way on " + who);
            if (turned > 0) summary.Add(turned + " creature(s) turned differently in 3D on " + who);
            if (missing > 0) summary.Add(missing + " creature(s) missing on " + who);
            if (extra > 0) summary.Add(extra + " creature(s) only on " + who);
            if (asset > 0) summary.Add(asset + " species mismatch(es) on " + who);
        }

        private static string Desc(JObject u)
        {
            return (string)u["a"] + " #" + (long)u["id"] + " at " + (float)u["x"] + "," + (float)u["y"] + ((bool)u["moving"] ? " walking" : "") +
                   ((bool)u["flip"] ? " >" : " <") + (u["wf"] is JArray w ? " 3d " + (int)((float)w[0] * Mathf.Rad2Deg) : "") + (u["task"] != null ? " (" + (string)u["task"] + ")" : "");
        }

        /// <summary>Each player's own creature against the other game's copy of it (position, facing).</summary>
        private static void ComparePlayers(JObject host, JObject g, string who, StringBuilder sb, List<string> summary)
        {
            Pair(host, g, (string)host["player"], who, sb, summary);
            Pair(g, host, who, (string)host["player"], sb, summary);
        }

        private static void Pair(JObject owner, JObject viewer, string ownerName, string viewerName, StringBuilder sb, List<string> summary)
        {
            if (!(owner["me"] is JObject me)) return;
            JObject copy = null;
            if (viewer["remotes"] is JArray rs)
                foreach (JToken t in rs) if (t is JObject r && (string)r["player"] == ownerName) copy = r;
            if (copy == null || !(bool)copy["on"]) { summary.Add(viewerName + " doesn't see " + ownerName + "'s creature"); sb.AppendLine("  " + viewerName + " has no copy of " + ownerName + "'s " + Desc(me)); return; }
            if (!(copy["actor"] is JObject pa)) { summary.Add(viewerName + " has no creature for " + ownerName); return; }
            float d = Vector2.Distance(P(me), P(pa));
            sb.AppendLine("  " + ownerName + "'s " + Desc(me) + " (view yaw " + owner["yaw"] + ") - on " + viewerName + ": " + Desc(pa) +
                          ((bool?)copy["standin"] == true ? " STAND-IN" : "") + ", " + d.ToString("0.00", CultureInfo.InvariantCulture) + " tiles apart");
            if ((long)me["id"] != (long)pa["id"]) summary.Add(viewerName + " shows " + ownerName + " as another creature (#" + (long)pa["id"] + " instead of #" + (long)me["id"] + ")");
            if (d > 1f) summary.Add(ownerName + "'s creature is " + d.ToString("0.0", CultureInfo.InvariantCulture) + " tiles off on " + viewerName);
            if (owner["yaw"] != null && owner["yaw"].Type != JTokenType.Null && pa["wf"] is JArray w)
            {
                float diff = Mathf.Abs(Mathf.DeltaAngle((float)owner["yaw"] * Mathf.Rad2Deg, (float)w[0] * Mathf.Rad2Deg));
                if (diff > 45f) summary.Add(ownerName + " looks " + (int)diff + " deg away from where their body faces on " + viewerName);
            }
        }

        private static void CheckTags(JObject st, StringBuilder sb, List<string> summary)
        {
            if (!(bool)st["fp"] || !(st["remotes"] is JArray rs)) return;
            foreach (JToken t in rs)
            {
                if (!(t is JObject r) || !(bool)r["on"] || !(r["actor"] is JObject a)) continue;
                var pr = r["project"] as JObject;
                bool projected = pr != null && (bool)pr["ok"];
                double age = (double?)r["tagAge"] ?? 999;
                bool offScreen = pr != null && !string.IsNullOrEmpty((string)pr["raw"]);   // computed, just outside the view (pitched up/down)
                if (!projected && !offScreen && st["me"] is JObject me && st["yaw"] != null && st["yaw"].Type != JTokenType.Null)
                {
                    Vector2 d = P(a) - P(me);
                    float yaw = (float)st["yaw"];
                    if (d.magnitude < 12f && Vector2.Dot(d.normalized, new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw))) > 0.7f)
                    {
                        summary.Add((string)st["player"] + " can't place " + (string)r["player"] + "'s name tag (Worldfall projection failed) although they're in front");
                        sb.AppendLine("  name tag: projection failed for " + (string)r["player"] + "'s " + Desc(a) + " seen from " + Desc(me) + " (" + (string)pr?["raw"] + ")");
                    }
                }
                if (projected && age > 1.0)
                {
                    summary.Add((string)st["player"] + " doesn't draw " + (string)r["player"] + "'s name tag although it projects on screen");
                    sb.AppendLine("  name tag: " + (string)st["player"] + " -> " + (string)r["player"] + " projects at " + pr["x"] + "," + pr["y"] + " depth " + pr["depth"] + " but last drawn " + age + " s ago");
                }
            }
        }
    }
}
