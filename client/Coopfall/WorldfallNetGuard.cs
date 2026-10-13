using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Intercepts Worldfall 0.9.5's newly added built-in multiplayer so it is completely disabled
    /// whenever Coopfall is installed. Replaces the F2 settings "Multiplayer" tab with a dedicated
    /// Coopfall portal and ensures no conflicting net sessions or HUD overlays run.
    /// </summary>
    internal static class WorldfallNetGuard
    {
        private static bool _installed;

        public static void Install(Harmony h, Assembly wf)
        {
            if (_installed || wf == null) return;
            _installed = true;
            try
            {
                Type netSession = wf.GetType("FirstPerson.Net.NetSession", false);
                if (netSession != null)
                {
                    // Disable NetSession.Tick so Worldfall's networking loop never runs
                    MethodInfo tick = AccessTools.Method(netSession, "Tick");
                    if (tick != null) h.Patch(tick, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(BlockNetTickPrefix)));

                    // Disable Host, JoinIp, JoinSteam
                    MethodInfo host = AccessTools.Method(netSession, "Host");
                    if (host != null) h.Patch(host, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(BlockHostPrefix)));

                    MethodInfo joinIp = AccessTools.Method(netSession, "JoinIp", new[] { typeof(string), typeof(string) });
                    if (joinIp != null) h.Patch(joinIp, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(BlockJoinPrefix)));

                    MethodInfo joinSteam = AccessTools.Method(netSession, "JoinSteam");
                    if (joinSteam != null) h.Patch(joinSteam, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(BlockJoinPrefix)));
                }

                // Worldfall 0.9.8's other ways in: Steam "Join game" invites, the rejoin after its
                // own update/restart, the home-network list, the router port opener (UPnP) and the
                // updater. All void: skipping them leaves nothing half-started.
                string[] blocked =
                {
                    "Net.NetSession.JoinFromStart", "Net.MpUi.JoinSteamHost", "Net.MpUi.JoinSteamLobby",
                    "Net.SteamLobbies.Hook", "Net.LanFind.Search", "Net.Upnp.Open", "Net.Updater.Start", "Net.Updater.Tick",
                };
                int n = 0;
                foreach (string b in blocked)
                {
                    int dot = b.LastIndexOf('.');
                    Type t = wf.GetType("FirstPerson." + b.Substring(0, dot), false);
                    MethodInfo m = t == null ? null : AccessTools.Method(t, b.Substring(dot + 1));
                    if (m == null || m.ReturnType != typeof(void)) continue;   // older Worldfall: not there
                    h.Patch(m, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(BlockPrefix)));
                    n++;
                }
                Log.Info("Worldfall's own multiplayer is off while Coopfall is installed (" + n + " extra entry points blocked)");

                // Update SettingsMenu tab label from "Multiplayer" to "Coopfall"
                Type settingsMenu = wf.GetType("FirstPerson.SettingsMenu", false);
                if (settingsMenu != null)
                {
                    FieldInfo tabsField = AccessTools.Field(settingsMenu, "Tabs");
                    string[] tabs = tabsField?.GetValue(null) as string[];
                    if (tabs != null && tabs.Length > 6)
                    {
                        tabs[6] = "Coopfall";
                    }
                }

                // Hook Hud.DrawMultiplayerPage & MultiplayerPageHeight to render Coopfall UI
                Type hud = wf.GetType("FirstPerson.Hud", false);
                if (hud != null)
                {
                    MethodInfo drawMp = AccessTools.Method(hud, "DrawMultiplayerPage");
                    if (drawMp != null)
                    {
                        h.Patch(drawMp, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(DrawMultiplayerPagePrefix)));
                    }

                    MethodInfo mpHeight = AccessTools.Method(hud, "MultiplayerPageHeight");
                    if (mpHeight != null)
                    {
                        h.Patch(mpHeight, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(MultiplayerPageHeightPrefix)));
                    }

                    // Block DrawNet and DrawNetOver
                    MethodInfo drawNet = AccessTools.Method(hud, "DrawNet");
                    if (drawNet != null) h.Patch(drawNet, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(BlockPrefix)));

                    MethodInfo drawNetOver = AccessTools.Method(hud, "DrawNetOver");
                    if (drawNetOver != null) h.Patch(drawNetOver, prefix: new HarmonyMethod(typeof(WorldfallNetGuard), nameof(BlockPrefix)));
                }

                Log.Info("Worldfall multiplayer guard: disabled Worldfall's built-in net in favor of Coopfall (F2 tab redirect installed)");
            }
            catch (Exception e)
            {
                Log.Warn("Worldfall multiplayer guard install failed: " + e.Message);
            }
        }

        private static bool BlockPrefix() => false;

        private static bool BlockNetTickPrefix() => false;

        private static bool BlockHostPrefix(ref string __result)
        {
            __result = "Worldfall built-in multiplayer is disabled because Coopfall is installed.";
            return false;
        }

        private static bool BlockJoinPrefix(ref string __result)
        {
            __result = "Worldfall built-in multiplayer is disabled because Coopfall is installed.";
            return false;
        }

        private static bool MultiplayerPageHeightPrefix(float cw, ref float __result)
        {
            __result = Page(0f, 0f, cw, false, null);
            return false;
        }

        private static bool DrawMultiplayerPagePrefix(float cx, float y, float cw, object menu, ref float __result)
        {
            __result = Page(cx, y, cw, true, menu);
            return false;
        }

        // Worldfall's UI scale (GameArt.P) and its text styles (GameArt.Style), so this page reads
        // at the same size as the rest of F2 on any screen; plain fallbacks if they're missing
        private static FieldInfo _artP;
        private static MethodInfo _artStyle;
        private static bool _artLooked;

        private static float P()
        {
            LookUpArt();
            try { if (_artP != null) return Mathf.Max(1f, (float)_artP.GetValue(null)); } catch { }
            return 2f;
        }

        private static void LookUpArt()
        {
            if (_artLooked) return;
            _artLooked = true;
            Type art = WorldfallBridge.Assembly?.GetType("FirstPerson.GameArt", false);
            _artP = art == null ? null : AccessTools.Field(art, "P");
            _artStyle = art == null ? null : AccessTools.Method(art, "Style", new[] { typeof(float), typeof(TextAnchor), typeof(bool) });
        }

        private static GUIStyle Style(float size, TextAnchor anchor, bool wrap)
        {
            LookUpArt();
            GUIStyle st = null;
            try { st = _artStyle?.Invoke(null, new object[] { size, anchor, wrap }) as GUIStyle; } catch { }
            st = st != null ? new GUIStyle(st) : new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(8, Mathf.RoundToInt(size * P())), alignment = anchor, wordWrap = wrap };
            st.richText = true;
            return st;
        }

        private static float Page(float cx, float y, float cw, bool draw, object menu)
        {
            float p = P(), row = Mathf.Round(16f * p), pad = 3f * p, startY = y;
            GUIStyle title = Style(5.6f, TextAnchor.MiddleCenter, false), body = Style(4.4f, TextAnchor.UpperLeft, true), status = Style(4.8f, TextAnchor.MiddleLeft, false);
            string note = "Worldfall's built-in multiplayer is turned off because Coopfall is installed: two multiplayer systems would fight over the same world.\n\n" +
                          "Coopfall keeps every player's world identical (creatures, kingdoms, wars, weather and first-person possession) over its own relay or Steam.";
            float noteH = body.CalcHeight(new GUIContent(note), cw - 4f * pad);
            float h = pad + row + pad + noteH + pad + row + pad + row * 1.4f + pad + row * 1.2f + pad;
            if (!draw) return startY + h + pad;

            GUI.Box(new Rect(cx, y, cw, h), "");
            y += pad;
            GUI.Label(new Rect(cx + 2f * pad, y, cw - 4f * pad, row), "<b><color=#ffc54a>Coopfall: online co-op</color></b>", title);
            y += row + pad;
            GUI.Label(new Rect(cx + 2f * pad, y, cw - 4f * pad, noteH), note, body);
            y += noteH + pad;

            var session = CoopMod.Instance?.Session;
            string statusStr = session != null && session.Online
                ? "<color=#8cd457>Online:</color> " + (session.IsHost ? "hosting" : "connected as a guest") + (session.InWorld ? " in room " + (session.RoomId ?? "world") : " (in the lobby)")
                : "<color=#9fb5c0>Offline:</color> open the co-op menu to host or join";
            GUI.Label(new Rect(cx + 2f * pad, y, cw - 4f * pad, row), statusStr, status);
            y += row + pad;

            var button = new GUIStyle(GUI.skin.button) { fontSize = status.fontSize, richText = true };
            if (status.font != null) button.font = status.font;
            string menuKey = session?.Cfg?.menuKey ?? "F8";
            string mapKey = session?.Cfg?.mapKey ?? "F7";
            float half = (cw - 5f * pad) * 0.5f;
            if (GUI.Button(new Rect(cx + 2f * pad, y, half, row * 1.4f), "Open Co-op Menu (" + menuKey + ")", button))
            {
                CloseMenu(menu);
                if (CoopMod.Instance?.UI != null) CoopMod.Instance.UI.MenuOpen = true;
            }
            if (GUI.Button(new Rect(cx + 3f * pad + half, y, half, row * 1.4f), "World Map (" + mapKey + ")", button))
            {
                CloseMenu(menu);
                if (CoopMod.Instance?.UI != null) CoopMod.Instance.UI.MapOpen = true;
            }
            y += row * 1.4f + pad;
            if (GUI.Button(new Rect(cx + 2f * pad, y, cw - 4f * pad, row * 1.2f), "Join the Coopfall Discord (help and matchmaking)", button))
                Application.OpenURL("https://discord.gg/nuvpXycag8");
            return startY + h + pad;
        }

        private static void CloseMenu(object menu)
        {
            if (menu == null) return;
            try { AccessTools.Field(menu.GetType(), "Open")?.SetValue(menu, false); } catch { }
        }
    }
}
