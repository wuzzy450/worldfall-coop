using System;
using System.Collections.Generic;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Player actions in a lockstep session. Nothing a player does touches the world directly:
    /// each action becomes an Input for a future tick (tick N + delay), every player's PC queues
    /// the same inputs, and they are applied at the start of that tick, in a fixed order, with
    /// the shared dice. Speed and pause are session settings (how many ticks are granted), not
    /// world changes, so they are not inputs.
    /// </summary>
    public static class LockstepInput
    {
        public enum Kind { Power = 1, War = 2, Possess = 3, Release = 4, Control = 5 }

        public struct Input
        {
            public long tick;
            public int player;
            /// <summary>Order among one player's inputs for the same tick.</summary>
            public int seq;
            public Kind kind;
            /// <summary>Power id (Power), war type id (War), encoded controls (Possess, Control).</summary>
            public string id;
            /// <summary>Tile (Power), attacker and defender kingdom IDs (War), creature ID (Possess, Release, Control).</summary>
            public long a, b;
            public string brush;

            public override string ToString() => "t" + tick + " p" + player + "#" + seq + " " + kind + " " + id + " " + a + "," + b + (brush != null ? " " + brush : "");

            /// <summary>Wire format: tab-separated, culture-invariant.</summary>
            public string Encode() => tick + "\t" + player + "\t" + seq + "\t" + (int)kind + "\t" + id + "\t" + a + "\t" + b + "\t" + (brush ?? "");

            public static bool TryDecode(string s, out Input i)
            {
                i = default;
                string[] p = s.Split('\t');
                if (p.Length < 8 || !long.TryParse(p[0], out i.tick) || !int.TryParse(p[1], out i.player) || !int.TryParse(p[2], out i.seq)
                    || !int.TryParse(p[3], out int k) || !long.TryParse(p[5], out i.a) || !long.TryParse(p[6], out i.b)) return false;
                i.kind = (Kind)k;
                i.id = p[4];
                i.brush = p[7].Length > 0 ? p[7] : null;
                return true;
            }
        }

        /// <summary>Inputs waiting for their tick, sorted by (tick, player, seq).</summary>
        private static readonly List<Input> _pending = new List<Input>();
        /// <summary>Called for each input as it is applied (tests, logging).</summary>
        public static event Action<Input> Applied;
        /// <summary>Inputs that arrived after their tick ran: a desync on the PC that got it late.</summary>
        public static int Late;

        public static void Clear() { _pending.Clear(); Late = 0; }

        public static int PendingCount => _pending.Count;

        public static void Queue(Input i)
        {
            if (i.tick < LockstepClock.Tick || (i.tick == LockstepClock.Tick && LockstepClock.InTick))
            {
                Late++;
                Log.Error("lockstep: input for tick " + i.tick + " arrived at tick " + LockstepClock.Tick + ": " + i);
                return;
            }
            int at = _pending.Count;
            while (at > 0 && Compare(_pending[at - 1], i) > 0) at--;
            _pending.Insert(at, i);
        }

        private static int Compare(Input x, Input y)
        {
            int c = x.tick.CompareTo(y.tick);
            if (c == 0) c = x.player.CompareTo(y.player);
            if (c == 0) c = x.seq.CompareTo(y.seq);
            return c;
        }

        /// <summary>Called by LockstepClock inside each tick, after the dice are seeded.</summary>
        internal static void ApplyFor(long tick)
        {
            int n = 0;
            while (n < _pending.Count && _pending[n].tick <= tick) n++;
            if (n == 0) return;
            Input[] due = _pending.GetRange(0, n).ToArray();
            _pending.RemoveRange(0, n);
            foreach (Input i in due)
            {
                try { Apply(i); }
                catch (Exception e) { Log.Error("lockstep: input " + i + " failed: " + e.Message); }
                SectionTrace.Mark("input " + i.kind + " " + i.id);
                Applied?.Invoke(i);
            }
        }

        private static void Apply(Input i)
        {
            switch (i.kind)
            {
                case Kind.Power: ApplyPower(i); break;
                case Kind.War: ApplyWar(i); break;
                case Kind.Possess: LockstepControl.ApplyPossess(i); break;
                case Kind.Release: LockstepControl.ApplyRelease(i); break;
                case Kind.Control: LockstepControl.ApplyControl(i); break;
                default: Log.Error("lockstep: unknown input " + i); break;
            }
        }

        /// <summary>
        /// The world part of PlayerControl.clickedFinal: no premium check, no click-interval
        /// timer (the sender decides how often), no power statistics. The brush is the sender's.
        /// </summary>
        private static void ApplyPower(Input i)
        {
            GodPower p = AssetManager.powers.get(i.id);
            WorldTile tile = World.world.GetTile((int)i.a, (int)i.b);
            if (p == null || tile == null) { Log.Error("lockstep: input " + i + ": no such power or tile"); return; }
            string brush = Config.current_brush;
            if (i.brush != null) Config.current_brush = i.brush;
            InputPointer.Pointer = new Vector2(tile.x + 0.5f, tile.y + 0.5f);
            try
            {
                if (p.click_special_action != null) p.click_special_action(tile, p.id);
                else if (p.click_power_brush_action != null) p.click_power_brush_action(tile, p);
                else if (p.click_power_action != null) p.click_power_action(tile, p);
                else if (p.click_brush_action != null) p.click_brush_action(tile, p.id);
                else if (p.click_action != null) p.click_action(tile, p.id);
            }
            finally { Config.current_brush = brush; }
        }

        private static void ApplyWar(Input i)
        {
            Kingdom att = World.world.kingdoms.get(i.a), def = World.world.kingdoms.get(i.b);
            WarTypeAsset type = AssetManager.war_types_library.get(i.id ?? "normal");
            if (att == null || def == null || type == null || !att.isAlive() || !def.isAlive()) { Log.Error("lockstep: input " + i + ": no such kingdoms or war type"); return; }
            World.world.wars.newWar(att, def, type);
        }
    }
}
