using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// A single gate on the track. Deliberately dumb: it holds an ordering <see cref="index"/> and,
    /// when a racer's collider overlaps it, reports the crossing to the <see cref="RaceDirector"/>.
    /// All lap/validation logic lives in the director — a checkpoint only ever says "racer X touched
    /// gate i". That separation is what lets the same checkpoints feed a local race today and a
    /// server-authoritative race later without changing a line here.
    ///
    /// Setup: put this on a GameObject with a trigger Collider (Is Trigger = ON) spanning the track
    /// width. Number them around the circuit; index 0 = the start/finish line.
    ///
    /// IMPORTANT — the ship has no Rigidbody (kinematic transform controller), and Unity only fires
    /// OnTriggerEnter when at least one side has a Rigidbody. So the CHECKPOINT carries a kinematic
    /// Rigidbody (auto-added and configured below); "Kinematic Rigidbody Trigger vs Static Collider"
    /// reliably sends trigger messages, so the ship's plain collider crossing the gate is detected.
    ///
    /// Trigger detection is discrete (per physics step), so make the volume DEEP along the direction
    /// of travel — at 150 m/s the ship covers ~3 m per 50 Hz step, so a 10–15 m deep box can't be
    /// tunnelled through. It's invisible to the player; only the depth matters for reliability.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(Rigidbody))]
    public class Checkpoint : MonoBehaviour
    {
        [Tooltip("Ordering around the track. 0 = start/finish line, then 1,2,3… in race direction. " +
                 "Racers must cross these in sequence; out-of-order touches are ignored by the director.")]
        public int index;

        [Tooltip("Director this gate reports to. Auto-found in the scene if left null.")]
        public RaceDirector director;

        void Reset()
        {
            // Editor convenience: the moment the component is added, make the required Rigidbody a
            // static-but-trigger-enabling kinematic body, and flag the collider as a trigger.
            ConfigureBody();
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        void Awake()
        {
            if (director == null) director = FindFirstObjectByType<RaceDirector>();
            ConfigureBody();

            var col = GetComponent<Collider>();
            if (col != null && !col.isTrigger)
                Debug.LogWarning($"[Checkpoint {index}] Collider is not a trigger — set Is Trigger = ON " +
                                 "or crossings won't register.", this);
        }

        // A non-moving kinematic body: it never simulates, but its presence is what lets the
        // engine generate trigger events against the Rigidbody-less ship.
        void ConfigureBody()
        {
            var rb = GetComponent<Rigidbody>();
            if (rb == null) return;
            rb.isKinematic = true;
            rb.useGravity  = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }

        void OnTriggerEnter(Collider other)
        {
            if (director == null) return;

            // The racer identity may be on a parent of whatever collider entered (the ship's body
            // collider sits under the ship root that carries RaceParticipant).
            var racer = other.GetComponentInParent<RaceParticipant>();
            if (racer != null) director.ReportCheckpoint(racer, index);
        }

        // Editor aid: outline the gate (green = start/finish, orange = intermediate) so the
        // ordering is visible while placing them.
        void OnDrawGizmos()
        {
            var col = GetComponent<Collider>();
            if (col == null) return;
            Gizmos.color = index == 0 ? Color.green : new Color(1f, 0.6f, 0.1f);
            Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
        }
    }
}
