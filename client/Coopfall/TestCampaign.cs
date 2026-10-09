using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Worldfall's per-player campaign features in the "lockstep" scenario (Lockstep.PlayerScope).
    /// From 170 s each player controls a king (the host the first, the guest another). At 175 s the
    /// host declares war on a rival through Worldfall's king's council and at 180 s sends half
    /// its warriors; the guest plants a war flag by its king and assigns five warriors to it.
    /// At 195 s the guest hits a creature of another realm in front of witnesses (a crime).
    /// At 230 s and 260 s both PCs log every player's council and war map as they have them.
    /// </summary>
    public partial class TestDriver
    {
        private int _lsCampStep;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private bool LockstepKingTime(float now) => now - _lsStart > 168f && WorldfallBridge.Present;

        private void LockstepCampaignTick(float now)
        {
            if (NoPossess || !WorldfallBridge.Present) return;
            float t = now - _lsStart;
            Assembly wf = WorldfallBridge.Assembly;
            Type modT = wf.GetType("FirstPerson.WorldBoxMod", false);
            object mod = modT?.GetProperty("Instance", Any)?.GetValue(null, null);
            if (mod == null) return;
            try
            {
                if (_lsCampStep == 0 && t > 170f)
                {
                    _lsCampStep = 1;
                    var kings = new List<Actor>();
                    foreach (Kingdom k in World.world.kingdoms)
                        if (k != null && k.isAlive() && k.isCiv() && k.king != null && k.king.isAlive()) kings.Add(k.king);
                    kings.Sort((a, b) => a.getID().CompareTo(b.getID()));
                    Actor king = kings.Count > (_s.IsHost ? 0 : 1) ? kings[_s.IsHost ? 0 : 1] : null;
                    if (king == null) { Log.Warn("TEST lockstep: campaign: not enough kings (" + kings.Count + ")"); _lsCampStep = 99; return; }
                    if (ControllableUnit.isControllingUnit()) ControllableUnit.clear(false);
                    ControllableUnit.setControllableCreature(king);
                    _lsPossessAt = now;
                    Log.Info("TEST lockstep: campaign: possessing king #" + king.getID() + " of " + king.kingdom.name);
                }
                Actor me = ControllableUnit.getControllableUnit();
                if (_lsCampStep == 1 && t > 175f)
                {
                    _lsCampStep = 2;
                    if (me == null || !me.isAlive()) { Log.Warn("TEST lockstep: campaign: no king"); return; }
                    object royal = modT.GetField("Royal", Any).GetValue(mod);
                    Action<string> toast = s => Log.Info("TEST lockstep: campaign toast: " + s);
                    Action horn = () => { };
                    if (_s.IsHost)
                    {
                        Type royalT = royal.GetType();
                        var rivals = (IList)royalT.GetMethod("Rivals", Any).Invoke(null, new object[] { me.kingdom });
                        Kingdom rival = rivals != null && rivals.Count > 0 ? (Kingdom)rivals[0] : null;
                        if (rival == null) { Log.Warn("TEST lockstep: campaign: no rival for " + me.kingdom.name); return; }
                        _lsRival = rival.getID();
                        Log.Info("TEST lockstep: campaign: #" + me.getID() + " declares war on " + rival.name);
                        royalT.GetMethod("DeclareWar", Any).Invoke(royal, new object[] { me, rival, toast, horn });
                    }
                    else
                    {
                        object map = modT.GetField("WarMap", Any).GetValue(mod);
                        Type mapT = map.GetType();
                        object flag = mapT.GetMethod("Plant", Any).Invoke(map, new object[] { me.current_tile, me.kingdom, 0 });
                        var warriors = new List<Actor>();
                        foreach (Actor a in World.world.units)
                            if (a != null && a != me && a.isAlive() && a.kingdom == me.kingdom && a.isWarrior() && warriors.Count < 5) warriors.Add(a);
                        Log.Info("TEST lockstep: campaign: #" + me.getID() + " plants flag " + (flag == null ? "none" : "#" + flag.GetType().GetField("Number").GetValue(flag)) + " and assigns " + warriors.Count + " warriors");
                        if (flag != null)
                        {
                            MethodInfo assign = mapT.GetMethod("Assign", Any);
                            object[] args = { royal, me, warriors, flag, 0 };
                            assign.Invoke(map, args);
                        }
                    }
                }
                if (_lsCampStep == 2 && t > 180f)
                {
                    _lsCampStep = 3;
                    if (_s.IsHost && me != null && me.isAlive() && _lsRival != 0)
                    {
                        object royal = modT.GetField("Royal", Any).GetValue(mod);
                        Kingdom rival = World.world.kingdoms.get(_lsRival);
                        Log.Info("TEST lockstep: campaign: #" + me.getID() + " sends half its army against " + (rival?.name ?? "?"));
                        if (rival != null)
                            royal.GetType().GetMethod("SendArmy", Any).Invoke(royal, new object[] { me, rival, 0.5f, (Action<string>)(s => Log.Info("TEST lockstep: campaign toast: " + s)), (Action)(() => { }), null });
                    }
                }
                if (_lsCampStep == 3 && t > 195f)
                {
                    _lsCampStep = 4;
                    if (!_s.IsHost && me != null && me.isAlive())
                    {
                        Actor victim = null;
                        float best = float.MaxValue;
                        foreach (Actor a in World.world.units)
                        {
                            if (a == null || a == me || !a.isAlive() || a.kingdom == me.kingdom || a.kingdom == null || !a.kingdom.isCiv()) continue;
                            float d = (a.current_position - me.current_position).sqrMagnitude;
                            if (d < best) { best = d; victim = a; }
                        }
                        Log.Info("TEST lockstep: campaign: #" + me.getID() + " assaults " + (victim == null ? "nobody" : "#" + victim.getID() + " of " + victim.kingdom.name));
                        if (victim != null) wf.GetType("FirstPerson.Law", false)?.GetMethod("OnHit", Any)?.Invoke(null, new object[] { victim });
                    }
                }
                if ((_lsCampStep == 4 && t > 230f) || (_lsCampStep == 5 && t > 260f))
                {
                    _lsCampStep++;
                    Log.Info("TEST lockstep: campaign state at " + (int)t + " s: " + Lockstep.PlayerScope.Describe());
                }
            }
            catch (Exception e) { Log.Error("TEST lockstep: campaign: " + (e.InnerException ?? e)); _lsCampStep = 99; }
        }

        private long _lsRival;
    }
}
