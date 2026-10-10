using System;
using System.Reflection;
using HarmonyLib;

namespace Coopfall
{
    /// <summary>
    /// Worldfall's 3D audio adds its spatial effect to a game sound's FMOD channel group
    /// (FirstPersonAudio.Attach). When FMOD is stopping that group at the same moment, addDSP crashes
    /// the whole game natively (seen twice in co-op tests, 2026-10-08 and 2026-10-10; no Coopfall code
    /// on the stack). A group that isn't playing any more is skipped; Worldfall tries again next frame.
    /// </summary>
    internal static class WorldfallAudioGuard
    {
        private static MethodInfo _isPlaying;

        public static void Install(Harmony h, Assembly wf)
        {
            try
            {
                Type audio = wf?.GetType("FirstPerson.FirstPersonAudio", false);
                MethodInfo attach = audio == null ? null : AccessTools.Method(audio, "Attach");
                Type group = attach?.GetParameters().Length == 2 ? attach.GetParameters()[1].ParameterType : null;
                _isPlaying = group == null ? null : AccessTools.Method(group, "isPlaying");
                if (_isPlaying == null) { Log.Warn("audio guard: Worldfall's FirstPersonAudio.Attach not found (changed?)"); return; }
                h.Patch(attach, prefix: new HarmonyMethod(typeof(WorldfallAudioGuard), nameof(AttachPrefix)));
            }
            catch (Exception e) { Log.Warn("audio guard not installed: " + e.Message); }
        }

        private static bool AttachPrefix(object __1)
        {
            try
            {
                object[] args = { false };
                object result = _isPlaying.Invoke(__1, args);
                return Convert.ToInt32(result) == 0 && (bool)args[0];   // FMOD.RESULT.OK and still playing
            }
            catch { return true; }
        }
    }
}
