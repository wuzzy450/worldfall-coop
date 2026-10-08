using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Automated two-game test, only active when WorldBox is started with "-coopfall-test".
    /// Run two games on one PC (each with its own "-coopfall-profile"), both set to connect
    /// automatically. The host then changes the world step by step (renames a kingdom, moves a
    /// border, starts a war, changes terrain, a world law, a creature's name and traits, kills
    /// a creature, possesses a creature in Worldfall's first person), and the guest possesses a
    /// creature, kills one of the host's creatures with it and hits the host's possessed
    /// creature. Both log every step as "TEST ..." in their coopfall log and take screenshots
    /// (coopfall/.../test-*.png), so the result can be checked afterwards.
    /// </summary>
    public partial class TestDriver
    {
        private readonly CoopSession _s;
        private float _t0 = -1f, _nextStep, _nextReport;
        private int _step;
        private Actor _victim;
        private int _victimHits;
        private string _shotDir;

        /// <summary>"-coopfall-test-load N": load save slot N before connecting (a world with kingdoms to test on).</summary>
        private int _loadSlot;
        private bool _loadStarted;
        public bool HoldConnect { get { return _loadSlot > 0; } }

        public static TestDriver FromCommandLine(CoopSession s)
        {
            string[] args = Environment.GetCommandLineArgs();
            TestDriver d = null;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "-coopfall-test", StringComparison.OrdinalIgnoreCase))
                {
                    Application.runInBackground = true;   // both games keep simulating side by side
                    Log.Info("TEST mode on");
                    d = new TestDriver(s);
                }
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-coopfall-scenario", StringComparison.OrdinalIgnoreCase))
                {
                    if (d == null) { Application.runInBackground = true; d = new TestDriver(s); }
                    d._scenario = args[i + 1].ToLowerInvariant();
                    Log.Info("TEST scenario " + d._scenario);
                }
            if (d != null)
                for (int i = 0; i + 1 < args.Length; i++)
                    if (string.Equals(args[i], "-coopfall-test-load", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(args[i + 1], out d._loadSlot);
            return d;
        }

        private void LoadFirst()
        {
            if (!_loadStarted)
            {
                if (!WorldBoxApi.WorldReady) return;
                _loadStarted = true;
                Log.Info("TEST loading save slot " + _loadSlot);
                SaveManager.setCurrentSlot(_loadSlot);
                R.Call0(R.Get(World.world, "save_manager"), "startLoadSlot");
                _loadedAt = Time.unscaledTime;
                return;
            }
            if (WorldBoxApi.WorldReady && Time.unscaledTime - _loadedAt > 5f)
            {
                Log.Info("TEST save slot " + _loadSlot + " loaded: " + World.world.units.Count + " creatures, " + World.world.kingdoms.Count + " kingdoms");
                _loadSlot = 0;
                if (_s.Cfg.autoConnect) _s.Connect();
            }
        }
        private float _loadedAt;

        private TestDriver(CoopSession s) { _s = s; _shotDir = Log.Dir; }

        private string _scenario = "";

        public void Tick()
        {
            Application.runInBackground = true;
            if (_loadSlot > 0) { LoadFirst(); return; }
            if (!_s.Online || !_s.InWorld || !WorldBoxApi.WorldReady || _s.OthersInRoom() == 0) { _t0 = -1f; return; }
            float now = Time.unscaledTime;
            if (_t0 < 0f) { _t0 = now; _nextStep = now + 20f; _step = 0; Log.Info("TEST start as " + (_s.IsHost ? "host" : "guest")); }
            if (_scenario == "patched") PatchedTick(now);
            if (_scenario == "meet" || _scenario == "patched") { Meet(now); return; }
            if (now >= _nextReport) { _nextReport = now + 15f; Report(); }
            if (_victim != null) KeepHitting();
            if (now < _nextStep) return;
            _nextStep = now + 15f;
            try { if (_s.IsHost) HostStep(_step++); else GuestStep(_step++); }
            catch (Exception e) { Log.Warn("TEST step " + _step + " failed: " + e); }
        }

        private void Report()
        {
            WorldBoxMod m = CoopMod.Instance;
            Log.Info("TEST report: units " + World.world.units.Count + ", cities " + World.world.cities.Count + ", kingdoms " + World.world.kingdoms.Count +
                     ", wars " + World.world.wars.Count + " | live: loaded " + m.Sync.UnitsLoaded + " killed " + m.Sync.UnitsKilled + " removed " + m.Sync.UnitsRemoved +
                     " | meta: created " + m.Meta.ObjectsCreated + " updated " + m.Meta.ObjectsUpdated + " removed " + m.Meta.ObjectsRemoved +
                     " creatures " + m.Meta.ActorsUpdated + " mismatches " + m.Meta.Mismatches + " last " + m.Meta.LastCheck +
                     " | tiles: zones " + m.Tiles.ZonesApplied + " tiles " + m.Tiles.TilesChanged +
                     " | combat: sent " + m.Combat.HitsSent + " applied " + m.Combat.HitsApplied + " kills " + m.Combat.KillsApplied + " saved " + m.Combat.DeathsPrevented +
                     " | fp " + WorldfallBridge.FirstPerson);
            Log.Info("TEST state: " + State());
            if (!_s.IsHost) Log.Info("TEST creatures vs host: " + m.Sync.TakeAccuracy());
        }

        /// <summary>What the host changed, as seen in this game (both games log it, to compare).</summary>
        private static string State()
        {
            var sb = new System.Text.StringBuilder();
            Kingdom k = AnyKingdom(0);
            sb.Append("kingdom1='").Append(k?.data.name).Append("'");
            City c = AnyCity();
            var zones = c == null ? null : R.Get(c, "zones") as List<TileZone>;
            sb.Append(" city").Append(c?.data.id).Append("zones=").Append(zones?.Count ?? -1);
            sb.Append(" wars=[");
            foreach (War w in World.world.wars) if (w != null && w.isAlive()) sb.Append(w.data.id).Append(':').Append(w.data.name).Append(w.hasEnded() ? "(ended)" : "").Append(' ');
            sb.Append("]");
            WorldTile t = World.world.GetTile(MapBox.width / 2, MapBox.height / 2);
            sb.Append(" centre=").Append(t?.main_type?.id).Append(" fire6=").Append(World.world.GetTile(MapBox.width / 2 + 6, MapBox.height / 2)?.isOnFire());
            WorldLaws wl = R.WorldLaws;
            if (wl?.dict != null && wl.dict.TryGetValue("world_law_rebellions", out PlayerOptionData o)) sb.Append(" rebellions=").Append(o.boolVal);
            Actor named = null;
            foreach (Actor a in World.world.units.getSimpleList()) if (a != null && a.isAlive() && ((ActorData)a.getData()).name == "Testy McTestface") named = a;
            sb.Append(" testy=").Append(named == null ? "none" : "#" + named.getID() + (named.hasTrait("immortal") ? "+immortal" : ""));
            MapStats ms = R.MapStats;
            if (ms != null) sb.Append(" age=").Append(ms.world_age_id).Append(" year=").Append(Date.getCurrentYear());
            return sb.ToString();
        }

        private void Shot(string name)
        {
            string path = Path.Combine(_shotDir, "test-" + name + ".png");
            try { ScreenCapture.CaptureScreenshot(path); Log.Info("TEST screenshot " + path); }
            catch (Exception e) { Log.Warn("TEST screenshot: " + e.Message); }
        }

        // ---------------------------------------------------------------- host

        private void HostStep(int step)
        {
            switch (step)
            {
                case 0:
                {
                    Kingdom k = AnyKingdom(0);
                    if (k == null) { Log.Info("TEST no kingdoms in this world: spawning a village"); SpawnVillage(); break; }
                    string name = "Coopfall Test " + UnityEngine.Random.Range(100, 999);
                    k.data.name = name;
                    Log.Info("TEST host renamed kingdom #" + k.data.id + " to '" + name + "'");
                    break;
                }
                case 1:
                {
                    City c = AnyCity();
                    if (c == null) break;
                    var zones = R.Get(c, "zones") as List<TileZone>;
                    TileZone add = null;
                    if (zones != null)
                        foreach (TileZone z in zones)
                        {
                            foreach (TileZone n in z.neighbours) if (n != null && n.city == null) { add = n; break; }
                            if (add != null) break;
                        }
                    if (add == null) { Log.Info("TEST no free zone next to city #" + c.data.id); break; }
                    R.Call(c, "addZone", new[] { typeof(TileZone) }, add);
                    Log.Info("TEST host gave zone " + add.x + "," + add.y + " to city #" + c.data.id + " (now " + zones.Count + " zones)");
                    break;
                }
                case 2:
                {
                    Kingdom a = AnyKingdom(0), b = AnyKingdom(1);
                    if (a == null || b == null) { Log.Info("TEST fewer than 2 kingdoms, no war"); break; }
                    War w = World.world.wars.newWar(a, b, AssetManager.war_types_library.get("normal"));
                    Log.Info("TEST host started war #" + w.data.id + " '" + w.data.name + "' between #" + a.data.id + " and #" + b.data.id);
                    break;
                }
                case 3:
                {
                    WorldTile c = World.world.GetTile(MapBox.width / 2, MapBox.height / 2);
                    TileType sand = AssetManager.tiles.get("sand");
                    int n = 0;
                    for (int dx = -3; dx <= 3; dx++)
                        for (int dy = -3; dy <= 3; dy++)
                        {
                            WorldTile t = World.world.GetTile(c.pos.x + dx, c.pos.y + dy);
                            if (t != null && sand != null) { t.setTileTypes(sand, null, true); n++; }
                        }
                    WorldTile f = World.world.GetTile(c.pos.x + 6, c.pos.y);
                    if (f != null) R.Call(f, "startFire", new[] { typeof(bool) }, true);
                    Log.Info("TEST host turned " + n + " tiles around " + c.pos.x + "," + c.pos.y + " into sand and lit a fire");
                    break;
                }
                case 4:
                {
                    WorldLaws wl = R.WorldLaws;
                    if (wl?.dict != null && wl.dict.TryGetValue("world_law_rebellions", out PlayerOptionData o))
                    {
                        o.boolVal = !o.boolVal;
                        Log.Info("TEST host set world law " + o.name + " = " + o.boolVal);
                    }
                    Actor a = AnyUnit(false);
                    if (a != null)
                    {
                        a.setName("Testy McTestface", false);
                        a.addTrait("immortal");
                        Log.Info("TEST host renamed creature #" + a.getID() + " and gave it 'immortal'");
                    }
                    break;
                }
                case 5:
                {
                    Actor a = AnyUnit(false);
                    if (a == null) break;
                    Log.Info("TEST host kills " + a.asset.id + " #" + a.getID() + " with lightning damage");
                    CoopMod.Instance.Combat.ApplyHit(a, a.getHealth() + 100, AttackType.Fire, null);
                    break;
                }
                case 6:
                {
                    Actor a = AnyUnit(true, 50);
                    if (a == null) { Log.Info("TEST no creature to possess"); break; }
                    ControllableUnit.setControllableCreature(a);
                    if (WorldfallBridge.Present) WorldfallBridge.ViewEnabled = true;
                    Log.Info("TEST host possesses " + a.asset.id + " #" + a.getID() + " (Worldfall present " + WorldfallBridge.Present + ")");
                    break;
                }
                case 7: Shot("host-first-person"); Log.Info("TEST host first person = " + WorldfallBridge.FirstPerson); break;
                case 10: Shot("host-late"); break;
            }
        }

        // ---------------------------------------------------------------- guest

        private void GuestStep(int step)
        {
            switch (step)
            {
                case 1:
                {
                    Actor a = AnyUnit(true);
                    if (a == null) break;
                    ControllableUnit.setControllableCreature(a);
                    Log.Info("TEST guest possesses " + a.asset.id + " #" + a.getID());
                    break;
                }
                case 2:
                {
                    if (!ControllableUnit.isControllingUnit())
                    {
                        Actor me = AnyUnit(true);
                        if (me != null) ControllableUnit.setControllableCreature(me);
                    }
                    Actor mine = ControllableUnit.isControllingUnit() ? ControllableUnit.getControllableUnit() : null;
                    if (mine == null) { Log.Info("TEST guest isn't possessing anything"); break; }
                    foreach (Actor a in World.world.units.getSimpleList())
                        if (a != null && a != mine && a.isAlive() && CoopMod.Instance.Sync.IsHostUnit(a.getID()) && CoopMod.Instance.Avatars.PuppetOwner(a) == null)
                        { _victim = a; break; }
                    if (_victim != null) Log.Info("TEST guest attacks host creature " + _victim.asset.id + " #" + _victim.getID() + " (" + _victim.getHealth() + " hp)");
                    break;
                }
                case 7:
                {
                    // The host possesses something at step 6: hit it (possessing something ourselves first).
                    if (!ControllableUnit.isControllingUnit())
                    {
                        Actor me = AnyUnit(true);
                        if (me != null) { ControllableUnit.setControllableCreature(me); Log.Info("TEST guest possesses " + me.asset.id + " #" + me.getID()); }
                    }
                    foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values)
                        if (r.on && r.actor != null && r.actor.isAlive())
                        {
                            _victim = r.actor;
                            Log.Info("TEST guest attacks " + r.name + "'s possessed " + r.asset + " #" + r.aid + " (" + r.hp + " hp)");
                        }
                    break;
                }
                case 4: Shot("guest-mid"); break;
                case 10: Shot("guest-late"); break;
            }
        }

        /// <summary>Hits the victim as my possessed creature would (through the game's getHit, so the hooks see it).</summary>
        private void KeepHitting()
        {
            Actor mine = ControllableUnit.isControllingUnit() ? ControllableUnit.getControllableUnit() : null;
            if (_victim == null || !_victim.isAlive() || mine == null || _victimHits > 40)
            {
                if (_victim != null) Log.Info("TEST guest attack over: victim alive " + (_victim.isAlive()) + " after " + _victimHits + " hits");
                _victim = null; _victimHits = 0;
                return;
            }
            if (Time.frameCount % 15 != 0) return;
            _victimHits++;
            R.Method(typeof(Actor), "getHit", typeof(float), typeof(bool), typeof(AttackType), typeof(BaseSimObject), typeof(bool), typeof(bool), typeof(bool))
             ?.Invoke(_victim, new object[] { 25f, true, AttackType.Weapon, mine, false, false, false });
        }

        // ---------------------------------------------------------------- "meet" scenario

        private static readonly string[][] MeetActions =
        {
            new[] { "host", "dwarf", "3" }, new[] { "guest", "orc", "4" }, new[] { "host", "elf", "3" }, new[] { "guest", "skeleton", "4" },
            new[] { "host", "zombie", "4" }, new[] { "guest", "cat", "3" }, new[] { "host", "crab", "5" }, new[] { "guest", "snake", "4" },
            new[] { "host", "blessing", "3" }, new[] { "guest", "curse", "3" }, new[] { "host", "madness", "4" }, new[] { "guest", "tile_sand", "6" },
            new[] { "host", "meteorite", "10" }, new[] { "guest", "fertilizer_trees", "7" }, new[] { "host", "bomb", "9" }, new[] { "guest", "demon", "6" },
        };

        private int _meetPhase, _meetAction;
        private float _meetAt;
        private Vector2 _spot;
        private long _aId, _bId;
        private string _guestId;
        // guest side
        private long _wantPossess;
        private Vector2 _lookAt;
        private float _wantSince;

        /// <summary>
        /// Host: spawns two humans side by side, possesses one, tells the guest to possess the other,
        /// both face each other; then host and guest take turns using god powers around them, and a
        /// synchronized diagnostic snapshot is compared after each one (DIAG lines in the host's log).
        /// </summary>
        private void Meet(float now)
        {
            if (!_s.IsHost) { GuestMeet(now); return; }
            if (now < _meetAt) return;
            if (_scenario == "patched" && _meetPhase == 6) { Patched(now); return; }
            switch (_meetPhase)
            {
                case 0:
                {
                    if (now - _t0 < 8f) return;
                    foreach (PlayerInfo pl in _s.Players) if (pl.id != _s.MyId && pl.room == _s.RoomId) _guestId = pl.id;
                    _spot = FindMeetingSpot();
                    WorldTile ta = World.world.GetTile((int)_spot.x - 3, (int)_spot.y), tb = World.world.GetTile((int)_spot.x + 3, (int)_spot.y);
                    Actor a = World.world.units.spawnNewUnit("human", ta, false, false, 0f);
                    Actor b = World.world.units.spawnNewUnit("human", tb, false, false, 0f);
                    if (a == null || b == null) { Log.Warn("TEST meet: couldn't spawn the two humans"); _meetPhase = 99; return; }
                    a.setName("Host Hero", false); b.setName("Guest Hero", false);
                    // Possessed creatures don't eat by themselves: start them fed, or they can starve mid-test.
                    a.setNutrition(a.getMaxNutrition()); b.setNutrition(b.getMaxNutrition());
                    _aId = a.getID(); _bId = b.getID();
                    Log.Info("TEST meet: spawned #" + _aId + " and #" + _bId + " around " + _spot.x + "," + _spot.y + " (clear, flat, 6 tiles apart)");
                    _meetPhase = 1; _meetAt = now + 4f;
                    break;
                }
                case 1:
                {
                    Actor a = WorldBoxApi.FindActor(_aId), b = WorldBoxApi.FindActor(_bId);
                    if (a == null || b == null) { Log.Warn("TEST meet: a hero died before the start"); _meetPhase = 0; _t0 = now; return; }
                    ControllableUnit.setControllableCreature(a);
                    if (WorldfallBridge.Present) WorldfallBridge.ViewEnabled = true;
                    _s.Net.Send("diag", new Newtonsoft.Json.Linq.JObject
                    {
                        ["cmd"] = "possess", ["for"] = _guestId, ["aid"] = _bId.ToString(), ["lx"] = a.current_position.x, ["ly"] = a.current_position.y,
                    });
                    Log.Info("TEST meet: host possesses #" + _aId + ", guest asked to possess #" + _bId);
                    _meetPhase = 2; _meetAt = now + 6f;
                    break;
                }
                case 2:
                    FaceEachOther();
                    _meetPhase = 6; _meetAt = now + 1.5f;   // let the new view directions reach the other game
                    _pendingReason = "standing face to face";
                    break;
                case 6:
                {
                    CoopMod.Instance.Diag.Request("meet: " + _pendingReason);
                    // Humans walking around between the two players: compare exact positions before anything else.
                    for (int i = 0; i < 5; i++)
                    {
                        WorldTile t = World.world.GetTile((int)_spot.x - 1 + i % 3, (int)_spot.y - 2 + (i / 3) * 4);
                        Actor w = t == null ? null : World.world.units.spawnNewUnit("human", t, false, false, 0f);
                        if (w != null) w.setName("Walker " + (i + 1), false);
                    }
                    Log.Info("TEST meet: 5 walkers spawned between the players");
                    _walkerChecks = 0;
                    _meetPhase = 7; _meetAt = now + 6f;
                    break;
                }
                case 7:
                    FaceEachOther();
                    _pendingReason = "walkers in front (" + (++_walkerChecks) + "/4)";
                    _meetPhase = _walkerChecks < 4 ? 8 : 5; _meetAt = now + 1.5f;
                    break;
                case 8:
                    CoopMod.Instance.Diag.Request("meet: " + _pendingReason);
                    _meetPhase = 7; _meetAt = now + 5f;
                    break;
                case 5:
                    CoopMod.Instance.Diag.Request("meet: " + _pendingReason);
                    _meetPhase = 3; _meetAt = now + 6f;
                    break;
                case 3:
                {
                    if (_meetAction >= MeetActions.Length)
                    {
                        Log.Info("TEST meet: done");
                        _meetPhase = 99;
                        return;
                    }
                    string[] act = MeetActions[_meetAction++];
                    float r = float.Parse(act[2], System.Globalization.CultureInfo.InvariantCulture);
                    float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    Vector2 at = _spot + new Vector2(1.5f, 0f) + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
                    if (act[0] == "host")
                    {
                        bool ok = CoopMod.Instance.Powers.UseLocal(act[1], World.world.GetTile((int)at.x, (int)at.y));
                        Log.Info("TEST meet: host uses " + act[1] + " at " + (int)at.x + "," + (int)at.y + (ok ? "" : " (not available)"));
                    }
                    else
                    {
                        _s.Net.Send("diag", new Newtonsoft.Json.Linq.JObject { ["cmd"] = "act", ["for"] = _guestId, ["p"] = act[1], ["x"] = (int)at.x, ["y"] = (int)at.y });
                        Log.Info("TEST meet: guest asked to use " + act[1] + " at " + (int)at.x + "," + (int)at.y);
                    }
                    _pendingReason = act[0] + " used " + act[1];
                    _meetPhase = 4; _meetAt = now + 5f;
                    break;
                }
                case 4:
                    FaceEachOther();
                    _pendingReason = "after " + _pendingReason;
                    _meetPhase = 5; _meetAt = now + 1.5f;
                    break;
            }
        }

        private string _pendingReason;
        private int _walkerChecks;

        private void FaceEachOther()
        {
            Actor a = WorldBoxApi.FindActor(_aId), b = WorldBoxApi.FindActor(_bId);
            if (a == null || b == null) return;
            Face(b.current_position);
            _s.Net.Send("diag", new Newtonsoft.Json.Linq.JObject { ["cmd"] = "face", ["for"] = _guestId, ["lx"] = a.current_position.x, ["ly"] = a.current_position.y });
        }

        private static void Face(Vector2 target)
        {
            Actor me = ControllableUnit.isControllingUnit() ? ControllableUnit.getControllableUnit() : null;
            if (me == null) return;
            Vector2 d = target - me.current_position;
            if (d.sqrMagnitude < 0.0001f) return;
            WorldfallBridge.SetViewYaw(Mathf.Atan2(d.y, d.x));
        }

        private static Vector2 FindMeetingSpot()
        {
            City c = AnyCity();
            WorldTile centre = (c != null ? c.getTile(false) : null) ?? World.world.GetTile(MapBox.width / 2, MapBox.height / 2);
            // Open, flat ground: nothing (buildings, trees, hills, water) within 5 tiles, so both players
            // see each other and the creatures between them.
            for (int r = 0; r < 120; r += 2)
                for (int i = 0; i < 32; i++)
                {
                    float ang = i / 32f * Mathf.PI * 2f;
                    int x = centre.pos.x + (int)(Mathf.Cos(ang) * r), y = centre.pos.y + (int)(Mathf.Sin(ang) * r);
                    if (OpenArea(x, y, 5)) { Log.Info("TEST meet: open spot " + x + "," + y); return new Vector2(x, y); }
                }
            Log.Warn("TEST meet: no fully open spot found, using the map centre");
            return centre.posV3;
        }

        private static bool OpenArea(int cx, int cy, int r)
        {
            string kind = null;
            for (int dx = -r - 3; dx <= r + 3; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    WorldTile t = World.world.GetTile(cx + dx, cy + dy);
                    if (!Clear(cx + dx, cy + dy)) return false;
                    string k = t.main_type?.id;                    // one height band: no hills or slopes in the way
                    if (kind == null) kind = k; else if (k != kind) return false;
                }
            return true;
        }

        private static bool Clear(int x, int y)
        {
            WorldTile t = World.world.GetTile(x, y);
            return t != null && t.Type != null && t.Type.ground && !t.Type.liquid && t.building == null && !t.Type.block;
        }

        /// <summary>Commands from the host's scenario (relayed as "diag" messages).</summary>
        public void OnCommand(string cmd, Newtonsoft.Json.Linq.JObject p)
        {
            if ((string)p["for"] != _s.MyId) return;
            Vector2 look = new Vector2((float?)p["lx"] ?? 0f, (float?)p["ly"] ?? 0f);
            if (GuestCommand(cmd, p)) return;
            switch (cmd)
            {
                case "possess":
                    long.TryParse((string)p["aid"] ?? "0", out _wantPossess);
                    _lookAt = look; _wantSince = Time.unscaledTime;
                    Log.Info("TEST meet: asked to possess #" + _wantPossess);
                    break;
                case "face": Face(look); break;
                case "act":
                {
                    string power = (string)p["p"];
                    bool ok = CoopMod.Instance.Powers.UseLocal(power, World.world.GetTile((int?)p["x"] ?? 0, (int?)p["y"] ?? 0));
                    Log.Info("TEST meet: guest uses " + power + " at " + p["x"] + "," + p["y"] + (ok ? "" : " (not available)"));
                    break;
                }
            }
        }

        private void GuestMeet(float now)
        {
            if (_wantPossess == 0) return;
            Actor a = WorldBoxApi.FindActor(_wantPossess);
            if (a == null || !a.isAlive())
            {
                if (now - _wantSince > 20f) { Log.Warn("TEST meet: creature #" + _wantPossess + " never arrived"); _wantPossess = 0; }
                return;
            }
            ControllableUnit.setControllableCreature(a);
            if (WorldfallBridge.Present) WorldfallBridge.ViewEnabled = true;
            Face(_lookAt);
            Log.Info("TEST meet: guest possesses #" + _wantPossess);
            _wantPossess = 0;
        }

        // ---------------------------------------------------------------- helpers

        private static Kingdom AnyKingdom(int index)
        {
            var list = new List<Kingdom>();
            foreach (Kingdom k in World.world.kingdoms) if (k != null && k.isAlive()) list.Add(k);
            list.Sort((a, b) => a.data.id.CompareTo(b.data.id));
            return index < list.Count ? list[index] : null;
        }

        private static City AnyCity()
        {
            City best = null;
            foreach (City c in World.world.cities) if (c != null && c.isAlive() && (best == null || c.data.id < best.data.id)) best = c;
            return best;
        }

        private static Actor AnyUnit(bool possessable, int skip = 0)
        {
            Actor best = null;
            var taken = new HashSet<long>();
            foreach (AvatarManager.Remote r in CoopMod.Instance.Avatars.Remotes.Values) taken.Add(r.aid);
            foreach (Actor a in World.world.units.getSimpleList())
            {
                if (a == null || !a.isAlive() || WorldBoxApi.IsStandin(a) || ControllableUnit.isControllingUnit(a)) continue;
                if (CoopMod.Instance.Avatars.PuppetOwner(a) != null || taken.Contains(a.getID()) || a.getID() < skip) continue;
                if (possessable && !a.canBePossessed()) continue;
                if (best == null || a.getID() < best.getID()) best = a;
            }
            return best;
        }

        private static void SpawnVillage()
        {
            WorldTile c = World.world.GetTile(MapBox.width / 2, MapBox.height / 2);
            for (int i = 0; i < 12; i++)
            {
                WorldTile t = World.world.GetTile(c.pos.x + i % 4, c.pos.y + i / 4);
                if (t != null) World.world.units.spawnNewUnit("human", t, false, false, 0f);
            }
        }
    }
}
