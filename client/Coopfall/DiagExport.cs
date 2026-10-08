using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// "Export diagnostics": one zip with everything a bug report needs (Coopfall's log, Unity's
    /// Player.log, settings, the mod list, session info and the newest sync reports), written to
    /// coopfall/reports. The Windows user name and home folder are replaced by &lt;user&gt; and the
    /// server address is left out, so the zip can be posted publicly.
    /// </summary>
    public static class DiagExport
    {
        public static string Create(CoopSession s)
        {
            Log.Flush();
            string dir = Path.Combine(Log.Dir ?? Log.DataDir(), "reports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "coopfall-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddText(zip, "info.txt", Info(s));
                AddFile(zip, "log.txt", Path.Combine(Log.Dir, "log.txt"));
                AddFile(zip, "unity.log", Path.Combine(Log.Dir, "unity.log"));
                // with -logFile, Unity's log is coopfall's unity.log already
                if (!string.Equals(Path.GetFullPath(Application.consoleLogPath ?? "x"), Path.GetFullPath(Path.Combine(Log.Dir, "unity.log")), StringComparison.OrdinalIgnoreCase))
                    AddFile(zip, "Player.log", Application.consoleLogPath);
                AddFile(zip, "Player-prev.log", Path.Combine(Path.GetDirectoryName(Application.consoleLogPath) ?? "", "Player-prev.log"));
                AddFile(zip, "config.json", Path.Combine(Log.Dir, "config.json"));
                string diag = Path.Combine(Log.Dir, "diag");
                if (Directory.Exists(diag))
                {
                    var files = new List<FileInfo>(new DirectoryInfo(diag).GetFiles());
                    files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    for (int i = 0; i < files.Count && i < 8; i++) AddFile(zip, "diag/" + files[i].Name, files[i].FullName);
                }
            }
            // keep the 10 newest
            var old = new List<string>(Directory.GetFiles(dir, "coopfall-report-*.zip"));
            old.Sort(StringComparer.Ordinal);
            for (int i = 0; i < old.Count - 10; i++) try { File.Delete(old[i]); } catch { }
            Log.Info("diagnostics exported to " + Scrub(path));
            return path;
        }

        private static string Info(CoopSession s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Coopfall " + CoopMod.Version + ", protocol v" + CoopSession.ProtocolVersion);
            sb.AppendLine("WorldBox " + Application.version + ", Unity " + Application.unityVersion);
            sb.AppendLine("OS: " + SystemInfo.operatingSystem + ", " + SystemInfo.systemMemorySize + " MB RAM, " + SystemInfo.graphicsDeviceName);
            sb.AppendLine("Worldfall: " + (WorldfallBridge.Present ? "yes" : "no"));
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine();
            sb.AppendLine("Phase: " + s.Phase + ", online: " + s.Online + ", ping: " + s.PingMs + " ms");
            sb.AppendLine("World: " + (s.RoomId ?? "-") + (s.IsHost ? " (host)" : "") + (s.Spectating ? " (spectating)" : ""));
            sb.AppendLine("Players: " + s.Players.Count + ", in my world: " + (s.OthersInRoom() + (s.RoomId != null ? 1 : 0)));
            foreach (PlayerInfo p in s.Players)
                sb.AppendLine("  " + p.name + " in " + (string.IsNullOrEmpty(p.room) ? "-" : p.room) + (p.host ? " host" : "") +
                              (p.spectator ? " spectating" : "") + ", game " + p.game + ", ping " + p.ping + " ms, " + p.mods + " mods");
            if (CoopMod.Instance?.Sync != null)
            {
                WorldSync w = CoopMod.Instance.Sync;
                sb.AppendLine("Live sync: " + (w.Disabled ? "off (old server)" : "on") + ", creatures " + w.UnitsLoaded + " in / " + w.UnitsRemoved +
                              " out, buildings " + w.BuildingsLoaded + " in / " + w.BuildingsRemoved + " out, far corrections " + w.UnitsCorrected);
            }
            sb.AppendLine();
            sb.AppendLine("Gameplay mods:");
            foreach (ModScan.Mod m in ModScan.Mods) sb.AppendLine("  " + m.id + " " + m.ver + " (" + m.where + ")");
            sb.AppendLine("Client-only mods: " + string.Join(", ", ModScan.ClientOnly.ToArray()));
            return sb.ToString();
        }

        private static void AddFile(ZipArchive zip, string name, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    zip.CreateEntryFromFile(path, name);
                    return;
                }
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs))
                    text = r.ReadToEnd();
                if (text.Length > 24 << 20) text = "[first part cut]\n" + text.Substring(text.Length - (24 << 20));
                if (name == "config.json") text = System.Text.RegularExpressions.Regex.Replace(text, "\"serverHost\":\\s*\"[^\"]*\"", "\"serverHost\": \"<hidden>\"");
                AddText(zip, name, text);
            }
            catch (Exception e) { AddText(zip, name + ".error.txt", "could not read: " + e.Message); }
        }

        private static void AddText(ZipArchive zip, string name, string text)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(Scrub(text));
        }

        /// <summary>Removes the Windows user name / home folder (they often are a real name).</summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    text = text.Replace(home, "<home>").Replace(home.Replace('\\', '/'), "<home>");
                }
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                    text = System.Text.RegularExpressions.Regex.Replace(text, "\\b" + System.Text.RegularExpressions.Regex.Escape(user) + "\\b", "<user>",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { }
            return text;
        }
    }
}
