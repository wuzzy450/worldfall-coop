using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// With "-coopfall-profile" two games run on one PC, but WorldBox keeps its history database in a
    /// fixed file (persistentDataPath/stats.s3db). Both games writing it at once broke it (UNIQUE
    /// constraint errors, then a native crash). Point this game's database at the profile folder.
    /// </summary>
    public static class ProfileDb
    {
        private static FieldInfo _path, _conn;
        private static bool _failed;
        private static float _next;

        /// <summary>This profile's own database file, or null when no "-coopfall-profile" is in use.</summary>
        public static string Mine()
        {
            if (Log.Dir == null) return null;
            string mine = Path.Combine(Log.Dir, "stats.s3db");
            return Path.GetFullPath(Path.GetDirectoryName(mine)) == Path.GetFullPath(Path.Combine(Application.persistentDataPath, "coopfall")) ? null : mine;
        }

        /// <summary>
        /// Fallback for when the path patch (Lockstep.ProfileDbPatch) isn't in: moving the open
        /// database later can catch a world load between two of its steps ("database schema has
        /// changed", the load then hangs), so the patch sets the path before the game opens it.
        /// </summary>
        public static void Tick()
        {
            if (_failed || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            string mine = Path.Combine(Log.Dir ?? "", "stats.s3db");
            string shared = Application.persistentDataPath + "/stats.s3db";
            if (Log.Dir == null || Path.GetFullPath(Path.GetDirectoryName(mine)) == Path.GetFullPath(Path.Combine(Application.persistentDataPath, "coopfall"))) { _failed = true; return; }
            try
            {
                if (_path == null)
                {
                    const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;
                    _path = typeof(db.DBManager).GetField("_dbpath", F);
                    _conn = typeof(db.DBManager).GetField("_dbconn", F);
                    if (_path == null || _conn == null) { _failed = true; Log.Warn("profile db: DBManager fields not found"); return; }
                }
                string cur = _path.GetValue(null) as string;
                if (cur == null || Path.GetFullPath(cur) == Path.GetFullPath(mine)) return;
                // The game (re)opened the shared file: move to our own copy, carrying over what it holds.
                bool open = _conn.GetValue(null) != null;
                if (open) { db.DBInserter.waitForAsync(); db.DBManager.closeDB(); }
                try { if (File.Exists(shared)) File.Copy(shared, mine, true); else if (File.Exists(mine)) File.Delete(mine); }
                catch (Exception e) { Log.Warn("profile db: copy failed, starting empty: " + e.Message); try { File.Delete(mine); } catch { } }
                _path.SetValue(null, mine);
                if (open)
                {
                    // the copy can catch the other game mid-write ("database disk image is malformed"):
                    // then start empty rather than leave the game without its database (a load hung on that)
                    try { db.DBManager.openDB(); }
                    catch (Exception e)
                    {
                        Log.Warn("profile db: copied database unreadable (" + e.Message + "), starting empty");
                        try { db.DBManager.closeDB(); } catch { }
                        try { File.Delete(mine); } catch { }
                        db.DBManager.openDB();
                    }
                }
                Log.Info("profile db: history database moved to " + mine);
            }
            catch (Exception e) { _failed = true; Log.Warn("profile db: " + e); }
        }
    }
}
