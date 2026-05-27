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

        Vector3 _posVelocity;

        void LateUpdate()
        {
            if (target == null) return;

            // Ship-local offset rotated into world space. When the ship strafes
            // without rotating, target.rotation is unchanged but target.position
            // moves laterally → desiredPos moves laterally too → camera follows.
            Vector3 localOffset = new Vector3(0f, cameraHeight, -cameraDistance);
            Vector3 desiredPos = target.position + target.rotation * localOffset;

            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPos, ref _posVelocity,
                Mathf.Max(0.0001f, positionLag));

            // Aim slightly ahead of the ship rather than directly at it — feels less
            // like a tripod and more like the camera is leading the action.
            Vector3 lookTarget = target.position + target.forward * lookAheadDistance;
            Vector3 lookDir = lookTarget - transform.position;
            if (lookDir.sqrMagnitude < 1e-6f) lookDir = target.forward;
            lookDir.Normalize();

            Quaternion desiredRot = Quaternion.LookRotation(lookDir, target.up);
            float rotT = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, rotationLag));
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotT);
        }
    }
}
