using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// The gameplay mods this game runs, so the relay can keep players whose mods differ out of a
    /// world (a missing or different gameplay mod makes the worlds drift apart quietly).
    ///
    /// Scanned: DLLs and folders in StreamingAssets/mods (WorldBox's own mod loader) and folders
    /// in the game's Mods folder (NeoModLoader). A mod's version is a short hash of its files, so
    /// two builds with the same name still count as different.
    ///
    /// Left out (client-only, they don't change the shared world): Coopfall itself, the mods listed
    /// in config.json "clientOnlyMods" (Worldfall by default), and mods that ship a
    /// "coopfall.json" with {"clientOnly": true} (in a mod folder, or next to a DLL as
    /// "&lt;name&gt;.coopfall.json").
    /// </summary>
    public static class ModScan
    {
        public class Mod { public string id, ver, where; }

        private static List<Mod> _mods;
        public static readonly List<string> ClientOnly = new List<string>();

        public static List<Mod> Mods { get { if (_mods == null) Scan(); return _mods; } }

        public static void Scan()
        {
            var list = new List<Mod>();
            ClientOnly.Clear();
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in CoopMod.Instance?.Session?.Cfg?.clientOnlyMods ?? new string[0])
                if (!string.IsNullOrWhiteSpace(s)) skip.Add(s.Trim());
            try { ScanDir(Path.Combine(Application.streamingAssetsPath, "mods"), "mods", list, skip); }
            catch (Exception e) { Log.Warn("mod scan (StreamingAssets/mods): " + e.Message); }
            try { ScanDir(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", "Mods"), "Mods", list, skip); }
            catch (Exception e) { Log.Warn("mod scan (Mods): " + e.Message); }
            list.Sort((a, b) => string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase));
            _mods = list;
            var names = new List<string>();
            foreach (Mod m in list) names.Add(m.id + " " + m.ver);
            foreach (string fake in TestDriver.FakeMods) list.Add(new Mod { id = fake, ver = "test", where = "test" });
            Log.Info("gameplay mods: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())) +
                     (ClientOnly.Count > 0 ? "; client-only: " + string.Join(", ", ClientOnly.ToArray()) : ""));
        }

        public static JArray ToJson()
        {
            var arr = new JArray();
            foreach (Mod m in Mods) arr.Add(new JObject { ["id"] = m.id, ["ver"] = m.ver });
            return arr;
        }

        private static void ScanDir(string dir, string where, List<Mod> list, HashSet<string> skip)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.dll"))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                if (string.Equals(id, "Coopfall", StringComparison.OrdinalIgnoreCase)) continue;   // checked by the protocol version
                if (skip.Contains(id) || IsClientOnly(Path.Combine(dir, id + ".coopfall.json"))) { ClientOnly.Add(id); continue; }
                list.Add(new Mod { id = id, ver = HashFiles(new[] { f }, dir), where = where });
            }
            foreach (string d in Directory.GetDirectories(dir))
            {
                string id = ModId(d);
                if (skip.Contains(id) || skip.Contains(Path.GetFileName(d)) || IsClientOnly(Path.Combine(d, "coopfall.json"))) { ClientOnly.Add(id); continue; }
                string[] files = Directory.GetFiles(d, "*", SearchOption.AllDirectories);
                if (files.Length == 0) continue;
                list.Add(new Mod { id = id, ver = HashFiles(files, d), where = where });
            }
        }

        /// <summary>A folder mod's id: the GUID or name in its mod.json, else the folder name.</summary>
        private static string ModId(string dir)
        {
            try
            {
                string json = Path.Combine(dir, "mod.json");
                if (File.Exists(json))
                {
                    JObject o = JObject.Parse(File.ReadAllText(json));
                    string id = (string)o["GUID"] ?? (string)o["name"];
                    if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
                }
            }
            catch { }
            return Path.GetFileName(dir);
        }

        private static bool IsClientOnly(string path)
        {
            try { return File.Exists(path) && ((bool?)JObject.Parse(File.ReadAllText(path))["clientOnly"] ?? false); }
            catch { return false; }
        }

        /// <summary>Short hash over the files' relative paths and contents (logs and caches left out).</summary>
        private static string HashFiles(string[] files, string root)
        {
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            using (var sha = SHA256.Create())
            {
                var buf = new byte[1 << 16];
                foreach (string f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".log" || ext == ".pdb" || ext == ".tmp" || ext == ".cache") continue;
                    var info = new FileInfo(f);
                    if (info.Length > 256L << 20) continue;
                    byte[] name = Encoding.UTF8.GetBytes(f.Substring(root.Length).Replace('\\', '/').ToLowerInvariant() + "\n");
                    sha.TransformBlock(name, 0, name.Length, null, 0);
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash, 0, 5).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
