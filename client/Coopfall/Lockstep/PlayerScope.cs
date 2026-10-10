using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall is written for one player: its campaign features (crimes and bounties, family
    /// follow, army service, ...) keep their state in static fields about "you", and find "you"
    /// through WorldBoxMod.Body. Without changing Worldfall, lockstep runs them per player:
    ///
    /// - Each player has their own copy of those features' state. Outside ticks the live fields
    ///   hold this PC's player (so Worldfall's menus show your own state); in a tick, the fields
    ///   are swapped to the acting player's copy and back.
    /// - The features' entry points that Worldfall calls from one PC's frame code (a hit you
    ///   landed, a theft you tried, a menu choice) travel as relayed calls (WorldCalls) and are
    ///   replayed in a tick on every PC inside the caller's scope: WorldBoxMod.Body/Host are that
    ///   player's creature, and the copies hold that player's state. So every PC keeps the same
    ///   copy of every player's state.
    /// - Their per-frame updates run in ticks instead, once per player, in ID order.
    /// - Messages ("Wanted in ...") and sounds only play on the acting player's own PC.
    /// Every copy is reset to fresh values when an epoch loads (copies of a running world can't
    /// be sent along with the save).
    /// </summary>
    public static class PlayerScope
    {
        /// <summary>Bisecting switches: "-coopfall-ls-off <part>" (scope, ordered, restore, windups, peace, butchery).</summary>
        public static bool Off(string part)
        {
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < a.Length; i++) if (a[i] == "-coopfall-ls-off" && a[i + 1].Split(',').Length > 0 && Array.IndexOf(a[i + 1].Split(','), part) >= 0) return true;
            return false;
        }
        private static readonly bool OffScope = Off("scope"), OffOrdered = Off("ordered"), OffRestore = Off("restore"), OffWindups = Off("windups"), OffPeace = Off("peace"), OffButchery = Off("butchery");
        /// <summary>The acting player (LockstepSession.PlayerKey) while a scope is open; 0 otherwise.</summary>
        public static int Current => _stack.Count > 0 ? _stack[_stack.Count - 1].player : 0;
        /// <summary>The acting player's creature (null: none, or no scope).</summary>
        public static Actor CurrentBody => _stack.Count > 0 ? _stack[_stack.Count - 1].body : null;
        public static bool Open => _stack.Count > 0;
        /// <summary>The acting player is this PC's player (or no scope is open).</summary>
        public static bool IsLocal => _stack.Count == 0 || Current == LockstepSession.MyPlayer;

        private struct Frame { public int player; public Actor body; public bool entered; }
        private static readonly List<Frame> _stack = new List<Frame>();

        /// <summary>One per-player field: a static, or an instance field of WorldBoxMod.</summary>
        private sealed class Part
        {
            public FieldInfo f;
            public bool content;   // a collection: contents are copied (the field may be readonly)
            public bool instance;  // WorldBoxMod field
            public bool fresh;     // fresh object per player (Royal, WarMap)
            public bool ui;        // only this PC's menus use it: kept between ticks
            public object pristine;
            public List<Part> inner;   // fresh objects: their own fields
        }

        /// <summary>Fields that only hold what this PC's menus show (open pages, selection, marks, groups, view).</summary>
        private static bool UiName(string n)
        {
            foreach (string w in new[] { "Open", "Selected", "Mark", "Hover", "Scroll", "Page", "Tab", "Zoom", "Pan", "View", "Drag", "Cursor", "Picked", "Focus",
                "group", "Group", "Texture", "Tex", "Camera", "Menu", "Card", "Shown", "Toast", "Said", "Prompt", "Board", "Sort" })
                if (n.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static List<Part> InnerParts(Type t)
        {
            var l = new List<Part>();
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral) continue;
                    bool coll = f.FieldType.IsArray || typeof(IList).IsAssignableFrom(f.FieldType) || typeof(IDictionary).IsAssignableFrom(f.FieldType) || Coll(f.FieldType);
                    if (f.IsInitOnly && !coll) continue;
                    l.Add(new Part { f = f, content = coll, ui = UiName(f.Name) });
                }
            return l;
        }

        private static readonly List<Part> _parts = new List<Part>();
        private static readonly Dictionary<int, object[]> _store = new Dictionary<int, object[]>();
        private static readonly SortedSet<int> _seen = new SortedSet<int>();
        /// <summary>Whose state the live fields hold (0: this PC's player).</summary>
        private static int _live;
        private static Type _mod;
        private static PropertyInfo _instance;
        private static readonly List<string> _features = new List<string>();

        // per-tick drivers
        private static MethodInfo _royalUpdate, _peaceUpdate;
        private static FieldInfo _peaceWatch;
        private static MethodInfo _economyUpdate;
        private static MethodInfo _lawUpdate, _familyUpdate, _serviceUpdate, _serviceReset, _butcheryTick, _toast, _horn, _windUps;
        private static FieldInfo _settings, _familyFollows, _enemyWindups, _serviceFor, _royal, _warMap;
        private static readonly List<BaseSimObject> _attackers = new List<BaseSimObject>();
        private static readonly AccessTools.FieldRef<Actor, bool> _inside = AccessTools.FieldRefAccess<Actor, bool>("is_inside_building");
        private static readonly AccessTools.FieldRef<Actor, bool> _hasTarget = AccessTools.FieldRefAccess<Actor, bool>("has_attack_target");
        private static readonly AccessTools.FieldRef<Actor, BaseSimObject> _target = AccessTools.FieldRefAccess<Actor, BaseSimObject>("attack_target");

        public static void Install(Harmony h, Assembly wf)
        {
            _mod = wf.GetType("FirstPerson.WorldBoxMod", false);
            if (_mod == null) return;
            _instance = AccessTools.Property(_mod, "Instance");
            _toast = AccessTools.Method(_mod, "Toast", new[] { typeof(string) });
            _horn = AccessTools.Method(_mod, "PlayHorn", Type.EmptyTypes);
            _settings = AccessTools.Field(_mod, "Settings");
            _familyFollows = _settings == null ? null : AccessTools.Field(_settings.FieldType, "FamilyFollows");
            _enemyWindups = _settings == null ? null : AccessTools.Field(_settings.FieldType, "EnemyWindups");
            _serviceFor = AccessTools.Field(_mod, "_serviceFor");
            _royal = AccessTools.Field(_mod, "Royal");
            _warMap = AccessTools.Field(_mod, "WarMap");

            // who "you" are
            foreach (string p in new[] { "Body", "Host" })
            {
                MethodInfo g = AccessTools.PropertyGetter(_mod, p);
                if (g != null) h.Patch(g, prefix: new HarmonyMethod(typeof(PlayerScope), nameof(BodyPrefix)));
                else Log.Warn("lockstep: Worldfall's WorldBoxMod." + p + " not found: campaign features may act for the wrong player");
            }
            if (_toast != null) h.Patch(_toast, prefix: new HarmonyMethod(typeof(PlayerScope), nameof(ToastPrefix)));
            if (_horn != null) h.Patch(_horn, prefix: new HarmonyMethod(typeof(PlayerScope), nameof(OnlyLocalPrefix)));

            Type law = wf.GetType("FirstPerson.Law", false);
            Type family = wf.GetType("FirstPerson.FamilyFollow", false);
            Type service = wf.GetType("FirstPerson.Service", false);
            Type butchery = wf.GetType("FirstPerson.Butchery", false);
            Type royalT = wf.GetType("FirstPerson.Royal", false), warMapT = wf.GetType("FirstPerson.WarMap", false);

            if (law != null)
            {
                _lawUpdate = AccessTools.Method(law, "Update", new[] { typeof(Actor), typeof(float), typeof(Action<string>) });
                if (_lawUpdate != null && Statics(h, law))
                {
                    Relay(h, law, "OnKill", "OnHit", "WatchTheft", "Striking", "TookShip", "LootSeen", "SetBounty", "StandDown", "LoyaltyBoost");
                    _features.Add("law");
                }
                else Log.Warn("lockstep: Worldfall's Law.Update not found: crimes and guards stay off in lockstep");
            }
            // hired guards and kingdoms' protection: Worldfall's frame update sends them at whoever
            // threatens you (a grudge and a fight), on the owner's PC only; in ticks, per player
            Type economy = wf.GetType("FirstPerson.Economy", false), hireling = wf.GetType("FirstPerson.Hireling", false);
            if (economy != null)
            {
                _economyUpdate = AccessTools.Method(economy, "Update", new[] { typeof(Actor), typeof(float), typeof(Action<string>) });
                MethodInfo hirelingOf = AccessTools.Method(economy, "HirelingOf", new[] { typeof(Actor) });
                FieldInfo hid = hireling == null ? null : AccessTools.Field(hireling, "Id");
                if (_economyUpdate != null && hirelingOf != null && hid != null && Statics(h, economy))
                {
                    WorldCalls.Codec(hireling, o => (long)hid.GetValue(o), v => hirelingOf.Invoke(null, new object[] { World.world.units.get(v) }));
                    Relay(h, economy, "Hire", "Dismiss", "Protect", "SetProtection");
                    _features.Add("hired guards and protection");
                }
                else { _economyUpdate = null; Log.Warn("lockstep: Worldfall's Economy.Update not found: hired guards stay off in lockstep"); }
            }
            if (family != null)
            {
                _familyUpdate = AccessTools.Method(family, "Update", new[] { typeof(Actor), typeof(bool), typeof(Action<string>) });
                if (_familyUpdate != null && _familyFollows != null && Statics(h, family))
                {
                    Relay(h, family, "Give", "ShareFood", "SettleHere");
                    _features.Add("family follow");
                }
                else Log.Warn("lockstep: Worldfall's FamilyFollow.Update not found: family follow stays off in lockstep");
            }
            if (service != null && royalT != null && warMapT != null)
            {
                _serviceUpdate = AccessTools.Method(service, "Update", new[] { typeof(Actor), royalT, warMapT, typeof(float), typeof(Action<string>), typeof(Action) });
                _serviceReset = AccessTools.Method(service, "Reset", Type.EmptyTypes);
                if (_serviceUpdate != null && _serviceReset != null && _royal != null && _warMap != null && Statics(h, service))
                {
                    Relay(h, service, "Enlist", "Discharge", "AddKills", "OnKill", "CommandArmy", "MoveSite", "GiveUpCommission", "Found");
                    if (_serviceFor != null) _parts.Add(new Part { f = _serviceFor, instance = true });
                    _features.Add("army service");
                }
                else Log.Warn("lockstep: Worldfall's Service.Update not found: army service stays off in lockstep");
            }
            if (_royal != null && royalT != null)
            {
                _parts.Add(new Part { f = _royal, instance = true, fresh = true });
                // a king's council: campaigns, raids, storms, deeds, diplomacy (one Royal per player)
                _royalUpdate = AccessTools.Method(royalT, "Update", new[] { typeof(float), typeof(Action<string>) });
                Type peace = wf.GetType("FirstPerson.Peace", false);
                _peaceUpdate = peace == null ? null : AccessTools.Method(peace, "Update", new[] { typeof(float) });
                _peaceWatch = peace == null ? null : AccessTools.Field(peace, "_watchIn");
                if (_royalUpdate != null && _peaceUpdate != null)
                {
                    // grudges between kingdoms belong to the world: once per tick, outside any player
                    h.Patch(_peaceUpdate, prefix: new HarmonyMethod(typeof(PlayerScope), nameof(PeacePrefix)));
                    WorldfallInTick.SwapClockOf(h, royalT);
                    WorldfallInTick.SwapClockOf(h, peace);
                    Relay(h, royalT, "DeclareWar", "SendArmy", "Recall", "SendTo", "LeaveCampaign", "Unleash", "MakePeace", "ProposeAlliance",
                        "SendGift", "DemandTribute", "Levy", "Rally", "Defend", "TouchObelisk", "CallStorm");
                    _features.Add("king's council");
                }
                else { _royalUpdate = null; Log.Warn("lockstep: Worldfall's Royal.Update/Peace.Update not found: the king's council stays off in lockstep"); }
            }
            if (_warMap != null && warMapT != null)
            {
                _parts.Add(new Part { f = _warMap, instance = true, fresh = true });
                InstallWarMap(h, wf, warMapT, royalT);
            }
            // quests and trials, naming newborns, kin tracking
            try { CivilInTick.Install(h, wf, _features); }
            catch (Exception e) { Log.Error("lockstep: Worldfall's civil life (quests, births, kin) not per player: " + e); }
            // conversations keep per-player memory (petitions, refusals, small talk turns): per player
            Type dialogue = wf.GetType("FirstPerson.Dialogue", false);
            if (dialogue != null && Statics(h, dialogue)) _features.Add("conversation memory");
            try { TownsInTick.Install(h, wf, _features); }
            catch (Exception e) { Log.Error("lockstep: Worldfall's towns and day clock not shared: " + e); }
            try { BelongingsInTick.Install(h, wf, _features); }
            catch (Exception e) { Log.Error("lockstep: Worldfall's bag and gear not shared: " + e); }
            try { TalkInTick.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall's conversation choices not shared: " + e); }
            try { TickRandom.Install(h, wf); }
            catch (Exception e) { Log.Error("lockstep: Worldfall's random generators not tied to the tick: " + e); }
            foreach (string f in new[] { "Orders", "Charge" })
            {
                FieldInfo fi = AccessTools.Field(_mod, f);
                if (fi != null && !fi.IsInitOnly) _parts.Add(new Part { f = fi, instance = true });
            }
            // enemies near a first-person player hold their swing a moment (so it can be parried):
            // in ticks, for the creatures attacking each player
            Type windUps = wf.GetType("FirstPerson.WindUps", false);
            _windUps = windUps == null ? null : AccessTools.Method(windUps, "Update", new[] { typeof(Actor), typeof(List<BaseSimObject>), typeof(bool) });
            if (_windUps != null && _enemyWindups != null && Statics(h, windUps)) _features.Add("enemy wind-ups");
            else { _windUps = null; Log.Warn("lockstep: Worldfall's WindUps.Update not found: enemy wind-ups stay off in lockstep"); }
            if (butchery != null)
            {
                // carcasses belong to the world: they age in ticks, one cut is one relayed call
                _butcheryTick = AccessTools.Method(butchery, "Tick", new[] { typeof(float) });
                Type carcass = AccessTools.Inner(butchery, "Carcass");
                MethodInfo of = AccessTools.Method(butchery, "Of", new[] { typeof(long) });
                FieldInfo id = carcass == null ? null : AccessTools.Field(carcass, "Id");
                if (_butcheryTick != null && of != null && id != null)
                {
                    h.Patch(_butcheryTick, prefix: new HarmonyMethod(typeof(PlayerScope), nameof(TickOnlyPrefix)));
                    WorldCalls.Codec(carcass, o => (long)id.GetValue(o), v => of.Invoke(null, new object[] { v }));
                    WorldfallInTick.SwapClockOf(h, butchery);
                    Relay(h, butchery, "Cut");
                    _features.Add("butchery");
                }
                else Log.Warn("lockstep: Worldfall's Butchery not hooked: cutting carcasses stays off in lockstep");
            }
            foreach (Part p in _parts)
            {
                if (p.instance && p.fresh) { WorldCalls.PerPlayer(p.f.FieldType, () => p.f.GetValue(Mod())); p.inner = InnerParts(p.f.FieldType); }
                if (p.instance && !p.fresh) p.ui = UiName(p.f.Name);
            }
            LockstepClock.BeforeTick += t => { if (LockstepControl.Running) RestoreShared(); };
            LockstepClock.AfterTick += t => { if (LockstepControl.Running) SnapShared(); };
            Log.Info("lockstep: Worldfall per player: " + (_features.Count > 0 ? string.Join(", ", _features.ToArray()) : "nothing") + " (" + _parts.Count + " values per player)");
        }

        /// <summary>This static field is kept per player.</summary>
        public static bool IsPart(FieldInfo f)
        {
            foreach (Part p in _parts) if (p.f == f) return true;
            return false;
        }

        /// <summary>A feature's static fields become per player; its clock reads become the tick clock.</summary>
        internal static bool Statics(Harmony h, Type t)
        {
            var types = new List<Type> { t };
            for (int i = 0; i < types.Count; i++) types.AddRange(types[i].GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (Type x in types)
            {
                if (x.IsGenericTypeDefinition) continue;
                foreach (FieldInfo f in x.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || f.Name.Contains("egistered") || f.Name.Contains("nstalled") || f.Name.EndsWith("ForTest")) continue;
                    object v = f.GetValue(null);
                    bool coll = v is IList || v is IDictionary || (v != null && Coll(v.GetType()));
                    if (f.IsInitOnly && !coll) continue;   // scratch builders, colors, tables
                    if (!f.IsInitOnly && v != null && !coll && !Immutable(f.FieldType) && x != t) continue;
                    _parts.Add(new Part { f = f, content = coll, pristine = coll ? Clone(v) : v, ui = UiName(f.Name) });
                }
            }
            WorldfallInTick.SwapClockOf(h, t);
            return true;
        }

        private static bool Immutable(Type t) => t.IsValueType || t == typeof(string) || t.IsEnum;

        private static bool Coll(Type t) => t.IsGenericType && AccessTools.Method(t, "Clear", Type.EmptyTypes) != null && typeof(IEnumerable).IsAssignableFrom(t)
            && Array.Exists(t.GetMethods(), m => m.Name == "Add" && m.GetParameters().Length == 1);

        internal static void Relay(Harmony h, Type t, params string[] names)
        {
            foreach (string n in names)
            {
                bool any = false;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != n || m.IsAbstract || m.ContainsGenericParameters) continue;
                    if (WorldCalls.Register(h, m)) any = true;
                }
                if (!any) Log.Warn("lockstep: Worldfall's " + t.Name + "." + n + " not relayed (renamed?): it may change one PC's world only");
            }
        }

        private static object Mod() => _instance?.GetValue(null, null);

        // ------------------------------------------------------------------ war map

        private static MethodInfo _upkeepFlags, _upkeepFleet, _flagNumbered, _orderOf;
        private static FieldInfo _wmCurrent, _posts, _flags, _quick, _number, _tile, _order, _radius, _flagTarget, _targetId, _what;
        private static Type _flagType;
        private static int _orderLogs;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Serial> _serialOf = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Serial>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Serial> _postsMade = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Serial>();
        private sealed class Serial { public long n; }
        private static int _postsPending;
        private static readonly Dictionary<int, float> _numbersPending = new Dictionary<int, float>();

        /// <summary>
        /// Worldfall's war map (flags, quick orders, fleets): one WarMap per player. Its orders
        /// travel as relayed calls; the flag it hands back to the menu is a stand-in with the
        /// number (or, for quick orders, the serial) the real flag gets when the call is replayed,
        /// so later orders on it reach the real flag. Marking, groups and the chosen order stay in
        /// each player's own menu (orders name their creatures).
        /// </summary>
        private static void InstallWarMap(Harmony h, Assembly wf, Type warMapT, Type royalT)
        {
            _flagType = wf.GetType("FirstPerson.WarFlag", false);
            _upkeepFlags = AccessTools.Method(warMapT, "UpkeepFlags", new[] { typeof(Actor), typeof(float), typeof(Action<string>) });
            _upkeepFleet = AccessTools.Method(warMapT, "UpkeepFleet", new[] { typeof(Actor), typeof(float), typeof(Action<string>) });
            _flagNumbered = AccessTools.Method(warMapT, "FlagNumbered", new[] { typeof(int) });
            _orderOf = AccessTools.Method(warMapT, "OrderOf", new[] { typeof(Actor) });
            _wmCurrent = AccessTools.Field(warMapT, "_current");
            _posts = AccessTools.Field(warMapT, "Posts");
            _flags = AccessTools.Field(warMapT, "Flags");
            MethodInfo plant = AccessTools.Method(warMapT, "Plant"), post = AccessTools.Method(warMapT, "Post"), ordered = AccessTools.Method(warMapT, "Ordered");
            if (_flagType != null)
            {
                _quick = AccessTools.Field(_flagType, "Quick"); _number = AccessTools.Field(_flagType, "Number"); _tile = AccessTools.Field(_flagType, "Tile");
                _order = AccessTools.Field(_flagType, "Order"); _radius = AccessTools.Field(_flagType, "Radius"); _flagTarget = AccessTools.Field(_flagType, "Target"); _targetId = AccessTools.Field(_flagType, "TargetId");
            }
            Type shipOrder = wf.GetType("FirstPerson.ShipOrder", false);
            _what = shipOrder == null ? null : AccessTools.Field(shipOrder, "What");
            if (_upkeepFlags == null || _upkeepFleet == null || _flagNumbered == null || _orderOf == null || _posts == null || _flags == null || plant == null || post == null
                || ordered == null || _quick == null || _number == null || _tile == null || _what == null)
            {
                _upkeepFlags = null;
                Log.Warn("lockstep: Worldfall's war map changed (methods not found): war flags and fleets stay off in lockstep");
                return;
            }
            WorldfallInTick.SwapClockOf(h, warMapT);
            // a flag's band picks the nearest foe from lists filled chunk by chunk out of a
            // HashSet<MapChunk>: chunks hash by object identity, so the order (and which of two
            // foes at the same distance wins) differed between PCs (the war-flag drift, t3982)
            MethodInfo gather = AccessTools.Method(warMapT, "Gather");
            _gathered = new[] { "_fighters", "_others", "_houses" }.Select(n => AccessTools.Field(warMapT, n)).ToArray();
            if (gather != null && _gathered.All(f => f != null)) h.Patch(gather, postfix: new HarmonyMethod(typeof(PlayerScope), nameof(GatherPostfix)));
            else Log.Warn("lockstep: Worldfall's WarMap.Gather not found: a flag's band may pick different foes on each PC");
            WorldCalls.Codec(_flagType, FlagId, FlagById);
            h.Patch(ordered, prefix: new HarmonyMethod(typeof(PlayerScope), nameof(OrderedPrefix)));
            h.Patch(post, prefix: new HarmonyMethod(typeof(PlayerScope), nameof(PostCountPrefix)), postfix: new HarmonyMethod(typeof(PlayerScope), nameof(PostCountPostfix)));
            WorldCalls.Register(h, plant, PlantStandIn);
            WorldCalls.Register(h, post, PostStandIn);
            Relay(h, warMapT, "PullUp", "Move", "StandDown", "Assign", "Board", "Ferry", "LandAt", "SailTo", "Bombard", "CeaseFire", "ClearFleet", "ClearFlags", "ClearLifts");
            _features.Add("war flags and fleets");
        }

        private static FieldInfo[] _gathered;
        private static readonly Comparison<BaseSimObject> ById = (x, y) => x.getID().CompareTo(y.getID());

        private static void GatherPostfix(object __instance)
        {
            if (!LockstepClock.Active) return;
            foreach (FieldInfo f in _gathered)
            {
                if (f.GetValue(__instance) is List<Actor> actors) actors.Sort(ById);
                else if (f.GetValue(__instance) is List<Building> houses) houses.Sort(ById);
            }
        }

        private static object LiveMap() => _warMap?.GetValue(Mod());

        /// <summary>A numbered flag by its number; a quick order by -serial.</summary>
        private static long FlagId(object f)
        {
            if (!(bool)_quick.GetValue(f)) return (int)_number.GetValue(f);
            return _serialOf.TryGetValue(f, out Serial s) ? -s.n : 0;
        }

        private static object FlagById(long id)
        {
            object map = LiveMap();
            if (map == null || id == 0) return null;
            if (id > 0) return _flagNumbered.Invoke(map, new object[] { (int)id });
            foreach (object f in (IList)_posts.GetValue(map))
                if (_serialOf.TryGetValue(f, out Serial s) && s.n == -id) return f;
            return null;
        }

        private static void PostCountPrefix(object __instance, out long __state)
        {
            __state = 0;
            if (!LockstepClock.InTick) return;
            Serial c = _postsMade.GetOrCreateValue(__instance);
            __state = ++c.n;
            if (IsLocal && _postsPending > 0) _postsPending--;
        }

        private static void PostCountPostfix(object __result, long __state)
        {
            if (__state != 0 && __result != null) _serialOf.Add(__result, new Serial { n = __state });
        }

        /// <summary>Plant(tile, kingdom, number), sent: the number is chosen here so the stand-in matches.</summary>
        private static object PlantStandIn(object map, object[] args)
        {
            if (args[0] == null) return null;
            foreach (int k in new List<int>(_numbersPending.Keys)) if (Time.unscaledTime - _numbersPending[k] > 3f) _numbersPending.Remove(k);
            int want = (int)args[2];
            if (((IList)_flags.GetValue(map)).Count + _numbersPending.Count >= 6) return null;
            int i = want < 1 || want > 6 || Taken(map, want) ? 1 : want;
            while (i <= 6 && Taken(map, i)) i++;
            if (i > 6) return null;
            args[2] = i;
            _numbersPending[i] = Time.unscaledTime;
            object f = Activator.CreateInstance(_flagType, true);
            _number.SetValue(f, i);
            _tile.SetValue(f, args[0]);
            return f;
        }

        private static bool Taken(object map, int n) => _numbersPending.ContainsKey(n) || _flagNumbered.Invoke(map, new object[] { n }) != null;

        /// <summary>Post(royal, host, warriors, tile, order, radius, target, out cantReach), sent.</summary>
        private static object PostStandIn(object map, object[] args)
        {
            if (args[3] == null || args[1] == null) return null;
            Serial c = _postsMade.GetOrCreateValue(map);
            object f = Activator.CreateInstance(_flagType, true);
            _quick.SetValue(f, true);
            _tile.SetValue(f, args[3]);
            _order?.SetValue(f, args[4]);
            _radius?.SetValue(f, args[5]);
            if (args[6] is BaseSimObject t) { _flagTarget?.SetValue(f, t); _targetId?.SetValue(f, t.getID()); }
            _serialOf.Add(f, new Serial { n = c.n + ++_postsPending });
            return f;
        }

        /// <summary>Worldfall asks "does this ship have orders?" of the last war map made: every player's, in order.</summary>
        private static bool OrderedPrefix(Actor a, ref bool firing, ref bool __result)
        {
            if (!LockstepClock.Active || _orderOf == null || OffOrdered) return true;
            if (Off("orderlog") && _orderLogs < 20)
            {
                object cur = _wmCurrent.GetValue(null);
                object live = LiveMap();
                object o1 = cur == null || a == null ? null : _orderOf.Invoke(cur, new object[] { a });
                var maps = AllOf(_warMap);
                if (!ReferenceEquals(cur, live) || o1 != null || maps.Count != 1)
                { _orderLogs++; Log.Info("lockstep: ordered check tick " + LockstepClock.Tick + " in tick " + LockstepClock.InTick + " current==live " + ReferenceEquals(cur, live) + " current null " + (cur == null) + " order " + (o1 != null) + " maps " + maps.Count + " store " + _store.Count); }
            }
            firing = false;
            __result = false;
            if (a == null) return false;
            foreach (object map in AllOf(_warMap))
            {
                object o = map == null ? null : _orderOf.Invoke(map, new object[] { a });
                if (o == null) continue;
                firing = _what.GetValue(o).ToString() == "Bombard";
                __result = true;
                return false;
            }
            return false;
        }

        /// <summary>Test log: one per-player static field's value for every player this PC knows, in player order.</summary>
        public static SortedDictionary<int, object> PeekAll(FieldInfo f)
        {
            var byPlayer = new SortedDictionary<int, object>();
            int idx = -1;
            for (int i = 0; i < _parts.Count; i++) if (_parts[i].f == f) { idx = i; break; }
            if (idx < 0) return byPlayer;
            object live = f.GetValue(_parts[idx].instance ? Mod() : null);
            byPlayer[_live == 0 ? LockstepSession.MyPlayer : _live] = _parts[idx].content ? Clone(live) : live;
            foreach (KeyValuePair<int, object[]> kv in _store)
                if (kv.Value != null) byPlayer[kv.Key == 0 ? LockstepSession.MyPlayer : kv.Key] = kv.Value[idx];
            return byPlayer;
        }

        /// <summary>Test log: every player's council and war map, as this PC has them.</summary>
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            SortedDictionary<int, object> royals = ByPlayer(_royal), maps = ByPlayer(_warMap);
            var keys = new SortedSet<int>(royals.Keys);
            keys.UnionWith(maps.Keys);
            foreach (int k in keys)
            {
                sb.Append("[player ").Append(k).Append(" body ").Append(LockstepControl.BodyOf(k)?.getID().ToString() ?? "none");
                if (royals.TryGetValue(k, out object r) && r != null)
                {
                    object c = AccessTools.Field(r.GetType(), "Active")?.GetValue(r);
                    sb.Append(", campaign ");
                    if (c == null) sb.Append("none");
                    else
                    {
                        IList troops = AccessTools.Field(c.GetType(), "Troops")?.GetValue(c) as IList;
                        object target = AccessTools.Field(c.GetType(), "Target")?.GetValue(c);
                        sb.Append("vs ").Append((target as Kingdom)?.name ?? "?").Append(" troops ").Append(troops?.Count ?? -1);
                    }
                    IList raids = AccessTools.Field(r.GetType(), "Raids")?.GetValue(r) as IList;
                    sb.Append(", raids ").Append(raids?.Count ?? -1);
                    if (AccessTools.Field(r.GetType(), "Storms")?.GetValue(r) is IList storms)
                    {
                        sb.Append(", storms ").Append(storms.Count);
                        foreach (object st in storms)
                            sb.Append(" (").Append(AccessTools.Field(st.GetType(), "Kind")?.GetValue(st)).Append(" on ").Append(AccessTools.Field(st.GetType(), "TargetName")?.GetValue(st))
                              .Append(" meteors left ").Append(AccessTools.Field(st.GetType(), "MeteorsLeft")?.GetValue(st)).Append(" bolts left ").Append(AccessTools.Field(st.GetType(), "BoltsLeft")?.GetValue(st)).Append(')');
                    }
                }
                if (maps.TryGetValue(k, out object m) && m != null && _flags != null)
                {
                    sb.Append(", flags");
                    foreach (object f in (IList)_flags.GetValue(m))
                    {
                        IList band = AccessTools.Field(f.GetType(), "Band")?.GetValue(f) as IList;
                        sb.Append(' ').Append(_number.GetValue(f)).Append(':').Append(band?.Count ?? -1);
                    }
                    sb.Append(", posts ").Append(((IList)_posts.GetValue(m)).Count);
                }
                sb.Append("] ");
            }
            return sb.Length == 0 ? "nobody" : sb.ToString();
        }

        private static SortedDictionary<int, object> ByPlayer(FieldInfo f)
        {
            var byPlayer = new SortedDictionary<int, object>();
            int idx = -1;
            for (int i = 0; i < _parts.Count; i++) if (_parts[i].f == f) { idx = i; break; }
            object mod = Mod();
            if (idx < 0 || mod == null) return byPlayer;
            byPlayer[_live == 0 ? LockstepSession.MyPlayer : _live] = f.GetValue(mod);
            foreach (KeyValuePair<int, object[]> kv in _store)
                if (kv.Value != null && kv.Value[idx] != null) byPlayer[kv.Key == 0 ? LockstepSession.MyPlayer : kv.Key] = kv.Value[idx];
            return byPlayer;
        }

        /// <summary>Every player's value of a per-player WorldBoxMod field, in player order.</summary>
        private static List<object> AllOf(FieldInfo f)
        {
            var l = new List<object>();
            int idx = -1;
            for (int i = 0; i < _parts.Count; i++) if (_parts[i].f == f) { idx = i; break; }
            object mod = Mod();
            if (idx < 0 || mod == null) return l;
            var byPlayer = new SortedDictionary<int, object>();
            byPlayer[_live == 0 ? LockstepSession.MyPlayer : _live] = f.GetValue(mod);
            foreach (KeyValuePair<int, object[]> kv in _store)
                if (kv.Value != null && kv.Value[idx] != null) byPlayer[kv.Key == 0 ? LockstepSession.MyPlayer : kv.Key] = kv.Value[idx];
            l.AddRange(byPlayer.Values);
            return l;
        }


        // ------------------------------------------------------------------ scopes

        /// <summary>Runs body as the given player (0: no player; just runs it).</summary>
        public static void Run(int player, Action body)
        {
            if (player == 0 || _parts.Count == 0 && _mod == null) { body(); return; }
            Enter(player);
            try { body(); }
            finally { Leave(); }
        }

        private static void Enter(int player)
        {
            Actor b = LockstepControl.BodyOf(player);
            var fr = new Frame { player = player, body = b };
            if (b != null) { LockstepControl.EnterBody(b); fr.entered = true; }
            _stack.Add(fr);
            _seen.Add(player);
            Swap(player);
        }

        private static void Leave()
        {
            Frame fr = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            if (fr.entered) LockstepControl.LeaveBody();
            Swap(_stack.Count > 0 ? Current : 0);
        }

        private static int Key(int player) => player == LockstepSession.MyPlayer ? 0 : player;

        /// <summary>Puts this player's copy into the live fields (0: this PC's player).</summary>
        private static void Swap(int player)
        {
            int to = Key(player);
            if (to == _live || _parts.Count == 0) return;
            object mod = Mod();
            _store[_live] = Capture(mod);
            if (!_store.TryGetValue(to, out object[] s)) s = null;
            else _store.Remove(to);
            Apply(mod, s);
            _live = to;
            // WarMap remembers the last one made (for ship labels): the live one
            if (_wmCurrent != null && mod != null) _wmCurrent.SetValue(null, _warMap.GetValue(mod));
        }

        private static object[] Capture(object mod)
        {
            var s = new object[_parts.Count];
            for (int i = 0; i < _parts.Count; i++)
            {
                Part p = _parts[i];
                if (p.instance && mod == null) continue;
                object v = p.f.GetValue(p.instance ? mod : null);
                s[i] = p.content ? Clone(v) : v;
            }
            return s;
        }

        /// <summary>s null: fresh values.</summary>
        private static void Apply(object mod, object[] s)
        {
            for (int i = 0; i < _parts.Count; i++)
            {
                Part p = _parts[i];
                object target = p.instance ? mod : null;
                if (p.instance && mod == null) continue;
                object v = s != null ? s[i] : p.fresh ? Fresh(p.f.FieldType) : p.pristine;
                try
                {
                    if (p.content) CopyInto(v, p.f.GetValue(target));
                    else p.f.SetValue(target, v);
                }
                catch (Exception e) { Log.Warn("lockstep: per-player value " + p.f.DeclaringType.Name + "." + p.f.Name + " not swapped: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ between ticks

        /// <summary>This PC's player's values as the last tick left them.</summary>
        private static object[] _shared;
        private static object[][] _sharedInner;

        /// <summary>
        /// Between ticks Worldfall's menus and frame code still read (and, through caches, write)
        /// this PC's player's values. A tick must only see what ticks made, so after every tick
        /// those values are noted, and before the next one they are put back (menu-only fields kept).
        /// </summary>
        private static void SnapShared()
        {
            if (_parts.Count == 0 || _live != 0) return;
            object mod = Mod();
            if (mod == null) return;
            _shared = Capture(mod);
            _sharedInner = new object[_parts.Count][];
            for (int i = 0; i < _parts.Count; i++)
            {
                Part p = _parts[i];
                if (p.inner == null || _shared[i] == null) continue;
                var snap = new object[p.inner.Count];
                for (int j = 0; j < p.inner.Count; j++)
                {
                    if (p.inner[j].ui) continue;
                    object v = p.inner[j].f.GetValue(_shared[i]);
                    snap[j] = p.inner[j].content ? Clone(v) : v;
                }
                _sharedInner[i] = snap;
            }
        }

        private static void RestoreShared()
        {
            if (_shared == null || _live != 0 || OffRestore) return;
            object mod = Mod();
            if (mod == null) return;
            for (int i = 0; i < _parts.Count; i++)
            {
                Part p = _parts[i];
                if (p.ui) continue;
                object target = p.instance ? mod : null;
                try
                {
                    if (p.inner != null)
                    {
                        object o = p.f.GetValue(target);
                        if (!ReferenceEquals(o, _shared[i])) { p.f.SetValue(target, _shared[i]); o = _shared[i]; }
                        object[] snap = _sharedInner[i];
                        if (o == null || snap == null) continue;
                        for (int j = 0; j < p.inner.Count; j++)
                        {
                            Part q = p.inner[j];
                            if (q.ui) continue;
                            if (q.content) CopyInto(snap[j], q.f.GetValue(o));
                            else q.f.SetValue(o, snap[j]);
                        }
                    }
                    else if (p.content) CopyInto(_shared[i], p.f.GetValue(target));
                    else p.f.SetValue(target, _shared[i]);
                }
                catch (Exception e)
                {
                    if (_failed.Add("restore " + p.f.Name)) Log.Warn("lockstep: couldn't put back " + p.f.DeclaringType.Name + "." + p.f.Name + " for the tick: " + e.Message);
                }
            }
        }

        private static object Fresh(Type t)
        {
            try { return Activator.CreateInstance(t, true); }
            catch (Exception e) { Log.Warn("lockstep: couldn't make a fresh " + t.Name + ": " + (e.InnerException ?? e).Message); return null; }
        }

        private static object Clone(object v)
        {
            if (v == null) return null;
            if (v is Array arr) return arr.Clone();
            try
            {
                object c = Activator.CreateInstance(v.GetType());
                CopyInto(v, c);
                return c;
            }
            catch (Exception e)
            {
                if (_failed.Add("clone " + v.GetType().Name)) Log.Warn("lockstep: can't copy a " + v.GetType().Name + " per player: " + (e.InnerException ?? e).Message);
                return v;
            }
        }

        private static void CopyInto(object from, object to)
        {
            if (to == null || ReferenceEquals(from, to)) return;
            if (to is Array ta)
            {
                if (from is Array fa && fa.Length == ta.Length) Array.Copy(fa, ta, fa.Length);
                return;
            }
            var items = new List<object>();
            if (from is IDictionary fd) foreach (DictionaryEntry e in fd) items.Add(e);
            else if (from is IEnumerable fe) foreach (object o in fe) items.Add(o);
            AccessTools.Method(to.GetType(), "Clear", Type.EmptyTypes).Invoke(to, null);
            if (to is IDictionary td) { foreach (DictionaryEntry e in items) td[e.Key] = e.Value; return; }
            if (to is IList tl) { foreach (object o in items) tl.Add(o); return; }
            MethodInfo add = Array.Find(to.GetType().GetMethods(), m => m.Name == "Add" && m.GetParameters().Length == 1);
            foreach (object o in items) add.Invoke(to, new[] { o });
        }

        /// <summary>A new epoch: every player starts from fresh values (this PC's player too).</summary>
        public static void Reset()
        {
            if (_parts.Count == 0) return;
            _stack.Clear();
            _store.Clear();
            _seen.Clear();
            Apply(Mod(), null);
            _live = 0;
            _shared = null;
            _sharedInner = null;
            if (_peaceWatch != null) _peaceWatch.SetValue(null, 0f);
            if (_wmCurrent != null && Mod() != null) _wmCurrent.SetValue(null, _warMap.GetValue(Mod()));
            _postsPending = 0;
            _numbersPending.Clear();
            ApplyCarried();
        }

        // ------------------------------------------------------------------ across epochs

        /// <summary>Every player's state as the host's last tick left it (sent with the epoch).</summary>
        private static JObject _carried;

        /// <summary>
        /// Host, as it saves the world for a new epoch: every player's campaign state (between
        /// ticks: what the last tick left) as JSON, world objects by ID. Null: nothing to carry.
        /// </summary>
        public static JObject Save(bool quiet = false)
        {
            object mod = Mod();
            if (_parts.Count == 0 || mod == null || _stack.Count > 0 || _live != 0) return null;
            RestoreShared();
            var all = new JObject();
            int skipped = 0;
            var players = new SortedDictionary<int, object[]>();
            players[LockstepSession.MyPlayer] = Capture(mod);
            foreach (KeyValuePair<int, object[]> kv in _store) if (kv.Key != 0 && kv.Value != null) players[kv.Key] = kv.Value;
            foreach (KeyValuePair<int, object[]> kv in players)
            {
                var w = new StateJson(_mod);
                var o = new JObject();
                for (int i = 0; i < _parts.Count; i++)
                {
                    Part p = _parts[i];
                    if (p.ui) continue;
                    try { o[PartKey(p)] = w.Write(kv.Value[i]); }
                    catch (Exception e) { if (_failed.Add("save " + p.f.Name)) Log.Warn("lockstep: can't carry " + PartKey(p) + " to the next epoch: " + e.Message); }
                }
                skipped += w.Skipped;
                all[kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = o;
            }
            if (!quiet) Log.Info("lockstep: carrying " + players.Count + " players' Worldfall state to the next epoch (" + all.ToString(Newtonsoft.Json.Formatting.None).Length / 1024 + " KB, " + skipped + " values left out)");
            return all;
        }

        /// <summary>The state to start the next epoch from (null: fresh). Kept for reloads of the same epoch.</summary>
        public static void Carry(JObject all) => _carried = all;

        private static string PartKey(Part p) => p.f.DeclaringType.Name + "." + p.f.Name;

        private static void ApplyCarried()
        {
            object mod = Mod();
            if (_carried == null || mod == null) return;
            var fresh = Capture(mod);
            var keys = new SortedSet<int>();
            foreach (KeyValuePair<string, JToken> kv in _carried)
                if (int.TryParse(kv.Key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int k)) keys.Add(k);
            int n = 0;
            foreach (int player in keys)
            {
                var o = _carried[player.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JObject;
                if (o == null) continue;
                var r = new StateJson(_mod);
                var s = new object[_parts.Count];
                for (int i = 0; i < _parts.Count; i++)
                {
                    Part p = _parts[i];
                    s[i] = p.fresh ? null : p.content ? Clone(fresh[i]) : fresh[i];
                    if (!p.ui && r.Read(o[PartKey(p)], p.f.FieldType, out object v)) s[i] = v;
                    if (p.fresh && s[i] == null) s[i] = Fresh(p.f.FieldType);
                }
                if (player == LockstepSession.MyPlayer) Apply(mod, s);
                else _store[player] = s;
                _seen.Add(player);
                RenumberPosts(s);
                n++;
            }
            if (_wmCurrent != null) _wmCurrent.SetValue(null, _warMap.GetValue(mod));
            // the rebuilt state written again must be what was carried (else something didn't round-trip)
            string want = _carried.ToString(Newtonsoft.Json.Formatting.None).Replace("{\"$skip\":1}", "null"), got = (Save(true)?.ToString(Newtonsoft.Json.Formatting.None) ?? "").Replace("{\"$skip\":1}", "null");
            Log.Info("lockstep: Worldfall state of " + n + " players carried into this epoch (" + want.Length / 1024 + " KB, " + (want == got ? "verified" : "DIFFERS after rebuild: " + FirstDiff(want, got)) + ")");
        }

        private static string FirstDiff(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            int from = Math.Max(0, i - 80);
            return "at " + i + ": carried ..." + a.Substring(from, Math.Min(160, a.Length - from)) + " / rebuilt ..." + b.Substring(from, Math.Min(160, b.Length - from));
        }

        /// <summary>Quick orders are found by serial (FlagId): number a rebuilt war map's posts again, in order.</summary>
        private static void RenumberPosts(object[] s)
        {
            if (_posts == null || _warMap == null) return;
            int idx = _parts.FindIndex(p => p.f == _warMap);
            object map = idx < 0 ? null : s[idx];
            if (map == null) return;
            Serial c = _postsMade.GetOrCreateValue(map);
            c.n = 0;
            foreach (object f in (IList)_posts.GetValue(map))
            {
                _serialOf.Remove(f);
                _serialOf.Add(f, new Serial { n = ++c.n });
            }
        }

        // ------------------------------------------------------------------ patches

        private static bool BodyPrefix(ref Actor __result)
        {
            if (_stack.Count == 0 || !LockstepClock.InTick) return true;
            __result = CurrentBody;
            return false;
        }

        /// <summary>Messages and sounds for the acting player only.</summary>
        private static bool OnlyLocalPrefix() => !LockstepClock.InTick || IsLocal;

        /// <summary>Test runs log the messages Worldfall shows this player (why a council order was refused, ...).</summary>
        public static bool LogToasts;

        private static bool ToastPrefix(string __0)
        {
            bool mine = OnlyLocalPrefix();
            if (mine && LogToasts) Log.Info("TEST features: toast: " + __0);
            return mine;
        }

        /// <summary>Royal.Update calls Peace.Update: in ticks it runs once, from RunTick, outside any player.</summary>
        private static bool PeacePrefix() => !(LockstepClock.InTick && Open);

        private static bool TickOnlyPrefix() => !LockstepClock.Active || LockstepClock.InTick;

        /// <summary>A toast that only shows on the acting player's PC (relayed Action&lt;string&gt; arguments).</summary>
        public static Action<string> ScopedToast()
        {
            object mod = Mod();
            return s => { if (IsLocal && mod != null) try { _toast?.Invoke(mod, new object[] { s }); } catch { } };
        }

        public static Action ScopedHorn()
        {
            object mod = Mod();
            return () => { if (IsLocal && mod != null) try { _horn?.Invoke(mod, null); } catch { } };
        }

        // ------------------------------------------------------------------ per tick

        /// <summary>In every tick (WorldfallInTick.Run): the features' frame updates, once per player.</summary>
        public static void RunTick()
        {
            if (_mod == null || OffScope) return;
            float dt = LockstepClock.DefaultStep;
            if (_butcheryTick != null && !OffButchery) Try("butchery", () => _butcheryTick.Invoke(null, new object[] { dt }));
            if (_royalUpdate != null && !OffPeace) Try("peace", () => _peaceUpdate.Invoke(null, new object[] { dt }));
            Try("towns", TownsInTick.RunTick);
            Try("bundles", BelongingsInTick.RunTick);
            var players = new SortedSet<int>(LockstepControl.Players());
            players.UnionWith(_seen);
            foreach (int p in players)
                Run(p, () =>
                {
                    Actor you = CurrentBody;
                    Action<string> toast = ScopedToast();
                    if (_royalUpdate != null)
                    {
                        object royal = _royal.GetValue(Mod());
                        if (royal != null) Try("king's council", () => _royalUpdate.Invoke(royal, new object[] { dt, toast }));
                    }
                    if (_upkeepFlags != null)
                    {
                        object map = _warMap.GetValue(Mod());
                        if (map != null)
                        {
                            Try("war flags", () => _upkeepFlags.Invoke(map, new object[] { you, dt, toast }));
                            Try("fleet", () => _upkeepFleet.Invoke(map, new object[] { you, dt, toast }));
                        }
                    }
                    if (_lawUpdate != null) Try("law", () => _lawUpdate.Invoke(null, new object[] { you, dt, toast }));
                    if (_economyUpdate != null) Try("hired guards", () => _economyUpdate.Invoke(null, new object[] { you, dt, toast }));
                    CivilInTick.RunPlayer(you, dt, toast, Try);
                    if (_familyUpdate != null)
                    {
                        object settings = _settings.GetValue(Mod());
                        bool standing = settings != null && (bool)_familyFollows.GetValue(settings);
                        Try("family follow", () => _familyUpdate.Invoke(null, new object[] { you, standing, toast }));
                    }
                    if (_windUps != null && you != null && !OffWindups)
                    {
                        object settings = _settings.GetValue(Mod());
                        bool on = settings != null && (bool)_enemyWindups.GetValue(settings) && you.isAlive() && !_inside(you);
                        _attackers.Clear();
                        if (on)
                            foreach (Actor a in World.world.units)
                                if (a != you && _hasTarget(a) && _target(a) == you) _attackers.Add(a);
                        Try("enemy wind-ups", () => _windUps.Invoke(null, new object[] { you, _attackers, on }));
                    }
                    if (_serviceUpdate != null && you != null)
                    {
                        object mod = Mod();
                        if (_serviceFor != null && !ReferenceEquals(_serviceFor.GetValue(mod), you))
                        {
                            _serviceFor.SetValue(mod, you);
                            Try("service reset", () => _serviceReset.Invoke(null, null));
                        }
                        Try("service", () => _serviceUpdate.Invoke(null, new object[] { you, _royal.GetValue(mod), _warMap.GetValue(mod), dt, toast, ScopedHorn() }));
                    }
                });
        }

        private static readonly HashSet<string> _failed = new HashSet<string>();

        private static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                if (_failed.Add(what)) Log.Error("lockstep: Worldfall's " + what + " (in a tick): " + (e.InnerException ?? e));
            }
        }
    }
}
