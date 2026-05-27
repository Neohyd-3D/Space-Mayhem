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

        [Header("Dependencies")]
        public MomentumSystem momentum;

        public Vector3 currentVelocity { get; private set; }
        public float personalTimescale { get; private set; } = 1f;
        public bool isBraking { get; private set; }

        Vector3 _thrustInput;
        Vector3 _rotationInput;  // degrees this frame (already source-scaled by SpaceshipInput)
        bool _wasBraking;

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
