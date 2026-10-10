using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// "-coopfall-scenario lockstep-looks": do both PCs draw every creature the same in Worldfall 3D?
    /// At ticks 400 and 1400 of every epoch (the same world state on both PCs), each PC draws the
    /// 16 lowest-ID creatures with Worldfall's character view (the inventory figure: one creature on a
    /// plain floor, no camera or weather of this PC involved) and logs one "TEST looks:" line per
    /// creature (sprite, sex, the 3D parts used, a hash of the picture) and saves the picture as
    /// looks/e{epoch}-t{tick}-{id}.png in the profile folder. Compare with tools/compare-looks.py.
    /// The figure's pose follows each PC's own clock (Worldfall animates with real time), so
    /// pictures can differ a little in pose; the parts line must match exactly.
    /// </summary>
    public partial class TestDriver
    {
        private bool _lkMode, _lkHooked;
        private const float LooksSeconds = 150f;
        private const int LooksCount = 16, LooksW = 128, LooksH = 192;

        private void LooksTick(float now)
        {
            if (_lkHooked) return;
            _lkHooked = true;
            if (!WorldfallBridge.Present) { Log.Warn("TEST looks: Worldfall not loaded"); return; }
            Lockstep.LockstepClock.AfterTick += tick =>
            {
                if (tick != 400 && tick != 1400) return;
                try { DrawLooks(tick); }
                catch (Exception e) { Log.Warn("TEST looks: " + e); }
            };
            Log.Info("TEST looks: drawing " + LooksCount + " creatures at ticks 400 and 1400 of every epoch");
        }

        private void DrawLooks(long tick)
        {
            object mod = FxMod(out Type modT);
            Type viewT = FxType("CharacterView");
            object sprites = modT?.GetField("_sprites", Any)?.GetValue(mod);
            MethodInfo draw = viewT?.GetMethod("Draw", Any);
            FieldInfo parts = viewT?.GetField("PartNames", Any);
            PropertyInfo picture = viewT?.GetProperty("Picture", Any);
            if (mod == null || sprites == null || draw == null || parts == null || picture == null) { Log.Warn("TEST looks: Worldfall's character view not found"); return; }

            var all = new List<Actor>();
            foreach (Actor a in World.world.units)
                if (a != null && a.isAlive() && a.asset != null && !a.asset.is_boat) all.Add(a);
            // people first (what a player looks at most), then the rest, each by ID
            all.Sort((x, y) => Civ(x) != Civ(y) ? (Civ(x) ? -1 : 1) : x.getID().CompareTo(y.getID()));
            int epoch = _s.Lockstep.Epoch;
            string dir = Path.Combine(_shotDir, "looks");
            Directory.CreateDirectory(dir);
            // the same creatures on both PCs, drawn or not (a sprite this PC hasn't loaded yet is "not drawn")
            for (int i = 0; i < all.Count && i < LooksCount; i++)
            {
                Actor a = all[i];
                // a fresh view per creature: the view keeps one picture per frame
                object view = Activator.CreateInstance(viewT, true);
                bool ok = (bool)draw.Invoke(view, new object[] { a, sprites, 0f, LooksW, LooksH, false });
                string sprite = "?";
                try { sprite = (R.CallN(a, "getSpriteToRender", 0) as Sprite)?.name ?? "null"; } catch { }
                string line = "#" + a.getID() + " " + a.asset.id + " " + (a.isSexFemale() ? "female" : "male") + " sprite " + sprite;
                if (!ok) { Log.Info("TEST looks: e" + epoch + " t" + tick + " " + line + " | not drawn"); continue; }
                Texture2D tex = picture.GetValue(view, null) as Texture2D;
                string hash = "none";
                if (tex != null)
                {
                    byte[] png = tex.EncodeToPNG();
                    File.WriteAllBytes(Path.Combine(dir, "e" + epoch + "-t" + tick + "-" + a.getID() + ".png"), png);
                    hash = Hash(tex.GetRawTextureData());
                }
                Log.Info("TEST looks: e" + epoch + " t" + tick + " " + line + " | parts " + parts.GetValue(view) + " | picture " + hash);
            }
        }

        private static bool Civ(Actor a)
        {
            try { return R.CallN(a, "isKingdomCiv", 0) is bool b && b; }
            catch { return false; }
        }

        private static string Hash(byte[] b)
        {
            ulong h = 1469598103934665603UL;
            foreach (byte x in b) { h ^= x; h *= 1099511628211UL; }
            return h.ToString("x16");
        }
    }
}
