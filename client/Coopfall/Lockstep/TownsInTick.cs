using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Worldfall's towns and days in a shared world.
    /// - Day and night: Worldfall's clock runs on each PC's real time, and the calendar (a new day
    ///   at dawn, quests posted per day, seasons) follows it. In lockstep the time of day is part of
    ///   the world: it moves on in ticks (the host's day length), travels with each epoch, and every
    ///   PC draws it; a new day starts in the same tick everywhere. Sleeping until dawn is an input.
    /// - Shops: which house in a town is its smith or tailor is picked in ticks, town by town in ID
    ///   order (Worldfall picks them near your own creature, on your PC).
    /// - House plots: placing your plot and the house being finished travel as inputs.
    /// - Town messages (Hub.Toast) in ticks show on the acting player's PC only.
    /// - Memories (Chronicle): each PC keeps its own sweep; opinions and notes travel as inputs.
    /// - Independence (Freedom.Declare) travels as an input.
    /// The rest of the towns' frame code stays on the owner's PC: its world changes travel as
    /// relayed calls and data writes (DataCalls), and conversation choices whole (TalkInTick).
    /// </summary>
    public static class TownsInTick
    {
        private static FieldInfo _dayTime, _settings, _dayMinutes, _slept, _sleepStart, _sleptToDay;
        private static MethodInfo _calendarTick, _skipToDawn, _calendarDay, _assign, _place, _sleeping, _restore;
        private static FieldInfo _town, _race, _tier, _paid, _for, _aiming;
        private static PropertyInfo _modInstance;
        private static Type _mod;
        public static float Tod = -1f;
        private static readonly AccessTools.FieldRef<Actor, ActorData> _actorData = AccessTools.FieldRefAccess<Actor, ActorData>("data");
        private static int _shopCursor;
        private static float _shopTimer;
        private static readonly List<City> _cities = new List<City>();

        public static void Install(Harmony h, Assembly wf, List<string> features)
        {
            _mod = wf.GetType("FirstPerson.WorldBoxMod", false);
            _modInstance = _mod == null ? null : AccessTools.Property(_mod, "Instance");
            _settings = _mod == null ? null : AccessTools.Field(_mod, "Settings");
            _dayMinutes = _settings == null ? null : AccessTools.Field(_settings.FieldType, "DayMinutes");
            InstallClock(h, wf, features);
            InstallTowns(h, wf, features);
            InstallChronicle(h, wf, features);
        }

        private static object Mod() => _modInstance?.GetValue(null, null);

        // ------------------------------------------------------------------ day and night

        private static void InstallClock(Harmony h, Assembly wf, List<string> features)
        {
            Type day = wf.GetType("FirstPerson.DayCycle", false), cal = wf.GetType("FirstPerson.Towns.Calendar", false);
            _dayTime = day == null ? null : AccessTools.Field(day, "TimeOfDay");
            MethodInfo update = day == null ? null : AccessTools.Method(day, "Update");
            _calendarTick = cal == null ? null : AccessTools.Method(cal, "Tick", new[] { typeof(float) });
            _skipToDawn = cal == null ? null : AccessTools.Method(cal, "SkipToDawn", Type.EmptyTypes);
            _calendarDay = cal == null ? null : AccessTools.PropertyGetter(cal, "Day");
            MethodInfo tod = cal == null ? null : AccessTools.PropertyGetter(cal, "TimeOfDay");
            Type home = wf.GetType("FirstPerson.Towns.HomeLife", false);
            MethodInfo sleep = home == null ? null : AccessTools.Method(home, "Sleep", new[] { typeof(Actor) });
            _slept = home == null ? null : AccessTools.Field(home, "_slept");
            _sleepStart = home == null ? null : AccessTools.Field(home, "_sleepStart");
            _sleptToDay = home == null ? null : AccessTools.Field(home, "SleptToDay");
            _sleeping = home == null ? null : AccessTools.PropertyGetter(home, "Sleeping");
            if (_dayTime == null || update == null || _calendarTick == null || _skipToDawn == null || _calendarDay == null || tod == null || _dayMinutes == null)
            {
                Log.Warn("lockstep: Worldfall's day clock changed (not found): each PC keeps its own time of day");
                return;
            }
            h.Patch(update, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(DayUpdatePrefix)));
            h.Patch(_calendarTick, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(OnlyInTicksPrefix)));
            h.Patch(tod, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(TimeOfDayPrefix)));
            h.Patch(_skipToDawn, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(SkipToDawnPrefix)));
            if (sleep != null && _slept != null && _sleepStart != null && _sleptToDay != null && _sleeping != null && WorldCalls.Register(h, AccessTools.Method(typeof(TownsInTick), nameof(Slept))))
                h.Patch(sleep, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(SleepPrefix)));
            else Log.Warn("lockstep: Worldfall's HomeLife.Sleep not found: sleeping until dawn changes one PC's world");
            WorldCalls.Register(h, AccessTools.Method(typeof(TownsInTick), nameof(Dawn)));
            features.Add("day and night");
        }

        private static bool Shared => LockstepControl.Running && LockstepClock.Active && Tod >= 0f;

        /// <summary>Host, starting an epoch: the time of day to send (this PC's clock the first time).</summary>
        public static float CaptureTod()
        {
            if (Tod >= 0f) return Tod;
            try { object mod = Mod(); object d = mod == null ? null : AccessTools.Field(_mod, "_day")?.GetValue(mod); return d == null ? -1f : (float)_dayTime.GetValue(d); }
            catch { return -1f; }
        }

        /// <summary>Every PC, at an epoch: the time of day the world starts at (negative: none, Worldfall missing).</summary>
        public static void SetTod(float tod) => Tod = tod < 0f ? -1f : tod - Mathf.Floor(tod);

        /// <summary>Lockstep stopped: each PC's own clock again (it goes on from the shared time).</summary>
        public static void Stop() => Tod = -1f;

        private static void DayUpdatePrefix(object __instance, ref float dt)
        {
            if (!Shared) return;
            _dayTime.SetValue(__instance, Tod);
            dt = 0f;
        }

        private static bool OnlyInTicksPrefix() => !LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick;

        private static bool TimeOfDayPrefix(ref float __result)
        {
            if (!Shared) return true;
            __result = Tod;
            return false;
        }

        /// <summary>In every tick, before the players: the clock moves on; a new day at dawn.</summary>
        private static void TickClock()
        {
            if (Tod < 0f || _dayMinutes == null) return;
            float minutes = 20f;
            try { object s = _settings.GetValue(Mod()); if (s != null) minutes = (float)_dayMinutes.GetValue(s); } catch { }
            Tod += LockstepClock.DefaultStep / Mathf.Max(1f, minutes * 60f);
            Tod -= Mathf.Floor(Tod);
            try { _calendarTick.Invoke(null, new object[] { Tod }); }
            catch (Exception e) { Fail("calendar", e); }
        }

        private static bool _dawning;

        private static bool SkipToDawnPrefix(ref int __result)
        {
            if (!Shared || _dawning) return true;
            if (LockstepClock.InTick) { Tod = 0.255f; return true; }
            Dawn();   // caught by WorldCalls and sent
            int day = 0;
            try { day = (int)_calendarDay.Invoke(null, null); } catch { }
            __result = day + 1;
            return false;
        }

        /// <summary>Relayed: the night is skipped (in a tick, on every PC).</summary>
        public static void Dawn()
        {
            if (!LockstepClock.InTick) return;
            try { _skipToDawn.Invoke(null, null); }
            catch (Exception e) { Fail("dawn", e); }
        }

        /// <summary>HomeLife.Sleep: the fade runs here; the sleep itself (dawn, health, stamina, a good dream) in a tick.</summary>
        private static bool SleepPrefix(Actor host)
        {
            if (!Shared || LockstepClock.InTick) return true;
            if ((bool)_slept.GetValue(null) || !(bool)_sleeping.Invoke(null, null) || Time.unscaledTime - (float)_sleepStart.GetValue(null) < 1.1f) return false;
            _slept.SetValue(null, true);
            int day = 0;
            try { day = (int)_calendarDay.Invoke(null, null); } catch { }
            _sleptToDay.SetValue(null, day + 1);
            if (host != null) Slept(host);   // caught by WorldCalls and sent
            return false;
        }

        /// <summary>Relayed: a player slept until dawn.</summary>
        public static void Slept(Actor host)
        {
            if (!LockstepClock.InTick || host == null || !host.isAlive()) return;
            Dawn();
            try
            {
                host.restoreHealthPercent(1f);
                ActorData d = _actorData(host);
                if (d != null) d.stamina = host.getMaxStamina();
                R.Call(host, "addStatusEffect", new[] { typeof(string), typeof(float), typeof(bool) }, "had_good_dream", 0f, false);
            }
            catch (Exception e) { Fail("sleep", e); }
            PlayerScope.ScopedToast()("You sleep until dawn");
        }

        // ------------------------------------------------------------------ towns

        private static void InstallTowns(Harmony h, Assembly wf, List<string> features)
        {
            Type hub = wf.GetType("FirstPerson.Towns.Hub", false), shops = wf.GetType("FirstPerson.Towns.Shops", false), plot = wf.GetType("FirstPerson.Towns.LandPlot", false);
            if (hub == null) return;
            MethodInfo pick = AccessTools.Method(hub, "PickShops", new[] { typeof(float) });
            _assign = shops == null ? null : AccessTools.Method(shops, "Assign", new[] { typeof(City) });
            if (pick != null && _assign != null)
            {
                h.Patch(pick, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(NotInLockstepPrefix)));
                features.Add("town shops");
            }
            else Log.Warn("lockstep: Worldfall's shop picking not found: towns' shops may differ between PCs");
            MethodInfo toast = AccessTools.PropertyGetter(hub, "Toast");
            if (toast != null) h.Patch(toast, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(HubToastPrefix)));
            if (plot != null)
            {
                _place = AccessTools.Method(plot, "Place", new[] { typeof(Actor), typeof(WorldTile) });
                _town = AccessTools.Field(plot, "_town"); _race = AccessTools.Field(plot, "_race"); _tier = AccessTools.Field(plot, "_tier");
                _paid = AccessTools.Field(plot, "_paid"); _for = AccessTools.Field(plot, "_for"); _aiming = AccessTools.Field(plot, "<Aiming>k__BackingField");
                if (_place != null && _town != null && _race != null && _tier != null && _paid != null && _for != null && _aiming != null
                    && WorldCalls.Register(h, AccessTools.Method(typeof(TownsInTick), nameof(PlacePlot))))
                {
                    h.Patch(_place, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(PlacePrefix)));
                    PlayerScope.Relay(h, plot, "Finish");
                    features.Add("house plots");
                }
                else Log.Warn("lockstep: Worldfall's LandPlot.Place not found: a house plot is placed on one PC only");
            }
            Type freedom = wf.GetType("FirstPerson.Towns.Freedom", false);
            MethodInfo declare = freedom == null ? null : AccessTools.Method(freedom, "Declare", new[] { typeof(Actor), typeof(Action<string>) });
            MethodInfo whyNot = freedom == null ? null : AccessTools.Method(freedom, "WhyNot");
            MethodInfo homeTown = freedom == null ? null : AccessTools.Method(freedom, "HomeTown");
            if (declare != null && whyNot != null && homeTown != null)
                WorldCalls.RegisterGuess(h, declare, (o, a) => a[0] is Actor you && whyNot.Invoke(null, new[] { you, homeTown.Invoke(null, new object[] { you }) }) == null);
            else Log.Warn("lockstep: Worldfall's Freedom.Declare not found: declaring independence changes one PC's world");
        }

        private static bool NotInLockstepPrefix() => !LockstepControl.Running || !LockstepClock.Active;

        private static bool HubToastPrefix(ref Action<string> __result)
        {
            if (!LockstepClock.InTick) return true;
            __result = PlayerScope.ScopedToast();
            return false;
        }

        /// <summary>In ticks: one town's shops every half second, towns in ID order.</summary>
        private static void TickShops()
        {
            if (_assign == null) return;
            _shopTimer -= LockstepClock.DefaultStep;
            if (_shopTimer > 0f) return;
            _shopTimer = 0.5f;
            _cities.Clear();
            foreach (City c in World.world.cities) if (c != null && c.isAlive()) _cities.Add(c);
            if (_cities.Count == 0) return;
            _cities.Sort((x, y) => x.getID().CompareTo(y.getID()));
            _shopCursor = (_shopCursor + 1) % _cities.Count;
            try { _assign.Invoke(null, new object[] { _cities[_shopCursor] }); }
            catch (Exception e) { Fail("shops", e); }
        }

        /// <summary>LandPlot.Place from this PC's aiming: the plot travels; aiming ends here now.</summary>
        private static bool PlacePrefix(Actor you, WorldTile t, ref Building __result)
        {
            if (!LockstepControl.Running || !LockstepClock.Active || LockstepClock.InTick || _placing) return true;
            __result = null;
            City town = _town.GetValue(null) as City;
            if (you == null || t == null || town == null) return false;
            PlacePlot(you, t, town, _race.GetValue(null) as string ?? "", (int)_tier.GetValue(null), (int)_paid.GetValue(null));   // caught by WorldCalls and sent
            _aiming.SetValue(null, false);
            _for.SetValue(null, null);
            _paid.SetValue(null, 0);
            return false;
        }

        private static bool _placing;

        /// <summary>Relayed: a player's house plot is marked out (in a tick, on every PC).</summary>
        public static void PlacePlot(Actor you, WorldTile t, City town, string race, int tier, int paid)
        {
            if (!LockstepClock.InTick || you == null || t == null || town == null) return;
            object[] was = { _town.GetValue(null), _race.GetValue(null), _tier.GetValue(null), _paid.GetValue(null), _for.GetValue(null), _aiming.GetValue(null) };
            _placing = true;
            try
            {
                _town.SetValue(null, town); _race.SetValue(null, race); _tier.SetValue(null, tier); _paid.SetValue(null, paid); _for.SetValue(null, you);
                _place.Invoke(null, new object[] { you, t });
            }
            catch (Exception e) { Fail("house plot", e); }
            finally
            {
                _placing = false;
                // this PC's own aiming (another player's plot didn't end it)
                _town.SetValue(null, was[0]); _race.SetValue(null, was[1]); _tier.SetValue(null, was[2]); _paid.SetValue(null, was[3]); _for.SetValue(null, was[4]); _aiming.SetValue(null, was[5]);
            }
        }

        // ------------------------------------------------------------------ memories

        private static void InstallChronicle(Harmony h, Assembly wf, List<string> features)
        {
            Type chronicle = wf.GetType("FirstPerson.Chronicle", false);
            MethodInfo tick = chronicle == null ? null : AccessTools.Method(chronicle, "Tick", Type.EmptyTypes);
            if (tick == null) { Log.Warn("lockstep: Worldfall's Chronicle.Tick not found: people's memories may be written on one PC only"); return; }
            // the sweep that writes what people remember (kin, events) is read by the simulation
            // (quests, service, teachers, family): it runs in ticks, a fixed number of people per tick
            h.Patch(tick, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(ChronicleGatePrefix)));
            _chronTick = tick;
            _chronPerFrame = AccessTools.Field(chronicle, "PerFrame");
            _chronBudget = AccessTools.Field(chronicle, "BudgetMs");
            _chronReset = AccessTools.Method(chronicle, "Reset", Type.EmptyTypes);
            if (_chronPerFrame == null || _chronBudget == null) Log.Warn("lockstep: Worldfall's Chronicle sweep settings not found: memories may differ between PCs");
            // Frame code (the open conversation, the HUD) reads people's records, and a read can create one
            // (RegardOf makes a regard toward you): that copy existed on one PC only and the next in-tick sweep
            // saved it (fp_you drift 2026-10-10, t137). Records touched outside ticks are dropped before each
            // tick, so ticks always start from the shared data.
            MethodInfo load = AccessTools.Method(chronicle, "Load", new[] { typeof(Actor) });
            _chronRecords = AccessTools.Field(chronicle, "Records")?.GetValue(null) as System.Collections.IDictionary;
            if (load != null && _chronRecords != null)
            {
                h.Patch(load, postfix: new HarmonyMethod(typeof(TownsInTick), nameof(ChronicleLoadPostfix)));
                LockstepClock.BeforeTick += t => DropFrameRecords();
            }
            else Log.Warn("lockstep: Worldfall's Chronicle.Load/Records not found: a PC's own reads may reach the shared memories");
            // what you did to someone (opinions, talks, gifts, crimes) travels
            PlayerScope.Relay(h, chronicle, "ChangeOpinion", "Note", "AddOwnMemory");
            features.Add("opinions");
            // what each creature has seen of the map (a sweep over every creature, and this PC's view):
            // each PC's own
            Type explored = wf.GetType("FirstPerson.Explored", false);
            if (explored != null)
                foreach (string n in new[] { "Tick", "Write", "WriteAll" })
                    foreach (MethodInfo m in explored.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (m.Name == n) h.Patch(m, prefix: new HarmonyMethod(typeof(TownsInTick), nameof(LocalPrefix)), finalizer: new HarmonyMethod(typeof(TownsInTick), nameof(LocalFinalizer)));
        }

        private static System.Collections.IDictionary _chronRecords;
        private static readonly HashSet<long> _frameRecords = new HashSet<long>();

        private static void ChronicleLoadPostfix(Actor a)
        {
            if (LockstepClock.InTick || !LockstepControl.Running || !LockstepClock.Active) return;
            try { _frameRecords.Add(a.getID()); } catch { }   // Records is keyed by data.id, the same as the creature ID
        }

        private static void DropFrameRecords()
        {
            if (_frameRecords.Count == 0) return;
            foreach (long id in _frameRecords) _chronRecords.Remove(id);
            _frameRecords.Clear();
        }

        private static MethodInfo _chronTick, _chronReset;
        private static FieldInfo _chronPerFrame, _chronBudget;
        private static bool _chronOurs;

        /// <summary>Chronicle.Tick: not from the frame while lockstep runs (RunTick calls it in ticks).</summary>
        private static bool ChronicleGatePrefix() => _chronOurs || !LockstepControl.Running || !LockstepClock.Active;

        /// <summary>In a tick: the chronicle sweep with a fixed count (no time budget, which differs per PC).</summary>
        private static void TickChronicle()
        {
            if (_chronTick == null || _chronPerFrame == null || _chronBudget == null) return;
            object per = _chronPerFrame.GetValue(null), budget = _chronBudget.GetValue(null);
            _chronOurs = true;
            try
            {
                _chronPerFrame.SetValue(null, 16);
                _chronBudget.SetValue(null, 1e9);
                _chronTick.Invoke(null, null);
            }
            catch (Exception e) { if (_chronFailed++ == 0) Log.Error("lockstep: Worldfall's chronicle in a tick: " + (e.InnerException ?? e)); }
            finally { _chronOurs = false; _chronPerFrame.SetValue(null, per); _chronBudget.SetValue(null, budget); }
        }

        private static int _chronFailed;

        private static void LocalPrefix() => DataCalls.EnterLocal();

        private static Exception LocalFinalizer(Exception __exception)
        {
            DataCalls.LeaveLocal();
            return __exception;
        }

        // ------------------------------------------------------------------ per tick

        /// <summary>Every tick, outside any player (WorldfallInTick.Run): the clock, shops.</summary>
        public static void RunTick()
        {
            TickClock();
            TickShops();
            TickChronicle();
        }

        public static void Reset()
        {
            _shopCursor = 0;
            _shopTimer = 0f;
            try { _chronReset?.Invoke(null, null); } catch { }   // its cache and sweep position start fresh on every PC
        }

        private static readonly HashSet<string> _failed = new HashSet<string>();

        private static void Fail(string what, Exception e)
        {
            if (_failed.Add(what)) Log.Error("lockstep: Worldfall's " + what + " (in a tick): " + (e.InnerException ?? e));
        }
    }
}
