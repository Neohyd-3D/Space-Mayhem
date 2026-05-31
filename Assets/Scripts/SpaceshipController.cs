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
    /// Braking applies a linear deceleration that can bring the ship to a full stop;
    /// releasing the brake fires a forward boost via MomentumSystem (reward or penalty
    /// scaled by entry speed). A counter-strafe latches a momentum-steering drift.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpaceshipController : MonoBehaviour
    {
        [Header("Thrust")]
        [Tooltip("Acceleration for forward / backward thrust (m/s²).")]
        public float thrustForce = 50f;

        [Tooltip("Strafe acceleration (m/s²) at zero speed.")]
        public float strafeThrustForce = 30f;

        [Tooltip("Strafe acceleration (m/s²) at turnResistanceMaxSpeed. " +
                 "Linearly interpolated from strafeThrustForce as speed increases. " +
                 "Higher than strafeThrustForce = strafing becomes more powerful at speed.")]
        public float maxStrafeThrustForce = 30f;

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
        [Tooltip("Per-second velocity decay rate when NOT thrusting forward. " +
                 "While the trigger is held, forward momentum is preserved through turns. " +
                 "Release the trigger and this rate determines how fast the ship coasts to a stop.")]
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

        // ── HANDLING (lateral grip) ───────────────────────────────────────────
        // One physical model governs cornering: a saturating tire-style lateral force on
        // the slip angle (angle between the nose and the actual direction of travel).
        // Cornering weight, the way fast turns arc wider, and the drift itself ALL emerge
        // from these three — there is no separate turn-authority curve or drift latch.
        [Header("Handling (lateral grip)")]
        [Tooltip("CORNERING STIFFNESS — lateral force (m/s² per radian of slip) the surface makes " +
                 "while still gripping. HIGHER = the velocity snaps to the nose harder, tighter steering " +
                 "with little slip. LOWER = looser, the ship washes out into a slide more readily. " +
                 "The realign rate is force/speed, so the SAME stiffness already feels heavier at speed.")]
        public float lateralGrip = 80f;

        [Tooltip("FRICTION BUDGET — the maximum lateral force (m/s²) the surface can hold before it " +
                 "saturates and the ship slides. Once cornering demand exceeds this, the velocity can no " +
                 "longer keep up with the nose and a drift opens. LOWER = breaks loose sooner / slides easier.")]
        public float maxGripForce = 30f;

        [Tooltip("DRIFT COMMITMENT RANGE (degrees) — how much slip PAST breakaway counts as a fully " +
                 "committed drift (commitment = 1). Breakaway itself is emergent: maxGripForce/lateralGrip " +
                 "(the angle where the tyre lets go). Below breakaway the ship is gripping → commitment 0 → " +
                 "no lean, so simply yawing no longer tilts the mesh; only a real slide past breakaway does. " +
                 "Drives the visual swivel/lean and the path-tracer heatmap. LOWER = the lean snaps in fast " +
                 "once you break loose; HIGHER = the lean builds gradually as the slide deepens.")]
        public float peakSlipAngle = 20f;

        [Tooltip("Speed (m/s) below which the grip model is disabled. Guards the zero-speed direction " +
                 "singularity; keep just above autoLevelSpeedThreshold so the two never overlap.")]
        public float steeringMinSpeed = 1.5f;

        [Tooltip("Reference speed (m/s) for the speed-scaled strafe thrust lerp (strafeThrustForce → " +
                 "maxStrafeThrustForce). Set to your intended cruising speed, not maxSpeed.")]
        public float turnResistanceMaxSpeed = 30f;

        // ── HANDLING (yaw inertia) ────────────────────────────────────────────
        // The heading is no longer instant — it carries rotational mass. The player's
        // yaw becomes a steering torque; the lateral grip adds a self-aligning torque that
        // pulls the nose back toward the velocity. That aligning term makes spins
        // impossible AND makes yaw feel heavier the faster you go — both emerge from it.
        [Header("Handling (yaw inertia)")]
        [Tooltip("ROTATIONAL MASS — how much the nose resists changes to its turn rate. " +
                 "HIGHER = heavier, the turn takes longer to wind up and to stop; LOWER = darty, " +
                 "near-instant. This is what was missing after Phase A made yaw instant.")]
        public float yawInertia = 0.2f;

        [Tooltip("STEER TORQUE — turning force produced per unit of commanded yaw rate. " +
                 "HIGHER = the ship reaches its turn rate faster / turns harder for the same input. " +
                 "Trades off against yawInertia and yawDamping to set your top yaw rate.")]
        public float steerTorque = 1f;

        [Tooltip("WEATHERVANE TORQUE — directional-stability gain. The restoring moment that pulls " +
                 "the nose back toward the direction of travel scales with speed² (∝ dynamic pressure, " +
                 "like a real tail fin), so this is THE dial for how heavy fast yaw feels: at low " +
                 "speed yaw is near-free, at high speed it fights hard and forces you to strafe/drift " +
                 "through the corner. Also the no-spin stabilizer — HIGHER = planted/snaps straight, " +
                 "LOWER = looser, the nose wanders off heading more freely.")]
        public float alignTorque = 2f;

        [Tooltip("YAW DAMPING (1/s) — rotational drag that settles the turn rate and kills wobble. " +
                 "HIGHER = the turn stops crisply when you let go; LOWER = it coasts/oscillates. " +
                 "Top yaw rate ≈ steerTorque × commandedRate / (yawInertia × yawDamping).")]
        public float yawDamping = 5f;

        [Header("Drift Visual (cosmetic only)")]
        [Tooltip("Degrees the model swivels INTO the slide so the drift reads. Pure visual.")]
        public float driftYaw = 18f;

        [Tooltip("Degrees the model banks INTO the slide. Pure visual. Flip the sign if it leans wrong.")]
        public float driftLean = 12f;

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

        // Drift state lives in MomentumSystem now (it is part of the momentum model).
        // These delegate so external readers (ShipPathTracer, visuals) are unaffected.
        public bool  IsDrifting      => momentum != null && momentum.IsDrifting;
        public float DriftCommitment => momentum != null ? momentum.DriftCommitment : 0f;

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

            // ── Pitch (kinematic — instant, like before) ──────────────────────
            // Pitch and roll stay kinematic; only YAW became dynamic. Yaw is integrated
            // in MomentumSystem (it now has rotational inertia) and applied just after
            // Step from the returned yawRate — so it is NOT applied here.
            float scaledPitch = _rotationInput.x;
            if (Mathf.Abs(scaledPitch) > 1e-5f) transform.Rotate(Vector3.right, scaledPitch, Space.Self);

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

            // ── Velocity integration (delegated to MomentumSystem.Step) ───────
            // The entire velocity model — thrust, drag, the lateral-grip cornering/drift
            // model, brake decel, the brake-release redirect, and the speed clamp — lives
            // behind the pure Step seam. We snapshot input + tunables, hand them in, and
            // read the new velocity (and drift commitment, owned by MomentumSystem) back.
            // Step writes no transforms; transform.position is applied just below.
            if (momentum != null)
            {
                // yawCommand is the commanded yaw RATE (deg/s): the input arrives as
                // degrees-this-frame, so dividing by dt recovers the rate the player asked
                // for (works for both mouse pixel-delta and gamepad stick). MomentumSystem
                // turns it into a steering torque against the heading's inertia.
                float yawCommand = _rotationInput.y / dt;
                var intent = new MotionIntent(
                    currentVelocity, _thrustInput, yawCommand,
                    transform.rotation, isBraking, _brakePressure, _isBarrelRolling);
                var tunables = new MotionTunables(
                    thrustForce, strafeThrustForce, maxStrafeThrustForce, hoverThrustForce,
                    linearDrag, strafeDrag, hoverDrag, maxSpeed, turnResistanceMaxSpeed,
                    steeringMinSpeed, brakeForce,
                    lateralGrip, peakSlipAngle * Mathf.Deg2Rad, maxGripForce,
                    yawInertia, steerTorque, alignTorque, yawDamping);

                MotionState state = momentum.Step(intent, tunables, dt);
                currentVelocity = state.velocity;

                // Apply the dynamic yaw the model integrated this tick (deg/s × dt).
                float yawDeg = state.yawRate * dt;
                if (Mathf.Abs(yawDeg) > 1e-6f) transform.Rotate(Vector3.up, yawDeg, Space.Self);
            }
            else
            {
                // Fallback when no MomentumSystem is attached: instant commanded yaw.
                if (Mathf.Abs(_rotationInput.y) > 1e-5f)
                    transform.Rotate(Vector3.up, _rotationInput.y, Space.Self);
            }

            transform.position += currentVelocity * dt;

            // ── Collision depenetration ───────────────────────────────────────
            if (shipCollider != null)
                DepenetrateFromWorld();
        }

        void DepenetrateFromWorld()
        {
            // Broad-phase first — skip SyncTransforms entirely if nothing is nearby.
            // This avoids the expensive all-transforms sync every frame when flying
            // in open air, which was causing periodic frame hitches.
            Bounds b0     = shipCollider.bounds;
            float  radius = b0.extents.magnitude;
            int broadHits = Physics.OverlapSphereNonAlloc(
                b0.center, radius, _overlapBuffer,
                collisionMask, QueryTriggerInteraction.Ignore);

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
                Bounds b      = shipCollider.bounds;
                radius = b.extents.magnitude;

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

            // Drift commitment yaws and banks the visible mesh INTO the slide so the drift
            // reads. Drift state lives in MomentumSystem now; read it back for the visual.
            float driftBlend = momentum != null ? momentum.DriftCommitment : 0f;
            float driftDir   = momentum != null ? momentum.DriftDir        : 0f;
            float driftYawAngle = driftBlend * driftYaw  * driftDir;
            bank = Mathf.Clamp(bank + driftBlend * driftLean * driftDir,
                               -maxLeanAngle - driftLean, maxLeanAngle + driftLean);

            Quaternion targetLean = Quaternion.Euler(pitch, driftYawAngle, bank);
            float t = 1f - Mathf.Exp(-leanSpeed * Time.deltaTime);
            visualMesh.localRotation = Quaternion.Slerp(visualMesh.localRotation, targetLean, t);
        }
    }
}
