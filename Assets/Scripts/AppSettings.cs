using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Application-wide settings applied once at startup, before any scene loads.
    /// No GameObject attachment required.
    /// TargetFrameRate remains -1 so display/VSync pacing owns frame timing.
    /// </summary>
    static class AppSettings
    {
        // -1 = let VSync control timing (set QualitySettings.vSyncCount = 1 in Project Settings).
        // Use a positive value (60/90/120/144) only when vSyncCount is 0 (VSync off).
        const int TargetFrameRate = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
            // VSync is ON via QualitySettings; macOS player pacing is handled by
            // ProjectSettings/PlayerSettings.metalUseMetalDisplayLink.
            Application.targetFrameRate = TargetFrameRate;
        }
    }
}
