using System;
using System.IO;
using UnityEngine;

namespace Coopfall
{
    /// <summary>File logging + Unity debug logging. Writes to persistentDataPath/coopfall/log.txt</summary>
    public static class Log
    {
        private static string _path;
        private static readonly object _lock = new object();
        private static float _lastFlush;
        private static readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder();

        public static string Dir { get; private set; }

        public static void Init()
        {
            try
            {
                Dir = Path.Combine(Application.persistentDataPath, "coopfall");
                Directory.CreateDirectory(Dir);
                _path = Path.Combine(Dir, "log.txt");
                File.WriteAllText(_path, "=== Coopfall " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\n");
            }
            catch (Exception e) { Debug.LogError("[Coopfall] Log init failed: " + e.Message); }
        }

        public static void Info(string msg) { Write("INFO ", msg); Debug.Log("[Coopfall] " + msg); }
        public static void Warn(string msg) { Write("WARN ", msg); Debug.LogWarning("[Coopfall] " + msg); }
        public static void Error(string msg) { Write("ERROR", msg); Debug.LogError("[Coopfall] " + msg); }

        private static void Write(string lvl, string msg)
        {
            if (_path == null) return;
            lock (_lock)
            {
                _sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" [").Append(lvl).Append("] ").Append(msg).Append('\n');
                if (_sb.Length > 8000) FlushLocked();
            }
        }

        /// <summary>Called from Update; flushes buffered lines every ~2s.</summary>
        public static void Tick()
        {
            if (_path == null || Time.unscaledTime - _lastFlush < 2f) return;
            _lastFlush = Time.unscaledTime;
            lock (_lock) { if (_sb.Length > 0) FlushLocked(); }
        }

        public static void Flush() { lock (_lock) { if (_sb.Length > 0) FlushLocked(); } }

        private static void FlushLocked()
        {
            try { File.AppendAllText(_path, _sb.ToString()); } catch { }
            _sb.Length = 0;
        }
    }

    /// <summary>Mod settings, persisted to persistentDataPath/coopfall/config.json (editable by hand).</summary>
    [Serializable]
    public class CoopConfig
    {
        public string playerName = "";
        public string color = "";
        public string serverHost = "127.0.0.1";
        public int serverPort = 25598;
        /// <summary>"shared" = everyone plays one world together; "own" = each player hosts their own world and visits others.</summary>
        public string mode = "shared";
        public bool autoConnect = false;
        public bool showNameTags = true;
        public bool showCursors = true;
        public bool syncSpeed = true;
        /// <summary>
        /// Safety net: guests re-download the host's world this often (0 = never). Live sync keeps
        /// creatures and buildings in step continuously, so this is rarely needed. Works while
        /// possessing too: you are put back into your creature afterwards.
        /// </summary>
        public int autoResyncMinutes = 30;
        /// <summary>Host streams creatures (positions, health, births, deaths) and buildings to guests continuously.</summary>
        public bool liveSync = true;
        /// <summary>Creature position updates per second while live sync is on.</summary>
        public float liveSyncHz = 2f;
        /// <summary>Re-sync automatically when cities/kingdoms stay different from the host's for 90 s.</summary>
        public bool resyncOnDrift = true;
        /// <summary>Host uploads its world to the server this often so it is saved and late joiners get a recent copy.</summary>
        public int hostSaveMinutes = 3;
        public float avatarSendHz = 15f;
        // F5 is Worldfall's render-width key; WorldBox only uses F7-F11 in its hidden trailer mode.
        public string menuKey = "F8";
        public string mapKey = "F7";
        public string chatKey = "Return";
        public int configVersion = 0;
        private const int CurrentConfigVersion = 3;

        [NonSerialized] private static string _path;

        public static readonly string[] Palette =
        {
            "#ff5a5a", "#ffa63d", "#ffe14d", "#7ee35a", "#3fd8c8", "#4fa3ff", "#9b7bff", "#ff6fd0", "#ffffff"
        };

        public static CoopConfig Load()
        {
            CoopConfig cfg = new CoopConfig();
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "coopfall");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "config.json");
                if (File.Exists(_path))
                {
                    CoopConfig loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<CoopConfig>(File.ReadAllText(_path));
                    if (loaded != null) cfg = loaded;
                }
            }
            catch (Exception e) { Log.Warn("config load failed, using defaults: " + e.Message); }
            if (string.IsNullOrWhiteSpace(cfg.playerName))
                cfg.playerName = DefaultName();
            if (string.IsNullOrEmpty(cfg.color) || cfg.color.Length != 7 || cfg.color[0] != '#')
                cfg.color = Palette[UnityEngine.Random.Range(0, Palette.Length - 1)];
            if (cfg.mode != "own") cfg.mode = "shared";
            if (cfg.configVersion < CurrentConfigVersion)
            {
                // v1 used F5/F6, which collide with Worldfall
                if (cfg.mapKey == "F5") cfg.mapKey = "F7";
                if (cfg.menuKey == "F6") cfg.menuKey = "F8";
                // v2 re-downloaded the world every 5 min to fight drift; live sync replaces that
                if (cfg.configVersion < 3 && cfg.autoResyncMinutes == 5) cfg.autoResyncMinutes = 30;
                cfg.configVersion = CurrentConfigVersion;
            }
            if (cfg.serverPort <= 0 || cfg.serverPort > 65535) cfg.serverPort = 25598;
            cfg.Save();
            return cfg;
        }

        private static string DefaultName()
        {
            try
            {
                string n = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(n) && n.Length <= 20) return n;
            }
            catch { }
            return "Player" + UnityEngine.Random.Range(100, 999);
        }

        public void Save()
        {
            try
            {
                if (_path == null) _path = Path.Combine(Application.persistentDataPath, "coopfall", "config.json");
                File.WriteAllText(_path, Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e) { Log.Warn("config save failed: " + e.Message); }
        }

        public static KeyCode ParseKey(string name, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), name, true); }
            catch { return fallback; }
        }

        public static Color ParseColor(string hex)
        {
            Color c;
            return ColorUtility.TryParseHtmlString(hex, out c) ? c : new Color(1f, 0.8f, 0.2f);
        }
    }
}
