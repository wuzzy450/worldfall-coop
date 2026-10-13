using NeoModLoader.api;
using UnityEngine;

namespace Coopfall
{
    /// <summary>
    /// NeoModLoader's way in: NML finds this IMod in Coopfall.dll in its Mods folder and calls
    /// OnLoad, which starts Coopfall exactly as WorldBox's own mod loader does (one
    /// Coopfall.WorldBoxMod component). If Coopfall.dll is also in StreamingAssets/mods, that copy
    /// is already running and this one stays off.
    /// </summary>
    public class CoopfallNml : MonoBehaviour, IMod
    {
        private ModDeclare _declare;

        public ModDeclare GetDeclaration() => _declare;

        public GameObject GetGameObject() => gameObject;

        public string GetUrl() => "https://github.com/wuzzy450/worldfall-coop";

        public void OnLoad(ModDeclare pModDecl, GameObject pGameObject)
        {
            _declare = pModDecl;
            foreach (MonoBehaviour b in FindObjectsOfType<MonoBehaviour>())
                if (b != null && b.GetType().FullName == "Coopfall.WorldBoxMod")
                {
                    Debug.LogWarning("[Coopfall] Already running from Coopfall.dll in the game's mods folder, so the NeoModLoader copy stays off. Keep one of the two installs.");
                    return;
                }
            pGameObject.AddComponent<WorldBoxMod>();
            Debug.Log("[Coopfall] Started by NeoModLoader from " + pModDecl.FolderPath);
        }
    }
}
