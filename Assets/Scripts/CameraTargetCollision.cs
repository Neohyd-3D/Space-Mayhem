using System.Collections.Generic;
using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Gives a Cinemachine Follow / LookAt target the SAME collision the ship uses: it pushes the
    /// target's collider out of any solid world geometry it overlaps, with Physics.ComputePenetration,
    /// exactly as SpaceshipController.DepenetrateFromWorld does — just without the velocity response,
    /// since the target has no velocity (it's positioned by its parent).
    ///
    /// The target is a child of the ship, so its world position is dragged wherever the ship goes and
    /// otherwise sinks straight through the road; this depenetrates it back out every frame. It runs
    /// in LateUpdate after the ship has placed the target, at a negative execution order so
    /// CinemachineBrain samples the corrected pose.
    ///
    /// A push-out modifies the target's LOCAL offset, and nothing else rewrites it — so without help the
    /// displacement would stick. Each frame we first ease the local offset back toward its authored home
    /// (<see cref="returnSpeed"/>), THEN depenetrate. If the way home is clear it settles there; if home
    /// is buried, the push-out shoves it straight back out — so the resting place is automatically the
    /// closest spot to home that isn't inside geometry. No path-testing: the collision IS the test.
    ///
    /// Needs a Collider on this object (a small SphereCollider is ideal). Put world geometry on a layer
    /// and select it in <see cref="collisionMask"/>; the whole Player prefab (ship body + this target)
    /// is auto-excluded so it only ever pushes off the world, never off itself or the ship.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public class CameraTargetCollision : MonoBehaviour
    {
        [Tooltip("Collider depenetrated from the world. Defaults to a Collider on this object.")]
        public Collider targetCollider;

        [Tooltip("Layers tested for collision. Exclude the ship/player layer; the prefab's own " +
                 "colliders are auto-excluded regardless.")]
        public LayerMask collisionMask = ~0;

        [Range(1, 8)]
        [Tooltip("Max depenetration passes per frame. 3 resolves nearly all geometry.")]
        public int depenetrationIterations = 3;

        [Tooltip("How fast the target eases back to its authored offset once it's clear of geometry " +
                 "(1/s). Higher = snaps home quickly; lower = a slow, gentle drift back. ~4 is a soft return.")]
        public float returnSpeed = 4f;

        static readonly Collider[] _overlapBuffer = new Collider[16];
        readonly HashSet<Collider> _ownColliders = new HashSet<Collider>();
        Vector3 _homeLocalPos;   // authored offset relative to the ship; the target is eased back to this

        void Awake()
        {
            _homeLocalPos = transform.localPosition;

            if (targetCollider == null) targetCollider = GetComponent<Collider>();

            // Exclude every collider in the Player prefab (ship body + this target) so we only
            // ever depenetrate against the world, never against ourselves or the ship.
            foreach (var col in transform.root.GetComponentsInChildren<Collider>(true))
                _ownColliders.Add(col);

            if (targetCollider == null)
                Debug.LogWarning("[CameraTargetCollision] No Collider on the target — add one " +
                                 "(e.g. a SphereCollider) or the camera target won't collide.", this);
        }

        void LateUpdate()
        {
            if (targetCollider == null) return;

            // Pull the local offset back toward home (frame-rate-independent exponential ease),
            // THEN push out of anything that move just buried us in. The depenetration is the
            // gatekeeper: it overrides as much of the return as geometry demands and no more, so
            // the target creeps home whenever it's clear and stops short the instant it isn't.
            transform.localPosition = Vector3.Lerp(
                transform.localPosition, _homeLocalPos, 1f - Mathf.Exp(-returnSpeed * Time.deltaTime));

            DepenetrateFromWorld();
        }

        // Mirrors SpaceshipController.DepenetrateFromWorld: broad-phase OverlapSphere early-out,
        // then ComputePenetration push-out, iterated so stacked contacts settle in one frame.
        void DepenetrateFromWorld()
        {
            Bounds b0     = targetCollider.bounds;
            float  radius = b0.extents.magnitude;
            int broadHits = Physics.OverlapSphereNonAlloc(
                b0.center, radius, _overlapBuffer, collisionMask, QueryTriggerInteraction.Ignore);

            bool anyNearby = false;
            for (int i = 0; i < broadHits; i++)
            {
                if (!_ownColliders.Contains(_overlapBuffer[i]) && !_overlapBuffer[i].isTrigger)
                { anyNearby = true; break; }
            }
            if (!anyNearby) return;

            Physics.SyncTransforms();

            for (int iter = 0; iter < depenetrationIterations; iter++)
            {
                Bounds b = targetCollider.bounds;
                radius   = b.extents.magnitude;

                int hits = Physics.OverlapSphereNonAlloc(
                    b.center, radius, _overlapBuffer, collisionMask, QueryTriggerInteraction.Ignore);

                bool anyHit = false;
                for (int i = 0; i < hits; i++)
                {
                    Collider other = _overlapBuffer[i];
                    if (_ownColliders.Contains(other) || other.isTrigger) continue;

                    if (!Physics.ComputePenetration(
                            targetCollider, transform.position,       transform.rotation,
                            other,          other.transform.position, other.transform.rotation,
                            out Vector3 dir, out float depth))
                        continue;

                    transform.position += dir * (depth + 0.001f);
                    anyHit = true;
                }

                if (!anyHit) break;
                Physics.SyncTransforms();
            }
        }
    }
}
