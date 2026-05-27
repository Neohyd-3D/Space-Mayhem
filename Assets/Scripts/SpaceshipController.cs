using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Kinematic spaceship controller. Holds velocity + personal-timescale state and
    /// integrates them in Update() (NOT FixedUpdate) — this is critical for visual
    /// smoothness because we don't use a Rigidbody, so there is no built-in physics
    /// interpolation between fixed steps. Running integration at render rate matches
    /// transform updates to render frames and eliminates judder.
    ///
    /// Rotation input is expected to already be in **degrees this frame** — the
    /// SpaceshipInput layer is responsible for converting per-source semantics
    /// (mouse pixel-delta vs. gamepad normalized stick × Time.deltaTime).
    /// </summary>
    [DisallowMultipleComponent]
    public class SpaceshipController : MonoBehaviour
    {
        [Header("Thrust")]
        [Tooltip("Linear acceleration applied per unit of input (m/s²).")]
        public float thrustForce = 50f;

        [Tooltip("Maximum velocity magnitude (m/s).")]
        public float maxSpeed = 50f;

        [Range(0f, 5f)]
        [Tooltip("Per-second velocity decay rate. Higher = ship coasts to a stop faster. " +
                 "Equilibrium speed under full thrust = thrustForce / linearDrag.")]
        public float linearDrag = 1.0f;

        [Header("Brake / Time Dilation")]
        [Range(0.05f, 0.6f)]
        [Tooltip("Personal time scale while brake is held. Scales position integration only — rotation stays full speed.")]
        public float brakeTimescale = 0.25f;

        [Range(0f, 1f)]
        [Tooltip("Fraction of pre-brake speed preserved when redirecting momentum.")]
        public float momentumCarryover = 0.35f;

        [Tooltip("Seconds to blend velocity from old direction to new facing direction.")]
        public float redirectBlendDuration = 0.5f;

        [Tooltip("Rate at which personalTimescale eases toward its target (per second).")]
        public float brakeExitSmoothing = 5f;

        [Header("Auto-Level")]
        [Tooltip("Constantly rotates the ship toward world-level (pitch = 0, roll = 0). " +
                 "Strength scales with speed: full at rest, minimum at autoLevelFadeSpeed.")]
        public bool autoLevel = true;

        [Tooltip("Max leveling rotation rate (°/s) when the ship is stationary.")]
        public float autoLevelSpeed = 90f;

        [Tooltip("Ship speed (m/s) at which leveling strength reaches its minimum.")]
        public float autoLevelFadeSpeed = 30f;

        [Range(0f, 1f)]
        [Tooltip("Leveling strength multiplier at autoLevelFadeSpeed and above. " +
                 "0 = fully disabled at speed; 1 = always full strength regardless of speed.")]
        public float autoLevelMinStrength = 0f;

        [Header("Barrel Roll")]
        [Tooltip("Time in seconds to complete a full 360° barrel roll.")]
        public float barrelRollDuration = 0.45f;

        [Tooltip("Minimum time between barrel rolls (seconds). Prevents spam.")]
        public float barrelRollCooldown = 0.6f;

        [Tooltip("Instantaneous sideways velocity impulse (m/s) applied at roll start. Drag bleeds it off naturally.")]
        public float barrelRollDashForce = 25f;

        [Header("Visual Tilt")]
        [Tooltip("Child transform that holds the visible mesh. Will be banked/pitched based on lateral velocity. Leave null to disable tilt.")]
        public Transform visualMesh;

        [Tooltip("Max roll angle (degrees) when strafing at full deflection. Returns to zero when input is released.")]
        public float strafeTilt = 20f;

        [Tooltip("Max pitch angle (degrees) when hovering up/down at full deflection. Returns to zero when input is released.")]
        public float hoverTilt = 15f;

        [Tooltip("Max pitch angle (degrees) when thrusting forward/back at full deflection. Nose dips on acceleration, rises on reverse.")]
        public float accelerationTilt = 8f;

        [Tooltip("Maximum lean angle clamp (degrees) on any axis.")]
        public float maxLeanAngle = 25f;

        [Tooltip("How fast the visual mesh rotates toward its target lean (per second).")]
        public float leanSpeed = 8f;

        [Header("Collision")]
        [Tooltip("Collider used for terrain / obstacle depenetration. Must live on this GameObject. " +
                 "Assign a CapsuleCollider or SphereCollider that roughly matches your mesh.")]
        public Collider shipCollider;

        [Tooltip("Layers tested during depenetration. Exclude the ship's own layer to avoid self-collision.")]
        public LayerMask collisionMask = ~0;

        [Range(1, 8)]
        [Tooltip("Max depenetration passes per frame. 3 is enough for nearly all geometry.")]
        public int depenetrationIterations = 3;

        [Header("Dependencies")]
        public MomentumSystem momentum;

        public Vector3 currentVelocity { get; private set; }
        public float personalTimescale { get; private set; } = 1f;
        public bool isBraking { get; private set; }

        Vector3 _thrustInput;
        Vector3 _rotationInput;  // degrees this frame (already source-scaled by SpaceshipInput)
        bool _wasBraking;

        // Reused buffer for DepenetrateFromWorld — avoids per-frame GC allocation.
        // Static is fine for a single-player prototype; promote to instance for multiplayer.
        static readonly Collider[] _overlapBuffer = new Collider[16];

        // All colliders that belong to this ship or its children — never depenetrate against these.
        readonly System.Collections.Generic.HashSet<Collider> _ownColliders =
            new System.Collections.Generic.HashSet<Collider>();

        // Barrel roll state
        bool    _isBarrelRolling;
        float   _barrelRollTimer;        // 0 → barrelRollDuration
        float   _barrelRollDirection;    // +1 = left/up, -1 = right/down
        float   _barrelRollCooldownTimer;
        Vector3 _barrelRollAxis;         // local-space axis: Vector3.forward (left/right), Vector3.right (up/down)

        public bool IsBarrelRolling => _isBarrelRolling;

        void Awake()
        {
            if (momentum == null) momentum = GetComponent<MomentumSystem>();

            // Cache every collider on this GameObject and all its children so
            // DepenetrateFromWorld never tries to push out of its own mesh geometry.
            foreach (var col in GetComponentsInChildren<Collider>(includeInactive: true))
                _ownColliders.Add(col);
        }

        // Phase-2 swap point: replace this call site with networked input.
        // - thrust: local-space thrust direction, components in [-1, 1]
        // - rotation: degrees to rotate this frame, per axis (already in absolute degrees, NOT scaled by dt)
        public void ApplyInput(Vector3 thrust, Vector3 rotation, bool braking)
        {
            _thrustInput = Vector3.ClampMagnitude(thrust, 1f);
            _rotationInput = rotation;
            isBraking = braking;
        }

        // direction: +1 = roll left / roll up,  -1 = roll right / roll down.
        // localAxis:  Vector3.forward  → left/right barrel roll (rotates around ship's nose)
        //             Vector3.right    → up/down barrel roll    (rotates around ship's wing axis)
        // Ignored if a roll is already in progress or on cooldown.
        public void TriggerBarrelRoll(float direction, Vector3 localAxis)
        {
            if (_isBarrelRolling || _barrelRollCooldownTimer > 0f) return;
            _isBarrelRolling     = true;
            _barrelRollTimer     = 0f;
            _barrelRollDirection = Mathf.Sign(direction);
            _barrelRollAxis      = localAxis;

            // Instantaneous dash impulse perpendicular to the roll axis.
            // Left/right roll (axis = forward): dash along local X.
            //   +direction = roll left  → dash local -X (leftward)
            //   -direction = roll right → dash local +X (rightward)
            // Up/down roll (axis = right): dash along local Y.
            //   +direction = roll up   → dash local +Y (upward)
            //   -direction = roll down → dash local -Y (downward)
            // Drag bleeds the impulse off naturally over time.
            if (localAxis == Vector3.forward)
                currentVelocity += transform.right * (-_barrelRollDirection * barrelRollDashForce);
            else
                currentVelocity += transform.up * (_barrelRollDirection * barrelRollDashForce);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Brake release → start momentum redirect blend
            if (_wasBraking && !isBraking && momentum != null)
                momentum.StartRedirect(currentVelocity, transform.forward, momentumCarryover, redirectBlendDuration);
            _wasBraking = isBraking;

            float targetTs = isBraking ? brakeTimescale : 1f;
            personalTimescale = Mathf.MoveTowards(personalTimescale, targetTs, brakeExitSmoothing * dt);

            // Rotation FIRST — using current frame's orientation for the thrust direction
            // makes thrust feel "stuck to the nose" without lag. Rotation input is already
            // in degrees this frame (computed by SpaceshipInput from mouse-delta or stick×dt).
            if (Mathf.Abs(_rotationInput.x) > 1e-5f) transform.Rotate(Vector3.right, _rotationInput.x, Space.Self);
            if (Mathf.Abs(_rotationInput.y) > 1e-5f) transform.Rotate(Vector3.up,    _rotationInput.y, Space.Self);

            // Barrel roll — smoothstepped 360° over barrelRollDuration, full speed regardless of timescale.
            if (_barrelRollCooldownTimer > 0f) _barrelRollCooldownTimer -= dt;
            if (_isBarrelRolling)
            {
                float prevT = Mathf.Clamp01(_barrelRollTimer / barrelRollDuration);
                _barrelRollTimer += dt;
                float nextT = Mathf.Clamp01(_barrelRollTimer / barrelRollDuration);

                // SmoothStep gives ease-in/out so the roll feels snappy, not mechanical.
                float deltaAngle = (Mathf.SmoothStep(0f, 360f, nextT)
                                  - Mathf.SmoothStep(0f, 360f, prevT))
                                  * _barrelRollDirection;
                transform.Rotate(_barrelRollAxis, deltaAngle, Space.Self);

                if (_barrelRollTimer >= barrelRollDuration)
                {
                    _isBarrelRolling       = false;
                    _barrelRollCooldownTimer = barrelRollCooldown;
                }
            }

            // Auto-level — pulls ship toward pitch=0, roll=0 while preserving yaw.
            // Suppressed during barrel rolls (would fight the animation).
            // Player input simply overpowers it; no explicit suppression needed.
            if (autoLevel && !_isBarrelRolling)
            {
                // Project forward onto the world XZ plane to get the flat heading.
                Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);

                // Near-vertical flight (straight up/down): no meaningful flat heading,
                // skip rather than snapping to an arbitrary direction.
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    Quaternion levelTarget = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);

                    // Strength fades linearly from 1 → autoLevelMinStrength as speed
                    // climbs from 0 → autoLevelFadeSpeed.
                    float speedFraction = Mathf.Clamp01(
                        currentVelocity.magnitude / Mathf.Max(0.01f, autoLevelFadeSpeed));
                    float strength = Mathf.Lerp(1f, autoLevelMinStrength, speedFraction);

                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation, levelTarget, autoLevelSpeed * strength * dt);
                }
            }

            // Velocity integration
            if (momentum != null && momentum.IsRedirecting)
            {
                currentVelocity = momentum.Tick(dt);
            }
            else
            {
                Vector3 worldThrust = transform.TransformDirection(_thrustInput) * thrustForce;
                currentVelocity += worldThrust * dt;

                // Frame-rate independent exponential drag.
                float decay = Mathf.Exp(-linearDrag * dt);
                currentVelocity *= decay;
            }
            currentVelocity = Vector3.ClampMagnitude(currentVelocity, maxSpeed);

            transform.position += currentVelocity * personalTimescale * dt;

            // ── Collision depenetration ────────────────────────────────────────
            if (shipCollider != null)
                DepenetrateFromWorld();

        }

        // Pushes the ship out of any overlapping colliders and deflects velocity.
        // Uses Unity's Physics.ComputePenetration — the same engine the built-in
        // CharacterController relies on.  No Rigidbody required.
        void DepenetrateFromWorld()
        {
            // After a manual transform.position change the physics broadphase cache
            // is stale.  Sync it before querying or bounds/overlap results are wrong.
            Physics.SyncTransforms();

            for (int iter = 0; iter < depenetrationIterations; iter++)
            {
                // Broad-phase: sphere enclosing the collider's AABB finds candidates.
                Bounds b      = shipCollider.bounds;
                float  radius = b.extents.magnitude;

                int hits = Physics.OverlapSphereNonAlloc(
                    b.center, radius, _overlapBuffer,
                    collisionMask, QueryTriggerInteraction.Ignore);

                bool anyHit = false;
                for (int i = 0; i < hits; i++)
                {
                    Collider other = _overlapBuffer[i];
                    if (_ownColliders.Contains(other) || other.isTrigger) continue;

                    // Narrow-phase: exact penetration vector + depth.
                    if (!Physics.ComputePenetration(
                            shipCollider, transform.position,       transform.rotation,
                            other,        other.transform.position, other.transform.rotation,
                            out Vector3 dir, out float depth))
                        continue;

                    // Translate the ship out of the surface.
                    // The tiny skin (1 mm) keeps floating-point from re-penetrating.
                    transform.position += dir * (depth + 0.001f);

                    // Cancel the velocity component driving INTO the surface,
                    // so the ship slides along rather than tunnelling next frame.
                    float into = Vector3.Dot(currentVelocity, -dir);
                    if (into > 0f)
                        currentVelocity += dir * into;

                    anyHit = true;
                }

                if (!anyHit) break;          // clean pass — no further work needed
                Physics.SyncTransforms();    // re-sync after each position nudge
            }
        }

        void LateUpdate()
        {
            if (visualMesh == null) return;

            // Tilt is driven by active input, not velocity — returns to neutral as soon as
            // the player releases the key/stick. _thrustInput components are in [-1, 1].
            float bank = Mathf.Clamp(-_thrustInput.x * strafeTilt, -maxLeanAngle, maxLeanAngle);

            // Pitch: two contributors, both clamped to maxLeanAngle together.
            //   Hover Tilt        — vertical input: nose up on ascend, nose down on descend.
            //   Acceleration Tilt — forward input: nose dips on thrust, rises on reverse.
            // Both negated because Unity +X rotation tilts the nose down.
            float pitchFromVertical = -_thrustInput.y * hoverTilt;
            float pitchFromAccel    =  _thrustInput.z * accelerationTilt;
            float pitch = Mathf.Clamp(pitchFromVertical + pitchFromAccel, -maxLeanAngle, maxLeanAngle);

            Quaternion targetLean = Quaternion.Euler(pitch, 0f, bank);
            float t = 1f - Mathf.Exp(-leanSpeed * Time.deltaTime);
            visualMesh.localRotation = Quaternion.Slerp(visualMesh.localRotation, targetLean, t);
        }
    }
}
