using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

namespace SpaceMayhem
{
    /// <summary>
    /// Level picker. Finds every <see cref="LevelButton"/> beneath it, sets each button's label to the
    /// level's display name, and wires its click to load that level's scene. Adding a level needs no code or
    /// inspector wiring here — just drop another button carrying a <see cref="LevelButton"/>. Click handling
    /// is done in code, so nothing on this object needs UnityEvents configured.
    /// </summary>
    [DisallowMultipleComponent]
    public class LevelSelectMenu : MonoBehaviour
    {
        void Awake()
        {
            foreach (var level in GetComponentsInChildren<LevelButton>(includeInactive: true))
            {
                if (level == null) continue;

                var label = level.GetComponentInChildren<TMP_Text>(includeInactive: true);
                if (label != null && !string.IsNullOrEmpty(level.displayName))
                    label.text = level.displayName;

                var button = level.GetComponent<Button>();
                if (button != null)
                {
                    string scene = level.sceneName;          // capture per-button for the closure
                    button.onClick.AddListener(() => Load(scene));
                }
            }
        }

        public void Load(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                Debug.LogWarning("[LevelSelect] Level button has no sceneName assigned.");
                return;
            }
            SceneManager.LoadScene(sceneName);
        }
    }
}
