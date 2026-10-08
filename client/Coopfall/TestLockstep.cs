using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// "-coopfall-scenario lockstep": host and guest both run lockstep; once the world runs, each
    /// uses a god power every few seconds (alternating), through the lockstep inputs. Both log
    /// progress; the host compares checksums (any drift is logged and re-synced by the session).
    /// </summary>
    public partial class TestDriver
    {
        private static readonly string[] LockstepPowers =
        {
            "meteorite", "lightning", "fire", "bomb", "evil_mage", "white_mage", "tornado", "acid", "rain",
            "plague", "curse", "dragon", "grenade", "lava", "earthquake", "madness", "blessing", "necromancer",
        };
        private float _lsStart = -1f, _lsNextPower, _lsNextLog;
        private int _lsUsed;
        private bool _lsDone;
        private const float LockstepSeconds = 300f;

        private void LockstepTick()
        {
            Lockstep.LockstepSession ls = _s.Lockstep;
            float now = Time.unscaledTime;
            if (now >= _lsNextLog)
            {
                _lsNextLog = now + 10f;
                Log.Info("TEST lockstep: " + ls.StatusLine() + ", players " + (_s.OthersInRoom() + 1) + ", powers used " + _lsUsed + ", checks ok " + ls.Checked + ", desyncs " + ls.Desyncs + ", late inputs " + Lockstep.LockstepInput.Late);
            }
            if (_lsDone || !_s.Online || !_s.InWorld || _s.OthersInRoom() == 0 || !ls.Active || Lockstep.LockstepClock.Granted < 100) return;
            if (_lsStart < 0f) { _lsStart = now; _lsNextPower = now + (_s.IsHost ? 5f : 6.5f); Log.Info("TEST lockstep: running as " + (_s.IsHost ? "host" : "guest")); }
            if (now - _lsStart > LockstepSeconds)
            {
                _lsDone = true;
                Log.Info("TEST lockstep: done - " + ls.StatusLine() + ", powers used " + _lsUsed + ", checks ok " + ls.Checked + ", desyncs " + ls.Desyncs + (ls.LastDesync != null ? " (last: " + ls.LastDesync + ")" : ""));
                return;
            }
            if (now < _lsNextPower) return;
            _lsNextPower = now + 3f;
            // a creature's tile picked with the local dice (each player clicks somewhere different)
            var units = World.world.units.getSimpleList();
            if (units.Count == 0) return;
            Actor a = units[Random.Range(0, units.Count)];
            if (a == null || a.current_tile == null) return;
            string p = LockstepPowers[(_lsUsed * 2 + (_s.IsHost ? 0 : 1)) % LockstepPowers.Length];
            if (CoopMod.Instance.Powers.UseLocal(p, a.current_tile)) _lsUsed++;
        }
    }
}
