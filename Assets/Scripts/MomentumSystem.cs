using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Per-frame input + state snapshot handed to <see cref="MomentumSystem.Step"/>.
    /// Everything the velocity model needs to advance one tick, with NO reads of
    /// Unity globals or the transform inside Step — that purity is the multiplayer
    /// contract (the step becomes serializable / replayable / deterministic).
    /// </summary>
    public readonly struct MotionIntent
    {
        public readonly Vector3    velocity;      // current world velocity, in
        public readonly Vector3    localThrust;   // ship-local thrust, components in [-1,1]
        public readonly float      yawInput;      // raw yaw command this frame (for counter-strafe sign)
        public readonly Quaternion heading;       // ship world rotation (post-rotation this frame)
        public readonly bool       braking;
        public readonly float      brakePressure; // 0..1
        public readonly bool       barrelRolling; // steering is skipped mid-roll

        public MotionIntent(Vector3 velocity, Vector3 localThrust, float yawInput,
                            Quaternion heading, bool braking, float brakePressure, bool barrelRolling)
        {
            this.velocity      = velocity;
            this.localThrust   = localThrust;
            this.yawInput      = yawInput;
            this.heading       = heading;
            this.braking       = braking;
            this.brakePressure = brakePressure;
            this.barrelRolling = barrelRolling;
        }
    }

    /// <summary>Result of one <see cref="MomentumSystem.Step"/>: the new velocity plus the
    /// drift bookkeeping the controller reads back for rotation authority and visuals.</summary>
    public readonly struct MotionState
    {
        public readonly Vector3 velocity;
        public readonly bool    isDrifting;
        public readonly float   driftDir;
        public readonly float   driftBlend;

        public MotionState(Vector3 velocity, bool isDrifting, float driftDir, float driftBlend)
        {
            this.velocity   = velocity;
            this.isDrifting = isDrifting;
            this.driftDir   = driftDir;
            this.driftBlend = driftBlend;
        }
    }

    /// <summary>
    /// Physical tunables, snapshotted by the controller from its Inspector fields each
    /// frame and passed into Step. Kept as a pass-in snapshot (rather than fields on
    /// MomentumSystem) so Phase 0 does NOT have to re-wire the prefab — the serialized
    /// values stay on SpaceshipController. A later phase can migrate ownership here.
    /// </summary>
    public readonly struct MotionTunables
    {
        public readonly float thrustForce, strafeThrustForce, maxStrafeThrustForce, hoverThrustForce;
        public readonly float linearDrag, strafeDrag, hoverDrag, maxSpeed, turnResistanceMaxSpeed;
        public readonly float steeringMinSpeed, steeringGripLow, steeringGripHigh, strafeGripScale;
        public readonly float driftGrip, driftCounterGrip, driftBlendSpeed;
        public readonly float brakeForce, driftMinSpeed, driftInputDeadzone;

        public MotionTunables(
            float thrustForce, float strafeThrustForce, float maxStrafeThrustForce, float hoverThrustForce,
            float linearDrag, float strafeDrag, float hoverDrag, float maxSpeed, float turnResistanceMaxSpeed,
            float steeringMinSpeed, float steeringGripLow, float steeringGripHigh, float strafeGripScale,
            float driftGrip, float driftCounterGrip, float driftBlendSpeed,
            float brakeForce, float driftMinSpeed, float driftInputDeadzone)
        {
            this.thrustForce            = thrustForce;
            this.strafeThrustForce      = strafeThrustForce;
            this.maxStrafeThrustForce   = maxStrafeThrustForce;
            this.hoverThrustForce       = hoverThrustForce;
            this.linearDrag             = linearDrag;
            this.strafeDrag             = strafeDrag;
            this.hoverDrag              = hoverDrag;
            this.maxSpeed               = maxSpeed;
            this.turnResistanceMaxSpeed = turnResistanceMaxSpeed;
            this.steeringMinSpeed       = steeringMinSpeed;
            this.steeringGripLow        = steeringGripLow;
            this.steeringGripHigh       = steeringGripHigh;
            this.strafeGripScale        = strafeGripScale;
            this.driftGrip              = driftGrip;
            this.driftCounterGrip       = driftCounterGrip;
            this.driftBlendSpeed        = driftBlendSpeed;
            this.brakeForce             = brakeForce;
            this.driftMinSpeed          = driftMinSpeed;
            this.driftInputDeadzone     = driftInputDeadzone;
        }
    }

    [DisallowMultipleComponent]
    public class MomentumSystem : MonoBehaviour
    {
        // ── Redirect / brake-boost (unchanged from before) ───────────────────────
        // A short smoothstep blend from one velocity to an explicit target velocity.
        // Driven by SpaceshipController on brake release. While this is active, Step
        // hands back the blended velocity instead of integrating thrust.
        public bool IsRedirecting { get; private set; }

        Vector3 _oldVelocity;
        Vector3 _targetVelocity;
        float   _timer;
        float   _duration;
        Vector3 _blended;

        // ── Drift state (owned here now — it IS part of the momentum model) ───────
        bool  _isDrifting;
        float _driftDir;     // sign of yaw captured at drift entry (+1 = turning right)
        float _driftBlend;   // 0→1 eased commitment; drives drift grip + visual yaw/lean

        public bool  IsDrifting      => _isDrifting;
        public float DriftCommitment => _driftBlend;
        public float DriftDir        => _driftDir;

        // Smoothly accelerates from fromVelocity to an explicit toVelocity over duration
        // seconds (smoothstep curve). Used for brake boosts where the target is a known
        // desired velocity, not a direction-carryover calculation.
        public void StartBoost(Vector3 fromVelocity, Vector3 toVelocity, float duration)
        {
            _oldVelocity    = fromVelocity;
            _targetVelocity = toVelocity;
            _timer          = 0f;
            _duration       = Mathf.Max(0.001f, duration);
            _blended        = fromVelocity;
            IsRedirecting   = true;
        }

        // Advances the redirect blend. Called from inside Step while IsRedirecting.
        public Vector3 Tick(float dt)
        {
            if (!IsRedirecting) return _blended;
            _timer += dt;
            float t = Mathf.Clamp01(_timer / _duration);
            float s = t * t * (3f - 2f * t);
            _blended = Vector3.Lerp(_oldVelocity, _targetVelocity, s);
            if (t >= 1f) IsRedirecting = false;
            return _blended;
        }

        public Vector3 GetBlendedVelocity() => _blended;

        /// <summary>
        /// Advance the ship's velocity one tick. PHASE 0: this is the old
        /// SpaceshipController velocity block, relocated verbatim — same thrust
        /// integration, same drag, same momentum-steering Slerp, same drift latch,
        /// same brake decel, same clamp. Behaviour is identical to pre-extraction; this
        /// only proves the seam (intent in, state out, transforms applied by the caller).
        ///
        /// Math equivalences used to drop the transform: Unity's TransformDirection /
        /// InverseTransformDirection are rotation-only (scale/position independent), so
        /// heading * v and Quaternion.Inverse(heading) * v match them exactly.
        /// </summary>
        public MotionState Step(in MotionIntent intent, in MotionTunables k, float dt)
        {
            Vector3 velocity = intent.velocity;

            // speedT: 0 at rest → 1 at turnResistanceMaxSpeed. Computed from the incoming
            // velocity, identical to the controller's rotation-block value (same vector).
            float speedT = Mathf.Clamp01(velocity.magnitude / Mathf.Max(1f, k.turnResistanceMaxSpeed));

            if (IsRedirecting)
            {
                // Brake-release redirect owns velocity; drift eases out.
                velocity    = Tick(dt);
                _isDrifting = false;
                _driftBlend = Mathf.MoveTowards(_driftBlend, 0f, k.driftBlendSpeed * dt);
            }
            else
            {
                // ── Thrust (suppressed while braking — brake wins completely) ──────
                if (!intent.braking)
                {
                    float effectiveStrafe = Mathf.Lerp(k.strafeThrustForce, k.maxStrafeThrustForce, speedT);
                    Vector3 localThrust = new Vector3(
                        intent.localThrust.x * effectiveStrafe,
                        intent.localThrust.y * k.hoverThrustForce,
                        intent.localThrust.z * k.thrustForce);
                    velocity += (intent.heading * localThrust) * dt;
                }

                // ── Uniform world-space drag ──────────────────────────────────────
                velocity *= Mathf.Exp(-k.linearDrag * dt);

                // ── Quadratic strafe/hover drag — gated on active input only ───────
                // Strafe drag is suppressed while drifting so it never bleeds the lateral
                // momentum the counter-strafe is building. Uses last tick's _isDrifting
                // (not yet updated this tick) — matching the original ordering exactly.
                Vector3 localVel = Quaternion.Inverse(intent.heading) * velocity;
                if (!_isDrifting && Mathf.Abs(intent.localThrust.x) > 1e-5f)
                    localVel.x -= k.strafeDrag * localVel.x * Mathf.Abs(localVel.x) * dt;
                if (Mathf.Abs(intent.localThrust.y) > 1e-5f)
                    localVel.y -= k.hoverDrag * localVel.y * Mathf.Abs(localVel.y) * dt;
                velocity = intent.heading * localVel;

                // ── Momentum steering + drift latch (yaw-plane) ───────────────────
                float horizSpeed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
                Vector3 horizFwd = Vector3.ProjectOnPlane(intent.heading * Vector3.forward, Vector3.up);
                if (!intent.barrelRolling && horizSpeed >= k.steeringMinSpeed && horizFwd.sqrMagnitude > 1e-4f)
                {
                    horizFwd.Normalize();
                    Vector3 horizVel    = new Vector3(velocity.x, 0f, velocity.z);
                    Vector3 horizVelDir = horizVel / horizSpeed;

                    bool yawing        = Mathf.Abs(intent.yawInput)     > 1e-4f;
                    bool strafing      = Mathf.Abs(intent.localThrust.x) > k.driftInputDeadzone;
                    bool counterStrafe = yawing && strafing &&
                                         Mathf.Sign(intent.localThrust.x) != Mathf.Sign(intent.yawInput);

                    if (!_isDrifting)
                    {
                        if (counterStrafe && horizSpeed >= k.driftMinSpeed)
                        {
                            _isDrifting = true;
                            _driftDir   = Mathf.Sign(intent.yawInput);
                        }
                    }
                    else if (!counterStrafe || horizSpeed < k.driftMinSpeed)
                    {
                        _isDrifting = false;
                    }

                    _driftBlend = Mathf.MoveTowards(_driftBlend, _isDrifting ? 1f : 0f,
                                                    k.driftBlendSpeed * dt);

                    float hemisphere   = Mathf.Clamp01(Vector3.Dot(horizVelDir, horizFwd));
                    float gripBase     = Mathf.Lerp(k.steeringGripLow, k.steeringGripHigh, speedT);
                    float strafeLoosen = Mathf.Lerp(1f, k.strafeGripScale, Mathf.Abs(intent.localThrust.x));
                    gripBase *= strafeLoosen;

                    float counterMag    = Mathf.Clamp01(
                        (Mathf.Abs(intent.localThrust.x) - k.driftInputDeadzone) / (1f - k.driftInputDeadzone));
                    float commandedGrip = Mathf.Lerp(k.driftGrip, k.driftCounterGrip, counterMag);
                    float driftGripNow  = Mathf.Lerp(k.steeringGripLow, commandedGrip, speedT);
                    float grip          = Mathf.Lerp(gripBase, driftGripNow, _driftBlend) * hemisphere;

                    if (grip > 1e-5f)
                    {
                        float steerT = 1f - Mathf.Exp(-grip * dt);
                        Vector3 steered = Vector3.Slerp(horizVel, horizFwd * horizSpeed, steerT);
                        velocity = steered + Vector3.up * velocity.y;
                    }
                }
                else if (_isDrifting || _driftBlend > 0f)
                {
                    // Below steering speed or degenerate heading: drop the drift, ease out.
                    _isDrifting = false;
                    _driftBlend = Mathf.MoveTowards(_driftBlend, 0f, k.driftBlendSpeed * dt);
                }

                // ── Braking: linear decel toward zero, scaled by pressure ─────────
                if (intent.brakePressure > 0f)
                    velocity = Vector3.MoveTowards(
                        velocity, Vector3.zero, k.brakeForce * intent.brakePressure * dt);
            }

            velocity = Vector3.ClampMagnitude(velocity, k.maxSpeed);
            return new MotionState(velocity, _isDrifting, _driftDir, _driftBlend);
        }
    }
}
