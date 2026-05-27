using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Kinematic spaceship controller. Holds velocity state and integrates it in
    /// Update() (NOT FixedUpdate) — critical for visual smoothness because we don't
    /// use a Rigidbody, so there is no built-in physics interpolation between fixed
    /// steps. Running integration at render rate matches transform updates to render
    /// frames and eliminates judder.
    ///
    /// Rotation input is expected to already be in **degrees this frame** — the
    /// SpaceshipInput layer is responsible for converting per-source semantics
    /// (mouse pixel-delta vs. gamepad normalized stick × Time.deltaTime).
    ///
    /// Braking applies a high extra drag that physically slows the ship.
    /// On release, MomentumSystem blends velocity toward the new facing direction.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpaceshipController : MonoBehaviour
    {
        [Header("Thrust")]
        [Tooltip("Acceleration for forward / backward thrust (m/s²).")]
        public float thrustForce = 50f;

        [Tooltip("Acceleration for left/right strafe (m/s²).")]
        public float strafeThrustForce = 30f;

        [Tooltip("Quadratic drag coefficient on strafe (left/right) velocity. " +
                 "Drag force = strafeDrag × v². Near zero it barely resists, but ramps hard at speed. " +
                 "Terminal strafe speed ≈ sqrt(strafeThrustForce / strafeDrag).")]
        public float strafeDrag = 0.05f;

        [Tooltip("Acceleration for up/down hover (m/s²).")]
        public float hoverThrustForce = 30f;

        [Tooltip("Quadratic drag coefficient on hover (up/down) velocity. " +
                 "Drag force = hoverDrag × v². Near zero it barely resists, but ramps hard at speed. " +
                 "Terminal hover speed ≈ sqrt(hoverThrustForce / hoverDrag).")]
        public float hoverDrag = 0.05f;

        [Tooltip("Maximum velocity magnitude (m/s).")]
        public float maxSpeed = 50f;

        [Range(0f, 5f)]
        [Tooltip("Per-second velocity decay rate during normal flight. " +
                 "Equilibrium speed under full thrust = thrustForce / linearDrag.")]
        public float linearDrag = 1.0f;

        [Header("Brake")]
        [Tooltip("Maximum deceleration (m/s²) at full brake pressure. " +
                 "Uses linear decel so the ship can reach a full stop.")]
        public float brakeForce = 80f;

        [Tooltip("How fast brake pressure builds from 0 → 1 while the button is held (per second). " +
                 "2 = full pressure in 0.5 s; 1 = full pressure in 1 s.")]
        public float brakeBuildUp = 2f;

        [Tooltip("Speed threshold (m/s) that separates reward from penalty on brake release. ~7 m/s ≈ 25 km/h.")]
        public float brakeThresholdSpeed = 7f;

        [Tooltip("Boost multiplier when entry speed was ABOVE the threshold. " +
                 "boostPeak = entrySpeed × boostMultiplier × pressure. >1.0 = net gain.")]
        public float boostMultiplier = 1.2f;

        [Tooltip("Boost multiplier when entry speed was BELOW the threshold. " +
                 "<1.0 = you exit slower than you entered (punishment for spamming at low speed).")]
        public float brakePenaltyMultiplier = 0.5f;

        [Tooltip("Seconds to ramp velocity from current to the boosted peak. " +
                 "Short (0.15–0.25) = snappy burst. Longer = rocket-like thrust.")]
        public float boostSnapDuration = 0.2f;

        [Header("Horizon Reset / Auto-Level")]
        [Tooltip("Rotation rate (°/s) for both the R3 manual snap and the idle auto-level.")]
        public float autoLevelSpeed = 120f;

        [Tooltip("Speed threshold (m/s) below which the ship auto-levels — only when not braking.")]
        public float autoLevelSpeedThreshold = 1f;

        [Header("Barrel Roll")]
        [Tooltip("Time in seconds to complete a full 360° barrel roll.")]
        public float barrelRollDuration = 0.45f;

        [Tooltip("Minimum time between barrel rolls (seconds). Prevents spam.")]
        public float barrelRollCooldown = 0.6f;

        [Tooltip("Instantaneous sideways velocity impulse (m/s) applied at roll start. Drag bleeds it off naturally.")]
        public float barrelRollDashForce = 25f;

        [Header("Visual Tilt")]
        [Tooltip("Child transform that holds the visible mesh. Will be banked/pitched based on input. Leave null to disable tilt.")]
        public Transform visualMesh;

        [Tooltip("Max roll angle (degrees) when strafing at full deflection.")]
        public float strafeTilt = 20f;

        [Tooltip("Max pitch angle (degrees) when hovering up/down at full deflection.")]
        public float hoverTilt = 15f;

        [Tooltip("Max pitch angle (degrees) when thrusting forward/back at full deflection.")]
        public float accelerationTilt = 8f;

        [Tooltip("Maximum lean angle clamp (degrees) on any axis.")]
        public float maxLeanAngle = 25f;

        [Tooltip("How fast the visual mesh rotates toward its target lean (per second).")]
        public float leanSpeed = 8f;

        [Header("Collision")]
        [Tooltip("Collider used for terrain / obstacle depenetration. Must live on this GameObject.")]
        public Collider shipCollider;

        [Tooltip("Layers tested during depenetration. Exclude the ship's own layer to avoid self-collision.")]
        public LayerMask collisionMask = ~0;

        [Range(1, 8)]
        [Tooltip("Max depenetration passes per frame. 3 is enough for nearly all geometry.")]
        public int depenetrationIterations = 3;

        [Header("Dependencies")]
        public MomentumSystem momentum;

        public Vector3 currentVelocity { get; private set; }
        public bool isBraking { get; private set; }

        Vector3 _thrustInput;
        Vector3 _rotationInput;  // degrees this frame (already source-scaled by SpaceshipInput)
        bool  _wasBraking;
        float _brakePressure;    // 0 → 1, builds while brake is held, resets on release
        bool  _brakeAboveThreshold; // was entry speed above brakeThresholdSpeed?
        float _brakeEntrySpeed;     // speed captured at the moment brake was first pressed

        // Reused buffer for DepenetrateFromWorld — avoids per-frame GC allocation.
        static readonly Collider[] _overlapBuffer = new Collider[16];

        // All colliders that belong to this ship or its children — never depenetrate against these.
        readonly System.Collections.Generic.HashSet<Collider> _ownColliders =
            new System.Collections.Generic.HashSet<Collider>();

        // Barrel roll state
        bool    _isBarrelRolling;
        float   _barrelRollTimer;
        float   _barrelRollDirection;    // +1 = left/up, -1 = right/down
        float   _barrelRollCooldownTimer;
        Vector3 _barrelRollAxis;         // local-space axis: Vector3.forward (L/R), Vector3.right (U/D)

        public bool IsBarrelRolling => _isBarrelRolling;

        // Horizon reset state (R3 manual snap)
        bool _isResettingHorizon;

        public bool IsResettingHorizon => _isResettingHorizon;

        void Awake()
        {
            if (momentum == null) momentum = GetComponent<MomentumSystem>();

            // Cache every collider on this GO and its children so DepenetrateFromWorld
            // never tries to push out of the ship's own mesh geometry.
            foreach (var col in GetComponentsInChildren<Collider>(includeInactive: true))
                _ownColliders.Add(col);
        }

        // Phase-2 swap point: replace with networked input.
        // thrust:   local-space direction, components in [-1, 1]
        // rotation: degrees to rotate this frame, per axis (NOT scaled by dt)
        public void ApplyInput(Vector3 thrust, Vector3 rotation, bool braking)
        {
            _thrustInput   = Vector3.ClampMagnitude(thrust, 1f);
            _rotationInput = rotation;
            isBraking      = braking;
        }

        // direction: +1 = roll left / roll up,  -1 = roll right / roll down.
        // localAxis:  Vector3.forward  → left/right barrel roll
        //             Vector3.right    → up/down barrel roll
        // Ignored if a roll is already in progress or on cooldown.
        public void TriggerBarrelRoll(float direction, Vector3 localAxis)
        {
            if (_isBarrelRolling || _barrelRollCooldownTimer > 0f) return;
            _isBarrelRolling     = true;
            _barrelRollTimer     = 0f;
            _barrelRollDirection = Mathf.Sign(direction);
            _barrelRollAxis      = localAxis;

            if (localAxis == Vector3.forward)
                currentVelocity += transform.right * (-_barrelRollDirection * barrelRollDashForce);
            else
                currentVelocity += transform.up * (_barrelRollDirection * barrelRollDashForce);
        }

        // Activates the horizon reset. SpaceshipInput calls this on R3 press.
        // Ignored during a barrel roll.
        public void TriggerHorizonReset()
        {
            if (_isBarrelRolling) return;
            _isResettingHorizon = true;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // ── Brake press — record entry speed and whether above threshold ──
            if (!_wasBraking && isBraking)
            {
                _brakeEntrySpeed    = currentVelocity.magnitude;
                _brakeAboveThreshold = _brakeEntrySpeed >= brakeThresholdSpeed;
            }

            // ── Brake release ─────────────────────────────────────────────────
            if (_wasBraking && !isBraking && momentum != null)
            {
                // Above threshold → reward (multiplier > 1, net speed gain).
                // Below threshold → punishment (multiplier < 1, you exit slower).
                float multiplier = _brakeAboveThreshold ? boostMultiplier : brakePenaltyMultiplier;
                float boostSpeed = _brakeEntrySpeed * multiplier * _brakePressure;
                momentum.StartBoost(currentVelocity, transform.forward * boostSpeed, boostSnapDuration);
            }
            _wasBraking = isBraking;

            // ── Rotation ──────────────────────────────────────────────────────
            // Applied before velocity so thrust feels immediately "stuck to the nose".
            if (Mathf.Abs(_rotationInput.x) > 1e-5f) transform.Rotate(Vector3.right, _rotationInput.x, Space.Self);
            if (Mathf.Abs(_rotationInput.y) > 1e-5f) transform.Rotate(Vector3.up,    _rotationInput.y, Space.Self);

            // ── Barrel roll ───────────────────────────────────────────────────
            if (_barrelRollCooldownTimer > 0f) _barrelRollCooldownTimer -= dt;
            if (_isBarrelRolling)
            {
                float prevT = Mathf.Clamp01(_barrelRollTimer / barrelRollDuration);
                _barrelRollTimer += dt;
                float nextT = Mathf.Clamp01(_barrelRollTimer / barrelRollDuration);

                float deltaAngle = (Mathf.SmoothStep(0f, 360f, nextT)
                                  - Mathf.SmoothStep(0f, 360f, prevT))
                                  * _barrelRollDirection;
                transform.Rotate(_barrelRollAxis, deltaAngle, Space.Self);

                if (_barrelRollTimer >= barrelRollDuration)
                {
                    _isBarrelRolling        = false;
                    _barrelRollCooldownTimer = barrelRollCooldown;
                }
            }

            // ── Auto-level at rest (not braking) ─────────────────────────────
            // Gently snaps to horizon when the ship coasts to a natural stop.
            // Suppressed while braking so the player can hold a new heading.
            if (!isBraking && !_isBarrelRolling && !_isResettingHorizon &&
                currentVelocity.magnitude < autoLevelSpeedThreshold)
            {
                Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    Quaternion levelTarget = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation, levelTarget, autoLevelSpeed * dt);
                }
            }

            // ── Horizon reset (R3) ────────────────────────────────────────────
            // Rotates toward world-level at autoLevelSpeed each frame.
            // Target is recomputed from the current forward so the player can still
            // steer during the snap and the reset tracks their heading.
            // Completes when within 0.5° of level.
            if (_isResettingHorizon)
            {
                Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    Quaternion levelTarget = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation, levelTarget, autoLevelSpeed * dt);
                    if (Quaternion.Angle(transform.rotation, levelTarget) < 0.5f)
                        _isResettingHorizon = false;
                }
                else
                {
                    _isResettingHorizon = false;
                }
            }

            // ── Brake pressure ────────────────────────────────────────────────
            // Ramps 0 → 1 while held, resets on release.
            if (isBraking)
                _brakePressure = Mathf.MoveTowards(_brakePressure, 1f, brakeBuildUp * dt);
            else
                _brakePressure = 0f;

            // ── Velocity integration ──────────────────────────────────────────
            if (momentum != null && momentum.IsRedirecting)
            {
                currentVelocity = momentum.Tick(dt);
            }
            else
            {
                // Thrust is suppressed while braking — brake wins completely.
                if (!isBraking)
                {
                    Vector3 localThrust = new Vector3(
                        _thrustInput.x * strafeThrustForce,
                        _thrustInput.y * hoverThrustForce,
                        _thrustInput.z * thrustForce);
                    currentVelocity += transform.TransformDirection(localThrust) * dt;
                }

                // Base exponential drag always runs.
                currentVelocity *= Mathf.Exp(-linearDrag * dt);

                // Quadratic drag on strafe (X) and hover (Y) velocity in local space.
                // Force = drag × v² — nearly zero at low speed, ramps hard at high speed.
                Vector3 localVel = transform.InverseTransformDirection(currentVelocity);
                localVel.x -= strafeDrag * localVel.x * Mathf.Abs(localVel.x) * dt;
                localVel.y -= hoverDrag  * localVel.y * Mathf.Abs(localVel.y) * dt;
                currentVelocity = transform.TransformDirection(localVel);

                // Braking: linear deceleration toward zero, scaled by pressure.
                // MoveTowards (unlike exponential drag) can reach an exact full stop.
                if (_brakePressure > 0f)
                    currentVelocity = Vector3.MoveTowards(
                        currentVelocity, Vector3.zero, brakeForce * _brakePressure * dt);
            }
            currentVelocity = Vector3.ClampMagnitude(currentVelocity, maxSpeed);

            transform.position += currentVelocity * dt;

            // ── Collision depenetration ───────────────────────────────────────
            if (shipCollider != null)
                DepenetrateFromWorld();
        }

        void DepenetrateFromWorld()
        {
            Physics.SyncTransforms();

            for (int iter = 0; iter < depenetrationIterations; iter++)
            {
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

                    if (!Physics.ComputePenetration(
                            shipCollider, transform.position,       transform.rotation,
                            other,        other.transform.position, other.transform.rotation,
                            out Vector3 dir, out float depth))
                        continue;

                    transform.position += dir * (depth + 0.001f);

                    float into = Vector3.Dot(currentVelocity, -dir);
                    if (into > 0f)
                        currentVelocity += dir * into;

                    anyHit = true;
                }

                if (!anyHit) break;
                Physics.SyncTransforms();
            }
        }

        void LateUpdate()
        {
            if (visualMesh == null) return;

            float bank = Mathf.Clamp(-_thrustInput.x * strafeTilt, -maxLeanAngle, maxLeanAngle);

            float pitchFromVertical = -_thrustInput.y * hoverTilt;
            float pitchFromAccel    =  _thrustInput.z * accelerationTilt;
            float pitch = Mathf.Clamp(pitchFromVertical + pitchFromAccel, -maxLeanAngle, maxLeanAngle);

            Quaternion targetLean = Quaternion.Euler(pitch, 0f, bank);
            float t = 1f - Mathf.Exp(-leanSpeed * Time.deltaTime);
            visualMesh.localRotation = Quaternion.Slerp(visualMesh.localRotation, targetLean, t);
        }
    }
}
