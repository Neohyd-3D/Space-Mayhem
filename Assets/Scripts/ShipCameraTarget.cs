using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Stabilised camera proxy used as the Cinemachine follow target.
    ///
    /// This script owns ALL camera-feel logic — position lag, rotation lag,
    /// look-ahead, roll-free tracking, and barrel-roll freeze.  It outputs the
    /// final desired camera position/rotation to its own Transform each LateUpdate.
    ///
    /// The Cinemachine VirtualCamera follows this proxy with zero damping and a
    /// zero offset, so it acts as a pure pass-through.  Cinemachine's value is
    /// therefore in its add-ons (ImpulseListener for shake, Brain for blending,
    /// Timeline integration) rather than in its position/rotation math.
    ///
    /// All feel parameters are exposed in the Inspector and update live in Play Mode.
    ///
    /// ExecutionOrder –100 ensures this runs before CinemachineBrain (LateUpdate).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public class ShipCameraTarget : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The ship's root transform (not the visual mesh child).")]
        public Transform ship;

        [Tooltip("SpaceshipController on the ship — used to detect barrel rolls.")]
        public SpaceshipController controller;

        [Header("Offset")]
        [Tooltip("Distance behind the ship along its local –Z.")]
        public float cameraDistance = 8f;

        [Tooltip("Height above the ship along its local +Y.")]
        public float cameraHeight = 2.5f;

        [Tooltip("Distance ahead of the ship to aim the camera at. Gives a slight lead.")]
        public float lookAheadDistance = 12f;

        [Header("Lag")]
        [Tooltip("SmoothDamp time constant for position (seconds). Lower = tighter camera.")]
        public float positionLag = 0.06f;

        [Tooltip("Exponential rotation lag time constant (seconds). Lower = tighter camera.")]
        public float rotationLag = 0.04f;

        // ── Internal state ────────────────────────────────────────────────────
        Vector3    _posVelocity;
        Vector3    _cameraUp = Vector3.up;
        Quaternion _prevShipRot;
        Vector3    _rollingFwd;
        bool       _wasRolling;
        bool       _initialized;

        void LateUpdate()
        {
            if (ship == null) return;

            if (!_initialized)
            {
                _prevShipRot     = ship.rotation;
                _rollingFwd      = ship.forward;
                _initialized     = true;
            }

            float dt = Time.deltaTime;

            // ── Barrel-roll freeze ────────────────────────────────────────────
            bool isRolling = controller != null && controller.IsBarrelRolling;

            if (isRolling && !_wasRolling)
                _rollingFwd = ship.forward;
            _wasRolling = isRolling;

            Vector3 effectiveFwd = isRolling ? _rollingFwd : ship.forward;

            // ── Camera-up tracking ────────────────────────────────────────────
            // Apply the full rotation delta to _cameraUp — no twist-discard.
            //
            // The old swing-twist decomposition discarded the twist component
            // (roll around the forward axis) to prevent barrel rolls from rolling
            // the camera. Now that barrel rolls are handled by the isRolling freeze
            // above, we no longer need that filter — and removing it lets _cameraUp
            // follow auto-level's roll correction so the camera resets with the horizon.
            //
            // Incremental deltas mean there is no world-up singularity at steep pitch.
            if (!isRolling)
            {
                Quaternion delta = ship.rotation * Quaternion.Inverse(_prevShipRot);
                _cameraUp = delta * _cameraUp;

                // Re-orthogonalise against effectiveFwd each frame to suppress drift.
                _cameraUp -= Vector3.Dot(_cameraUp, effectiveFwd) * effectiveFwd;
                if (_cameraUp.sqrMagnitude < 1e-4f) _cameraUp = ship.up;
                else _cameraUp.Normalize();
            }

            _prevShipRot = ship.rotation;

            // ── Position ──────────────────────────────────────────────────────
            Quaternion rollFreeRot = Quaternion.LookRotation(effectiveFwd, _cameraUp);
            Vector3 desiredPos = ship.position
                               + rollFreeRot * new Vector3(0f, cameraHeight, -cameraDistance);

            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPos, ref _posVelocity,
                Mathf.Max(0.0001f, positionLag));

            // ── Rotation ──────────────────────────────────────────────────────
            Vector3 lookTarget = ship.position + effectiveFwd * lookAheadDistance;
            Vector3 lookDir    = lookTarget - transform.position;
            if (lookDir.sqrMagnitude < 1e-6f) lookDir = effectiveFwd;
            lookDir.Normalize();

            Quaternion desiredRot = Quaternion.LookRotation(lookDir, _cameraUp);
            float rotT = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, rotationLag));

            // Short-path slerp guard (quaternion double-cover).
            if (Quaternion.Dot(transform.rotation, desiredRot) < 0f)
                desiredRot = new Quaternion(-desiredRot.x, -desiredRot.y,
                                            -desiredRot.z, -desiredRot.w);

            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotT);
        }
    }
}
