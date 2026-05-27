using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// VFX bridge for the brake mechanic. Intentionally disabled while we focus on
    /// movement + camera. Will be re-implemented in a later pass alongside the
    /// HDRP Volume + radial blur post-process.
    /// </summary>
    [DisallowMultipleComponent]
    public class TimeDilationEffects : MonoBehaviour
    {
        // Fields kept (as inactive) so that any stale prefab references resolve
        // without throwing. Logic is deliberately empty for this iteration.
        [HideInInspector] public SpaceshipController controllerRef;
        [HideInInspector] public UnityEngine.Rendering.Volume brakeVolume;
        [HideInInspector] public AudioSource engineAudio;
    }
}
