using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// Thin wrapper over WorldBox APIs (decompiled reference: knowledge/worldbox-decomp).
    /// Members that are INTERNAL in Assembly-CSharp are reached through cached reflection.
    /// Everything here must be called on the Unity main thread.
    /// </summary>
    public static class WorldBoxApi
    {
        public const string StandinFlag = "coopfall_standin";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _inited;
        private static MethodInfo _miAddStatus, _miSetFlip, _miPunch, _miSetCurTilePos;
        private static FieldInfo _fiFlip;
        private static PropertyInfo _piCamera;
        private static Action<Actor, bool> _findCurrentTile;

        private static void Init()
        {
            if (_inited) return;
            _inited = true;
            try
            {
                _miAddStatus = typeof(BaseSimObject).GetMethod("addStatusEffect", Any, null, new[] { typeof(string), typeof(float), typeof(bool) }, null);
                _miSetFlip = typeof(Actor).GetMethod("setFlip", Any, null, new[] { typeof(bool) }, null);
                _fiFlip = typeof(Actor).GetField("flip", Any);
                _miSetCurTilePos = typeof(Actor).GetMethod("setCurrentTilePosition", Any, null, new[] { typeof(WorldTile) }, null);
                foreach (MethodInfo m in typeof(Actor).GetMethods(Any))
                    if (m.Name == "punchTargetAnimation" && m.GetParameters().Length == 4) { _miPunch = m; break; }
                _piCamera = typeof(MapBox).GetProperty("camera", Any);
                MethodInfo find = typeof(Actor).GetMethod("findCurrentTile", Any, null, new[] { typeof(bool) }, null);
                if (find != null) _findCurrentTile = (Action<Actor, bool>)Delegate.CreateDelegate(typeof(Action<Actor, bool>), find);
                Log.Info("api: status=" + (_miAddStatus != null) + " flip=" + (_miSetFlip != null) + " punch=" + (_miPunch != null) +
                         " tp=" + (_miSetCurTilePos != null) + " cam=" + (_piCamera != null) + " tile=" + (_findCurrentTile != null));
            }
            catch (Exception e) { Log.Warn("WorldBoxApi init: " + e.Message); }
        }

        public static bool WorldReady
        {
            get { return Config.game_loaded && MapBox.instance != null && !Config.worldLoading; }
        }

        // ---------------------------------------------------------------- camera

        public static Camera MapCamera
        {
            get
            {
                Init();
                try { if (MapBox.instance != null && _piCamera != null) return _piCamera.GetValue(MapBox.instance, null) as Camera; }
                catch { }
                return Camera.main;
            }
        }

        /// <summary>
        /// True when WorldBox's own 2D map camera is what the player sees. Worldfall's 3D view uses
        /// another camera drawn on top; world-anchored overlays (name tags, cursors) are hidden then.
        /// </summary>
        public static bool MapCameraIsTopmost(Camera cam)
        {
            if (cam == null || !cam.isActiveAndEnabled) return false;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == cam || !c.isActiveAndEnabled || c.targetTexture != null) continue;
                if (c.depth > cam.depth && c.clearFlags != CameraClearFlags.Depth && c.clearFlags != CameraClearFlags.Nothing)
                    return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- actors

        public static Actor FindActor(long id)
        {
            try { return id > 0 && MapBox.instance != null ? MapBox.instance.units.get(id) : null; }
            catch { return null; }
        }

        public static void AddStatus(Actor a, string id, float seconds)
        {
            Init();
            try { _miAddStatus?.Invoke(a, new object[] { id, seconds, false }); } catch { }
        }

        public static bool GetFlip(Actor a)
        {
            Init();
            try { return _fiFlip != null && (bool)_fiFlip.GetValue(a); } catch { return false; }
        }

        public static void SetFlip(Actor a, bool flip)
        {
            Init();
            try { if (GetFlip(a) != flip) _miSetFlip?.Invoke(a, new object[] { flip }); } catch { }
        }

        public static void Punch(Actor a, Vector2 towards)
        {
            Init();
            try { _miPunch?.Invoke(a, new object[] { (Vector3)towards, false, false, 40f }); } catch { }
        }

        /// <summary>
        /// Moves a unit to a world position and updates the tile it stands on. Setting
        /// current_position alone leaves current_tile stale (the game only re-derives it while
        /// walking a path), and saves, combat and pathing all use current_tile.
        /// </summary>
        public static void SetPosition(Actor a, Vector2 pos)
        {
            Init();
            a.current_position = pos;
            if (_findCurrentTile != null) { try { _findCurrentTile(a, true); } catch { } }
        }

        public static void Teleport(Actor a, Vector2 pos)
        {
            Init();
            WorldTile tile = Toolbox.getTileAt(pos.x, pos.y);
            if (tile == null) return;
            try
            {
                a.stopMovement();
                if (_miSetCurTilePos != null) _miSetCurTilePos.Invoke(a, new object[] { tile });
                else a.current_tile = tile;
                a.current_position = pos;
            }
            catch (Exception e) { Log.Warn("teleport: " + e.Message); }
        }

        /// <summary>Removes an actor silently (no corpse, no death statistics).</summary>
        public static void RemoveActor(Actor a)
        {
            try { if (a != null && a.isAlive()) a.removeByMetamorphosis(); }
            catch (Exception e) { Log.Warn("remove actor: " + e.Message); }
        }

        public static Actor SpawnStandin(string assetId, Vector2 pos, string name, string ownerId)
        {
            if (AssetManager.actor_library.get(assetId) == null) assetId = "human";
            WorldTile tile = Toolbox.getTileAt(pos.x, pos.y) ?? World.world.GetTile(MapBox.width / 2, MapBox.height / 2);
            if (tile == null) return null;
            Actor a = World.world.units.spawnNewUnit(assetId, tile, false, false, 0f);
            if (a == null) return null;
            try
            {
                a.setName(name, false);
                a.getData().addFlag(StandinFlag);
                a.getData().set("coopfall_owner", ownerId);
            }
            catch (Exception e) { Log.Warn("tag standin: " + e.Message); }
            return a;
        }

        public static bool IsStandin(Actor a)
        {
            try { return a != null && a.getData().hasFlag(StandinFlag); } catch { return false; }
        }

        /// <summary>After a world load: remove stand-ins that were saved inside the snapshot.</summary>
        public static int PurgeStandins()
        {
            int n = 0;
            try
            {
                var list = new List<Actor>(World.world.units.getSimpleList());
                foreach (Actor a in list)
                    if (a != null && a.isAlive() && IsStandin(a)) { RemoveActor(a); n++; }
            }
            catch (Exception e) { Log.Warn("purge standins: " + e.Message); }
            return n;
        }

        // ---------------------------------------------------------------- worlds

        /// <summary>Serializes the live world exactly like a save file's map.wbox (zlib'd JSON).</summary>
        public static byte[] TakeSnapshot()
        {
            SavedMap map = SaveManager.currentWorldToSavedMap();
            return map.toZip();
        }

        /// <summary>Starts loading a snapshot into the live game (async via SmoothLoader; poll WorldReady).</summary>
        public static void LoadSnapshot(byte[] data)
        {
            try { ControllableUnit.clear(false); } catch { }
            try { SelectedUnit.clear(); } catch { }
            try { ScrollWindow.hideAllEvent(false); } catch { }
            SaveManager.loadMapFromBytes(data);
        }

        /// <summary>Saves the current world as a normal WorldBox save folder (loadable by copying into saves/).</summary>
        public static string BackupCurrentWorld(string reason)
        {
            string root = Path.Combine(Application.persistentDataPath, "coopfall", "backups");
            Directory.CreateDirectory(root);
            string dir = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + reason);
            SaveManager.saveWorldToDirectory(dir);
            // keep the 8 newest
            var dirs = new List<string>(Directory.GetDirectories(root));
            dirs.Sort(StringComparer.Ordinal);
            for (int i = 0; i < dirs.Count - 8; i++)
                try { Directory.Delete(dirs[i], true); } catch { }
            return dir;
        }

        /// <summary>Small PNG thumbnail of the current map (for the in-game world browser).</summary>
        public static byte[] MakePreviewPng(int maxSize)
        {
            Texture2D src = null, dst = null;
            try
            {
                src = PreviewHelper.convertMapToTexture();
                int sw = src.width, sh = src.height;
                float scale = Mathf.Min(1f, (float)maxSize / Mathf.Max(sw, sh));
                int dw = Mathf.Max(1, Mathf.RoundToInt(sw * scale)), dh = Mathf.Max(1, Mathf.RoundToInt(sh * scale));
                Color32[] sp = src.GetPixels32();
                var dp = new Color32[dw * dh];
                int dark = 0;
                for (int y = 0; y < dh; y++)
                {
                    int sy = Mathf.Min(sh - 1, (int)((y + 0.5f) / scale));
                    for (int x = 0; x < dw; x++)
                    {
                        int sx = Mathf.Min(sw - 1, (int)((x + 0.5f) / scale));
                        Color32 c = sp[sy * sw + sx];
                        c.a = 255;
                        if (c.r + c.g + c.b < 24) dark++;
                        dp[y * dw + x] = c;
                    }
                }
                // Right after a world load the map texture isn't drawn yet (all black): try later.
                if (dark > dp.Length * 0.9f) return null;
                dst = new Texture2D(dw, dh, TextureFormat.RGBA32, false);
                dst.SetPixels32(dp);
                dst.Apply();
                return ImageConversion.EncodeToPNG(dst);
            }
            catch (Exception e) { Log.Warn("preview: " + e.Message); return null; }
            finally
            {
                if (src != null) UnityEngine.Object.Destroy(src);
                if (dst != null) UnityEngine.Object.Destroy(dst);
            }
        }

        public static int Year()
        {
            try { return Date.getCurrentYear(); } catch { return 0; }
        }

        public static int Population()
        {
            try { return World.world.units.getSimpleList().Count; } catch { return 0; }
        }
    }
}
