using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpaceMayhem
{
    /// <summary>
    /// Basic game settings: master volume, fullscreen, and quality level. Loads saved values from
    /// PlayerPrefs and APPLIES them on Awake (so they hold even if the panel is never opened), then saves
    /// on every change. Controls are wired in code; the scene only needs the references assigned.
    /// </summary>
    [DisallowMultipleComponent]
    public class SettingsMenu : MonoBehaviour
    {
        public Slider       volumeSlider;
        public Toggle       fullscreenToggle;
        public TMP_Dropdown qualityDropdown;

        const string KeyVolume     = "settings.masterVolume";
        const string KeyFullscreen = "settings.fullscreen";
        const string KeyQuality    = "settings.qualityLevel";

        /// <summary>
        /// Apply the saved settings without touching any UI. Called at launch (by the main menu) so the
        /// player's choices hold even if they never open this panel.
        /// </summary>
        public static void ApplySaved()
        {
            AudioListener.volume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyVolume, 1f));
            Screen.fullScreen    = PlayerPrefs.GetInt(KeyFullscreen, Screen.fullScreen ? 1 : 0) == 1;
            QualitySettings.SetQualityLevel(
                Mathf.Clamp(PlayerPrefs.GetInt(KeyQuality, QualitySettings.GetQualityLevel()),
                            0, QualitySettings.names.Length - 1), true);
        }

        void Awake()
        {
            ApplySaved();

            float vol  = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyVolume, 1f));
            bool  full = PlayerPrefs.GetInt(KeyFullscreen, Screen.fullScreen ? 1 : 0) == 1;
            int   qual = Mathf.Clamp(PlayerPrefs.GetInt(KeyQuality, QualitySettings.GetQualityLevel()),
                                     0, QualitySettings.names.Length - 1);

            if (volumeSlider != null)
            {
                volumeSlider.minValue = 0f;
                volumeSlider.maxValue = 1f;
                volumeSlider.SetValueWithoutNotify(vol);
                volumeSlider.onValueChanged.AddListener(OnVolumeChanged);
            }
            if (fullscreenToggle != null)
            {
                fullscreenToggle.SetIsOnWithoutNotify(full);
                fullscreenToggle.onValueChanged.AddListener(OnFullscreenChanged);
            }
            if (qualityDropdown != null)
            {
                qualityDropdown.ClearOptions();
                qualityDropdown.AddOptions(new List<string>(QualitySettings.names));
                qualityDropdown.SetValueWithoutNotify(qual);
                qualityDropdown.RefreshShownValue();
                qualityDropdown.onValueChanged.AddListener(OnQualityChanged);
            }
        }

        void OnVolumeChanged(float v)
        {
            AudioListener.volume = v;
            PlayerPrefs.SetFloat(KeyVolume, v);
        }

        void OnFullscreenChanged(bool on)
        {
            Screen.fullScreen = on;
            PlayerPrefs.SetInt(KeyFullscreen, on ? 1 : 0);
        }

        void OnQualityChanged(int level)
        {
            QualitySettings.SetQualityLevel(level, true);
            PlayerPrefs.SetInt(KeyQuality, level);
        }
    }
}
