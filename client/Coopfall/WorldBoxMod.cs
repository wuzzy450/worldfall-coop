using System;
using UnityEngine;

namespace Coopfall
{
    /// <summary>Global access to the running mod.</summary>
    public static class CoopMod
    {
        public static WorldBoxMod Instance;
        /// <summary>True while the co-op UI wants the mouse/keyboard (game controls are locked).</summary>
        public static bool UiBlocking;
        public const string Version = "1.1.0";
    }

    /// <summary>
    /// REQUIRED BY WORLDBOX'S ModLoader CONTRACT: the game loads "Coopfall.dll" from
    /// worldbox_Data/StreamingAssets/mods and AddComponents "Coopfall.WorldBoxMod" once
    /// Config.game_loaded &amp;&amp; Config.experimental_mode are both true.
    /// </summary>
    public class WorldBoxMod : MonoBehaviour
    {
        public CoopSession Session;
        public AvatarManager Avatars;
        public PowerSync Powers;
        public WorldSync Sync;
        public MetaSync Meta;
        public TileSync Tiles;
        public WeatherSync Weather;
        public CombatSync Combat;
        public TestDriver Test;
        public DiagSync Diag;
        public CoopUI UI;
        public Lockstep.DeterminismProbe Probe;
        private readonly InputGuard _guard = new InputGuard();

        private bool _weLockedControls;
        private bool _autoConnectDone;
        private bool _weLeftFirstPerson;

        private void Awake()
        {
            if (CoopMod.Instance != null) { Destroy(this); return; }
            CoopMod.Instance = this;
            Log.Init();
            Lockstep.HarmonyLoader.Init();   // before anything that uses Harmony is compiled
            Log.Info("Coopfall " + CoopMod.Version + " starting (WorldBox " + Application.version + ")");
            CoopConfig cfg = CoopConfig.Load();
            Session = new CoopSession(cfg);
            Avatars = new AvatarManager(Session);
            Powers = new PowerSync(Session);
            Sync = new WorldSync(Session);
            Meta = new MetaSync(Session);
            Tiles = new TileSync(Session);
            Weather = new WeatherSync(Session);
            Combat = new CombatSync(Session);
            Diag = new DiagSync(Session);
            UI = new CoopUI(Session, Avatars);
            Test = TestDriver.FromCommandLine(Session);
            InitLockstep();
            Log.Info("ready - " + cfg.menuKey + " co-op menu, " + cfg.mapKey + " world map, " + cfg.chatKey + " chat");
        }

        /// <summary>
        /// Kept out of Awake: Awake runs HarmonyLoader.Init, and anything that touches a Harmony
        /// type must be compiled after that, which a separate non-inlined method guarantees.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void InitLockstep()
        {
            Probe = Lockstep.DeterminismProbe.FromCommandLine();
        }

        private void Start()
        {
            UI.ShowToast("Coopfall co-op loaded - press " + Session.Cfg.menuKey + " to play with friends");
        }

        private void Update()
        {
            Log.Tick();
            ProfileDb.Tick();
            Prof.Frame();
            if (!Config.game_loaded) return;
            try
            {
                Powers.TryInstall();
                Combat.TryInstall();
                UI.HandleKeys();
                Prof.Run("session", Session.Tick);
                Prof.Run("powers", Powers.Tick);
                Prof.Run("sync", Sync.Tick);
                Prof.Run("meta", Meta.Tick);
                Prof.Run("tiles", Tiles.Tick);
                Prof.Run("weather", Weather.Tick);
                if (Test != null) Prof.Run("test", Test.Tick);
                if (Probe != null) Prof.Run("determinism", Probe.Tick);
                Prof.Run("diag", Diag.Tick);

                if (!_autoConnectDone && WorldBoxApi.WorldReady && (Test == null || !Test.HoldConnect) && Probe == null)
                {
                    _autoConnectDone = true;
                    if (Session.Cfg.autoConnect) Session.Connect();
                }

                bool block = UI.ChatOpen || UI.MouseOverUi;
                CoopMod.UiBlocking = block || UI.Typing;
                if (block) { Config.lockGameControls = true; _weLockedControls = true; }
                else if (_weLockedControls) { Config.lockGameControls = false; _weLockedControls = false; }
                // Chat, and the co-op windows (their Esc must not also reach the game: it would end possession).
                _guard.Update(UI.Typing || UI.MenuOpen || UI.MapOpen);
                FirstPersonWindows();
                ConsoleOnError();
            }
            catch (Exception e) { Log.Error("Update: " + e); }
        }

        private bool _consoleMuted, _consoleWas;

        /// <summary>
        /// WorldBox opens its error console on the first error, and from then on rebuilds and lays out
        /// its whole text (up to 2500 lines) on every repeat: hundreds of ms a frame. In a co-op session
        /// don't open it by itself (errors still go to the logs; it still opens by hand).
        /// </summary>
        private void ConsoleOnError()
        {
            bool mute = Session.InWorld;
            if (mute == _consoleMuted) return;
            _consoleMuted = mute;
            if (mute) { _consoleWas = Config.show_console_on_error; Config.show_console_on_error = false; }
            else Config.show_console_on_error = _consoleWas;
        }

        private void LateUpdate()
        {
            Prof.Late();
            if (!Config.game_loaded) return;
            try
            {
                Prof.Run("avatars-captureactions", Avatars.CaptureActions);
                Prof.Run("avatars-late", Avatars.LateTick);
                Prof.Run("sync-late", Sync.LateTick);
                Prof.Run("shots-late", Combat.LateTick);
                Prof.Run("diag-late", Diag.LateTick);
            }
            catch (Exception e) { Log.Error("LateUpdate: " + e); }
        }

        /// <summary>
        /// The Co-op menu and World Map need the mouse, which Worldfall's first person captures for
        /// looking around: while one is open, switch to Worldfall's top-down view (its V key) and
        /// return to first person when it closes.
        /// </summary>
        private void FirstPersonWindows()
        {
            bool windows = UI.MenuOpen || UI.MapOpen;
            if (windows && !_weLeftFirstPerson && WorldfallBridge.FirstPerson)
            {
                WorldfallBridge.ViewEnabled = false;
                _weLeftFirstPerson = true;
            }
            else if (!windows && _weLeftFirstPerson)
            {
                _weLeftFirstPerson = false;
                if (ControllableUnit.isControllingUnit()) WorldfallBridge.ViewEnabled = true;
            }
        }

        private void OnDestroy()
        {
            _guard.Shutdown();
        }

        private void OnGUI()
        {
            try { if (UI != null) Prof.Run("gui-" + Event.current.type, UI.OnGUI); }
            catch (Exception e) { Log.Error("OnGUI: " + e); GUI.matrix = Matrix4x4.identity; }
        }

        private void OnApplicationQuit()
        {
            try
            {
                _guard.Shutdown();
                Session?.SaveBeforeQuit();
                Session?.Disconnect();
                Log.Flush();
            }
            catch { }
        }
    }
}
