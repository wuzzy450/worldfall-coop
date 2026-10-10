using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's civil life, per player in ticks (the same treatment as PlayerScope's features):
    /// - Quests: tasks, trials and duels. Each player's quest log, bout and vigil are their own
    ///   statics; Quests.Update runs in ticks once per player; the menus' and conversations' entry
    ///   points (accept, give up, hand in, a kill or a haul you made) travel as relayed calls, a
    ///   quest as the text Worldfall saves it as. Trial and sparring blows are counted in the
    ///   in-tick hit check, for the players whose creatures strike or are struck.
    /// - Births: naming your newborn travels as one call (the baby, the name). The rest of
    ///   Births.Update only spots your newborn and opens the naming box, and its sprint cap is
    ///   part of your sampled controls: it stays on the frame.
    /// - Kin tracking (PeopleMet: family members you know, people you talked to) writes creature
    ///   data: per player in ticks, and a conversation's "met" note is relayed.
    /// </summary>
    public static class CivilInTick
    {
        private static MethodInfo _questsUpdate, _keepFamily, _sparBlow, _trialBlow, _finishNaming;
        private static FieldInfo _log, _naming, _nameText, _nameFocus, _giver, _kind, _fromBoard;

        public static void Install(Harmony h, Assembly wf, List<string> features)
        {
            Type quests = wf.GetType("FirstPerson.Quests", false), quest = wf.GetType("FirstPerson.Quest", false);
            if (quests != null && quest != null)
            {
                _questsUpdate = AccessTools.Method(quests, "Update", new[] { typeof(Actor), typeof(float), typeof(Action<string>) });
                MethodInfo write = AccessTools.Method(quests, "Write", new[] { quest });
                MethodInfo parse = AccessTools.Method(quests, "Parse", new[] { typeof(string) });
                MethodInfo accept = AccessTools.Method(quests, "Accept", new[] { quest, typeof(Actor), typeof(Action<string>) });
                _sparBlow = AccessTools.Method(quests, "SparBlow");
                _trialBlow = AccessTools.Method(quests, "TrialBlow", new[] { typeof(BaseSimObject), typeof(BaseSimObject), typeof(bool), typeof(bool) });
                _log = AccessTools.Field(quests, "Log");
                _giver = AccessTools.Field(quest, "Giver"); _kind = AccessTools.Field(quest, "Kind"); _fromBoard = AccessTools.Field(quest, "FromBoard");
                if (_questsUpdate != null && write != null && parse != null && accept != null && _log != null && _giver != null && _kind != null && _fromBoard != null
                    && PlayerScope.Statics(h, quests))
                {
                    WorldCalls.TextCodec(quest, q => (string)write.Invoke(null, new[] { q }), s => QuestFrom(s, parse));
                    PlayerScope.Relay(h, quests, "Accept", "Abandon", "TakeNotice", "HandInAtBoard", "Reward", "Fail", "Converted", "OnKill", "OnGathered", "EndTrialState");
                    // the conversation goes on from Accept's answer: the caller's own guess (the replay decides)
                    h.Patch(accept, postfix: new HarmonyMethod(typeof(CivilInTick), nameof(AcceptPostfix)));
                    if (_sparBlow == null || _trialBlow == null) Log.Warn("lockstep: Worldfall's Quests.SparBlow/TrialBlow not found: trial hits aren't counted in lockstep");
                    features.Add("quests and trials");
                }
                else { _questsUpdate = null; Log.Warn("lockstep: Worldfall's Quests changed (methods not found): quests and trials stay on this PC in lockstep"); }
            }
            // "you" as the hit check knows it (trial blows, sparring, guarding): the acting player's creature
            Type blocks = wf.GetType("FirstPerson.Blocks", false);
            MethodInfo host = blocks == null ? null : AccessTools.PropertyGetter(blocks, "Host");
            if (host != null) h.Patch(host, prefix: new HarmonyMethod(typeof(CivilInTick), nameof(HostPrefix)));
            else Log.Warn("lockstep: Worldfall's Blocks.Host not found: trial hits may count for the wrong player");

            Type births = wf.GetType("FirstPerson.Births", false);
            _finishNaming = births == null ? null : AccessTools.Method(births, "FinishNaming", new[] { typeof(Actor), typeof(bool) });
            _naming = births == null ? null : AccessTools.Field(births, "Naming");
            _nameText = births == null ? null : AccessTools.Field(births, "NameText");
            _nameFocus = births == null ? null : AccessTools.Field(births, "NameFocus");
            MethodInfo name = AccessTools.Method(typeof(CivilInTick), nameof(NameBaby));
            if (_finishNaming != null && _naming != null && _nameText != null && _nameFocus != null && WorldCalls.Register(h, name))
            {
                h.Patch(_finishNaming, prefix: new HarmonyMethod(typeof(CivilInTick), nameof(FinishNamingPrefix)));
                // made in a conversation: the lovers' night, adopting, budding
                PlayerScope.Relay(h, births, "Conceive", "Adopt", "BringForth");
                features.Add("naming newborns");
            }
            else Log.Warn("lockstep: Worldfall's Births.FinishNaming not found: a newborn's name may be given on one PC only");

            Type met = wf.GetType("FirstPerson.PeopleMet", false);
            _keepFamily = met == null ? null : AccessTools.Method(met, "KeepFamily", new[] { typeof(Actor), typeof(float) });
            if (_keepFamily != null && PlayerScope.Statics(h, met))
            {
                PlayerScope.Relay(h, met, "Note");
                features.Add("kin tracking");
            }
            else { _keepFamily = null; Log.Warn("lockstep: Worldfall's PeopleMet.KeepFamily not found: the people you know may differ between PCs"); }
        }

        /// <summary>The frame updates these replace (held back outside ticks while lockstep runs).</summary>
        public static readonly string[] Gated = { "Quests.Update", "PeopleMet.KeepFamily" };

        /// <summary>PlayerScope.RunTick, inside the player's scope.</summary>
        internal static void RunPlayer(Actor you, float dt, Action<string> toast, Action<string, Action> run)
        {
            if (_questsUpdate != null) run("quests", () => _questsUpdate.Invoke(null, new object[] { you, dt, toast }));
            if (_keepFamily != null) run("kin tracking", () => _keepFamily.Invoke(null, new object[] { you, dt }));
        }

        // ------------------------------------------------------------------ quests

        /// <summary>A relayed quest: the acting player's own one if it is in their log (calls compare by reference).</summary>
        private static object QuestFrom(string s, MethodInfo parse)
        {
            object q = parse.Invoke(null, new object[] { s });
            if (q == null) return null;
            if (_log.GetValue(null) is IList log)
                foreach (object x in log)
                    if (Equals(_giver.GetValue(x), _giver.GetValue(q)) && Equals(_kind.GetValue(x), _kind.GetValue(q)) && Equals(_fromBoard.GetValue(x), _fromBoard.GetValue(q)))
                        return x;
            return q;
        }

        private static void AcceptPostfix(ref bool __result)
        {
            if (!LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick) return;
            __result = !(_log.GetValue(null) is IList log) || log.Count < 5;
        }

        private static bool HostPrefix(ref Actor __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = PlayerScope.CurrentBody;
            return false;
        }

        /// <summary>The in-tick hit check, before the blow lands: a sparring partner's blow is held under a kill.</summary>
        internal static void BeforeHit(ref AttackData data, BaseSimObject by, BaseSimObject target)
        {
            if (_sparBlow == null) return;
            var args = new object[] { data, target };
            foreach (int p in Players(by, target))
            {
                PlayerScope.Run(p, () =>
                {
                    try { _sparBlow.Invoke(null, args); }
                    catch (Exception e) { Fail("sparring", e); }
                });
            }
            data = (AttackData)args[0];
        }

        /// <summary>After the blow: trials count it (hits given and taken, shots from afar).</summary>
        internal static void AfterHit(BaseSimObject by, BaseSimObject target, bool hit, bool projectile)
        {
            if (_trialBlow == null || !hit) return;
            foreach (int p in Players(by, target))
                PlayerScope.Run(p, () =>
                {
                    try { _trialBlow.Invoke(null, new object[] { by, target, hit, projectile }); }
                    catch (Exception e) { Fail("trial blows", e); }
                });
        }

        private static readonly List<int> _players = new List<int>();

        private static List<int> Players(BaseSimObject by, BaseSimObject target)
        {
            _players.Clear();
            foreach (BaseSimObject o in new[] { by, target })
            {
                if (!(o is Actor a)) continue;
                int p = LockstepControl.OwnerOf(a.getID());
                if (p != 0 && !_players.Contains(p)) _players.Add(p);
            }
            _players.Sort();
            return _players;
        }

        // ------------------------------------------------------------------ births

        /// <summary>
        /// Births.FinishNaming from this PC's naming box: the name travels; the box closes here now.
        /// </summary>
        private static bool FinishNamingPrefix(Actor you, bool keep)
        {
            if (!LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick) return true;
            Actor baby = _naming.GetValue(null) as Actor;
            string text = _nameText.GetValue(null) as string ?? "";
            _naming.SetValue(null, null);
            _nameFocus.SetValue(null, false);
            if (baby != null && you != null) NameBaby(you, baby, text, keep);
            return false;
        }

        /// <summary>Relayed: in a tick on every PC, Worldfall names the baby (this PC's own naming box kept).</summary>
        public static void NameBaby(Actor you, Actor baby, string text, bool keep)
        {
            if (!LockstepClock.InTick) return;
            object naming = _naming.GetValue(null), nameText = _nameText.GetValue(null), focus = _nameFocus.GetValue(null);
            try
            {
                _naming.SetValue(null, baby);
                _nameText.SetValue(null, text);
                _finishNaming.Invoke(null, new object[] { you, keep });
            }
            catch (Exception e) { Fail("naming", e); }
            finally
            {
                bool same = ReferenceEquals(naming, baby);
                _naming.SetValue(null, same ? null : naming);
                _nameText.SetValue(null, nameText);
                _nameFocus.SetValue(null, same ? (object)false : focus);
            }
        }

        private static readonly HashSet<string> _failed = new HashSet<string>();

        private static void Fail(string what, Exception e)
        {
            if (_failed.Add(what)) Log.Error("lockstep: Worldfall's " + what + " (in a tick): " + (e.InnerException ?? e));
        }
    }
}
