using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Marks a ship as a competitor and is its identity to the race. Sits on the ship root (next to
    /// SpaceshipController). It registers with the <see cref="RaceDirector"/> on enable, carries the
    /// <see cref="ControlsLocked"/> flag the director raises during the countdown, and is the object
    /// checkpoints resolve to (via GetComponentInParent) when a ship overlaps a gate.
    ///
    /// This is the future network-identity seam. Today there's one local participant with racerId 0;
    /// later, AI racers and remote players each get a RaceParticipant and register through the exact
    /// same path — the director never learns the difference between local, AI, and networked input.
    /// </summary>
    [DisallowMultipleComponent]
    public class RaceParticipant : MonoBehaviour
    {
        [Tooltip("Identity within the race. 0 for the local player now; unique per racer once there " +
                 "are several (AI / networked). The HUD and standings key off this.")]
        public int racerId;

        [Tooltip("Director this racer competes in. Auto-found in the scene if left null.")]
        public RaceDirector director;

        [Tooltip("Optional display name for standings / HUD. Falls back to \"Racer <id>\" if empty.")]
        public string displayName;

        /// <summary>True while the director is holding this ship still (e.g. during the countdown).
        /// SpaceshipInput reads this and forwards zero input so the ship can't move.</summary>
        public bool ControlsLocked { get; private set; }

        public string Name => string.IsNullOrEmpty(displayName) ? $"Racer {racerId}" : displayName;

        void OnEnable()
        {
            if (director == null) director = FindFirstObjectByType<RaceDirector>();
            if (director != null) director.Register(this);
            else Debug.LogWarning("[RaceParticipant] No RaceDirector in the scene — this racer won't " +
                                   "be tracked. Add a RaceDirector or assign one.", this);
        }

        void OnDisable()
        {
            if (director != null) director.Unregister(this);
        }

        /// <summary>Called by the director only. Kept as an explicit setter (not a public field) so
        /// the lock always flows from the race brain, never from arbitrary code.</summary>
        public void SetControlsLocked(bool locked) => ControlsLocked = locked;
    }
}
