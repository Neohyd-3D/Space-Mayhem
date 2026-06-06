using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Tag/data component placed on a level-select button. Holds which scene that button loads and the name
    /// to show on it. <see cref="LevelSelectMenu"/> finds every one of these under itself and wires the
    /// button's click to load <see cref="sceneName"/> — so adding a level is just: duplicate the button and
    /// set these two strings. The scene must be present in Build Settings.
    /// </summary>
    [DisallowMultipleComponent]
    public class LevelButton : MonoBehaviour
    {
        [Tooltip("Scene to load when this button is pressed — must be in Build Settings.")]
        public string sceneName;

        [Tooltip("Name shown on the button (written to the button's text label at startup, if found).")]
        public string displayName;
    }
}
