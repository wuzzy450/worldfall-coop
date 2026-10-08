using System.Reflection;
using HarmonyLib;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// WorldBox's Randy holds three generators: Unity.Mathematics `rand` (randomInt, randomChance,
    /// ...), a System.Random `rnd` (every list/array Shuffle and GetRandom), and Unity's global
    /// UnityEngine.Random. Lockstep must treat all three as shared state.
    /// </summary>
    public static class Dice
    {
        public struct Snapshot
        {
            public Unity.Mathematics.Random rand;
            public System.Random rnd;
            public UnityEngine.Random.State unity;
        }

        private static readonly AccessTools.FieldRef<Unity.Mathematics.Random> _rand =
            AccessTools.StaticFieldRefAccess<Unity.Mathematics.Random>(AccessTools.Field(typeof(Randy), "rand"));
        private static readonly AccessTools.FieldRef<System.Random> _rnd =
            AccessTools.StaticFieldRefAccess<System.Random>(AccessTools.Field(typeof(Randy), "rnd"));
        private static readonly FieldInfo _inext = AccessTools.Field(typeof(System.Random), "inext") ?? AccessTools.Field(typeof(System.Random), "_inext");
        private static readonly FieldInfo _inextp = AccessTools.Field(typeof(System.Random), "inextp") ?? AccessTools.Field(typeof(System.Random), "_inextp");
        private static readonly FieldInfo _seeds = AccessTools.Field(typeof(System.Random), "SeedArray") ?? AccessTools.Field(typeof(System.Random), "_seedArray");
        /// <summary>Visual code draws from this instead of the shared System.Random.</summary>
        private static readonly System.Random _scratch = new System.Random(1);

        /// <summary>Save all three and point `rnd` at a scratch generator (put back by Restore).</summary>
        public static Snapshot Isolate()
        {
            var s = new Snapshot { rand = _rand(), rnd = _rnd(), unity = UnityEngine.Random.state };
            _rnd() = _scratch;
            return s;
        }

        public static void Restore(Snapshot s)
        {
            _rand() = s.rand;
            _rnd() = s.rnd;
            UnityEngine.Random.state = s.unity;
        }

        public static uint UnityState()
        {
            UnityEngine.Random.State st = UnityEngine.Random.state;
            uint h = 0;
            foreach (System.Reflection.FieldInfo f in typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                h = h * 31 + (uint)(int)f.GetValue(st);
            return h;
        }

        /// <summary>Fingerprint of all three generators' state.</summary>
        public static uint Fingerprint() => Fingerprint(true);

        public static uint Fingerprint(bool withUnity)
        {
            uint h = _rand().state;
            System.Random r = _rnd();
            if (r != null && _inext != null && _inextp != null)
            {
                int i = (int)_inext.GetValue(r), j = (int)_inextp.GetValue(r);
                h = h * 31 + (uint)i * 7 + (uint)j;
                if (_seeds?.GetValue(r) is int[] a && a.Length > 0) h = h * 31 + (uint)a[i % a.Length] + (uint)a[j % a.Length] * 3;
            }
            if (withUnity) h = h * 31 + UnityState();
            return h;
        }
    }
}
