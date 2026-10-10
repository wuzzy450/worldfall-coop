using System.Collections.Generic;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// IMGUI overlay (WorldBox itself is uGUI, so this never collides with game windows):
    /// HUD bar, toasts, chat, name tags / god cursors / chat bubbles, the Co-op menu and the
    /// clickable World Map. Everything is scaled for the screen height.
    /// </summary>
    public class CoopUI
    {
        private readonly CoopSession _s;
        private readonly CoopConfig _cfg;
        private readonly AvatarManager _av;

        public bool MenuOpen, MapOpen, ChatOpen;
        public bool MouseOverUi;

        private string _chatInput = "";
        private bool _focusChat;
        private string _nameEdit, _hostEdit, _portEdit, _newWorldName = "";
        private Vector2 _mapScroll, _playersScroll, _menuScroll, _modsScroll;
        private string _pwEdit = "", _joinPw = "", _setPw = "";
        private string _confirmKick;
        private string _confirmDelete;
        private readonly List<Rect> _blockRects = new List<Rect>();

        private class ToastMsg { public string text; public float until; }
        private readonly List<ToastMsg> _toasts = new List<ToastMsg>();

        private float _k = 1f;
        private bool _fp;   // Worldfall's first-person view is on screen: keep clear of its top-centre age panel / compass
        private float W { get { return Screen.width / _k; } }
        private float H { get { return Screen.height / _k; } }

        // styles
        private bool _styled;
        private GUIStyle _panel, _hud, _title, _label, _small, _dim, _btn, _btnAccent, _field, _tag, _bubble, _chatLine, _toast, _cardTitle;
        private Texture2D _texPanel, _texHud, _texBtn, _texBtnHover, _texAccent, _texAccentHover, _texField, _texWhite, _texRing, _texDot, _texBubble;

        public CoopUI(CoopSession s, AvatarManager av)
        {
            _s = s;
            _cfg = s.Cfg;
            _av = av;
            _nameEdit = _cfg.playerName;
            _hostEdit = _cfg.serverHost;
            _portEdit = _cfg.serverPort.ToString();
            _s.Toast += ShowToast;
        }

        public void ShowToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _toasts.Add(new ToastMsg { text = text, until = Time.unscaledTime + 5f });
            if (_toasts.Count > 5) _toasts.RemoveAt(0);
        }

        // ================================================================ input (Update)

        public void HandleKeys()
        {
            if (ChatOpen) return;
            if (Input.GetKeyDown(CoopConfig.ParseKey(_cfg.menuKey, KeyCode.F8))) { MenuOpen = !MenuOpen; if (MenuOpen) MapOpen = false; }
            if (Input.GetKeyDown(CoopConfig.ParseKey(_cfg.mapKey, KeyCode.F7))) { MapOpen = !MapOpen; if (MapOpen) MenuOpen = false; }
            // In Worldfall's conversations / naming, Enter belongs to Worldfall.
            if (Input.GetKeyDown(CoopConfig.ParseKey(_cfg.chatKey, KeyCode.Return)) && _s.Online && !MenuOpen && !MapOpen && !WorldfallBridge.WantsKeys)
            {
                ChatOpen = true;
                _focusChat = true;
                _chatInput = "";
            }
            if (Input.GetKeyDown(KeyCode.Escape)) { MenuOpen = false; MapOpen = false; }
        }

        public bool Typing { get { return ChatOpen || (MenuOpen && GUIUtility.keyboardControl != 0); } }

        // ================================================================ OnGUI

        public void OnGUI()
        {
            // Worldfall paints its 3D first-person frame in OnGUI at depth -1000; lower depth = drawn later = on top.
            GUI.depth = -2000;
            if (Event.current.type == EventType.Layout) _fp = WorldfallBridge.FirstPerson;
            BuildStyles();
            _k = Mathf.Clamp(Screen.height / 1000f, 0.8f, 2.5f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_k, _k, 1f));
            if (Event.current.type == EventType.Layout) _blockRects.Clear();

            DrawWorldOverlays();
            DrawHud();
            DrawToasts();
            DrawChat();
            DrawBusy();
            if (MenuOpen) DrawMenu();
            if (MapOpen) DrawMap();
            DrawJoinRequests();
            DrawPasswordPrompt();
            DrawModMismatch();

            if (Event.current.type == EventType.Repaint)
            {
                Vector2 m = new Vector2(Input.mousePosition.x / _k, (Screen.height - Input.mousePosition.y) / _k);
                bool over = false;
                foreach (Rect r in _blockRects) if (r.Contains(m)) { over = true; break; }
                MouseOverUi = over;
            }
            GUI.matrix = Matrix4x4.identity;
        }

        private void Block(Rect r) { _blockRects.Add(r); }

        private static readonly Color Backdrop = new Color(0.02f, 0.03f, 0.05f, 0.85f);

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _texWhite);
            GUI.color = old;
        }

        // ---------------------------------------------------------------- HUD

        private void DrawHud()
        {
            string dot, text;
            Color c;
            switch (_s.Phase)
            {
                case Phase.Offline: dot = "●"; c = new Color(0.6f, 0.6f, 0.6f); text = "Co-op offline"; break;
                case Phase.Connecting: dot = "●"; c = new Color(1f, 0.8f, 0.2f); text = "Connecting…"; break;
                case Phase.InWorld:
                    dot = "●"; c = new Color(0.35f, 0.95f, 0.45f);
                    int n = _s.OthersInRoom();
                    text = _s.RoomName + (_s.IsHost ? "  ·  host" : "") + (_s.Spectating ? "  ·  watching" : "") + "  ·  " + (n == 0 ? "just you" : (n + 1) + " players") +
                           (_s.PingMs >= 0 ? "  ·  " + _s.PingMs + " ms" : "");
                    break;
                default: dot = "●"; c = new Color(0.4f, 0.75f, 1f); text = _s.Status; break;
            }
            float w = Mathf.Min(620f, 140f + _small.CalcSize(new GUIContent(text)).x + 190f);
            Rect r;
            if (_fp)
            {
                // Worldfall's first person fills the top of the screen (unit panel left, age panel centre,
                // minimap right): sit on the right side just below the minimap, inset from the right edge.
                float right = W - Mathf.Max(24f, W * 0.05f);
                r = new Rect(Mathf.Max(W * 0.5f, right - w), H * 0.175f + 8f, w, 30f);
            }
            else r = new Rect((W - w) / 2f, 6f, w, 30f);
            _hudRect = r;
            GUI.Box(r, GUIContent.none, _hud);
            Block(r);
            GUI.color = c;
            GUI.Label(new Rect(r.x + 10, r.y + 5, 16, 20), dot, _label);
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 26, r.y + 6, w - 230, 20), "<b>Coopfall</b>  " + Esc(text), _small);
            if (GUI.Button(new Rect(r.xMax - 196, r.y + 4, 98, 22), "World Map " + Short(_cfg.mapKey), MapOpen ? _btnAccent : _btn)) { MapOpen = !MapOpen; MenuOpen = false; }
            if (GUI.Button(new Rect(r.xMax - 94, r.y + 4, 88, 22), "Co-op " + Short(_cfg.menuKey), MenuOpen ? _btnAccent : _btn)) { MenuOpen = !MenuOpen; MapOpen = false; }
        }

        private Rect _hudRect;

        private void DrawToasts()
        {
            float y = _fp ? _hudRect.yMax + 6f : 42f, now = Time.unscaledTime;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                ToastMsg t = _toasts[i];
                if (now > t.until) { _toasts.RemoveAt(i); continue; }
                float a = Mathf.Clamp01((t.until - now) / 0.6f);
                Vector2 size = _toast.CalcSize(new GUIContent(t.text));
                Rect r = new Rect(_fp ? _hudRect.xMax - size.x - 28 : (W - size.x - 28) / 2f, y, size.x + 28, 26);
                GUI.color = new Color(1, 1, 1, a);
                GUI.Box(r, t.text, _toast);
                GUI.color = Color.white;
                y += 30f;
            }
        }

        private void DrawBusy()
        {
            if (!_s.Busy) return;
            Rect r = new Rect((W - 360) / 2f, H * 0.32f, 360, 74);
            GUI.Box(r, GUIContent.none, _panel);
            Block(r);
            GUI.Label(new Rect(r.x + 16, r.y + 12, 330, 22), Esc(_s.Status), _label);
            float p = _s.Phase == Phase.Downloading ? _s.DownloadProgress : (_s.Phase == Phase.Loading ? 1f : Mathf.PingPong(Time.unscaledTime * 0.7f, 1f));
            Rect bar = new Rect(r.x + 16, r.y + 44, 328, 10);
            Fill(bar, Backdrop);
            GUI.color = CoopConfig.ParseColor(_cfg.color);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * p, bar.height), _texWhite);
            GUI.color = Color.white;
        }

        // ---------------------------------------------------------------- chat

        private void DrawChat()
        {
            float now = Time.unscaledTime;
            float x = 10f, bottom = H - 150f;
            if (ChatOpen)
            {
                Event e = Event.current;
                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n'))
                {
                    _s.SendChat(_chatInput);
                    _chatInput = "";
                    ChatOpen = false;
                    e.Use();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    ChatOpen = false;
                    e.Use();
                }
                Rect fr = new Rect(x, bottom + 6, 440, 26);
                Block(fr);
                GUI.SetNextControlName("coopfall_chat");
                _chatInput = GUI.TextField(fr, _chatInput ?? "", 300, _field);
                if (_focusChat) { GUI.FocusControl("coopfall_chat"); _focusChat = false; }
            }
            int shown = 0;
            float y = bottom - 22f;
            for (int i = _s.Chat.Count - 1; i >= 0 && shown < 9; i--)
            {
                ChatLine l = _s.Chat[i];
                float age = now - l.time;
                if (!ChatOpen && age > 14f) break;
                float a = ChatOpen ? 1f : Mathf.Clamp01((14f - age) / 2f);
                string txt = l.system ? "<i>" + Esc(l.text) + "</i>" : "<color=" + l.color + "><b>" + Esc(l.name) + "</b></color>: " + Esc(l.text);
                GUI.color = new Color(1, 1, 1, a);
                Vector2 size = _chatLine.CalcSize(new GUIContent(txt));
                Rect r = new Rect(x, y, Mathf.Min(560, size.x + 14), 22);
                GUI.Box(r, txt, _chatLine);
                GUI.color = Color.white;
                y -= 23f;
                shown++;
            }
            if (!ChatOpen && _s.Online && _s.Chat.Count == 0)
                GUI.Label(new Rect(x, bottom + 8, 300, 20), "Press " + _cfg.chatKey + " to chat", _dim);
        }

        // ---------------------------------------------------------------- world-anchored overlays

        private void DrawWorldOverlays()
        {
            if (!_s.InWorld || !WorldBoxApi.WorldReady) return;
            // Worldfall's 3D god view (zoomed-in map) covers the 2D map: place tags with its projection too.
            bool god = !_fp && WorldfallBridge.GodView;
            if (_fp || god) { DrawFirstPersonOverlays(god); return; }
            Camera cam = WorldBoxApi.MapCamera;
            // cullingMask 0: Worldfall has hidden the 2D map behind one of its own views
            if (cam == null || cam.cullingMask == 0 || !WorldBoxApi.MapCameraIsTopmost(cam)) return;
            float now = Time.unscaledTime;
            foreach (AvatarManager.Remote r in _av.Remotes.Values)
            {
                if (r.on && r.actor != null && r.actor.isAlive() && _cfg.showNameTags)
                {
                    Vector2 p = r.actor.current_position;
                    if (ToGui(cam, new Vector3(p.x, p.y + 2.2f, 0f), out Vector2 g))
                    {
                        string label = "<color=" + r.color + "><b>" + Esc(r.name) + "</b></color>";
                        Vector2 size = _tag.CalcSize(new GUIContent(label));
                        GUI.Box(new Rect(g.x - size.x / 2f - 6, g.y - 22, size.x + 12, 20), label, _tag);
                        if (r.mhp > 0)
                        {
                            float frac = Mathf.Clamp01((float)r.hp / r.mhp);
                            Rect hb = new Rect(g.x - 20, g.y, 40, 4);
                            Fill(hb, Backdrop);
                            GUI.color = Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(0.3f, 1f, 0.35f), frac);
                            GUI.DrawTexture(new Rect(hb.x, hb.y, hb.width * frac, hb.height), _texWhite);
                            GUI.color = Color.white;
                        }
                        DrawBubble(r, g, now, 26f);
                    }
                }
                else if (r.hasCursor && _cfg.showCursors)
                {
                    if (ToGui(cam, new Vector3(r.cursorShown.x, r.cursorShown.y, 0f), out Vector2 g))
                    {
                        GUI.color = r.col;
                        GUI.DrawTexture(new Rect(g.x - 9, g.y - 9, 18, 18), _texRing);
                        GUI.DrawTexture(new Rect(g.x - 2.5f, g.y - 2.5f, 5, 5), _texDot);
                        GUI.color = Color.white;
                        string label = "<color=" + r.color + "><b>" + Esc(r.name) + "</b></color>" + (string.IsNullOrEmpty(r.power) ? "" : "  <size=10>" + Esc(Pretty(r.power)) + "</size>");
                        Vector2 size = _tag.CalcSize(new GUIContent(label));
                        GUI.Box(new Rect(g.x + 10, g.y + 6, size.x + 12, 20), label, _tag);
                        DrawBubble(r, g, now, 14f);
                    }
                }
                if (now < r.powerFlashUntil && ToGui(cam, new Vector3(r.powerFlashAt.x, r.powerFlashAt.y, 0f), out Vector2 fg))
                {
                    float t = 1f - (r.powerFlashUntil - now) / 0.6f;
                    float s = 14f + 30f * t;
                    GUI.color = new Color(r.col.r, r.col.g, r.col.b, 1f - t);
                    GUI.DrawTexture(new Rect(fg.x - s / 2, fg.y - s / 2, s, s), _texRing);
                    GUI.color = Color.white;
                }
            }
        }

        /// <summary>Name tags, health bars and chat bubbles over remote players inside Worldfall's 3D view.</summary>
        private void DrawFirstPersonOverlays(bool god = false)
        {
            if (!_cfg.showNameTags) return;
            float now = Time.unscaledTime;
            foreach (AvatarManager.Remote r in _av.Remotes.Values)
            {
                if (!r.on || r.actor == null || !r.actor.isAlive()) continue;
                if (!WorldfallBridge.ProjectHead(r.actor, 0.35f, out Vector2 sp, out float depth, god)) continue;
                r.tagAt = now;
                float fade = god ? 1f : 1f - Mathf.Clamp01((depth - 40f) / 20f);
                if (fade <= 0f) continue;
                Vector2 g = sp / _k;
                GUI.color = new Color(1f, 1f, 1f, fade);
                string label = "<color=" + r.color + "><b>" + Esc(r.name) + "</b></color>";
                Vector2 size = _tag.CalcSize(new GUIContent(label));
                GUI.Box(new Rect(g.x - size.x / 2f - 6, g.y - 22, size.x + 12, 20), label, _tag);
                if (r.mhp > 0)
                {
                    float frac = Mathf.Clamp01((float)r.hp / r.mhp);
                    Rect hb = new Rect(g.x - 20, g.y, 40, 4);
                    Fill(hb, new Color(Backdrop.r, Backdrop.g, Backdrop.b, Backdrop.a * fade));
                    Color hc = Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(0.3f, 1f, 0.35f), frac);
                    GUI.color = new Color(hc.r, hc.g, hc.b, fade);
                    GUI.DrawTexture(new Rect(hb.x, hb.y, hb.width * frac, hb.height), _texWhite);
                }
                GUI.color = Color.white;
                DrawBubble(r, g, now, 26f);
            }
        }

        private void DrawBubble(AvatarManager.Remote r, Vector2 g, float now, float lift)
        {
            if (r.bubble == null || now > r.bubbleUntil) return;
            float a = Mathf.Clamp01((r.bubbleUntil - now) / 0.8f);
            Vector2 size = _bubble.CalcSize(new GUIContent(r.bubble));
            float w = Mathf.Min(size.x + 16, 260);
            float h = _bubble.CalcHeight(new GUIContent(r.bubble), w) + 6;
            GUI.color = new Color(1, 1, 1, a);
            GUI.Box(new Rect(g.x - w / 2f, g.y - lift - h - 6, w, h), r.bubble, _bubble);
            GUI.color = Color.white;
        }

        private bool ToGui(Camera cam, Vector3 world, out Vector2 g)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            g = new Vector2(sp.x / _k, (Screen.height - sp.y) / _k);
            return sp.z >= 0f && g.x > -50 && g.y > -50 && g.x < W + 50 && g.y < H + 50;
        }

        // ---------------------------------------------------------------- Co-op menu

        private void DrawMenu()
        {
            Rect r = new Rect((W - 580) / 2f, 48f, 580, Mathf.Min(H - 90f, 680f));
            GUI.Box(r, GUIContent.none, _panel);
            Block(r);
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 24));
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Coopfall</b>  ·  WorldBox co-op", _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("✕", _btn, GUILayout.Width(28))) MenuOpen = false;
            GUILayout.EndHorizontal();
            GUILayout.Label(Esc(_s.Status), _dim);
            GUILayout.Space(8);
            _menuScroll = GUILayout.BeginScrollView(_menuScroll, GUIStyle.none, GUI.skin.verticalScrollbar);

            bool offline = !_s.Net.Connected && !_s.Net.Connecting;
            GUI.enabled = offline;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name", _label, GUILayout.Width(90));
            _nameEdit = GUILayout.TextField(_nameEdit ?? "", 24, _field, GUILayout.Width(180));
            GUILayout.Space(12);
            for (int i = 0; i < CoopConfig.Palette.Length; i++)
            {
                string hex = CoopConfig.Palette[i];
                GUI.color = CoopConfig.ParseColor(hex);
                bool sel = _cfg.color == hex;
                if (GUILayout.Button(sel ? "●" : "", _btn, GUILayout.Width(20), GUILayout.Height(20))) { _cfg.color = hex; _cfg.Save(); }
                GUI.color = Color.white;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Server IP", _label, GUILayout.Width(90));
            _hostEdit = GUILayout.TextField(_hostEdit ?? "", 100, _field, GUILayout.Width(180));
            GUILayout.Label("  Port", _label, GUILayout.Width(44));
            _portEdit = GUILayout.TextField(_portEdit ?? "", 5, _field, GUILayout.Width(64));
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            bool hostHere = Tog(_cfg.hostServer, " Host the server on this PC (no download: friends connect to you)", _small);
            if (hostHere != _cfg.hostServer)
            {
                _cfg.hostServer = hostHere;
                _cfg.Save();
                if (!hostHere && _s.Relay != null) { _s.Disconnect(); _s.StopHosting(); }
            }
            if (_cfg.hostServer)
            {
                if (_s.Relay != null && _s.Relay.Running)
                {
                    GUILayout.Label("  Server running on port " + _s.Relay.Port + ", " + _s.Relay.Players + " connected. Same network: " + CoopSession.LanAddress() + ":" + _s.Relay.Port, _small);
                    GUILayout.Label("  Internet: " + (_s.PortMap?.Status ?? "-"), _small);
                    string steam = Net.SteamTransport.MyAddress;
                    if (steam != null)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("  Over Steam (no port forwarding): friends type " + steam, _small);
                        if (GUILayout.Button("Copy", _btn, GUILayout.Width(60))) GUIUtility.systemCopyBuffer = steam;
                        GUILayout.EndHorizontal();
                    }
                    if (_s.PortMap == null || !_s.PortMap.Open) GUILayout.Label("  For friends over the internet, forwarding TCP port " + _s.Relay.Port + " on your router to this PC is recommended.", _small);
                }
                else if (_s.Relay != null && _s.Relay.Error != null) GUILayout.Label("  Couldn't start the server: " + _s.Relay.Error, _small);
                else GUILayout.Label("  The server starts when you press Connect. Over the internet, forwarding TCP port " + _cfg.serverPort + " on your router is recommended.", _small);
            }

            GUILayout.Space(6);
            GUILayout.Label("Mode", _label);
            GUILayout.BeginHorizontal();
            if (Tog(_cfg.mode == "shared", "  Shared world - everyone plays one world together", _label) && _cfg.mode != "shared")
            {
                _cfg.mode = "shared"; _cfg.Save();
                if (_s.Online) _s.EnterModeWorld();
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Tog(_cfg.mode == "own", "  Own worlds - you host yours, visit others from the World Map", _label) && _cfg.mode != "own")
            {
                _cfg.mode = "own"; _cfg.Save();
                if (_s.Online) _s.EnterModeWorld();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (offline)
            {
                if (GUILayout.Button("Connect", _btnAccent, GUILayout.Width(140), GUILayout.Height(28)))
                {
                    ApplyConnectionFields();
                    _s.Connect();
                }
                if (_s.Reconnecting && GUILayout.Button("Stop retrying", _btn, GUILayout.Width(110), GUILayout.Height(28)))
                    _s.Disconnect();
            }
            else if (GUILayout.Button(_s.Net.Connecting ? "Cancel" : "Disconnect", _btn, GUILayout.Width(140), GUILayout.Height(28)))
                _s.Disconnect();
            GUILayout.Space(8);
            bool auto = Tog(_cfg.autoConnect, " Connect automatically on start", _small);
            if (auto != _cfg.autoConnect) { _cfg.autoConnect = auto; _cfg.Save(); }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("Options", _label);
            GUILayout.BeginHorizontal();
            bool tags = Tog(_cfg.showNameTags, " Name tags", _small, GUILayout.Width(110));
            bool curs = Tog(_cfg.showCursors, " God cursors", _small, GUILayout.Width(120));
            bool spd = Tog(_cfg.syncSpeed, " Sync speed & pause", _small);
            GUILayout.EndHorizontal();
            if (tags != _cfg.showNameTags || curs != _cfg.showCursors || spd != _cfg.syncSpeed)
            {
                _cfg.showNameTags = tags; _cfg.showCursors = curs; _cfg.syncSpeed = spd; _cfg.Save();
            }
            GUILayout.BeginHorizontal();
            bool live = Tog(_cfg.liveSync, " Live sync (creatures & buildings follow the host)", _small);
            if (live != _cfg.liveSync) { _cfg.liveSync = live; _cfg.Save(); CoopMod.Instance?.Sync.Reset(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool ls = Tog(_cfg.lockstep, " Lockstep (every game simulates, only powers travel; same mods needed)", _small);
            if (ls != _cfg.lockstep) { _cfg.lockstep = ls; _cfg.Save(); }
            GUILayout.EndHorizontal();
            if (_cfg.lockstep && _s.Lockstep != null && _s.InWorld)
                GUILayout.Label("<color=#aab>   " + _s.Lockstep.StatusLine() + "</color>", _small);
            WorldSync sync = CoopMod.Instance?.Sync;
            if (sync != null && _s.InWorld && !_s.IsHost && _cfg.liveSync)
                GUILayout.Label(sync.Disabled
                    ? "<color=#ffb14d>   The server is out of date - live sync is off</color>"
                    : "<color=#aab>   " + sync.UnitsLoaded + " creatures / " + sync.BuildingsLoaded + " buildings received, " +
                      sync.UnitsRemoved + " / " + sync.BuildingsRemoved + " removed, " + sync.UnitsCorrected + " far corrections</color>", _small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Full re-sync with host:", _small, GUILayout.Width(160));
            foreach (int m in new[] { 0, 10, 30, 60 })
                if (Tog(_cfg.autoResyncMinutes == m, m == 0 ? " off" : " " + m + " min", _small, GUILayout.Width(66)) && _cfg.autoResyncMinutes != m)
                { _cfg.autoResyncMinutes = m; _cfg.Save(); }
            GUILayout.EndHorizontal();

            if (_s.Online)
            {
                GUILayout.Space(10);
                GUILayout.Label("Players online (" + _s.Players.Count + ")", _label);
                _playersScroll = GUILayout.BeginScrollView(_playersScroll, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(150));
                foreach (PlayerInfo p in _s.Players)
                {
                    GUILayout.BeginHorizontal();
                    string where = _s.Rooms.TryGetValue(p.room ?? "", out RoomInfo ri) ? ri.name : (string.IsNullOrEmpty(p.room) ? "(lobby)" : p.room);
                    GUILayout.Label("<color=" + p.color + ">●</color> <b>" + Esc(p.name) + "</b>" + (p.id == _s.MyId ? " (you)" : "") +
                                    "  <color=#aab>in " + Esc(where) + (p.host ? " · host" : "") + (p.spectator ? " · watching" : "") +
                                    (p.ping >= 0 ? " · " + PingText(p.ping) : "") + "</color>", _small);
                    GUILayout.FlexibleSpace();
                    bool sameWorld = p.id != _s.MyId && p.room == _s.RoomId && _s.InWorld;
                    if (sameWorld && _s.IsAdmin && !_s.IsOwnerOf(p))
                    {
                        if (_confirmKick == p.id)
                        {
                            if (GUILayout.Button("Remove?", _btn, GUILayout.Width(70))) { _s.Kick(p.id); _confirmKick = null; }
                        }
                        else if (GUILayout.Button("Kick", _btn, GUILayout.Width(44))) _confirmKick = p.id;
                    }
                    if (sameWorld && GUILayout.Button("Find", _btn, GUILayout.Width(54))) FocusOn(p.id);
                    else if (p.id != _s.MyId && p.room != _s.RoomId && !string.IsNullOrEmpty(p.room) && GUILayout.Button("Join", _btn, GUILayout.Width(54)))
                    { _s.Travel(p.room); MenuOpen = false; }
                    GUILayout.EndHorizontal();
                    if (!string.IsNullOrEmpty(p.game) && p.game != Application.version && p.game != "test")
                        GUILayout.Label("   <color=#ffb14d>⚠ different WorldBox version (" + Esc(p.game) + ") - worlds may not load</color>", _small);
                }
                GUILayout.EndScrollView();
            }
            if (_s.IsAdmin) DrawWorldSettings();

            GUILayout.Space(10);
            GUILayout.Label("Tools", _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export diagnostics", _btn, GUILayout.Width(150))) _s.ExportDiagnostics();
            GUI.enabled = WorldBoxApi.WorldReady;
            if (GUILayout.Button("Back up this map now", _btn, GUILayout.Width(160)))
            {
                try { WorldBoxApi.BackupCurrentWorld("manual"); ShowToast("Map backed up (coopfall/backups)"); }
                catch (System.Exception e) { ShowToast("Backup failed: " + e.Message); }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aab>Diagnostics: a zip with logs, settings and mods for bug reports (your user name is removed). " +
                            "Gameplay mods: " + ModScan.Mods.Count + (ModScan.ClientOnly.Count > 0 ? ", client-only: " + Esc(string.Join(", ", ModScan.ClientOnly.ToArray())) : "") + "</color>", _small);
            GUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Keys: " + _cfg.mapKey + " world map · " + _cfg.menuKey + " this menu · " + _cfg.chatKey + " chat (/sync, /home)", _dim);
            GUILayout.EndArea();
        }

        /// <summary>A toggle that shows its state (the label styles have no check box).</summary>
        private static bool Tog(bool value, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            string mark = value ? "<color=#5fd16a>●</color>" : "<color=#667>○</color>";
            return GUILayout.Toggle(value, mark + " " + (text ?? "").TrimStart(), style, options);
        }

        /// <summary>Scrolls the Co-op menu to the bottom (world settings / tools); used by the lobby test.</summary>
        public void ScrollMenuToEnd() { _menuScroll.y = 100000f; }

        private static string PingText(int ms)
        {
            string c = ms < 100 ? "#7f7" : (ms < 250 ? "#fd6" : "#f77");
            return "<color=" + c + ">" + ms + " ms</color>";
        }

        /// <summary>Owner (or the shared world's host): who may come in and what guests may do.</summary>
        private void DrawWorldSettings()
        {
            RoomInfo r = _s.CurrentRoom;
            if (r == null) return;
            GUILayout.Space(10);
            GUILayout.Label("This world's settings <color=#aab>(" + (_s.IsRealAdmin ? "you run " + Esc(r.name) : "everyone here may change them") + ")</color>", _label);
            if (_s.IsRealAdmin)
            {
                bool all = Tog(r.everyoneAdmin, " Everyone here is an admin (may change these settings and kick)", _small);
                if (all != r.everyoneAdmin) _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["everyoneAdmin"] = all });
            }
            if (r.everyoneAdmin)
                GUILayout.Label("<color=#aab>   While everyone is an admin, the guest limits below don't apply to players here.</color>", _small);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Password", _small, GUILayout.Width(70));
            _setPw = GUILayout.TextField(_setPw ?? "", 32, _field, GUILayout.Width(130));
            if (GUILayout.Button(_setPw.Length > 0 ? "Set" : "Remove", _btn, GUILayout.Width(70)))
            {
                _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["password"] = _setPw.Trim() });
                ShowToast(_setPw.Trim().Length > 0 ? "Password set - tell your friends" : "Password removed");
            }
            GUILayout.Label(r.hasPassword ? "<color=#7f7>on</color>" : "<color=#aab>none</color>", _small);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool locked = Tog(r.locked, " Locked (nobody new)", _small, GUILayout.Width(160));
            bool approval = Tog(r.approval, " Ask me before people join", _small, GUILayout.Width(190));
            bool spect = Tog(r.spectatorsAllowed, " Spectators", _small);
            GUILayout.EndHorizontal();
            if (locked != r.locked) _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["locked"] = locked });
            if (approval != r.approval) _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["approval"] = approval });
            if (spect != r.spectatorsAllowed) _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["spectators"] = spect });

            GUILayout.BeginHorizontal();
            GUILayout.Label("Max players", _small, GUILayout.Width(90));
            foreach (int m in new[] { 0, 2, 4, 8, 16 })
                if (Tog(r.maxPlayers == m, m == 0 ? " any" : " " + m, _small, GUILayout.Width(56)) && r.maxPlayers != m)
                    _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["maxPlayers"] = m });
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Guest powers", _small, GUILayout.Width(90));
            foreach (string[] o in new[] { new[] { "all", " all" }, new[] { "safe", " no destructive" }, new[] { "none", " none" } })
                if (Tog(r.guestPowers == o[0], o[1], _small, GUILayout.Width(o[0] == "safe" ? 120 : 60)) && r.guestPowers != o[0])
                {
                    var msg = new Newtonsoft.Json.Linq.JObject { ["guestPowers"] = o[0] };
                    if (o[0] == "safe") msg["blocked"] = new Newtonsoft.Json.Linq.JArray(PowerSync.DestructivePowers().ToArray());
                    _s.SendSettings(msg);
                }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool speed = Tog(r.guestSpeed, " Guests may change speed / pause", _small, GUILayout.Width(240));
            bool extra = Tog(r.allowExtraMods, " Allow guests' extra mods", _small);
            GUILayout.EndHorizontal();
            if (speed != r.guestSpeed) _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["guestSpeed"] = speed });
            if (extra != r.allowExtraMods) _s.SendSettings(new Newtonsoft.Json.Linq.JObject { ["allowExtraMods"] = extra });
        }

        // ---------------------------------------------------------------- popups

        private void DrawJoinRequests()
        {
            if (_s.JoinRequests.Count == 0) return;
            float y = (_fp ? _hudRect.yMax : 40f) + 6f;
            foreach (JoinRequest jr in _s.JoinRequests.ToArray())
            {
                Rect r = new Rect((W - 420) / 2f, y, 420, 36);
                GUI.Box(r, GUIContent.none, _panel);
                Block(r);
                GUI.Label(new Rect(r.x + 10, r.y + 8, 250, 22), "<b>" + Esc(jr.name) + "</b> wants to " + (jr.spectate ? "watch" : "join"), _small);
                if (GUI.Button(new Rect(r.xMax - 150, r.y + 6, 70, 24), "Let in", _btnAccent)) _s.Answer(jr, true);
                if (GUI.Button(new Rect(r.xMax - 74, r.y + 6, 64, 24), "No", _btn)) _s.Answer(jr, false);
                y += 40f;
            }
        }

        private void DrawPasswordPrompt()
        {
            if (_s.PasswordRoom == null) return;
            Rect r = new Rect((W - 380) / 2f, H * 0.3f, 380, 120);
            GUI.Box(r, GUIContent.none, _panel);
            Block(r);
            GUI.Label(new Rect(r.x + 16, r.y + 12, 350, 22), "<b>" + Esc(_s.PasswordPrompt) + "</b>", _label);
            GUI.SetNextControlName("coopfall_pw");
            _joinPw = GUI.PasswordField(new Rect(r.x + 16, r.y + 44, 348, 26), _joinPw ?? "", '•', 32, _field);
            if (GUI.Button(new Rect(r.x + 16, r.y + 80, 120, 26), "Join", _btnAccent) ||
                (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "coopfall_pw"))
            {
                string pw = _joinPw;
                _joinPw = "";
                _s.JoinWithPassword(pw);
            }
            if (GUI.Button(new Rect(r.x + 144, r.y + 80, 90, 26), "Cancel", _btn)) { _s.PasswordRoom = null; _joinPw = ""; }
        }

        private void DrawModMismatch()
        {
            Newtonsoft.Json.Linq.JObject m = _s.ModMismatch;
            if (m == null) return;
            Rect r = new Rect((W - 480) / 2f, H * 0.22f, 480, 300);
            GUI.Box(r, GUIContent.none, _panel);
            Block(r);
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 24));
            string name = _s.Rooms.TryGetValue(_s.ModMismatchRoom ?? "", out RoomInfo ri) ? ri.name : _s.ModMismatchRoom;
            GUILayout.Label("<b>Your mods don't match " + Esc(name) + "</b>", _label);
            GUILayout.Label("<color=#aab>Different gameplay mods make the worlds drift apart, so you can't enter yet. Chat still works. " +
                            "Install / remove these, restart WorldBox and try again.</color>", _small);
            _modsScroll = GUILayout.BeginScrollView(_modsScroll, GUILayout.Height(150));
            ModList(m, "missing", "Missing (install these)", "#f77");
            ModList(m, "different", "Different version", "#fd6");
            ModList(m, "extra", "Only you have these (remove, or ask the owner to allow extra mods)", "#7cf");
            GUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("OK", _btnAccent, GUILayout.Width(90))) _s.ModMismatch = null;
            GUILayout.Label("<color=#aab>  A mod that only changes your screen can be added to clientOnlyMods in config.json.</color>", _small);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void ModList(Newtonsoft.Json.Linq.JObject m, string key, string title, string color)
        {
            if (!(m[key] is Newtonsoft.Json.Linq.JArray arr) || arr.Count == 0) return;
            GUILayout.Label("<color=" + color + "><b>" + title + "</b></color>", _small);
            foreach (Newtonsoft.Json.Linq.JToken t in arr) GUILayout.Label("   • " + Esc((string)t), _small);
        }

        private void ApplyConnectionFields()
        {
            _cfg.playerName = string.IsNullOrWhiteSpace(_nameEdit) ? _cfg.playerName : _nameEdit.Trim();
            string host = (_hostEdit ?? "").Trim();
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host.Substring(colon + 1), out int hp))
            {
                _portEdit = hp.ToString();
                host = host.Substring(0, colon);
                _hostEdit = host;
            }
            _cfg.serverHost = host.Length == 0 ? "127.0.0.1" : host;
            if (int.TryParse(_portEdit, out int port) && port > 0 && port < 65536) _cfg.serverPort = port;
            _portEdit = _cfg.serverPort.ToString();
            _cfg.Save();
        }

        private void FocusOn(string playerId)
        {
            if (!_av.Remotes.TryGetValue(playerId, out AvatarManager.Remote r)) { ShowToast("Can't see them yet"); return; }
            Vector2 at;
            if (r.on && r.actor != null) at = r.actor.current_position;
            else if (r.hasCursor) at = r.cursor;
            else { ShowToast(r.name + " hasn't moved yet"); return; }
            Camera cam = WorldBoxApi.MapCamera;
            if (cam != null) cam.transform.position = new Vector3(at.x, at.y, cam.transform.position.z);
            MenuOpen = false;
        }

        // ---------------------------------------------------------------- World Map

        private void DrawMap()
        {
            float w = Mathf.Min(W - 40f, 900f), h = Mathf.Min(H - 90f, 640f);
            Rect r = new Rect((W - w) / 2f, 48f, w, h);
            GUI.Box(r, GUIContent.none, _panel);
            Block(r);
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 24));
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>World Map</b>  ·  click a world to travel there", _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("✕", _btn, GUILayout.Width(28))) MapOpen = false;
            GUILayout.EndHorizontal();

            if (!_s.Online)
            {
                GUILayout.Space(30);
                GUILayout.Label("You're not connected. Open the Co-op menu (" + _cfg.menuKey + ") and press Connect.", _label);
                if (GUILayout.Button("Open Co-op menu", _btnAccent, GUILayout.Width(180))) { MapOpen = false; MenuOpen = true; }
                GUILayout.EndArea();
                return;
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = !_s.Busy;
            if (GUILayout.Button("⌂ My world", _btn, GUILayout.Width(110))) { _s.GoHome(); MapOpen = false; }
            if (GUILayout.Button("Shared world", _btn, GUILayout.Width(110))) { _s.Travel("shared", "Shared World", true, false); MapOpen = false; }
            GUI.enabled = !_s.Busy && _s.InWorld && !_s.IsHost;
            if (GUILayout.Button("⟳ Re-sync now", _btn, GUILayout.Width(120))) { _s.Resync(); MapOpen = false; }
            GUI.enabled = !_s.Busy && _s.InWorld;
            GUILayout.Space(16);
            _newWorldName = GUILayout.TextField(_newWorldName ?? "", 40, _field, GUILayout.Width(150));
            if (GUILayout.Button("+ Publish this map as new world", _btn)) { _s.PublishAsNewWorld(_newWorldName); _newWorldName = ""; MapOpen = false; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            const float cardW = 200f, cardH = 262f;
            int cols = Mathf.Max(1, (int)((r.width - 40) / (cardW + 12)));
            _mapScroll = GUILayout.BeginScrollView(_mapScroll);
            int i = 0;
            if (_s.RoomOrder.Count == 0) GUILayout.Label("No worlds on this server yet.", _label);
            while (i < _s.RoomOrder.Count)
            {
                GUILayout.BeginHorizontal();
                for (int c = 0; c < cols && i < _s.RoomOrder.Count; c++, i++)
                {
                    if (_s.Rooms.TryGetValue(_s.RoomOrder[i], out RoomInfo room)) DrawCard(room, cardW, cardH);
                    GUILayout.Space(12);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(12);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawCard(RoomInfo room, float w, float h)
        {
            bool here = room.id == _s.RoomId && _s.InWorld;
            bool going = room.id == _s.TravelTarget && _s.Busy;
            GUILayout.BeginVertical(_panel, GUILayout.Width(w), GUILayout.Height(h));
            Rect img = GUILayoutUtility.GetRect(w - 16, 128, GUILayout.Width(w - 16), GUILayout.Height(128));
            Fill(img, Backdrop);
            if (_s.Previews.TryGetValue(room.id, out Texture2D tex) && tex != null)
                GUI.DrawTexture(img, tex, ScaleMode.ScaleToFit);
            else GUI.Label(new Rect(img.x, img.y + 54, img.width, 20), "no preview yet", _dim);
            if (here)
            {
                GUI.color = CoopConfig.ParseColor(_cfg.color);
                GUI.Label(new Rect(img.x + 4, img.y + 4, 120, 20), "<b>● YOU ARE HERE</b>", _tag);
                GUI.color = Color.white;
            }

            GUILayout.Label((room.hasPassword || room.locked ? "🔒 " : "") + "<b>" + Esc(room.name) + "</b>", _cardTitle);
            string kind = room.kind == "shared" ? "Shared world" : (room.kind == "home" ? Esc(room.owner) + "'s own world" : "by " + Esc(room.owner));
            GUILayout.Label("<color=#aab>" + kind + (room.year > 0 ? "  ·  year " + room.year : "") + (room.pop > 0 ? "  ·  " + room.pop + " units" : "") + "</color>", _small);
            if (room.players > 0)
                GUILayout.Label("👥 " + Esc(string.Join(", ", room.names.ToArray())) + (string.IsNullOrEmpty(room.host) ? "" : "  <color=#aab>(host: " + Esc(room.host) + ")</color>") +
                                (room.spectating > 0 ? "  <color=#aab>· " + room.spectating + " watching</color>" : "") +
                                (room.maxPlayers > 0 ? "  <color=#aab>· max " + room.maxPlayers + "</color>" : ""), _small);
            else
                GUILayout.Label("<color=#aab>nobody here · " + (room.size > 0 ? (room.size / 1024) + " KB saved" : "empty") + "</color>", _small);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (here) GUILayout.Label("<color=#7f7>You are here</color>", _small);
            else if (going) GUILayout.Label("<color=#7cf>Traveling…</color>", _small);
            else
            {
                GUI.enabled = !_s.Busy;
                if (GUILayout.Button("Travel ➜", _btnAccent, GUILayout.Height(26))) { _s.Travel(room.id); MapOpen = false; }
                if (room.spectatorsAllowed && !string.IsNullOrEmpty(room.host) && GUILayout.Button("Watch", _btn, GUILayout.Width(56), GUILayout.Height(26)))
                { _s.Travel(room.id, null, false, false, false, true); MapOpen = false; }
                GUI.enabled = true;
            }
            bool mine = !string.IsNullOrEmpty(room.owner) && room.owner == _s.MyName;
            if (mine && room.players == 0 && room.kind != "home")
            {
                if (_confirmDelete == room.id)
                {
                    if (GUILayout.Button("Sure?", _btn, GUILayout.Width(52))) { _s.DeleteRoom(room.id); _confirmDelete = null; }
                }
                else if (GUILayout.Button("🗑", _btn, GUILayout.Width(30))) _confirmDelete = room.id;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        // ---------------------------------------------------------------- styles

        private void BuildStyles()
        {
            if (_styled) return;
            _styled = true;
            _texWhite = Solid(Color.white);
            _texPanel = Rounded(new Color(0.07f, 0.08f, 0.11f, 0.92f), new Color(1f, 1f, 1f, 0.10f));
            _texHud = Rounded(new Color(0.05f, 0.06f, 0.09f, 0.80f), new Color(1f, 1f, 1f, 0.08f));
            _texBtn = Rounded(new Color(0.20f, 0.22f, 0.28f, 1f), new Color(1f, 1f, 1f, 0.10f));
            _texBtnHover = Rounded(new Color(0.28f, 0.31f, 0.40f, 1f), new Color(1f, 1f, 1f, 0.16f));
            _texAccent = Rounded(new Color(0.18f, 0.55f, 0.95f, 1f), new Color(1f, 1f, 1f, 0.15f));
            _texAccentHover = Rounded(new Color(0.28f, 0.65f, 1f, 1f), new Color(1f, 1f, 1f, 0.2f));
            _texField = Rounded(new Color(0.02f, 0.02f, 0.04f, 0.85f), new Color(1f, 1f, 1f, 0.12f));
            _texBubble = Rounded(new Color(1f, 1f, 1f, 0.94f), new Color(0f, 0f, 0f, 0.2f));
            _texRing = Ring(32, 0.36f, 0.48f);
            _texDot = Ring(16, 0f, 0.5f);

            Font font = GUI.skin.font;
            _panel = new GUIStyle { normal = { background = _texPanel }, border = new RectOffset(6, 6, 6, 6), padding = new RectOffset(8, 8, 8, 8) };
            _hud = new GUIStyle(_panel) { normal = { background = _texHud } };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, normal = { textColor = new Color(0.92f, 0.94f, 1f) } };
            _title = new GUIStyle(_label) { fontSize = 18 };
            _cardTitle = new GUIStyle(_label) { fontSize = 15, wordWrap = false, clipping = TextClipping.Clip };
            _small = new GUIStyle(_label) { fontSize = 12, wordWrap = true };
            _dim = new GUIStyle(_small) { normal = { textColor = new Color(0.6f, 0.64f, 0.72f) } };
            _btn = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12, richText = true, border = new RectOffset(6, 6, 6, 6), padding = new RectOffset(8, 8, 3, 3),
                normal = { background = _texBtn, textColor = Color.white }, hover = { background = _texBtnHover, textColor = Color.white },
                active = { background = _texAccent, textColor = Color.white }, focused = { background = _texBtn, textColor = Color.white },
            };
            _btnAccent = new GUIStyle(_btn) { fontStyle = FontStyle.Bold, normal = { background = _texAccent, textColor = Color.white }, hover = { background = _texAccentHover, textColor = Color.white } };
            _field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13, border = new RectOffset(6, 6, 6, 6), padding = new RectOffset(7, 7, 4, 4),
                normal = { background = _texField, textColor = Color.white }, focused = { background = _texField, textColor = Color.white },
                hover = { background = _texField, textColor = Color.white },
            };
            _tag = new GUIStyle(_hud) { fontSize = 12, richText = true, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(6, 6, 2, 2), normal = { background = _texHud, textColor = Color.white } };
            _chatLine = new GUIStyle(_tag) { alignment = TextAnchor.MiddleLeft, fontSize = 13 };
            _toast = new GUIStyle(_tag) { fontSize = 13 };
            _bubble = new GUIStyle(_tag) { wordWrap = true, fontSize = 12, normal = { background = _texBubble, textColor = new Color(0.1f, 0.1f, 0.12f) } };
            if (font != null) { _label.font = font; }
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static Texture2D Rounded(Color fill, Color edge)
        {
            const int S = 16;
            const float R = 5.5f;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, R, S - R), cy = Mathf.Clamp(y + 0.5f, R, S - R);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    float a = Mathf.Clamp01(R - d + 0.5f);
                    Color c = d > R - 1.2f ? Color.Lerp(fill, edge, edge.a > 0 ? 0.6f : 0f) : fill;
                    c.a = (d > R - 1.2f ? Mathf.Max(fill.a, edge.a) : fill.a) * a;
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        private static Texture2D Ring(int size, float inner, float outer)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            float c = size / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(c, c)) / size;
                    float a = Mathf.Clamp01((outer - d) * size) * (inner <= 0f ? 1f : Mathf.Clamp01((d - inner) * size));
                    t.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            t.Apply();
            return t;
        }

        // ---------------------------------------------------------------- misc

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("<", "‹").Replace(">", "›");
        }

        private static string Short(string key) { return "<size=10><color=#99a>" + key + "</color></size>"; }

        private static string Pretty(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string s = id.Replace('_', ' ');
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
