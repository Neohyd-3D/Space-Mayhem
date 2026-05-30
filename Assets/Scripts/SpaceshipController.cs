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

        [Header("Rotation")]
        [Tooltip("Speed (m/s) at which turn authority reaches its minimum (minTurnFactor). " +
                 "Below this speed the curve is gentle; at and above it the full restriction applies. " +
                 "Set this to your intended cruising speed, not maxSpeed.")]
        public float turnResistanceMaxSpeed = 30f;

        [Range(0f, 1f)]
        [Tooltip("Turn authority at turnResistanceMaxSpeed, as a fraction of full authority. " +
                 "Scales quadratically so low-speed handling is barely affected. " +
                 "0.3 = 30% of normal turn rate at full cruising speed.")]
        public float minTurnFactor = 0.3f;

        // Normal (non-drifting) handling: how hard momentum is pulled to follow the nose.
        // The Drift section below temporarily drops grip from these values down to driftGrip —
        // that planted-vs-loose contrast is what makes a drift read as a drift.
        [Header("Steering Grip (normal handling)")]
        [Tooltip("How strongly the ship's momentum follows its heading (1/s) at low speed " +
                 "when NOT drifting. High = responsive, tight steering with almost no slip. " +
                 "This is the directional grip on the velocity vector — yaw-plane only.")]
        public float steeringGripLow = 8f;

        [Tooltip("Momentum-follow grip (1/s) at turnResistanceMaxSpeed when NOT drifting. " +
                 "Keep fairly high so a pure yaw is a tight, committed turn even at speed. " +
                 "Steady-state slip ≈ yawRate / grip. The drift latch drops grip to driftGrip.")]
        public float steeringGripHigh = 4f;

        [Tooltip("Speed (m/s) below which steering is disabled. Guards the zero-speed direction " +
                 "singularity; keep just above autoLevelSpeedThreshold so the two never overlap.")]
        public float steeringMinSpeed = 1.5f;

        [Tooltip("How much an active strafe LOOSENS directional grip, so a sideways thrust reads as a " +
                 "dodge instead of being folded into forward motion. 1 = no effect (strafe curves " +
                 "forward — usually wrong). LOWER = the strafe stays lateral longer. 0.3 = full strafe " +
                 "cuts grip to 30% while you hold it. Only applies when NOT drifting; the drift latch " +
                 "owns grip on its own.")]
        [Range(0f, 1f)]
        public float strafeGripScale = 0.3f;

        // ── DRIFT ─────────────────────────────────────────────────────────────
        // A drift is entered by COUNTER-STRAFING (yaw one way, strafe the other) and
        // is just "drop the grip": your normal steering grip (above) is swapped for
        // the low driftGrip while committed, so momentum lags the nose = the slide.
        // Tune these three for feel; the two fixed gates below are rarely touched.
        [Header("Drift")]
        [Tooltip("★ WASH FLOOR — grip (1/s) when you EASE OFF the counter-strafe at speed: how wide " +
                 "the drift runs when you stop fighting it. LOWER = washes wider / understeers harder " +
                 "if you don't hold it. This is the loose end of the balancing act; Drift Counter Grip " +
                 "is the tight end. Keep well below Steering Grip High so the slide actually breaks loose.")]
        public float driftGrip = 1.5f;

        [Tooltip("★ FIGHT — grip (1/s) you claw back by pushing the counter-strafe FULLY at speed: how " +
                 "tight you can hold the line when you fight the wash. HIGHER = a hard push nearly " +
                 "cancels the slide (skilled, tight). LOWER = even fighting flat-out it keeps sliding. " +
                 "Sits between Wash Floor and your normal grip. The high-speed balancing happens " +
                 "between this and Wash Floor as you feather the strafe.")]
        public float driftCounterGrip = 5f;

        [Tooltip("★ DRIFT BITE — turn authority while fully committed to a drift. Speed normally CUTS " +
                 "yaw (down to Min Turn Factor at top speed); a drift lifts it back to THIS value, " +
                 "regardless of speed — that's how the drift returns control mid-corner. 1 = full " +
                 "low-speed agility restored. >1 = restored PLUS extra bite, so the red drift line " +
                 "hooks a tighter radius than the broad blue cruise turn. The high-speed radius dial.")]
        public float driftYawBoost = 1.6f;

        [Tooltip("ENTRY/EXIT SNAPPINESS (per second) — how fast the drift (grip + visuals) eases in " +
                 "and out. HIGHER = snappier, more instant. LOWER = softer, floatier.")]
        public float driftBlendSpeed = 6f;

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

        // Fixed drift gates — rarely tuned, so kept out of the Inspector. Promote to
        // [SerializeField] fields if you ever need to tweak them per-ship.
        const float DriftMinSpeed      = 8f;   // min horizontal speed (m/s) to enter / sustain a drift
        const float DriftInputDeadzone = 0.3f; // counter-strafe magnitude (0–1) needed to trigger one

        // Drift latch state
        bool  _isDrifting;   // true while a counter-strafe drift is committed
        float _driftDir;     // sign of yaw captured at drift entry (+1 = turning right)
        float _driftBlend;   // 0→1 eased commitment; drives drift grip + visual yaw/lean

        public bool  IsDrifting      => _isDrifting;
        public float DriftCommitment => _driftBlend;

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
            // Turn authority drops quadratically with speed: full at rest, minTurnFactor
            // at maxSpeed. Quadratic keeps low-speed handling snappy while making
            // high-speed turns feel appropriately heavy.
            float speedT      = Mathf.Clamp01(currentVelocity.magnitude / Mathf.Max(1f, turnResistanceMaxSpeed));
            float turnFactor  = Mathf.Lerp(1f, minTurnFactor, speedT * speedT);
            // Drift restores the turn authority speed took away: while committed, the loose tail
            // LIFTS turnFactor back toward driftYawBoost (full agility + bite) regardless of speed —
            // this is what "returns control to the player" mid-corner, instead of merely scaling the
            // already-crippled high-speed value. Uses last frame's _driftBlend (updated later in the
            // velocity block); the one-frame lag is imperceptible.
            float driftTurnFactor = Mathf.Lerp(turnFactor, driftYawBoost, _driftBlend);
            float scaledPitch     = _rotationInput.x * turnFactor;
            float scaledYaw       = _rotationInput.y * driftTurnFactor;

            if (Mathf.Abs(scaledPitch) > 1e-5f) transform.Rotate(Vector3.right, scaledPitch, Space.Self);
            if (Mathf.Abs(scaledYaw)   > 1e-5f) transform.Rotate(Vector3.up,    scaledYaw,   Space.Self);

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
                _isDrifting = false;
                _driftBlend = Mathf.MoveTowards(_driftBlend, 0f, driftBlendSpeed * dt);
            }
            else
            {
                // Thrust is suppressed while braking — brake wins completely.
                if (!isBraking)
                {
                    float effectiveStrafe = Mathf.Lerp(strafeThrustForce, maxStrafeThrustForce, speedT);
                    Vector3 localThrust = new Vector3(
                        _thrustInput.x * effectiveStrafe,
                        _thrustInput.y * hoverThrustForce,
                        _thrustInput.z * thrustForce);
                    currentVelocity += transform.TransformDirection(localThrust) * dt;
                }

                // ── Drag ─────────────────────────────────────────────────────────
                // Uniform world-space drag — shrinks velocity magnitude regardless of
                // direction. This creates an emergent equilibrium speed below maxSpeed
                // (equilibrium ≈ thrustForce / linearDrag), leaving maxSpeed free as
                // an absolute cap for boosts and speed modifiers.
                // Speed loss during turns is minimal because the quadratic strafe drag
                // (the real culprit) is gated on input below.
                currentVelocity *= Mathf.Exp(-linearDrag * dt);

                // Quadratic drag on strafe/hover — gated on active input only.
                // This is the strafe/hover TERMINAL-VELOCITY cap (terminal ≈ sqrt(thrust/drag)),
                // not a drift mechanism. Gated on input so it never bleeds the lateral momentum
                // that yawing produces — momentum steering (below) owns that axis instead.
                Vector3 localVel = transform.InverseTransformDirection(currentVelocity);
                // Suppressed while drifting: strafe drag would bleed the lateral momentum the
                // counter-strafe is trying to build, shrinking the slide. Momentum steering owns it then.
                if (!_isDrifting && Mathf.Abs(_thrustInput.x) > 1e-5f)
                    localVel.x -= strafeDrag * localVel.x * Mathf.Abs(localVel.x) * dt;
                if (Mathf.Abs(_thrustInput.y) > 1e-5f)
                    localVel.y -= hoverDrag  * localVel.y * Mathf.Abs(localVel.y) * dt;
                currentVelocity = transform.TransformDirection(localVel);

                // ── Momentum steering + drift latch (yaw-plane) ──────────────────
                // Rotate the HORIZONTAL velocity toward the ship's horizontal heading,
                // preserving magnitude (Slerp between equal-length vectors); the vertical
                // component is untouched so hover/climb is unaffected.
                //
                // Normally grip is high → tight, committed turns. A COUNTER-STRAFE (strafe opposite
                // the yaw) drops grip into the drift band and lifts yaw authority (driftYawBoost, in
                // the rotation block). The gesture is the throttle: holding it sustains the drift,
                // releasing it (or strafing back into the turn, or slowing below DriftMinSpeed) exits
                // immediately. Inside the drift, grip is a SPEED-SCALED balancing act between washing
                // wide and biting the line — see the grip block below. Understeer is the only failure
                // mode; the velocity Slerp can never overshoot the nose, so you can't spin out.
                float horizSpeed = new Vector3(currentVelocity.x, 0f, currentVelocity.z).magnitude;
                Vector3 horizFwd  = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                if (!_isBarrelRolling && horizSpeed >= steeringMinSpeed && horizFwd.sqrMagnitude > 1e-4f)
                {
                    horizFwd.Normalize();
                    Vector3 horizVel    = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
                    Vector3 horizVelDir = horizVel / horizSpeed;

                    // Counter-strafe gesture: yaw and strafe both present and on OPPOSITE sides.
                    bool yawing        = Mathf.Abs(_rotationInput.y) > 1e-4f;
                    bool strafing      = Mathf.Abs(_thrustInput.x)   > DriftInputDeadzone;
                    bool counterStrafe = yawing && strafing &&
                                         Mathf.Sign(_thrustInput.x) != Mathf.Sign(_rotationInput.y);

                    // Input-sustained latch: the counter-strafe IS the throttle. Hold it → drift
                    // holds; release it → exit immediately (responsive, no grace timer). _driftBlend
                    // eases grip back so the return is smooth, not a snap. Strafing back INTO the turn
                    // flips the sign so counterStrafe goes false — that's the natural "catch".
                    if (!_isDrifting)
                    {
                        if (counterStrafe && horizSpeed >= DriftMinSpeed)
                        {
                            _isDrifting = true;
                            _driftDir   = Mathf.Sign(_rotationInput.y);
                        }
                    }
                    else if (!counterStrafe || horizSpeed < DriftMinSpeed)
                    {
                        _isDrifting = false;
                    }

                    // Ease commitment 0↔1 — drives both the drift grip and the visual lean.
                    _driftBlend = Mathf.MoveTowards(_driftBlend, _isDrifting ? 1f : 0f,
                                                    driftBlendSpeed * dt);

                    // Forward-hemisphere fade: full grip when aligned, fading to zero by 90° of
                    // slip, zero when reversing — sidesteps the antiparallel Slerp singularity.
                    float hemisphere = Mathf.Clamp01(Vector3.Dot(horizVelDir, horizFwd));
                    float gripBase   = Mathf.Lerp(steeringGripLow, steeringGripHigh, speedT);
                    // A plain strafe (no drift) loosens grip so the sideways thrust stays a dodge
                    // instead of the momentum steering folding it into forward motion. Lerped out
                    // once the drift commits (_driftBlend → 1), since the drift band owns grip then.
                    float strafeLoosen = Mathf.Lerp(1f, strafeGripScale, Mathf.Abs(_thrustInput.x));
                    gripBase *= strafeLoosen;

                    // ── The balancing act (understeer-only) ──────────────────────
                    // Drift grip is a tug-of-war between WASH and FIGHT:
                    //   • Analog counter-strafe DEPTH (past the deadzone) is the fight — ease off
                    //     and grip sags toward driftGrip (washes wide), push hard and it claws back
                    //     up to driftCounterGrip (bites the line). Strafe magnitude finally matters.
                    //   • Speed sets the stakes — at low speed grip stays near normal (slow corners
                    //     are trivial); at high speed it collapses onto the fight, so a fast corner
                    //     you don't actively hold will run you wide. That's "fighting the force you
                    //     entered with". No spin is possible: the Slerp only ever pulls velocity
                    //     TOWARD the nose, never past it — understeer is the only failure mode.
                    float counterMag    = Mathf.Clamp01(
                        (Mathf.Abs(_thrustInput.x) - DriftInputDeadzone) / (1f - DriftInputDeadzone));
                    float commandedGrip = Mathf.Lerp(driftGrip, driftCounterGrip, counterMag);
                    float driftGripNow  = Mathf.Lerp(steeringGripLow, commandedGrip, speedT);
                    float grip          = Mathf.Lerp(gripBase, driftGripNow, _driftBlend) * hemisphere;

                    if (grip > 1e-5f)
                    {
                        float steerT = 1f - Mathf.Exp(-grip * dt);
                        Vector3 steered = Vector3.Slerp(horizVel, horizFwd * horizSpeed, steerT);
                        currentVelocity = steered + Vector3.up * currentVelocity.y;
                    }
                }
                else if (_isDrifting || _driftBlend > 0f)
                {
                    // Below steering speed or degenerate heading: drop the drift and ease out.
                    _isDrifting = false;
                    _driftBlend = Mathf.MoveTowards(_driftBlend, 0f, driftBlendSpeed * dt);
                }

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
            // reads. _driftBlend eases 0→1 with the latch; _driftDir is the turn sign.
            float driftYawAngle = _driftBlend * driftYaw  * _driftDir;
            bank = Mathf.Clamp(bank + _driftBlend * driftLean * _driftDir,
                               -maxLeanAngle - driftLean, maxLeanAngle + driftLean);

            Quaternion targetLean = Quaternion.Euler(pitch, driftYawAngle, bank);
            float t = 1f - Mathf.Exp(-leanSpeed * Time.deltaTime);
            visualMesh.localRotation = Quaternion.Slerp(visualMesh.localRotation, targetLean, t);
        }
    }
}
