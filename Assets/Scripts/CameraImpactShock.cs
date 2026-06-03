using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Holds the chase camera steadier through a crash. The camera's Follow target is a child of the
    /// ship, so it normally inherits the ship's full rotation — which means a hard impact (where the ship
    /// is suddenly stopped and tumbling) whips the camera around and reads as nauseating chaos. While the
    /// ship is in its collision-recovery window (<see cref="SpaceshipController.CollisionRecovery"/>),
    /// this loosens the target's rotation so the camera does NOT chase the tumble — it hangs onto its
    /// pre-impact heading, lets the ship spin/recover inside the frame, then re-couples as recovery fades.
    /// Sells the hit as a punch you're recovering from instead of a blur.
    ///
    /// Deliberately additive and minimal: it ONLY overrides the target's rotation while recovering; the
    /// rest of the time it restores the authored local rotation so the target inherits the ship normally
    /// and the authored Cinemachine rig is untouched. Put it on the camera's Follow target (the same
    /// object as CameraTargetCollision); it auto-finds the ship up the hierarchy.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(120)]   // after the ship has moved AND after CameraTargetCollision
    public class CameraImpactShock : MonoBehaviour
    {
        [Tooltip("Ship to read crash recovery from. Auto-found up the parent hierarchy if left null.")]
        public SpaceshipController controller;

        [Range(0f, 1f)]
        [Tooltip("How much the camera detaches from the ship's tumble at the PEAK of a crash. 0 = off " +
                 "(camera always glued to the ship); 1 = it fully holds its pre-impact heading and lets " +
                 "the ship spin inside the frame.")]
        public float decoupleStrength = 1f;

        [Tooltip("How fast the camera RE-COUPLES to the ship as a crash recovers / in normal play (1/s). " +
                 "High = the effect is invisible except right after a hit.")]
        public float reCoupleSpeed = 30f;

        [Tooltip("How slowly the camera chases the ship at the PEAK of a crash (1/s). Low = it really holds " +
                 "still while the ship tumbles — the 'detached' feel.")]
        public float holdSpeed = 2f;

        Quaternion _restLocal;   // authored local rotation, restored whenever not shocked
        Quaternion _held;        // the camera target's smoothed world rotation
        bool _init;

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<SpaceshipController>();
            _restLocal = transform.localRotation;
        }

        void LateUpdate()
        {
            if (controller == null) return;

            // Where the target would sit if it inherited the ship normally.
            Quaternion targetWorld = controller.transform.rotation * _restLocal;
            if (!_init) { _held = targetWorld; _init = true; }

            float shock = Mathf.Clamp01(controller.CollisionRecovery * decoupleStrength);
            if (shock > 0.001f)
            {
                // Loose chase while shocked (holdSpeed), tightening back to reCoupleSpeed as it fades, so
                // the camera lags behind the tumble then catches up cleanly — no snap when it ends.
                float speed = Mathf.Lerp(reCoupleSpeed, holdSpeed, shock);
                _held = Quaternion.Slerp(_held, targetWorld, 1f - Mathf.Exp(-speed * Time.deltaTime));
                transform.rotation = _held;
            }
            else
            {
                // Not shocked: hand back to pure inheritance (untouched rig) and keep _held synced.
                if (transform.localRotation != _restLocal) transform.localRotation = _restLocal;
                _held = targetWorld;
            }
        }
    }
}
