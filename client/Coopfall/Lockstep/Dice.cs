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
            public System.Random arrayExtRnd;
            public System.Random listExtRnd;
            public UnityEngine.Random.State unity;
        }

        private static readonly AccessTools.FieldRef<Unity.Mathematics.Random> _rand =
            AccessTools.StaticFieldRefAccess<Unity.Mathematics.Random>(AccessTools.Field(typeof(Randy), "rand"));
        private static readonly AccessTools.FieldRef<System.Random> _rnd =
            AccessTools.StaticFieldRefAccess<System.Random>(AccessTools.Field(typeof(Randy), "rnd"));
        private static readonly AccessTools.FieldRef<System.Random> _arrayExtRnd =
            AccessTools.Field(typeof(ArrayExtensions), "rnd") != null ?
            AccessTools.StaticFieldRefAccess<System.Random>(AccessTools.Field(typeof(ArrayExtensions), "rnd")) : null;
        private static readonly AccessTools.FieldRef<System.Random> _listExtRnd =
            AccessTools.Field(typeof(ListExtensions), "rnd") != null ?
            AccessTools.StaticFieldRefAccess<System.Random>(AccessTools.Field(typeof(ListExtensions), "rnd")) : null;
        private static readonly FieldInfo _inext = AccessTools.Field(typeof(System.Random), "inext") ?? AccessTools.Field(typeof(System.Random), "_inext");
        private static readonly FieldInfo _inextp = AccessTools.Field(typeof(System.Random), "inextp") ?? AccessTools.Field(typeof(System.Random), "_inextp");
        private static readonly FieldInfo _seeds = AccessTools.Field(typeof(System.Random), "SeedArray") ?? AccessTools.Field(typeof(System.Random), "_seedArray");
        /// <summary>Visual code draws from this instead of the shared System.Random.</summary>
        private static readonly System.Random _scratch = new System.Random(1);
        private static Unity.Mathematics.Random _randScratch = new Unity.Mathematics.Random(1);

        /// <summary>Save all generators and point them at scratch generators (put back by Restore).</summary>
        public static Snapshot Isolate()
        {
            var s = new Snapshot
            {
                rand = _rand(),
                rnd = _rnd(),
                arrayExtRnd = _arrayExtRnd != null ? _arrayExtRnd() : null,
                listExtRnd = _listExtRnd != null ? _listExtRnd() : null,
                unity = UnityEngine.Random.state
            };
            _rand() = _randScratch;
            _rnd() = _scratch;
            if (_arrayExtRnd != null) _arrayExtRnd() = _scratch;
            if (_listExtRnd != null) _listExtRnd() = _scratch;
            return s;
        }

        public static void Restore(Snapshot s)
        {
            _randScratch = _rand();
            _rand() = s.rand;
            _rnd() = s.rnd;
            if (_arrayExtRnd != null && s.arrayExtRnd != null) _arrayExtRnd() = s.arrayExtRnd;
            if (_listExtRnd != null && s.listExtRnd != null) _listExtRnd() = s.listExtRnd;
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
