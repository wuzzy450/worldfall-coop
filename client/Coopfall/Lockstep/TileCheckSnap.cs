using System;
using System.IO;
using System.Text;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// The tile values the world checksum covered at the last few tile checks, kept so a "tiles"
    /// desync can be compared at the exact tick the PCs disagreed on (a dump of the live tiles is
    /// taken at whatever tick each PC is at when the desync is noticed, often 10+ ticks apart).
    /// </summary>
    public sealed class TileCheckSnap
    {
        private const int Keep = 4;
        private static readonly TileCheckSnap[] _ring = new TileCheckSnap[Keep];
        private static int _next;

        public long tick = -1;
        private int[] _type, _main, _health, _burned, _flash;
        private double[] _changed;
        private byte[] _flags;

        public static TileCheckSnap Next(long tick, int n)
        {
            TileCheckSnap s = _ring[_next] ?? (_ring[_next] = new TileCheckSnap());
            _next = (_next + 1) % Keep;
            if (s._type == null || s._type.Length != n)
            {
                s._type = new int[n]; s._main = new int[n]; s._health = new int[n]; s._burned = new int[n];
                s._flash = new int[n]; s._changed = new double[n]; s._flags = new byte[n];
            }
            s.tick = tick;
            return s;
        }

        public void Set(int i, int type, int main, int health, bool fire, int burned, int flash, double changed, bool frozen)
        {
            _type[i] = type; _main[i] = main; _health[i] = health; _burned[i] = burned; _flash[i] = flash; _changed[i] = changed;
            _flags[i] = (byte)((fire ? 1 : 0) | (frozen ? 2 : 0));
        }

        /// <summary>Clears the kept checks (a new epoch starts its ticks at 0 again).</summary>
        public static void Reset()
        {
            foreach (TileCheckSnap s in _ring) if (s != null) s.tick = -1;
        }

        /// <summary>One file per kept check: lockstep-tilecheck{epoch}-t{tick}.txt.</summary>
        public static void WriteAll(Func<string, string> pathFor, int epoch)
        {
            foreach (TileCheckSnap s in _ring)
            {
                if (s == null || s.tick < 0) continue;
                string path = pathFor("lockstep-tilecheck" + epoch + "-t" + s.tick + ".txt");
                try
                {
                    using (var w = new StreamWriter(path))
                    {
                        var sb = new StringBuilder();
                        for (int i = 0; i < s._type.Length; i++)
                        {
                            sb.Length = 0;
                            sb.Append(i).Append("\tty").Append(s._type[i]).Append("\tm").Append(s._main[i]).Append("\th").Append(s._health[i])
                              .Append("\tf").Append(s._flags[i] & 1).Append("\tbs").Append(s._burned[i]).Append("\tfl").Append(s._flash[i])
                              .Append("\tch").Append(s._changed[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                              .Append("\tfz").Append((s._flags[i] >> 1) & 1);
                            w.WriteLine(sb.ToString());
                        }
                    }
                    Log.Info("lockstep: wrote the tiles as checked at tick " + s.tick + " to " + Path.GetFileName(path));
                }
                catch (Exception e) { Log.Warn("lockstep: couldn't write the checked tiles: " + e.Message); }
            }
        }
    }
}
