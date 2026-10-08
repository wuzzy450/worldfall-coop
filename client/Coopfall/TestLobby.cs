using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// "-coopfall-scenario lobby": two-game test of the world settings (tools/run-lobby.sh).
    /// Host: sets a password, approval, "no destructive powers" and "no speed changes", lets every
    /// joiner in, removes the guest once it spectates, exports diagnostics.
    /// Guest (started with "-coopfall-fake-mod"): gets the password box, then the mods box, drops the
    /// fake mod and reconnects, checks the guest rules, travels away, comes back as a spectator, tries
    /// to possess a creature and waits to be removed. Both log "TEST lobby: ..." and take screenshots
    /// (lobby-*.png in the profile folder).
    /// </summary>
    public partial class TestDriver
    {
        /// <summary>"-coopfall-fake-mod NAME": pretend this gameplay mod is installed (tests the mods check).</summary>
        public static readonly List<string> FakeMods = ReadFakeMods();

        private static List<string> ReadFakeMods()
        {
            var list = new List<string>();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-coopfall-fake-mod", StringComparison.OrdinalIgnoreCase)) list.Add(args[i + 1]);
            return list;
        }

        private int _lb;
        private float _lbAt, _lbStart = -1f;
        private bool _lbShotRequest;
        private string _lbSpeedBefore;
        private string _lbGuestId;

        private void LShot(string name)
        {
            string path = Path.Combine(_shotDir, "lobby-" + name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Log.Info("TEST lobby: screenshot " + name);
        }

        private void Lobby(string what) { Log.Info("TEST lobby: " + what); }

        private void LobbyTick()
        {
            float now = Time.unscaledTime;
            if (_lbStart < 0f) _lbStart = now;
            if (_lb >= 0 && now - _lbStart > 420f) { Lobby("FAIL timed out at step " + _lb); Lobby("done"); _lb = -1; return; }
            try
            {
                if (_s.IsHost && _s.RoomId == "shared") HostLobby(now);
                else if (!_s.IsHost || _lb > 0) GuestLobby(now);
            }
            catch (Exception e) { Lobby("FAIL step " + _lb + ": " + e); _lb = -1; }
        }

        private void HostLobby(float now)
        {
            if (_lb < 0 || _lb >= 100) { if (_lb >= 100) HostLobbySteps(now); return; }
            _lb = 100;
            _lbAt = now + 5f;
        }

        private void HostLobbySteps(float now)
        {
            UI().MenuOpen = _lb < 104 || _lb == 105;
            // let in whoever asks (after a screenshot of the first request)
            if (_s.JoinRequests.Count > 0)
            {
                if (!_lbShotRequest) { _lbShotRequest = true; LShot("host-request"); _lbAt = now + 1.5f; return; }
                if (now >= _lbAt)
                {
                    JoinRequest jr = _s.JoinRequests[0];
                    Lobby("letting in " + jr.name + (jr.spectate ? " (to watch)" : ""));
                    _s.Answer(jr, true);
                    _lbShotRequest = false;
                }
                return;
            }
            if (now < _lbAt) return;
            switch (_lb)
            {
                case 100:
                    if (!_s.IsAdmin) { Lobby("FAIL host is not admin of the shared world"); _lb = -1; return; }
                    _s.SendSettings(new Newtonsoft.Json.Linq.JObject
                    {
                        ["password"] = "pw", ["approval"] = true, ["everyoneAdmin"] = false, ["guestPowers"] = "safe", ["guestSpeed"] = false, ["spectators"] = true,
                        ["blocked"] = new Newtonsoft.Json.Linq.JArray(PowerSync.DestructivePowers().ToArray()),
                    });
                    Lobby("settings sent (" + PowerSync.DestructivePowers().Count + " destructive powers blocked)");
                    _lb = 101; _lbAt = now + 3f;
                    break;
                case 101:
                    RoomInfo r = _s.CurrentRoom;
                    Lobby("settings back from the relay: password " + r.hasPassword + ", approval " + r.approval + ", powers " + r.guestPowers +
                          " (" + r.blocked.Count + "), speed " + r.guestSpeed);
                    UI().ScrollMenuToEnd();
                    _lbAt = now + 1f;
                    _lb = 105;
                    break;
                case 105:
                    LShot("host-settings");
                    _lb = 102;
                    break;
                case 102:
                    // wait for the guest to come back as a spectator
                    foreach (PlayerInfo p in _s.Players)
                        if (p.id != _s.MyId && p.room == _s.RoomId && p.spectator)
                        {
                            _lbGuestId = p.id;
                            Lobby(p.name + " is spectating, ping " + p.ping + " ms");
                            _lb = 103; _lbAt = now + 14f;
                        }
                    break;
                case 103:
                    LShot("host-players");
                    Lobby("removing the spectator");
                    _s.Kick(_lbGuestId);
                    _lb = 104; _lbAt = now + 3f;
                    break;
                case 104:
                    Lobby("players in my world after kick: " + _s.OthersInRoom());
                    string zip = _s.ExportDiagnostics();
                    CheckZip(zip);
                    Lobby("done");
                    _lb = -1;
                    break;
            }
        }

        private static CoopUI UI() { return CoopMod.Instance.UI; }

        private void CheckZip(string zip)
        {
            if (zip == null || !File.Exists(zip)) { Lobby("FAIL no diagnostics zip"); return; }
            var names = new List<string>();
            bool leak = false;
            string user = Environment.UserName;
            using (ZipArchive a = ZipFile.OpenRead(zip))
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    names.Add(e.FullName);
                    if (e.FullName.EndsWith(".png")) continue;
                    using (var r = new StreamReader(e.Open()))
                        if (user.Length >= 3 && System.Text.RegularExpressions.Regex.IsMatch(r.ReadToEnd(), "\b" + System.Text.RegularExpressions.Regex.Escape(user) + "\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { leak = true; Lobby("FAIL user name found in " + e.FullName); }
                }
            Lobby("diagnostics zip " + new FileInfo(zip).Length / 1024 + " KB: " + string.Join(", ", names.ToArray()) + (leak ? "" : " - no user name inside"));
        }

        private void GuestLobby(float now)
        {
            if (_lb < 0 || now < _lbAt) return;
            switch (_lb)
            {
                case 0:
                    if (_s.PasswordRoom == null) return;
                    Lobby("asked for a password: " + _s.PasswordPrompt);
                    LShot("guest-password");
                    _lb = 1; _lbAt = now + 1.5f;
                    break;
                case 1:
                    _s.JoinWithPassword("wrong");
                    _lb = 2;
                    break;
                case 2:
                    if (_s.PasswordRoom == null) return;
                    Lobby("wrong password refused: " + _s.PasswordPrompt);
                    _s.JoinWithPassword("pw");
                    _lb = 3;
                    break;
                case 3:
                    if (_s.ModMismatch == null) return;
                    Lobby("mods refused: " + _s.ModMismatch.ToString(Newtonsoft.Json.Formatting.None));
                    LShot("guest-mods");
                    _lb = 4; _lbAt = now + 1.5f;
                    break;
                case 4:
                    _s.ModMismatch = null;
                    FakeMods.Clear();
                    ModScan.Scan();
                    _s.Disconnect();
                    _lb = 5; _lbAt = now + 2f;
                    break;
                case 5:
                    _s.Connect();   // joins the shared world again (password remembered), now waiting for approval
                    _lb = 6;
                    break;
                case 6:
                    if (!_s.InWorld || _s.RoomId != "shared" || _s.IsHost) return;
                    Lobby("in as a guest: restricted " + _s.Restricted + ", bomb allowed " + _s.PowerAllowed("bomb") + ", rain allowed " +
                          _s.PowerAllowed("rain") + ", speed allowed " + _s.SpeedAllowed);
                    _lb = 7; _lbAt = now + 4f;
                    break;
                case 7:
                    _lbSpeedBefore = Config.time_scale_asset?.id;
                    string other = _lbSpeedBefore == "x5" ? "x2" : "x5";
                    Config.setWorldSpeed(other);
                    Lobby("set speed " + other + " (was " + _lbSpeedBefore + ")");
                    _lb = 8; _lbAt = now + 2f;
                    break;
                case 8:
                    Lobby("speed now " + Config.time_scale_asset?.id + (Config.time_scale_asset?.id == _lbSpeedBefore ? " - put back" : " - FAIL not put back"));
                    _s.Travel("world-lbt" + UnityEngine.Random.Range(1000, 9999), "Lobby test", true, true);
                    _lb = 9;
                    break;
                case 9:
                    if (!_s.InWorld || _s.RoomId == null || !_s.RoomId.StartsWith("world-lbt")) return;
                    Lobby("away in " + _s.RoomId + ", coming back to watch");
                    _s.Travel("shared", null, false, false, false, true);
                    _lb = 10;
                    break;
                case 10:
                    if (!_s.InWorld || _s.RoomId != "shared" || !WorldBoxApi.WorldReady) return;
                    Lobby("watching: spectating " + _s.Spectating + ", any power allowed " + _s.PowerAllowed("rain"));
                    _lb = 11; _lbAt = now + 4f;
                    break;
                case 11:
                    Actor pick = null;
                    foreach (Actor a in World.world.units) if (a != null && a.isAlive() && a.canBePossessed()) { pick = a; break; }
                    if (pick == null) { Lobby("no creature to possess - skipped"); _lb = 13; return; }
                    ControllableUnit.setControllableCreature(pick);
                    Lobby("tried to possess #" + pick.getID());
                    _lb = 12; _lbAt = now + 2f;
                    break;
                case 12:
                    Lobby("possessing after 2 s: " + ControllableUnit.isControllingUnit() + (ControllableUnit.isControllingUnit() ? " - FAIL" : " - let go"));
                    UI().MenuOpen = true;
                    LShot("guest-watching");
                    _lb = 13;
                    break;
                case 13:
                    if (_s.RoomId != null) return;
                    UI().MenuOpen = false;
                    Lobby("removed: phase " + _s.Phase + ", status " + _s.Status);
                    LShot("guest-kicked");
                    _lb = 14; _lbAt = now + 3f;
                    break;
                case 14:
                    _s.Travel("shared");
                    _lb = 15; _lbAt = now + 4f;
                    break;
                case 15:
                    Lobby("rejoin right after removal: in world " + _s.InWorld + " (" + _s.Status + ")");
                    Lobby("done");
                    _lb = -1;
                    break;
            }
        }
    }
}
