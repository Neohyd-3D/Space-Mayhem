using UnityEngine;
using Unity.Cinemachine;

namespace SpaceMayhem
{
    /// <summary>
    /// Speed-driven field-of-view: the faster the ship moves, the wider the camera FOV, for a
    /// sense-of-speed punch. Reads the ship's speed, normalizes it against a reference top speed,
    /// shapes it through a tunable curve, and lerps the CinemachineCamera lens between a low and a
    /// high FOV.
    ///
    /// This ONLY writes Lens.FieldOfView at runtime — it does not retune any of the vcam's authored
    /// settings (follow, damping, body/aim). It also caches the lens FOV the vcam was authored with
    /// and restores it when disabled, so leaving Play Mode never bakes a widened FOV into the asset.
    ///
    /// Attach to the CinemachineCamera object (auto-found) or any object with a reference to one.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpeedFOVEffect : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Cinemachine camera whose FOV is driven. Auto-found on this object / children if null.")]
        public CinemachineCamera targetCamera;

        [Tooltip("Ship to read speed from. Auto-found on a parent, else anywhere in the scene, if null.")]
        public SpaceshipController controller;

        [Header("FOV Range")]
        [Tooltip("FOV at zero speed (curve = 0). The narrow, calm look.")]
        public float lowestFOV = 36f;

        [Tooltip("FOV at full speed (curve = 1). The wide, fast look.")]
        public float highestFOV = 55f;

        [Header("Response")]
        [Tooltip("Maps normalized speed (0..1 of the reference top speed, X axis) to FOV blend " +
                 "(0 = lowest, 1 = highest, Y axis). Shape the ramp here — ease-in keeps low speeds " +
                 "calm and only opens up near the top; linear is a steady widen.")]
        public AnimationCurve speedResponse = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("Top speed (m/s) that maps to curve X = 1. Leave 0 to use the controller's maxSpeed.")]
        public float referenceTopSpeed = 0f;

        [Tooltip("How fast the FOV chases its target (1/s). Higher = snappier; lower = smoother, laggier.")]
        public float responseSpeed = 4f;

        float _fov;
        float _authoredFOV;
        bool  _hasAuthored;

        void Reset()
        {
            targetCamera = GetComponent<CinemachineCamera>();
            controller   = GetComponentInParent<SpaceshipController>();
        }

        void OnEnable()
        {
            if (targetCamera == null) targetCamera = GetComponent<CinemachineCamera>();
            if (targetCamera == null) targetCamera = GetComponentInChildren<CinemachineCamera>(true);
            if (controller == null)   controller   = GetComponentInParent<SpaceshipController>();
#if UNITY_2023_1_OR_NEWER
            if (controller == null)   controller   = FindAnyObjectByType<SpaceshipController>();
#else
            if (controller == null)   controller   = FindObjectOfType<SpaceshipController>();
#endif
            if (targetCamera == null)
            {
                Debug.LogWarning("[SpeedFOVEffect] No CinemachineCamera found — FOV won't change.", this);
                enabled = false;
                return;
            }

            // Remember what the vcam was authored with so we can restore it on disable and never
            // dirty the asset with a runtime FOV.
            _authoredFOV = targetCamera.Lens.FieldOfView;
            _hasAuthored = true;
            _fov         = _authoredFOV;
        }

        void OnDisable()
        {
            if (_hasAuthored && targetCamera != null)
                targetCamera.Lens.FieldOfView = _authoredFOV;
        }

        void LateUpdate()
        {
            if (targetCamera == null || controller == null) return;

            float refTop = referenceTopSpeed > 1e-3f ? referenceTopSpeed : controller.maxSpeed;
            float speedN = refTop > 1e-3f ? Mathf.Clamp01(controller.currentVelocity.magnitude / refTop) : 0f;

            float blend     = Mathf.Clamp01(speedResponse.Evaluate(speedN));
            float targetFOV = Mathf.Lerp(lowestFOV, highestFOV, blend);

            // Frame-rate independent smoothing toward the target FOV.
            _fov = Mathf.Lerp(_fov, targetFOV, 1f - Mathf.Exp(-responseSpeed * Time.deltaTime));
            targetCamera.Lens.FieldOfView = _fov;
        }
    }
}
