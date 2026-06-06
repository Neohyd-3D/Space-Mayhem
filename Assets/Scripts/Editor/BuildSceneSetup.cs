#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace SpaceMayhem.EditorTools
{
    /// <summary>
    /// One-click helper to keep the build scene list sane: MainMenu first (the game's entry point), then the
    /// playable levels. Run it from the "Space Mayhem" menu after adding a new level scene.
    /// </summary>
    public static class BuildSceneSetup
    {
        const string MainMenu = "Assets/Scenes/MainMenu.unity";

        [MenuItem("Space Mayhem/Setup Build Scenes")]
        public static void SetupBuildScenes()
        {
            var scenes = EditorBuildSettings.scenes.ToList();

            // MainMenu must exist exactly once, at index 0, enabled.
            scenes.RemoveAll(s => s.path == MainMenu);
            scenes.Insert(0, new EditorBuildSettingsScene(MainMenu, true));

            EditorBuildSettings.scenes = scenes.ToArray();
            UnityEngine.Debug.Log("[BuildSceneSetup] MainMenu set as build scene 0. Current scenes:\n" +
                string.Join("\n", EditorBuildSettings.scenes.Select((s, i) => $"  {i}: {(s.enabled ? "on " : "off")} {s.path}")));
        }
    }
}
#endif
