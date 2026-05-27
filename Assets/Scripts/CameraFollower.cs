using UnityEngine;

namespace SpaceMayhem
{
    [DisallowMultipleComponent]
    public class CameraFollower : MonoBehaviour
    {
        [Header("Target")]
        public Transform target;

        [Header("Offset (ship-local, applied via target.rotation)")]
        [Tooltip("Distance behind the ship along its local -Z.")]
        public float cameraDistance = 8f;

        [Tooltip("Height above the ship along its local +Y.")]
        public float cameraHeight = 2.5f;

        [Header("Lag (seconds — SmoothDamp time constant)")]
        [Tooltip("Position smoothing time. Lower = camera catches up faster.")]
        public float positionLag = 0.06f;

        [Tooltip("Rotation smoothing time. Lower = camera turns to face faster.")]
        public float rotationLag = 0.04f;

        [Header("Aim")]
        [Tooltip("Distance forward of the ship to aim the camera at — gives a slight lead and shows the world ahead.")]
        public float lookAheadDistance = 12f;

        [Header("Source")]
        public SpaceshipController controllerRef;

        Vector3    _posVelocity;
        Quaternion _prevShipRot;
        Vector3    _cameraUp = Vector3.up;
        bool       _initialized;

        // Barrel-roll freeze: camera orientation is held constant for the full 360°
        // animation. Because every barrel roll returns the ship to its original
        // orientation, there is zero snap when tracking resumes.
        Vector3 _rollingFwd;
        bool    _wasRolling;

        void LateUpdate()
        {
            if (target == null) return;

            // First-frame init — can't do this in Awake because target may not be
            // assigned yet.
            if (!_initialized)
            {
                _prevShipRot = target.rotation;
                _rollingFwd  = target.forward;
                _initialized = true;
            }

            float dt   = Time.deltaTime;
            float rotT = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, rotationLag));

            bool isRolling = controllerRef != null && controllerRef.IsBarrelRolling;

            // Capture the ship's forward at the very start of each barrel roll.
            // We use this frozen direction for ALL camera calculations while the
            // roll plays, so neither position nor rotation chases the animation.
            // On the first non-rolling frame the ship is back to its original
            // orientation (full 360°), so resuming live tracking produces no snap.
            if (isRolling && !_wasRolling)
                _rollingFwd = target.forward;
            _wasRolling = isRolling;

            Vector3 effectiveFwd = isRolling ? _rollingFwd : target.forward;

            // ── Swing-Twist decomposition ─────────────────────────────────────
            // Split the frame's rotation delta into:
            //   twist = rotation AROUND the forward axis  (roll)  → discarded
            //   swing = everything else                   (pitch + yaw) → applied
            //
            // Skipped entirely during barrel rolls — effectiveFwd is frozen so
            // we have no valid delta to decompose, and _cameraUp should not move.
            if (!isRolling)
            {
                Quaternion delta = target.rotation * Quaternion.Inverse(_prevShipRot);

                Vector3 r = new Vector3(delta.x, delta.y, delta.z);
                Vector3 p = Vector3.Project(r, effectiveFwd);
                float   twistMag = Mathf.Sqrt(p.x*p.x + p.y*p.y + p.z*p.z + delta.w*delta.w);
                Quaternion twist = twistMag < 1e-6f
                    ? Quaternion.identity
                    : new Quaternion(p.x / twistMag, p.y / twistMag, p.z / twistMag, delta.w / twistMag);

                Quaternion swing = delta * Quaternion.Inverse(twist);
                _cameraUp = swing * _cameraUp;

                // Re-orthogonalize against effectiveFwd each frame to suppress
                // floating-point drift.
                _cameraUp -= Vector3.Dot(_cameraUp, effectiveFwd) * effectiveFwd;
                if (_cameraUp.sqrMagnitude < 1e-4f) _cameraUp = target.up;
                else _cameraUp.Normalize();
            }

            // Always advance _prevShipRot so we never accumulate a phantom delta
            // when rolling ends.
            _prevShipRot = target.rotation;

            // ── Position ─────────────────────────────────────────────────────
            Quaternion rollFreeRot = Quaternion.LookRotation(effectiveFwd, _cameraUp);
            Vector3 desiredPos = target.position
                               + rollFreeRot * new Vector3(0f, cameraHeight, -cameraDistance);

            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPos, ref _posVelocity,
                Mathf.Max(0.0001f, positionLag));

            // ── Rotation ─────────────────────────────────────────────────────
            Vector3 lookTarget = target.position + effectiveFwd * lookAheadDistance;
            Vector3 lookDir    = lookTarget - transform.position;
            if (lookDir.sqrMagnitude < 1e-6f) lookDir = effectiveFwd;
            lookDir.Normalize();

            Quaternion desiredRot = Quaternion.LookRotation(lookDir, _cameraUp);

            // Guarantee short-path slerp (quaternion double-cover: Q and -Q represent
            // the same rotation, but Slerp may take the long arc without this check).
            if (Quaternion.Dot(transform.rotation, desiredRot) < 0f)
                desiredRot = new Quaternion(-desiredRot.x, -desiredRot.y,
                                            -desiredRot.z, -desiredRot.w);

            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotT);
        }
    }
}
