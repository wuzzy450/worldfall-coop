using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's settings are per PC, and a few of them decide how the shared world plays (or how
    /// it looks the same for everyone): friendly fire, parry timing, enemy wind-ups, family follow,
    /// villager and walking pace, the day/night mode and length, wind gusts. In lockstep the host's
    /// values are used by everyone: they travel with each epoch, guests take them for the session
    /// and get their own back when lockstep stops. Worldfall saving its settings file meanwhile
    /// writes the guest's own values, not the host's.
    /// </summary>
    internal static class WorldfallSettings
    {
        private static readonly string[] Shared =
        {
            "FriendlyFire", "ParryTiming", "EnemyWindups", "FamilyFollows", "FolkPace", "WalkSpeed",
            "TimeMode", "DayMinutes", "WindGusts", "Threats",
        };

        private static Dictionary<FieldInfo, object> _own;   // the guest's values while the host's are in use
        private static bool _hooked;

        private static object Settings()
        {
            object mod = WorldfallBridge.Mod;
            FieldInfo f = mod == null ? null : AccessTools.Field(mod.GetType(), "Settings");
            return f?.GetValue(mod);
        }

        private static IEnumerable<FieldInfo> Fields(object s)
        {
            foreach (string n in Shared)
            {
                FieldInfo f = AccessTools.Field(s.GetType(), n);
                if (f != null && (f.FieldType == typeof(bool) || f.FieldType == typeof(int) || f.FieldType == typeof(float))) yield return f;
            }
        }

        /// <summary>Host: its values, to send with an epoch (null without Worldfall).</summary>
        public static JObject Capture()
        {
            try
            {
                object s = Settings();
                if (s == null) return null;
                var o = new JObject();
                foreach (FieldInfo f in Fields(s)) o[f.Name] = JToken.FromObject(f.GetValue(s));
                return o;
            }
            catch (Exception e) { Log.Warn("lockstep: reading Worldfall's settings: " + e.Message); return null; }
        }

        /// <summary>Guest: use the host's values until Restore.</summary>
        public static void Apply(JObject host)
        {
            if (host == null) return;
            try
            {
                object s = Settings();
                if (s == null) return;
                Hook(s.GetType());
                var changed = new List<string>();
                foreach (FieldInfo f in Fields(s))
                {
                    JToken t = host[f.Name];
                    if (t == null) continue;
                    object v = t.ToObject(f.FieldType);
                    object was = f.GetValue(s);
                    if (_own == null) _own = new Dictionary<FieldInfo, object>();
                    if (!_own.ContainsKey(f)) _own[f] = was;
                    if (!Equals(was, v)) { f.SetValue(s, v); changed.Add(f.Name + "=" + v); }
                }
                if (changed.Count > 0) Log.Info("lockstep: using the host's Worldfall settings: " + string.Join(", ", changed.ToArray()));
            }
            catch (Exception e) { Log.Warn("lockstep: applying the host's Worldfall settings: " + e.Message); }
        }

        /// <summary>Lockstep stopped: the guest's own values again.</summary>
        public static void Restore()
        {
            if (_own == null) return;
            try
            {
                object s = Settings();
                if (s != null) foreach (KeyValuePair<FieldInfo, object> kv in _own) kv.Key.SetValue(s, kv.Value);
            }
            catch (Exception e) { Log.Warn("lockstep: restoring Worldfall's settings: " + e.Message); }
            _own = null;
        }

        private static void Hook(Type settings)
        {
            if (_hooked) return;
            _hooked = true;
            MethodInfo save = AccessTools.Method(settings, "Save");
            if (save == null) return;
            var h = new Harmony("coopfall.lockstep.wfsettings");
            h.Patch(save, prefix: new HarmonyMethod(typeof(WorldfallSettings), nameof(SavePrefix)), finalizer: new HarmonyMethod(typeof(WorldfallSettings), nameof(SaveFinalizer)));
        }

        private static void SavePrefix(object __instance, out Dictionary<FieldInfo, object> __state)
        {
            __state = null;
            if (_own == null) return;
            __state = new Dictionary<FieldInfo, object>();
            foreach (KeyValuePair<FieldInfo, object> kv in _own) { __state[kv.Key] = kv.Key.GetValue(__instance); kv.Key.SetValue(__instance, kv.Value); }
        }

        private static Exception SaveFinalizer(object __instance, Dictionary<FieldInfo, object> __state, Exception __exception)
        {
            if (__state != null) foreach (KeyValuePair<FieldInfo, object> kv in __state) kv.Key.SetValue(__instance, kv.Value);
            return __exception;
        }
    }
}
