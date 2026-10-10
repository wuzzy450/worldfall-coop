using System;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// With "-coopfall-profile", WorldBox's history database path is this profile's own file from
    /// the start (DBManager.resetDataPath), so two games on one PC never share or swap it mid-load.
    /// </summary>
    public static class ProfileDbPatch
    {
        private static string _mine;
        private static AccessTools.FieldRef<string> _path;

        public static void Install()
        {
            _mine = ProfileDb.Mine();
            if (_mine == null) return;
            try
            {
                _path = AccessTools.StaticFieldRefAccess<string>(AccessTools.Field(typeof(db.DBManager), "_dbpath"));
                new Harmony("coopfall.profiledb").Patch(AccessTools.Method(typeof(db.DBManager), "resetDataPath"), prefix: new HarmonyMethod(typeof(ProfileDbPatch), nameof(Prefix)));
                Log.Info("profile db: this game's history database is " + _mine);
            }
            catch (Exception e) { Log.Warn("profile db: path not patched (" + e.Message + "), moving it after the game opens it"); }
        }

        private static bool Prefix()
        {
            _path() = _mine;
            return false;
        }
    }
}
