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
        private static GUIStyle _titleStyle, _boldStyle, _descStyle;

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
            __result = 260f;
            return false;
        }

        private static bool DrawMultiplayerPagePrefix(float cx, float y, float cw, object menu, ref float __result)
        {
            float startY = y;
            InitStyles();

            // Background card
            GUI.Box(new Rect(cx, y, cw, 250f), "");

            // Title
            GUI.Label(new Rect(cx + 10f, y + 10f, cw - 20f, 25f), "<b><size=14><color=#ffc54a>Coopfall — Online Co-op Active</color></size></b>", _titleStyle);

            // Explanation note
            string note = "Worldfall's built-in multiplayer has been automatically disabled because Coopfall is installed.\n\n" +
                          "Coopfall provides full deterministic lockstep with complete world simulation synchronization " +
                          "(every creature, kingdom, war, culture, diplomacy, plot, and 3D possession), plus dedicated relay and Steam networking.";
            GUI.Label(new Rect(cx + 15f, y + 40f, cw - 30f, 75f), note, _descStyle);

            // Live status
            var session = CoopMod.Instance?.Session;
            string statusStr;
            if (session != null && session.Online)
            {
                statusStr = "<color=#8cd457>Online:</color> " + (session.IsHost ? "Hosting" : "Connected as Guest") +
                            (session.InWorld ? (" in room " + (session.RoomId ?? "world")) : " (in lobby)");
            }
            else
            {
                statusStr = "<color=#9fb5c0>Status:</color> Offline / Ready to Connect";
            }
            GUI.Label(new Rect(cx + 15f, y + 120f, cw - 30f, 24f), statusStr, _boldStyle);

            // Action Buttons
            string menuKey = session?.Cfg?.menuKey ?? "F8";
            string mapKey = session?.Cfg?.mapKey ?? "F7";

            if (GUI.Button(new Rect(cx + 15f, y + 150f, (cw - 40f) * 0.5f, 32f), "Open Co-op Menu (" + menuKey + ")"))
            {
                if (menu != null)
                {
                    try { AccessTools.Field(menu.GetType(), "Open")?.SetValue(menu, false); } catch { }
                }
                if (CoopMod.Instance?.UI != null) CoopMod.Instance.UI.MenuOpen = true;
            }

            if (GUI.Button(new Rect(cx + 25f + (cw - 40f) * 0.5f, y + 150f, (cw - 40f) * 0.5f, 32f), "World Map (" + mapKey + ")"))
            {
                if (menu != null)
                {
                    try { AccessTools.Field(menu.GetType(), "Open")?.SetValue(menu, false); } catch { }
                }
                if (CoopMod.Instance?.UI != null) CoopMod.Instance.UI.MapOpen = true;
            }

            if (GUI.Button(new Rect(cx + 15f, y + 190f, cw - 30f, 28f), "Join Coopfall Discord (Help & Matchmaking)"))
            {
                Application.OpenURL("https://discord.gg/nuvpXycag8");
            }

            __result = startY + 260f;
            return false;
        }

        private static void InitStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                fontSize = 14
            };
            _boldStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                richText = true,
                fontSize = 12
            };
            _descStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                richText = true,
                wordWrap = true,
                fontSize = 11
            };
        }
    }
}
