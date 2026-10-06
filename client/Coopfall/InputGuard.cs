using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Coopfall
{
    /// <summary>
    /// Keeps the game (and Worldfall) from reacting to keys while you type in the co-op chat or
    /// a co-op window is open. Coopfall's UI is IMGUI, which WorldBox and Worldfall can't see, so meanwhile:
    /// * an invisible uGUI InputField holds keyboard focus: WorldBox skips its hotkeys and Worldfall
    ///   treats the player as typing (both check for a focused InputField);
    /// * every WorldBox hotkey's key is unbound, because possession movement (WASD, jump, dash...)
    ///   reads hotkeys directly without that check. The bindings are restored when it closes;
    /// * Worldfall's talk/door prompts (E, Q, T, F, R) are told a conversation just closed.
    /// </summary>
    public class InputGuard
    {
        private GameObject _root, _fieldGo;
        private InputField _field;
        private bool _active;

        private class Keys { public KeyCode k1, k2, k3, m1, m2, m3; }
        private readonly Dictionary<HotkeyAsset, Keys> _saved = new Dictionary<HotkeyAsset, Keys>();

        public void Update(bool typing)
        {
            if (typing && !_active) Begin();
            else if (!typing && _active) End();
            if (!_active) return;

            WorldfallBridge.MutePrompts();
            try
            {
                if (_field != null)
                {
                    if (_field.text.Length > 0) _field.text = "";
                    EventSystem es = EventSystem.current;
                    if (es != null && es.currentSelectedGameObject != _fieldGo)
                    {
                        es.SetSelectedGameObject(_fieldGo);
                        _field.ActivateInputField();
                    }
                }
            }
            catch (Exception e) { Log.Warn("input guard: " + e.Message); }
        }

        private void Begin()
        {
            _active = true;
            MuteHotkeys();
            try
            {
                Build();
                EventSystem es = EventSystem.current;
                if (es != null) es.SetSelectedGameObject(_fieldGo);
                _field.text = "";
                _field.ActivateInputField();
            }
            catch (Exception e) { Log.Warn("input guard focus: " + e.Message); }
        }

        private void End()
        {
            _active = false;
            RestoreHotkeys();
            try
            {
                if (_field != null) _field.DeactivateInputField();
                EventSystem es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject == _fieldGo) es.SetSelectedGameObject(null);
            }
            catch (Exception e) { Log.Warn("input guard release: " + e.Message); }
        }

        /// <summary>Game closing or mod unloading: never leave the player's hotkeys unbound.</summary>
        public void Shutdown()
        {
            if (_active) End();
        }

        private void Build()
        {
            if (_field != null) return;
            _root = new GameObject("CoopfallChatFocus");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -32000;

            _fieldGo = new GameObject("Field", typeof(RectTransform));
            _fieldGo.transform.SetParent(_root.transform, false);
            var rt = (RectTransform)_fieldGo.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.anchoredPosition = new Vector2(-5000f, -5000f);   // far off screen
            rt.sizeDelta = new Vector2(10f, 10f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(_fieldGo.transform, false);
            var text = textGo.AddComponent<Text>();
            text.font = Font.CreateDynamicFontFromOSFont("Arial", 12);
            text.color = new Color(0f, 0f, 0f, 0f);
            text.raycastTarget = false;
            text.supportRichText = false;

            _field = _fieldGo.AddComponent<InputField>();
            _field.textComponent = text;
            _field.lineType = InputField.LineType.SingleLine;
            _field.transition = Selectable.Transition.None;
        }

        private void MuteHotkeys()
        {
            _saved.Clear();
            try
            {
                foreach (HotkeyAsset h in AssetManager.hotkey_library.list)
                {
                    if (h == null) continue;
                    _saved[h] = new Keys
                    {
                        k1 = h.default_key_1, k2 = h.default_key_2, k3 = h.default_key_3,
                        m1 = h.default_key_mod_1, m2 = h.default_key_mod_2, m3 = h.default_key_mod_3,
                    };
                    h.default_key_1 = h.default_key_2 = h.default_key_3 = KeyCode.None;
                    h.default_key_mod_1 = h.default_key_mod_2 = h.default_key_mod_3 = KeyCode.None;
                }
            }
            catch (Exception e) { Log.Warn("input guard mute: " + e.Message); RestoreHotkeys(); }
        }

        private void RestoreHotkeys()
        {
            foreach (var kv in _saved)
            {
                HotkeyAsset h = kv.Key;
                Keys k = kv.Value;
                h.default_key_1 = k.k1; h.default_key_2 = k.k2; h.default_key_3 = k.k3;
                h.default_key_mod_1 = k.m1; h.default_key_mod_2 = k.m2; h.default_key_mod_3 = k.m3;
            }
            _saved.Clear();
        }
    }
}
