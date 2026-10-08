using System;
using System.IO;
using System.Reflection;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// Harmony (MIT) ships inside Coopfall.dll as an embedded resource, so players still download
    /// one file. Must run before any method that touches a Harmony type is compiled: call Init()
    /// first, then call into the classes that use Harmony.
    /// </summary>
    internal static class HarmonyLoader
    {
        private static bool _done;

        public static void Init()
        {
            if (_done) return;
            _done = true;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            if (!string.Equals(new AssemblyName(e.Name).Name, "0Harmony", StringComparison.OrdinalIgnoreCase)) return null;
            // another mod may already have loaded one
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "0Harmony") return a;
            using (Stream s = typeof(HarmonyLoader).Assembly.GetManifestResourceStream("Coopfall.0Harmony.dll"))
            {
                if (s == null) { Log.Error("lockstep: embedded Harmony missing"); return null; }
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return Assembly.Load(ms.ToArray());
            }
        }
    }
}
