using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's conversations: a choice is a closure (Choice.Act) that may change the world in
    /// ways single relayed calls can't carry: it makes things (a forged sword, a new realm, an
    /// alliance, a war), writes game fields straight, or goes on from what a relayed call returned
    /// (a quest accepted or not, a baby made or not).
    ///
    /// Such a choice travels whole: its method and the values it captured (world objects by ID,
    /// Worldfall's own small records field by field) go out as one input, and in that tick every
    /// PC runs it, as the player who picked it. On that player's PC it is the very same closure on
    /// the live conversation, so the conversation goes on with the real results; elsewhere a quiet
    /// copy of the conversation takes the lines. Choices that only move through the menus, or that
    /// only make changes the relayed calls already carry, run at once as before.
    /// Which kind a choice is comes from its code (the calls it makes, followed into Worldfall).
    /// </summary>
    public static class TalkInTick
    {
        private static Type _conv, _choice;
        private static FieldInfo _act, _enabled, _label, _you, _with, _toast, _choices, _open;
        private static MethodInfo _typed, _offer, _topics, _say;
        private static PropertyInfo _instance;
        private static FieldInfo _converse;
        private static Module _module;
        private static Assembly _game, _wf;
        public static int Sent, Replayed, Local, Unsent;

        public static void Install(Harmony h, Assembly wf)
        {
            _wf = wf;
            _game = typeof(Actor).Assembly;
            _conv = wf.GetType("FirstPerson.Conversation", false);
            _choice = wf.GetType("FirstPerson.Choice", false);
            Type mod = wf.GetType("FirstPerson.WorldBoxMod", false), dialogue = wf.GetType("FirstPerson.Dialogue", false);
            if (_conv == null || _choice == null || mod == null || dialogue == null) { Log.Warn("lockstep: Worldfall's conversations not found: their choices change one PC's world"); return; }
            _act = AccessTools.Field(_choice, "Act"); _enabled = AccessTools.Field(_choice, "Enabled"); _label = AccessTools.Field(_choice, "Label");
            _you = AccessTools.Field(_conv, "You"); _with = AccessTools.Field(_conv, "With"); _toast = AccessTools.Field(_conv, "Toast");
            _choices = AccessTools.Field(_conv, "Choices"); _open = AccessTools.Field(_conv, "Open");
            _typed = AccessTools.PropertyGetter(_conv, "Typed");
            _offer = AccessTools.Method(_conv, "Offer");
            _say = AccessTools.Method(_conv, "Say");
            _topics = AccessTools.Method(dialogue, "Topics", new[] { _conv });
            _instance = AccessTools.Property(mod, "Instance");
            _converse = AccessTools.Field(mod, "Converse");
            MethodInfo pick = AccessTools.Method(_conv, "Pick", new[] { typeof(int) });
            _module = wf.ManifestModule;
            if (_act == null || _you == null || _with == null || _choices == null || _typed == null || _offer == null || _topics == null || _instance == null || _converse == null || pick == null
                || !WorldCalls.Register(h, AccessTools.Method(typeof(TalkInTick), nameof(RunChoice))))
            {
                Log.Warn("lockstep: Worldfall's conversation changed (fields not found): its choices change one PC's world");
                return;
            }
            h.Patch(pick, prefix: new HarmonyMethod(typeof(TalkInTick), nameof(PickPrefix)));
            Log.Info("lockstep: conversation choices that make things run in ticks");
        }

        private static object LiveConversation()
        {
            object mod = _instance.GetValue(null, null);
            return mod == null ? null : _converse.GetValue(mod);
        }

        // ------------------------------------------------------------------ picking

        /// <summary>This PC's choices on their way (by key), with the conversation they belong to.</summary>
        private sealed class Waiting { public Delegate act; public object conv; public float at; public string label; }
        private static readonly Dictionary<string, Waiting> _waiting = new Dictionary<string, Waiting>();
        private static int _serial;

        private static bool PickPrefix(object __instance, int index)
        {
            if (!LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick || _replaying) return true;
            if (!ReferenceEquals(__instance, LiveConversation())) return true;
            if (!(_choices.GetValue(__instance) is IList list) || index < 0 || index >= list.Count) return true;
            object choice = list[index];
            if (!(bool)_enabled.GetValue(choice) || !(bool)_typed.Invoke(__instance, null)) return true;
            if (!(_act.GetValue(choice) is Delegate act)) return true;
            string label = _label.GetValue(choice) as string ?? "?";
            string why = Needs(act.Method);
            if (why == null) { Local++; return true; }   // menus, or changes that travel by themselves
            // it only needs a relayed call's answer, but reads this PC's own state (an offer made in
            // an earlier step): it can't run the same way elsewhere; its changes travel call by call
            string local = why.StartsWith("uses what", StringComparison.Ordinal) ? LocalState(act.Method, 0) : null;
            if (local != null)
            {
                Local++;
                if (_noted.Add("local " + Name(act.Method))) Log.Info("lockstep: conversation choice \"" + label + "\" (" + why + ") runs here: it reads " + local);
                return true;
            }
            string key = LockstepSession.MyPlayer.ToString(CultureInfo.InvariantCulture) + "." + (++_serial).ToString(CultureInfo.InvariantCulture);
            JObject enc;
            string fail = null;
            try { enc = Encode(act, __instance, ref fail); }
            catch (Exception e) { enc = null; fail = e.Message; }
            if (enc == null)
            {
                Unsent++;
                if (_noted.Add(act.Method.DeclaringType.Name + "." + act.Method.Name))
                    Log.Warn("lockstep: conversation choice \"" + label + "\" (" + Name(act.Method) + ", " + why + ") runs on this PC only: " + fail);
                return true;
            }
            enc["k"] = key;
            _waiting[key] = new Waiting { act = act, conv = __instance, at = Time.unscaledTime, label = label };
            RunChoice(enc.ToString(Newtonsoft.Json.Formatting.None));   // caught by WorldCalls and sent
            Sent++;
            if (Trace) Log.Info("lockstep: conversation choice \"" + label + "\" sent to run in a tick (" + why + ")");
            // nothing more to pick until it has run (a few frames)
            list.Clear();
            return false;
        }

        private static readonly HashSet<string> _noted = new HashSet<string>();
        private static readonly bool Trace = Array.Exists(Environment.GetCommandLineArgs(), x => x.Equals("-coopfall-lockstep-trace", StringComparison.OrdinalIgnoreCase) || x.Equals("-coopfall-scenario", StringComparison.OrdinalIgnoreCase));

        /// <summary>Every frame: a choice that never came back (lost, or the world reloaded) gives the conversation back.</summary>
        public static void Frame()
        {
            if (_waiting.Count == 0) return;
            List<string> late = null;
            foreach (KeyValuePair<string, Waiting> kv in _waiting)
                if (Time.unscaledTime - kv.Value.at > 4f) (late ?? (late = new List<string>())).Add(kv.Key);
            if (late == null) return;
            foreach (string k in late)
            {
                Waiting w = _waiting[k];
                _waiting.Remove(k);
                Log.Warn("lockstep: conversation choice \"" + w.label + "\" didn't come back from the tick: the topics again");
                GiveBack(w.conv);
            }
        }

        private static void GiveBack(object conv)
        {
            try
            {
                if (conv != null && (bool)_open.GetValue(conv) && _choices.GetValue(conv) is IList l && l.Count == 0)
                    _offer.Invoke(conv, new[] { _topics.Invoke(null, new[] { conv }) });
            }
            catch (Exception e) { Log.Warn("lockstep: conversation: " + (e.InnerException ?? e).Message); }
        }

        /// <summary>A new epoch: choices still on their way are lost; their conversations go on.</summary>
        public static void Reset()
        {
            foreach (Waiting w in _waiting.Values) GiveBack(w.conv);
            _waiting.Clear();
        }

        // ------------------------------------------------------------------ the replay

        private static bool _replaying;
        private static object _live;

        /// <summary>Relayed (WorldCalls): a conversation choice, in a tick on every PC.</summary>
        public static void RunChoice(string enc)
        {
            if (!LockstepClock.InTick) return;
            JObject o;
            try { o = JObject.Parse(enc); }
            catch { return; }
            string key = (string)o["k"];
            object conv = null;
            if (PlayerScope.IsLocal && key != null && _waiting.TryGetValue(key, out Waiting w))
            {
                _waiting.Remove(key);
                // the live conversation, if it is still with the same person (the lines show here)
                if ((bool)_open.GetValue(w.conv) && (_with.GetValue(w.conv) as Actor)?.getID() == (long)o["w"]) conv = w.conv;
            }
            bool was = _replaying;
            _replaying = true;
            try
            {
                // every PC (this one too) runs the same copy of the choice, so the world changes the same way
                string fail = null;
                _live = conv;
                Delegate copy = Decode(o, ref fail);
                _live = null;
                if (copy == null) { Log.Warn("lockstep: conversation choice skipped (" + fail + ")"); if (conv != null) GiveBack(conv); return; }
                copy.DynamicInvoke();
                if (conv != null && _choices.GetValue(conv) is IList l && l.Count == 0 && (bool)_open.GetValue(conv)) GiveBack(conv);
                Replayed++;
            }
            catch (Exception e)
            {
                Log.Warn("lockstep: conversation choice failed in the tick: " + (e.InnerException ?? e).Message);
                if (conv != null) GiveBack(conv);
            }
            finally { _replaying = was; }
        }

        // ------------------------------------------------------------------ closures as data

        private static JObject Encode(Delegate d, object conv, ref string fail)
        {
            MethodInfo m = d.Method;
            if (m.Module != _module) { fail = "not Worldfall's"; return null; }
            var o = new JObject { ["m"] = m.MetadataToken, ["y"] = (_you.GetValue(conv) as Actor)?.getID() ?? -1, ["w"] = (_with.GetValue(conv) as Actor)?.getID() ?? -1 };
            if (d.Target != null)
            {
                JToken t = Obj(d.Target, conv, 0, ref fail);
                if (t == null) return null;
                o["t"] = t;
            }
            return o;
        }

        /// <summary>A captured value: world objects by ID, the conversation by name, records field by field.</summary>
        private static JToken Val(object v, object conv, int depth, ref string fail)
        {
            if (v == null) return JValue.CreateNull();
            if (ReferenceEquals(v, conv)) return new JObject { ["$"] = "conv" };
            Type t = v.GetType();
            if (t == _conv) { fail = "another conversation"; return null; }
            if (v is Delegate) { fail = "a function (" + t.Name + ")"; return null; }
            string leaf = WorldCalls.Leaf(v);
            if (leaf != null) return new JObject { ["$"] = "v", ["v"] = leaf, ["ty"] = t.AssemblyQualifiedName };
            if (depth > 3) { fail = "nested too deep"; return null; }
            if (t.IsArray && t.GetArrayRank() == 1)
            {
                var a = new JArray();
                foreach (object x in (Array)v) { JToken j = Val(x, conv, depth + 1, ref fail); if (j == null) return null; a.Add(j); }
                return new JObject { ["$"] = "arr", ["ty"] = t.GetElementType().AssemblyQualifiedName, ["l"] = a };
            }
            if (v is IList list && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var a = new JArray();
                foreach (object x in list) { JToken j = Val(x, conv, depth + 1, ref fail); if (j == null) return null; a.Add(j); }
                return new JObject { ["$"] = "list", ["ty"] = t.AssemblyQualifiedName, ["l"] = a };
            }
            if (t.Assembly == _wf && !t.IsValueType || t.Name.Contains("<>")) return Obj(v, conv, depth + 1, ref fail);
            fail = "a " + t.Name;
            return null;
        }

        private static JToken Obj(object v, object conv, int depth, ref string fail)
        {
            Type t = v.GetType();
            // a lambda that captures nothing: its class's one instance
            FieldInfo single = t.GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (single != null && ReferenceEquals(single.GetValue(null), v)) return new JObject { ["$"] = "c", ["ty"] = t.AssemblyQualifiedName };
            var f = new JObject();
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo fi in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    JToken j = Val(fi.GetValue(v), conv, depth, ref fail);
                    if (j == null) { fail = t.Name + "." + fi.Name + ": " + fail; return null; }
                    f[fi.DeclaringType.Name + "." + fi.Name] = j;
                }
            return new JObject { ["$"] = "o", ["ty"] = t.AssemblyQualifiedName, ["f"] = f };
        }

        private static Delegate Decode(JObject o, ref string fail)
        {
            MethodInfo m;
            try { m = _module.ResolveMethod((int)o["m"]) as MethodInfo; }
            catch { fail = "unknown method"; return null; }
            if (m == null) { fail = "unknown method"; return null; }
            Actor you = World.world.units.get((long)o["y"]), with = World.world.units.get((long)o["w"]);
            object conv = _live ?? StandIn(you, with);
            object target = null;
            if (o["t"] is JObject t && (target = ReadVal(t, conv, ref fail)) == null) return null;
            Type dt = m.GetParameters().Length == 0 && m.ReturnType == typeof(void) ? typeof(Action) : null;
            if (dt == null) { fail = "not a plain choice"; return null; }
            return m.IsStatic ? Delegate.CreateDelegate(dt, m) : Delegate.CreateDelegate(dt, target, m);
        }

        private static object ReadVal(JToken j, object conv, ref string fail)
        {
            if (j == null || j.Type == JTokenType.Null) return null;
            var o = (JObject)j;
            switch ((string)o["$"])
            {
                case "conv": return conv;
                case "v":
                {
                    Type want = Type.GetType((string)o["ty"], false);
                    if (want == null || !WorldCalls.ReadLeaf((string)o["v"], want, out object v)) { fail = "a " + (want?.Name ?? "value") + " is gone"; return null; }
                    return v;
                }
                case "list":
                {
                    Type lt = Type.GetType((string)o["ty"], false);
                    if (lt == null) { fail = "unknown list"; return null; }
                    var l = (IList)Activator.CreateInstance(lt);
                    foreach (JToken x in (JArray)o["l"]) l.Add(ReadVal(x, conv, ref fail));
                    return l;
                }
                case "arr":
                {
                    Type et = Type.GetType((string)o["ty"], false);
                    if (et == null) { fail = "unknown array"; return null; }
                    var src = (JArray)o["l"];
                    Array arr = Array.CreateInstance(et, src.Count);
                    for (int i = 0; i < src.Count; i++) arr.SetValue(ReadVal(src[i], conv, ref fail), i);
                    return arr;
                }
                case "c":
                {
                    Type ct = Type.GetType((string)o["ty"], false);
                    return ct?.GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                }
                case "o":
                {
                    Type ot = Type.GetType((string)o["ty"], false);
                    if (ot == null) { fail = "unknown record"; return null; }
                    object v = FormatterServices.GetUninitializedObject(ot);
                    foreach (KeyValuePair<string, JToken> kv in (JObject)o["f"])
                    {
                        int dot = kv.Key.IndexOf('.');
                        Type decl = ot;
                        while (decl != null && decl.Name != kv.Key.Substring(0, dot)) decl = decl.BaseType;
                        FieldInfo fi = decl?.GetField(kv.Key.Substring(dot + 1), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (fi == null) { fail = "unknown field " + kv.Key; return null; }
                        object x = ReadVal(kv.Value, conv, ref fail);
                        if (x == null && kv.Value.Type != JTokenType.Null && !(kv.Value is JObject jo && (string)jo["$"] == "c")) return null;
                        fi.SetValue(v, x);
                    }
                    return v;
                }
            }
            fail = "unknown value";
            return null;
        }

        /// <summary>The conversation on a PC that isn't the picker's: takes the lines, shows nothing.</summary>
        private static object StandIn(Actor you, Actor with)
        {
            object c = Activator.CreateInstance(_conv, true);
            _you.SetValue(c, you);
            _with.SetValue(c, with);
            _toast?.SetValue(c, PlayerScope.ScopedToast());
            _open.SetValue(c, true);
            return c;
        }

        private static string Name(MethodBase m) => m.DeclaringType?.Name + "." + m.Name;

        // ------------------------------------------------------------------ which choices travel

        private static readonly Dictionary<MethodBase, string> _needs = new Dictionary<MethodBase, string>();
        private static readonly HashSet<MethodBase> _visiting = new HashSet<MethodBase>();

        private static readonly string[] ReadNames = { "get", "is", "has", "can", "count", "find", "check", "Get", "Is", "Has", "Can", "Count", "Find", "Contains", "Equals", "ToString", "GetHashCode", "CompareTo", "distance", "same", "Distance" };
        private static readonly HashSet<string> ReadTypes = new HashSet<string> { "Toolbox", "Date", "LocalizedTextManager", "Randy", "AssetManager", "Config", "Mathf", "Debug", "MapBox", "World" };

        /// <summary>Why a choice must run in a tick (null: it can run at once).</summary>
        public static string Needs(MethodBase m) => Needs(m, 0);

        private static string Needs(MethodBase m, int depth)
        {
            if (m == null) return null;
            if (_needs.TryGetValue(m, out string r)) return r;
            if (depth > 8 || !_visiting.Add(m)) return null;
            r = null;
            try
            {
                foreach (KeyValuePair<OpCode, object> i in PatchProcessor.ReadMethodBody(m))
                {
                    if ((i.Key == OpCodes.Stfld || i.Key == OpCodes.Stsfld) && i.Value is FieldInfo f && f.DeclaringType != null && f.DeclaringType.Assembly == _game)
                    { r = "writes " + f.DeclaringType.Name + "." + f.Name; break; }
                    if (!(i.Value is MethodBase c) || c.DeclaringType == null) continue;
                    if (i.Key != OpCodes.Call && i.Key != OpCodes.Callvirt && i.Key != OpCodes.Newobj) continue;
                    Assembly a = c.DeclaringType.Assembly;
                    if (a == _game)
                    {
                        if (typeof(BaseSystemData).IsAssignableFrom(c.DeclaringType)) continue;   // DataCalls
                        if (WorldCalls.IsRelayed(c))
                        {
                            if (c is MethodInfo mi && mi.ReturnType != typeof(void) && !c.Name.StartsWith("set", StringComparison.Ordinal)) { r = "uses what " + c.Name + " returns"; break; }
                            continue;
                        }
                        if (i.Key == OpCodes.Newobj) { if (typeof(BaseSimObject).IsAssignableFrom(c.DeclaringType) || c.DeclaringType.Name.EndsWith("Data", StringComparison.Ordinal)) { r = "makes a " + c.DeclaringType.Name; break; } continue; }
                        if (c.Name.StartsWith("get_", StringComparison.Ordinal) || ReadTypes.Contains(c.DeclaringType.Name) || IsRead(c.Name)) continue;
                        r = "calls " + c.DeclaringType.Name + "." + c.Name;
                        break;
                    }
                    if (a != _wf) continue;
                    if (WorldCalls.IsRelayed(c))
                    {
                        if (c is MethodInfo mi && mi.ReturnType != typeof(void)) { r = "uses what " + Name(c) + " returns"; break; }
                        continue;
                    }
                    string inner = Needs(c, depth + 1);
                    if (inner != null) { r = inner + " (in " + Name(c) + ")"; break; }
                }
            }
            catch { }
            _visiting.Remove(m);
            _needs[m] = r;
            return r;
        }

        private static readonly Dictionary<MethodBase, string> _local = new Dictionary<MethodBase, string>();

        /// <summary>A Worldfall static value (not kept per player) the code reads, if any.</summary>
        private static string LocalState(MethodBase m, int depth)
        {
            if (m == null || depth > 3) return null;
            if (_local.TryGetValue(m, out string r)) return r;
            _local[m] = null;
            try
            {
                foreach (KeyValuePair<OpCode, object> i in PatchProcessor.ReadMethodBody(m))
                {
                    if (i.Key == OpCodes.Ldsfld && i.Value is FieldInfo f && f.DeclaringType?.Assembly == _wf && !f.IsInitOnly && !f.IsLiteral
                        && (f.FieldType.IsValueType || f.FieldType == typeof(string) || typeof(Actor).IsAssignableFrom(f.FieldType) || f.FieldType.Assembly == _wf) && !PlayerScope.IsPart(f) && !f.Name.Contains("<"))
                    { r = f.DeclaringType.Name + "." + f.Name; break; }
                    if ((i.Key == OpCodes.Call || i.Key == OpCodes.Callvirt) && i.Value is MethodBase c && c.DeclaringType?.Assembly == _wf && !c.Name.StartsWith("get_", StringComparison.Ordinal) && !WorldCalls.IsRelayed(c))
                    {
                        string inner = LocalState(c, depth + 1);
                        if (inner != null) { r = inner; break; }
                    }
                }
            }
            catch { }
            _local[m] = r;
            return r;
        }

        private static bool IsRead(string n)
        {
            foreach (string p in ReadNames) if (n.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
