using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// World changes made outside the simulation step travel as inputs.
    ///
    /// Worldfall plays a whole layer of its own from per-frame code: chopping and mining, trades,
    /// gold, happiness, inventory, crafting, eating, quests, joining cities, traits, guards, ...
    /// all by calling the game's own methods (addMoney, addToInventory, extractResources, ...).
    /// In lockstep such a call would change one PC's world only. Here the game methods that
    /// change the world are caught when called outside a tick: the call (method, object and
    /// arguments by ID) becomes an input, and every PC (this one too) makes the same call at the
    /// start of the same tick. The caller sees nothing happen yet (a void method does nothing, a
    /// method with a result returns the default); it shows up a few frames later, for everyone.
    /// Calls with arguments that can't be named across PCs are refused (and logged once).
    /// </summary>
    public static class WorldCalls
    {
        /// <summary>Methods by name; every declared overload in these classes whose arguments can travel.</summary>
        private static readonly string[] Names =
        {
            // creatures: fights, statuses, tasks (the old "core gate")
            "tryToAttack", "startAttackCooldown", "getHit", "calculateForce", "makeStunned", "makeConfused",
            "cancelAllBeh", "setTask", "die", "addStatusEffect", "finishStatusEffect", "applyRandomForce", "takeItems",
            "addAggro", "startFightingWith", "makeWait", "clearAttackTarget", "stopMovement", "stopSleeping",
            // creatures: Worldfall's life layer
            "addMoney", "changeHappiness", "addToInventory", "takeFromInventory", "joinCity", "joinKingdom",
            "addTrait", "removeTrait", "addExperience", "addRenown", "restoreHealth", "restoreHealthPercent",
            "restoreMana", "restoreStamina", "restoreStaminaPercent", "setHomeBuilding", "clearHomeBuilding",
            "stopBeingWarrior", "consumeFoodResource", "eatFoodItem", "giveInventoryResourcesToCity",
            "tryToConvertToReligion", "setArmy", "setFamily", "finishAngryStatus", "dieSimpleNone",
            "removeFromPreviousFaction", "clearCity", "setDefaultKingdom", "exitBoat", "getHitFullHealth",
            // buildings, cities, kingdoms, wars
            "extractResources", "addResourcesToRandomStockpile", "takeResource", "makeWarrior", "startFire", "stopFire",
            "endWar", "leaveWar", "setCaptain", "setLeader",
        };

        private static readonly Type[] Owners =
        {
            typeof(Actor), typeof(BaseSimObject), typeof(Building), typeof(City), typeof(Kingdom), typeof(WorldTile),
            typeof(War), typeof(WarManager), typeof(Army), typeof(Clan), typeof(Family),
        };

        private static readonly Dictionary<string, MethodBase> _byKey = new Dictionary<string, MethodBase>();
        private static readonly Dictionary<MethodBase, string> _keyOf = new Dictionary<MethodBase, string>();
        private static readonly HashSet<string> _refusedLogged = new HashSet<string>();
        private static readonly Dictionary<string, float> _inFlight = new Dictionary<string, float>();
        private static int _sentThisSecond;
        private static float _second;
        private static bool _replaying;
        private static int _traced;
        private static readonly bool Trace = Array.Exists(Environment.GetCommandLineArgs(), x => x.Equals("-coopfall-lockstep-trace", StringComparison.OrdinalIgnoreCase));

        /// <summary>Where calls are sent (set by LockstepSession).</summary>
        public static Func<string, bool> Submit;
        public static int Sent, Replayed, Refused;

        public static void Install(Harmony h)
        {
            var names = new HashSet<string>(Names);
            var prefix = new HarmonyMethod(typeof(WorldCalls), nameof(CallPrefix));
            int n = 0;
            foreach (Type t in Owners)
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!names.Contains(m.Name) || m.IsAbstract || m.ContainsGenericParameters) continue;
                    if (!Returnable(m.ReturnType)) continue;
                    bool ok = true;
                    foreach (ParameterInfo p in m.GetParameters()) if (!Travels(p.ParameterType) || p.ParameterType.IsByRef) { ok = false; break; }
                    if (!ok) continue;
                    string key = Key(m);
                    if (_byKey.ContainsKey(key)) continue;
                    try { h.Patch(m, prefix: prefix); _byKey[key] = m; _keyOf[m] = key; n++; }
                    catch (Exception e) { Log.Warn("lockstep: couldn't relay " + key + ": " + e.Message); }
                }
            Log.Info("lockstep: " + n + " world-changing game methods travel as inputs when called outside a tick");
        }

        private static string Key(MethodBase m)
        {
            var sb = new StringBuilder(m.DeclaringType.Name).Append('.').Append(m.Name).Append('(');
            ParameterInfo[] ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(','); sb.Append(ps[i].ParameterType.Name); }
            return sb.Append(')').ToString();
        }

        private static bool Returnable(Type t) => t == typeof(void) || t == typeof(bool) || t == typeof(int) || t == typeof(float) || t.IsEnum;

        private static bool Travels(Type t)
        {
            if (t.IsEnum || t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double) || t == typeof(string)) return true;
            if (t == typeof(Vector2) || t == typeof(Vector3)) return true;
            if (t == typeof(WorldTile) || t == typeof(BaseSimObject) || t == typeof(Item)) return true;
            if (typeof(Asset).IsAssignableFrom(t)) return true;
            return Manager(t) != null;
        }

        // ------------------------------------------------------------------ the catch

        private static bool CallPrefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (Trace && LockstepClock.InTick && __instance is Actor ta && LockstepControl.IsControlled(ta.getID())
                && (__originalMethod.Name == "getHit" || __originalMethod.Name == "die" || __originalMethod.Name == "calculateForce") && _traced++ < 40)
                Log.Info("lockstep: tick " + LockstepClock.Tick + " " + __originalMethod.Name + " on controlled #" + ta.getID() + " hp " + ta.getHealth() + " shake " + R.Get(ta, "_shake_active") + " invincible " + R.Get(ta, "is_invincible") + " shake timer " + R.Get(ta, "_shake_timer") + " args " + string.Join(",", Array.ConvertAll(__args ?? new object[0], x => x == null ? "null" : x is BaseSimObject so ? "#" + so.getID() : x.ToString())) + Environment.NewLine + new System.Diagnostics.StackTrace(2, false));
            if (_replaying || !LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick) return true;
            if (!_keyOf.TryGetValue(__originalMethod, out string key)) return true;
            string enc = null;
            try { enc = Encode(key, __instance, __args); } catch { }
            if (enc == null || Submit == null) { Refuse(key, "its object or arguments can't be named on the other PCs"); return false; }
            float now = Time.unscaledTime;
            // per-frame code repeats itself: one copy until it has been applied
            if (_inFlight.TryGetValue(enc, out float at) && now - at < 2f) return false;
            if (now - _second > 1f) { _second = now; _sentThisSecond = 0; }
            if (++_sentThisSecond > 200) { Refuse(key, "more than 200 calls a second"); return false; }
            if (!Submit(enc)) return false;
            _inFlight[enc] = now;
            Sent++;
            if (_inFlight.Count > 512) _inFlight.Clear();
            return false;
        }

        private static void Refuse(string key, string why)
        {
            Refused++;
            if (_refusedLogged.Add(key) && _refusedLogged.Count < 60)
                Log.Warn("lockstep: " + key + " outside a tick not relayed (" + why + "). Called from:\n" + new System.Diagnostics.StackTrace(3, false));
        }

        // ------------------------------------------------------------------ the replay

        /// <summary>LockstepInput, in a tick: make the call on this PC.</summary>
        internal static void Apply(string enc)
        {
            _inFlight.Remove(enc);
            if (!Decode(enc, out MethodBase m, out object inst, out object[] args, out string why))
            {
                Log.Warn("lockstep: relayed call skipped (" + why + "): " + enc);
                return;
            }
            _replaying = true;
            try { m.Invoke(inst, args); Replayed++; }
            catch (TargetInvocationException e) { Log.Warn("lockstep: relayed " + _keyOf[m] + " failed: " + (e.InnerException ?? e).Message); }
            finally { _replaying = false; }
        }

        // ------------------------------------------------------------------ encoding

        private static readonly Dictionary<Type, Func<long, object>> _managers = new Dictionary<Type, Func<long, object>>();
        private static readonly Dictionary<Type, Func<string, object>> _libraries = new Dictionary<Type, Func<string, object>>();

        private static Func<long, object> Manager(Type t)
        {
            if (_managers.TryGetValue(t, out Func<long, object> f)) return f;
            f = null;
            if (t == typeof(Actor)) f = id => World.world.units.get(id);
            else if (t == typeof(Building)) f = id => World.world.buildings.get(id);
            else
                foreach (FieldInfo mf in typeof(MapBox).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    MethodInfo get = mf.FieldType.GetMethod("get", new[] { typeof(long) });
                    if (get == null || get.ReturnType != t) continue;
                    f = id => { object mgr = mf.GetValue(World.world); return mgr == null ? null : get.Invoke(mgr, new object[] { id }); };
                    break;
                }
            _managers[t] = f;
            return f;
        }

        private static Func<string, object> Library(Type t)
        {
            if (_libraries.TryGetValue(t, out Func<string, object> f)) return f;
            f = null;
            foreach (FieldInfo lf in typeof(AssetManager).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                MethodInfo get = lf.FieldType.GetMethod("get", new[] { typeof(string) });
                if (get == null || get.ReturnType != t) continue;
                f = id => { object lib = lf.GetValue(null); return lib == null ? null : get.Invoke(lib, new object[] { id }); };
                break;
            }
            _libraries[t] = f;
            return f;
        }

        private static string Esc(string s) => Uri.EscapeDataString(s);
        private static string H(float f) => ((uint)BitConverter.SingleToInt32Bits(f)).ToString("x");
        private static float F(string s) => BitConverter.Int32BitsToSingle((int)uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        private static string Encode(string key, object inst, object[] args)
        {
            var sb = new StringBuilder(Esc(key));
            if (!Value(sb, inst)) return null;
            if (args != null) foreach (object a in args) if (!Value(sb, a)) return null;
            return sb.ToString();
        }

        private static bool Value(StringBuilder sb, object v)
        {
            sb.Append('|');
            switch (v)
            {
                case null: sb.Append('n'); return true;
                case bool b: sb.Append(b ? "t" : "f"); return true;
                case int i: sb.Append('i').Append(i.ToString(CultureInfo.InvariantCulture)); return true;
                case long l: sb.Append('l').Append(l.ToString(CultureInfo.InvariantCulture)); return true;
                case float f: sb.Append('r').Append(H(f)); return true;
                case double d: sb.Append('d').Append(BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture)); return true;
                case string s: sb.Append('s').Append(Esc(s)); return true;
                case Vector2 v2: sb.Append('v').Append(H(v2.x)).Append(',').Append(H(v2.y)); return true;
                case Vector3 v3: sb.Append('w').Append(H(v3.x)).Append(',').Append(H(v3.y)).Append(',').Append(H(v3.z)); return true;
                case WorldTile wt: sb.Append('T').Append(wt.x).Append(',').Append(wt.y); return true;
                case Actor a: sb.Append('A').Append(a.getID()); return true;
                case Building b: sb.Append('B').Append(b.getID()); return true;
                case Item it: sb.Append('I').Append(it.getID()); return true;
                case Asset asset:
                    if (asset.id == null || Library(asset.GetType()) == null) return false;
                    sb.Append('a').Append(Esc(asset.GetType().Name)).Append(':').Append(Esc(asset.id)); return true;
            }
            Type t = v.GetType();
            if (t.IsEnum) { sb.Append('e').Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return true; }
            if (Manager(t) != null)
            {
                MethodInfo getId = t.GetMethod("getID", Type.EmptyTypes);
                if (getId == null) return false;
                sb.Append('M').Append(Esc(t.Name)).Append(':').Append(Convert.ToInt64(getId.Invoke(v, null)).ToString(CultureInfo.InvariantCulture));
                return true;
            }
            return false;
        }

        private static readonly Dictionary<string, Type> _typeByName = new Dictionary<string, Type>();

        private static Type TypeNamed(string name)
        {
            if (_typeByName.TryGetValue(name, out Type t)) return t;
            t = typeof(Actor).Assembly.GetType(name, false);
            _typeByName[name] = t;
            return t;
        }

        private static bool Decode(string enc, out MethodBase m, out object inst, out object[] args, out string why)
        {
            m = null; inst = null; args = null; why = null;
            string[] p = enc.Split('|');
            if (!_byKey.TryGetValue(Uri.UnescapeDataString(p[0]), out m)) { why = "unknown method"; return false; }
            ParameterInfo[] ps = m.GetParameters();
            if (p.Length != ps.Length + 2) { why = "wrong argument count"; return false; }
            if (!Read(p[1], m.DeclaringType, out inst) || (!m.IsStatic && inst == null)) { why = "the object is gone"; return false; }
            args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                if (!Read(p[i + 2], ps[i].ParameterType, out args[i])) { why = "argument " + i + " is gone"; return false; }
            if (inst is BaseSimObject o && !o.isAlive() && m.Name != "die") { why = "the object died"; return false; }
            return true;
        }

        private static bool Read(string s, Type want, out object v)
        {
            v = null;
            if (s.Length == 0) return false;
            string r = s.Substring(1);
            switch (s[0])
            {
                case 'n': return true;
                case 't': v = true; return true;
                case 'f': v = false; return true;
                case 'i': v = int.Parse(r, CultureInfo.InvariantCulture); return true;
                case 'l': v = long.Parse(r, CultureInfo.InvariantCulture); return true;
                case 'r': v = F(r); return true;
                case 'd': v = BitConverter.Int64BitsToDouble(long.Parse(r, CultureInfo.InvariantCulture)); return true;
                case 's': v = Uri.UnescapeDataString(r); return true;
                case 'e': v = Enum.ToObject(want, long.Parse(r, CultureInfo.InvariantCulture)); return true;
                case 'v': { string[] q = r.Split(','); v = new Vector2(F(q[0]), F(q[1])); return true; }
                case 'w': { string[] q = r.Split(','); v = new Vector3(F(q[0]), F(q[1]), F(q[2])); return true; }
                case 'T': { string[] q = r.Split(','); v = World.world.GetTile(int.Parse(q[0], CultureInfo.InvariantCulture), int.Parse(q[1], CultureInfo.InvariantCulture)); return v != null; }
                case 'A': v = World.world.units.get(long.Parse(r, CultureInfo.InvariantCulture)); return v != null;
                case 'B': v = World.world.buildings.get(long.Parse(r, CultureInfo.InvariantCulture)); return v != null;
                case 'I': v = World.world.items.get(long.Parse(r, CultureInfo.InvariantCulture)); return v != null;
                case 'a':
                {
                    int c = r.IndexOf(':');
                    Type t = TypeNamed(Uri.UnescapeDataString(r.Substring(0, c)));
                    Func<string, object> lib = t == null ? null : Library(t);
                    v = lib?.Invoke(Uri.UnescapeDataString(r.Substring(c + 1)));
                    return v != null;
                }
                case 'M':
                {
                    int c = r.IndexOf(':');
                    Type t = TypeNamed(Uri.UnescapeDataString(r.Substring(0, c)));
                    Func<long, object> mgr = t == null ? null : Manager(t);
                    v = mgr?.Invoke(long.Parse(r.Substring(c + 1), CultureInfo.InvariantCulture));
                    return v != null;
                }
            }
            return false;
        }
    }
}
