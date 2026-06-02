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
        [Tooltip("How hard the ship accelerates forward/backward. Higher = quicker to reach top speed and " +
                 "a punchier throttle.")]
        public float thrustForce = 20f;

        [Tooltip("How hard the ship strafes side-to-side when barely moving.")]
        public float strafeThrustForce = 30f;

        [Tooltip("How hard the ship strafes side-to-side at cruising speed. Set higher than the at-rest " +
                 "value to make strafing stronger the faster you go.")]
        public float maxStrafeThrustForce = 30f;

        [Tooltip("How quickly sideways strafe speed levels off. Higher = strafing tops out sooner and " +
                 "feels tighter. Lower = strafe drifts faster and further.")]
        public float strafeDrag = 0.05f;

        [Tooltip("How hard the ship pushes up/down (hover).")]
        public float hoverThrustForce = 30f;

        [Tooltip("How quickly up/down hover speed levels off. Higher = hover tops out sooner. Lower = " +
                 "floatier vertical movement.")]
        public float hoverDrag = 0.05f;

        [Tooltip("Top speed (m/s).")]
        public float maxSpeed = 50f;

        [Range(0f, 5f)]
        [Tooltip("How fast the ship slows when you let off the throttle, and how long it takes to build up " +
                 "to top speed. Higher = quick stop and a short run-up. Lower = long glide and a slow climb " +
                 "to top speed.")]
        public float linearDrag = 0.4f;

        [Header("Brake")]
        [Tooltip("How hard the brake stops the ship at full pressure. Higher = stops faster.")]
        public float brakeForce = 80f;

        [Tooltip("How fast the brake reaches full strength while held. 2 = full in 0.5 s; 1 = full in 1 s.")]
        public float brakeBuildUp = 2f;

        [Tooltip("The entry speed that divides a rewarding brake-release boost from a penalty. Brake from " +
                 "above this = boost; below = penalty.")]
        public float brakeThresholdSpeed = 7f;

        [Tooltip("Speed boost when you release the brake after braking from a fast entry. Above 1 = you " +
                 "come out faster than you went in.")]
        public float boostMultiplier = 1.2f;

        [Tooltip("Speed penalty when you brake from low speed. Below 1 = you come out slower (discourages " +
                 "brake-spamming).")]
        public float brakePenaltyMultiplier = 0.5f;

        [Tooltip("How quickly the brake-release boost kicks in. Short (0.15–0.25) = a snappy burst. " +
                 "Longer = a smoother, rocket-like surge.")]
        public float boostSnapDuration = 0.2f;

        [Header("Drift Reward")]
        // Reward sticking a slide: a committed drift charges up (the harder + longer the
        // slide, the more charge), and the moment grip re-establishes the charge releases
        // as a forward burst along the new heading. The burst can briefly push you above
        // your thrust-terminal cruise; drag then bleeds it back down. Tunable below.
        [Tooltip("How much exit speed a drift rewards — the harder and longer you slide, the bigger the " +
                 "boost. Higher = drifting pays out harder.")]
        public float driftBoostGain = 40f;

        [Tooltip("Cap on the drift boost no matter how long you hold the slide. Stops endless donuts from " +
                 "banking a huge boost.")]
        public float driftBoostMaxSpeed = 80f;

        [Tooltip("How long you must hold a real slide before it pays out at all. A quick flick earns " +
                 "nothing — you have to commit.")]
        public float driftBoostMinCharge = 0.25f;

        [Tooltip("How quickly the drift boost kicks in on exit. Short (0.2–0.3) = a snappy kick out of the " +
                 "corner. Longer = a smoother surge.")]
        public float driftBoostSnap = 0.25f;

        [Range(0f, 1f)]
        [Tooltip("How much speed a committed drift keeps instead of bleeding. 0 = old behaviour (a drift " +
                 "slows you down). 1 = a drift fully maintains the speed you carried into it, so it rewards " +
                 "you rather than punishing you. Scaled by how committed the slide is — light slips keep " +
                 "less, deep drifts keep more.")]
        public float driftSpeedRetention = 1f;

        // ── HANDLING (lateral grip) ───────────────────────────────────────────
        // One physical model governs cornering: a saturating tire-style lateral force on
        // the slip angle (angle between the nose and the actual direction of travel).
        // Cornering weight, the way fast turns arc wider, and the drift itself ALL emerge
        // from these three — there is no separate turn-authority curve or drift latch.
        [Header("Handling (lateral grip)")]
        [Tooltip("How hard the ship grips when cornering. Higher = sharp, tight turns that stick to where " +
                 "the nose points. Lower = looser, slides into drifts more easily.")]
        public float lateralGrip = 80f;

        [Tooltip("How hard you can corner before the ship breaks loose into a drift. Higher = holds tight " +
                 "turns longer. Lower = breaks into a slide sooner.")]
        public float maxGripForce = 30f;

        [Tooltip("How deep a slide must get to read as a full drift (drives the lean and drift visuals). " +
                 "Lower = the drift lean snaps in fast once you break loose. Higher = it builds in gradually " +
                 "as the slide deepens.")]
        public float peakSlipAngle = 20f;

        [Tooltip("Below this speed, steering grip switches off so the ship doesn't spin on the spot at a " +
                 "standstill. Leave low.")]
        public float steeringMinSpeed = 1.5f;

        [Tooltip("Your reference cruising speed. Used to scale strafe strength and how heavy turning gets " +
                 "with speed. Set it to your normal flying speed, not the top speed.")]
        public float turnResistanceMaxSpeed = 30f;

        [Tooltip("Anti-carve: how much speed (m/s² of extra braking) you scrub when you strafe HARD in the " +
                 "same direction you're turning hard — the 'powered carve' that out-races drifting. Forcing " +
                 "a tight strafe-corner now costs momentum, so a real drift (which keeps its speed) becomes " +
                 "the faster line. 0 = off. ~40 = a noticeable cost; raise until carving stops winning. " +
                 "Only bites on strong strafe + strong same-way yaw — pure strafe, dodges, drifts, and " +
                 "counter-strafe are all free.")]
        public float carveStrength = 40f;

        [Tooltip("How hard you must be turning (deg/s) before strafing-into-the-turn counts as a carve " +
                 "rather than a gentle nudge. Higher = only bites on sharp turns; lower = bites on gentler " +
                 "ones too. Keep it above your normal micro-correction turn rate.")]
        public float carveYawThreshold = 80f;

        // ── HANDLING (yaw inertia) ────────────────────────────────────────────
        // The heading is no longer instant — it carries rotational mass. The player's
        // yaw becomes a steering torque; the lateral grip adds a self-aligning torque that
        // pulls the nose back toward the velocity. That aligning term makes spins
        // impossible AND makes yaw feel heavier the faster you go — both emerge from it.
        [Header("Handling (yaw inertia)")]
        [Tooltip("How heavy the nose feels when turning left/right. Higher = weighty, takes a moment to " +
                 "wind up and to stop. Lower = darty and near-instant.")]
        public float yawInertia = 0.2f;

        [Tooltip("How hard your stick input turns the ship left/right. Higher = turns harder and reaches " +
                 "its turn rate faster.")]
        public float steerTorque = 1f;

        [Tooltip("How hard it is to turn at speed. Higher = turning gets very heavy when you're fast (you " +
                 "have to strafe/drift through corners) and the ship snaps straight. Lower = stays light " +
                 "and loose at speed.")]
        public float alignTorque = 2f;

        [Tooltip("How quickly the turn settles when you let go of the stick. Higher = stops crisply. " +
                 "Lower = coasts on and can wobble.")]
        public float yawDamping = 5f;

        // ── HANDLING (pitch inertia — vertical) ───────────────────────────────
        // The vertical mirror of the yaw model. Pointing the nose up/down is a rotation with its own
        // inertia and a weathervane that pulls the nose back onto the velocity ∝ speed² — so pitch is
        // HARD at speed, exactly like yaw. You break that coupling by HOVERING while you pitch, just as
        // you strafe to break it laterally. A vertical grip then rolls the velocity onto the nose so the
        // ship actually climbs/dives. Separate tunables from yaw so pitch can feel heavier or lighter.
        [Header("Handling (pitch inertia — vertical)")]
        [Tooltip("How heavy the nose feels when pointing up/down. Higher = weighty, slower to start and " +
                 "stop. Lower = darty.")]
        public float pitchInertia = 0.2f;

        [Tooltip("How hard your stick input pitches the nose up/down. Higher = pitches harder and faster.")]
        public float pitchSteerTorque = 1f;

        [Tooltip("How hard it is to climb/dive at speed. Higher = pitching gets very heavy when you're " +
                 "fast (hover while you pitch to break it). Lower = stays light at speed.")]
        public float pitchAlignTorque = 2f;

        [Tooltip("How quickly pitching settles when you let go of the stick. Higher = stops crisply. " +
                 "Lower = coasts on and can wobble.")]
        public float pitchDamping = 5f;

        [Tooltip("How strongly the ship follows the nose when climbing/diving. Higher = commits hard to " +
                 "where you point vertically. Lower = floatier, takes longer to actually change height. " +
                 "Hover to break it loose.")]
        public float pitchGrip = 80f;

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

        [Tooltip("Level to the TRACK SURFACE instead of world-flat: a ray cast straight down beneath " +
                 "the ship reads the floor's normal, and every auto-level (idle, R3, drift-boost exit) " +
                 "aligns the ship's up to it — so it hugs banks and hills. Falls back to world-up when " +
                 "airborne. Turn off for the old world-flat leveling.")]
        public bool alignLevelToGround = true;

        [Tooltip("Layers the ground probe can hit. Keep the ship/players off it (its own colliders are " +
                 "excluded regardless); leave as Everything to align to any surface below.")]
        public LayerMask groundMask = ~0;

        [Tooltip("How far below the ship (m) to look for a surface to align to.")]
        public float groundProbeDistance = 60f;

        [Tooltip("How fast the read surface-normal is chased (1/s). Higher = snappier alignment but more " +
                 "twitch across seams; lower = smoother, calmer. Also eases back to world-up when airborne.")]
        public float groundNormalSmooth = 8f;

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

        [Tooltip("How much the nose visually leans INTO a climb/dive when you pitch — the cosmetic twin of " +
                 "the drift lean, scaled by how fast you're pitching. Higher = more exaggerated lead; 0 = off. " +
                 "Pure visual.")]
        public float pitchLean = 0.12f;

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

        [Range(0f, 3f)]
        [Tooltip("Surface friction on impact — how much sideways speed is scrubbed per unit of " +
                 "head-on impact speed. A glancing graze barely presses into the wall (tiny into-" +
                 "speed) so it scrubs almost nothing; a hard angled hit presses harder so it " +
                 "scrubs more; a dead-on hit already loses its whole forward component to the " +
                 "normal removal. Loss therefore emerges from the hit angle, not a fixed penalty.")]
        public float collisionFriction = 0.5f;

        [Tooltip("Radius of the swept sphere used for CONTINUOUS collision (anti-tunneling). The " +
                 "move is cast along the velocity each frame so a thin wall can't be skipped at " +
                 "high speed. 0 = auto (the thickest sphere that fits inside the ship collider). " +
                 "Smaller = squeezes through tighter gaps but can clip corners; larger = safer but " +
                 "stops short of walls.")]
        public float sweepRadius = 0f;

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

        // Drift-boost auto-level: a timed flatten triggered on a drift-boost exit, lasting exactly
        // the boost's duration so the redirect and the level finish together.
        bool  _driftLeveling;
        float _driftLevelTimer;
        float _driftLevelDuration;

        // Ground-aligned leveling: the smoothed "up" all auto-leveling aims at — the track-surface
        // normal beneath the ship, eased toward world-up when there's nothing below.
        Vector3 _groundUp = Vector3.up;
        readonly RaycastHit[] _groundHits = new RaycastHit[8];

        // Drift state lives in MomentumSystem now (it is part of the momentum model).
        // These delegate so external readers (ShipPathTracer, visuals) are unaffected.
        public bool  IsDrifting      => momentum != null && momentum.IsDrifting;
        public float DriftCommitment => momentum != null ? momentum.DriftCommitment : 0f;

        // Local-space thrust command this frame (x = strafe, y = hover, z = forward/back),
        // each in [-1,1]. Read by engine/propeller VFX so it reacts to the player actually
        // accelerating, not merely to speed (a ship coasting at top speed isn't throttling up).
        public Vector3 ThrustInput => _thrustInput;

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

        // The "up" every auto-level aims at. With alignLevelToGround on, it's the smoothed normal of
        // the track surface beneath the ship (cast straight down in the ship's own frame, so it finds
        // the floor it's riding even on a banked wall); otherwise, and whenever nothing's below, it
        // eases back to world up. Own colliders are skipped via root, which also keeps it correct
        // per-ship in multiplayer. One cheap ray per frame, smoothed so seams don't make it twitch.
        void UpdateGroundUp(float dt)
        {
            Vector3 target = Vector3.up;
            if (alignLevelToGround)
            {
                int n = Physics.RaycastNonAlloc(transform.position, -transform.up, _groundHits,
                                                groundProbeDistance, groundMask, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var h = _groundHits[i];
                    if (h.collider == null || h.collider.transform.root == transform.root) continue; // skip self
                    if (h.distance < best) { best = h.distance; target = h.normal; }
                }
            }
            _groundUp = Vector3.Slerp(_groundUp, target, 1f - Mathf.Exp(-groundNormalSmooth * dt));
            if (_groundUp.sqrMagnitude < 1e-6f) _groundUp = Vector3.up;
            _groundUp.Normalize();
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            UpdateGroundUp(dt);   // refresh the surface-aligned 'up' all leveling aims at

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

            // Pitch is now DYNAMIC like yaw — integrated in MomentumSystem (inertia + weathervane) and
            // applied just after Step from the returned pitchRate. Only roll stays kinematic (barrel roll).

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
                Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, _groundUp);
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    Quaternion levelTarget = Quaternion.LookRotation(flatFwd.normalized, _groundUp);
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
                Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, _groundUp);
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    Quaternion levelTarget = Quaternion.LookRotation(flatFwd.normalized, _groundUp);
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

            // ── Drift-boost auto-level ────────────────────────────────────────
            // Fired on a drift-boost exit; flattens the horizon over EXACTLY the boost's
            // duration so the redirect and the level land together. Like the manual reset
            // it re-aims at the live heading each frame, so steering still reads mid-snap.
            // The dt/remaining factor converges onto the (moving) level target precisely as
            // the timer expires — a clean, duration-locked ease rather than a fixed rate.
            if (_driftLeveling && !_isBarrelRolling)
            {
                _driftLevelTimer += dt;
                Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, _groundUp);
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    Quaternion levelTarget = Quaternion.LookRotation(flatFwd.normalized, _groundUp);
                    float remaining = _driftLevelDuration - _driftLevelTimer;
                    if (remaining <= dt)
                    {
                        transform.rotation = levelTarget;
                        _driftLeveling = false;
                    }
                    else
                    {
                        transform.rotation = Quaternion.Slerp(
                            transform.rotation, levelTarget, dt / remaining);
                    }
                }
                else
                {
                    _driftLeveling = false;
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
                float yawCommand   = _rotationInput.y / dt;
                float pitchCommand = _rotationInput.x / dt;   // same deg-this-frame → rate conversion
                var intent = new MotionIntent(
                    currentVelocity, _thrustInput, yawCommand, pitchCommand,
                    transform.rotation, isBraking, _brakePressure, _isBarrelRolling);
                var tunables = new MotionTunables(
                    thrustForce, strafeThrustForce, maxStrafeThrustForce, hoverThrustForce,
                    linearDrag, strafeDrag, hoverDrag, maxSpeed, turnResistanceMaxSpeed,
                    steeringMinSpeed, brakeForce,
                    lateralGrip, peakSlipAngle * Mathf.Deg2Rad, maxGripForce,
                    carveStrength, carveYawThreshold,
                    yawInertia, steerTorque, alignTorque, yawDamping,
                    pitchInertia, pitchSteerTorque, pitchAlignTorque, pitchDamping, pitchGrip,
                    driftBoostGain, driftBoostMaxSpeed, driftBoostMinCharge, driftBoostSnap,
                    driftSpeedRetention);

                MotionState state = momentum.Step(intent, tunables, dt);
                currentVelocity = state.velocity;

                // Apply the dynamic yaw AND pitch the model integrated this tick (deg/s × dt). Both axes
                // now go through the same inertia+weathervane model; only roll stays kinematic.
                float yawDeg = state.yawRate * dt;
                if (Mathf.Abs(yawDeg) > 1e-6f) transform.Rotate(Vector3.up, yawDeg, Space.Self);
                float pitchDeg = state.pitchRate * dt;
                if (Mathf.Abs(pitchDeg) > 1e-6f) transform.Rotate(Vector3.right, pitchDeg, Space.Self);

                // Drift-boost exit → start a timed auto-level matching the boost's duration, so the
                // ship snaps onto the nose and flattens out in one motion as it leaves the slide.
                if (momentum.DriftBoostFired && !_isBarrelRolling)
                {
                    _driftLeveling      = true;
                    _driftLevelTimer    = 0f;
                    _driftLevelDuration = Mathf.Max(0.01f, driftBoostSnap);
                }
            }
            else
            {
                // Fallback when no MomentumSystem is attached: instant commanded yaw + pitch.
                if (Mathf.Abs(_rotationInput.y) > 1e-5f)
                    transform.Rotate(Vector3.up, _rotationInput.y, Space.Self);
                if (Mathf.Abs(_rotationInput.x) > 1e-5f)
                    transform.Rotate(Vector3.right, _rotationInput.x, Space.Self);
            }

            // ── Movement + collision ──────────────────────────────────────────
            // Swept (continuous) move first so a wall can't be tunnelled through at
            // speed, then a depenetration pass to settle any resting overlap.
            SweepMove(currentVelocity * dt);
            if (shipCollider != null)
                DepenetrateFromWorld();
        }

        // Continuous-collision integration. Instead of teleporting the ship by the full
        // per-frame displacement (which lets fast ships skip clean over thin walls between
        // frames), we cast the ship's sphere along the path and advance only as far as the
        // first contact, apply the impact velocity response, then slide the leftover travel
        // along the surface. Repeated a few times so corners resolve in one frame.
        // Velocity response to contacting a surface with the given outward `normal`. Removes the
        // component driving us INTO the wall (head-on energy is lost), then scrubs grazing speed by
        // Coulomb friction ∝ how hard we hit. Shared by the swept (impact) and resting (depenetration)
        // passes so the model lives in ONE place — this is the single seam the collision rework reshapes
        // (impact severity, a recovery window, and an angle-aware scrub-drag will all land here).
        void ApplyCollisionResponse(Vector3 normal)
        {
            float into = Vector3.Dot(currentVelocity, -normal);
            if (into <= 0f) return;

            currentVelocity += normal * into;            // kill the into-surface component
            float tang = currentVelocity.magnitude;
            if (tang > 1e-4f)
            {
                float scrubbed = Mathf.Max(0f, tang - collisionFriction * into);
                currentVelocity *= scrubbed / tang;
            }
        }

        void SweepMove(Vector3 displacement)
        {
            float dist = displacement.magnitude;
            if (dist < 1e-6f) return;

            // No collider (or sweeping disabled) → plain kinematic move.
            if (shipCollider == null)
            {
                transform.position += displacement;
                return;
            }

            const float skin = 0.02f;
            Bounds b = shipCollider.bounds;
            float radius = sweepRadius > 1e-4f
                ? sweepRadius
                : Mathf.Max(0.05f, Mathf.Min(b.extents.x, b.extents.y, b.extents.z));

            Vector3 startCenter = b.center;   // sphere origin tracks the collider centre
            Vector3 center      = startCenter;
            Vector3 dir         = displacement / dist;
            float   remaining   = dist;

            for (int i = 0; i < 4 && remaining > 1e-5f; i++)
            {
                if (Physics.SphereCast(center, radius, dir, out RaycastHit hit,
                        remaining + skin, collisionMask, QueryTriggerInteraction.Ignore)
                    && !_ownColliders.Contains(hit.collider) && !hit.collider.isTrigger
                    && hit.distance > 1e-4f)   // distance 0 = already overlapping; leave it to depenetration
                {
                    float advance = Mathf.Max(0f, hit.distance - skin);
                    center    += dir * advance;
                    remaining -= advance;

                    // Impact response (shared with resting contact).
                    Vector3 n = hit.normal;
                    ApplyCollisionResponse(n);

                    // Redirect the leftover travel along the wall (slide, don't stop dead).
                    Vector3 slide = Vector3.ProjectOnPlane(dir * remaining, n);
                    remaining = slide.magnitude;
                    dir       = remaining > 1e-5f ? slide / remaining : Vector3.zero;
                    if (dir == Vector3.zero) break;
                }
                else
                {
                    center += dir * remaining;
                    remaining = 0f;
                }
            }

            transform.position += center - startCenter;
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

                    // Resting-contact velocity response (shared with the swept impact).
                    ApplyCollisionResponse(dir);

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

            // Lean the nose INTO the climb/dive — the cosmetic twin of the drift lean, scaled by how fast
            // the ship is actually pitching. Pure visual, added on top of the input-driven tilt.
            float pitchLeanAngle = momentum != null
                ? Mathf.Clamp(momentum.PitchRate * pitchLean, -maxLeanAngle, maxLeanAngle)
                : 0f;
            pitch += pitchLeanAngle;

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
