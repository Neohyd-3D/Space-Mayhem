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
        public readonly float      yawCommand;    // commanded yaw RATE this frame (deg/s) — drives steering torque
        public readonly Quaternion heading;       // ship world rotation (pre this-frame dynamic yaw)
        public readonly bool       braking;
        public readonly float      brakePressure; // 0..1
        public readonly bool       barrelRolling; // grip/align is skipped mid-roll

        public MotionIntent(Vector3 velocity, Vector3 localThrust, float yawCommand,
                            Quaternion heading, bool braking, float brakePressure, bool barrelRolling)
        {
            this.velocity      = velocity;
            this.localThrust   = localThrust;
            this.yawCommand    = yawCommand;
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
        public readonly float   yawRate;    // deg/s — the controller rotates the heading by this × dt
        public readonly bool    isDrifting;
        public readonly float   driftDir;
        public readonly float   driftBlend;

        public MotionState(Vector3 velocity, float yawRate, bool isDrifting, float driftDir, float driftBlend)
        {
            this.velocity   = velocity;
            this.yawRate    = yawRate;
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
        public readonly float steeringMinSpeed, brakeForce;
        // The three physical grip params (replace the old authored grip/drift pile).
        public readonly float lateralGrip;    // cornering stiffness: lateral force per radian of slip (m/s² per rad)
        public readonly float peakSlipAngle;  // RADIANS of slip at which the drift reads as fully committed
        public readonly float maxGripForce;   // friction budget: max lateral force the surface can hold (m/s²)
        // Yaw-plane dynamics (the heading now has rotational mass; no-spin via the aligning torque).
        public readonly float yawInertia;     // rotational mass — resists yaw acceleration (higher = heavier nose)
        public readonly float steerTorque;    // torque produced per unit of commanded yaw rate
        public readonly float alignTorque;    // weathervane torque gain: restoring moment ∝ speed²·slip (no-spin stabilizer + heavy fast yaw)
        public readonly float yawDamping;     // rotational drag (1/s) on yaw rate — settles the turn, kills oscillation
        // Drift-reward boost: a held slide charges, the exit releases a forward burst.
        public readonly float driftBoostGain;      // m/s of bonus speed per accumulated charge-second
        public readonly float driftBoostMaxSpeed;  // cap on the bonus speed however long the drift held
        public readonly float driftBoostMinCharge; // charge-seconds needed before a drift pays out at all
        public readonly float driftBoostSnap;      // seconds to blend the velocity into the boosted exit

        public MotionTunables(
            float thrustForce, float strafeThrustForce, float maxStrafeThrustForce, float hoverThrustForce,
            float linearDrag, float strafeDrag, float hoverDrag, float maxSpeed, float turnResistanceMaxSpeed,
            float steeringMinSpeed, float brakeForce,
            float lateralGrip, float peakSlipAngle, float maxGripForce,
            float yawInertia, float steerTorque, float alignTorque, float yawDamping,
            float driftBoostGain, float driftBoostMaxSpeed, float driftBoostMinCharge, float driftBoostSnap)
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
            this.brakeForce             = brakeForce;
            this.lateralGrip            = lateralGrip;
            this.peakSlipAngle          = peakSlipAngle;
            this.maxGripForce           = maxGripForce;
            this.yawInertia             = yawInertia;
            this.steerTorque            = steerTorque;
            this.alignTorque            = alignTorque;
            this.yawDamping             = yawDamping;
            this.driftBoostGain         = driftBoostGain;
            this.driftBoostMaxSpeed     = driftBoostMaxSpeed;
            this.driftBoostMinCharge    = driftBoostMinCharge;
            this.driftBoostSnap         = driftBoostSnap;
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
        // There is no drift "latch" any more: a drift is simply the velocity lagging the
        // heading. _commitment is the live slip-angle fraction (0 = planted, 1 = full
        // slide), recomputed every Step from the actual geometry — not a state we enter.
        float _commitment;   // Clamp01(slipAngle / peakSlipAngle); drives visual yaw/lean + heatmap
        float _driftDir;     // sign of the current slip (+1 = sliding such that the nose points right of travel)

        // ── Drift-reward charge ───────────────────────────────────────────────────
        // While a slide is genuinely committed the stored lateral momentum is integrated
        // into a charge; the payout fires the instant you RELEASE the strafe you were
        // holding to keep the slide open. That release is the real exit cue — holding
        // strafe suppresses grip authority (keeps the slide alive), so letting go is the
        // moment grip re-engages and swings the velocity back onto the nose. Firing then
        // rides that re-grip instead of waiting for the slip to decay across some level.
        const float DriftArmCommit    = 0.5f;  // commitment above this charges + arms the reward
        const float DriftReleaseFloor = 0.1f;  // fallback: a yaw-only slide (no strafe to release) pays out once the slip closes
        const float StrafeDeadzone    = 0.1f;  // strafe magnitude below this counts as "not strafing"
        float _driftCharge;  // ∫ commitment dt over the held slide (charge-seconds)
        bool  _driftArmed;   // true once a slide has crossed the arm threshold this run
        bool  _wasStrafing;  // was the player holding strafe last tick — to catch the release edge

        public float DriftCharge => _driftCharge;

        // ── Yaw-plane dynamics (the heading is no longer instant — it has inertia) ─
        float _yawRate;      // deg/s, integrated from steering + self-aligning torque

        public bool  IsDrifting      => _commitment > 0.5f;
        public float DriftCommitment => _commitment;
        public float DriftDir        => _driftDir;
        public float YawRate         => _yawRate;

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

            // Any boost consumes/clears the drift charge so a brake-boost can't chain
            // into a stale drift payout the instant it ends.
            _driftCharge = 0f;
            _driftArmed  = false;
            _wasStrafing = false;
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
        /// Advance the ship's velocity AND heading-yaw one tick.
        ///
        /// PHASE A — lateral grip: the authored grip/latch pile (Slerp, drift latch,
        /// counter-strafe detection, strafe-grip scale, hemisphere fade) is gone, replaced
        /// by ONE physical model — a saturating lateral-grip (tire) force on the slip angle.
        /// Cornering weight, speed-dependent turn radius, and the drift itself all EMERGE.
        ///
        /// PHASE B — yaw inertia: the heading is no longer instant. It carries rotational
        /// mass (yawInertia) and is driven by two torques — the player's steering torque
        /// and a directional-stability (weathervane) torque that always pulls the nose back
        /// toward the velocity. That aligning torque scales with speed² (∝ dynamic pressure,
        /// like a real fin) and does NOT saturate, so spins are structurally impossible (the
        /// nose can't out-run the velocity) AND fast yaw is genuinely heavy while slow yaw
        /// stays free. The controller applies the returned yawRate; pitch & roll stay
        /// kinematic.
        ///
        /// Math equivalences used to drop the transform: Unity's TransformDirection /
        /// InverseTransformDirection are rotation-only (scale/position independent), so
        /// heading * v and Quaternion.Inverse(heading) * v match them exactly.
        /// </summary>
        public MotionState Step(in MotionIntent intent, in MotionTunables k, float dt)
        {
            Vector3 velocity = intent.velocity;

            // speedT: 0 at rest → 1 at turnResistanceMaxSpeed. Used only to scale strafe
            // thrust with speed (the intentional "strafe gets stronger fast" feature).
            float speedT = Mathf.Clamp01(velocity.magnitude / Mathf.Max(1f, k.turnResistanceMaxSpeed));

            // Captured from the grip block below, then consumed by the yaw dynamics: the
            // directional-stability torque needs the signed slip and the speed it was
            // measured at (it grows with speed², so high-speed yaw gets genuinely heavy).
            float slipForYaw  = 0f;   // signed slip angle this tick (deg) — nose lead over velocity
            float speedForYaw = 0f;   // horizontal speed (m/s) at the slip measurement
            bool  gripLive    = false;

            if (IsRedirecting)
            {
                // Brake-release redirect owns velocity outright; no slide while boosting.
                velocity    = Tick(dt);
                _commitment = 0f;
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
                // Strafe damping fades out as the slide commits (×(1−commitment)) so a
                // committed drift keeps its lateral momentum instead of having it bled
                // away. No drift boolean is consulted — just the live commitment.
                Vector3 localVel = Quaternion.Inverse(intent.heading) * velocity;
                if (Mathf.Abs(intent.localThrust.x) > 1e-5f)
                    localVel.x -= (1f - _commitment) * k.strafeDrag * localVel.x * Mathf.Abs(localVel.x) * dt;
                if (Mathf.Abs(intent.localThrust.y) > 1e-5f)
                    localVel.y -= k.hoverDrag * localVel.y * Mathf.Abs(localVel.y) * dt;
                velocity = intent.heading * localVel;

                // ── Lateral grip (the one real model) ─────────────────────────────
                // The slip angle β is the horizontal angle between where the nose points
                // and where the ship is actually moving. A tire makes a lateral force
                // roughly proportional to β, up to a friction budget (maxGripForce) — past
                // that it saturates and slides. That force rotates the velocity toward the
                // heading at rate ω = F / speed. Because ω falls as speed rises, fast turns
                // arc wider and break loose sooner — with NO speed knob. Drift is simply
                // velocity lagging heading: yaw the nose (or strafe) faster than ω can
                // realign and the gap — the slide — opens on its own.
                Vector3 horizVel = new Vector3(velocity.x, 0f, velocity.z);
                float   horizSpeed = horizVel.magnitude;
                Vector3 horizFwd   = Vector3.ProjectOnPlane(intent.heading * Vector3.forward, Vector3.up);

                if (!intent.barrelRolling && horizSpeed >= k.steeringMinSpeed && horizFwd.sqrMagnitude > 1e-4f)
                {
                    horizFwd.Normalize();
                    Vector3 velDir = horizVel / horizSpeed;

                    float slipDeg = Vector3.SignedAngle(velDir, horizFwd, Vector3.up); // signed: +slip ↔ rotate velocity +θ onto heading
                    float beta    = Mathf.Abs(slipDeg) * Mathf.Deg2Rad;                // slip magnitude (rad)
                    float force   = Mathf.Min(k.lateralGrip * beta, k.maxGripForce);   // saturating tire curve
                    float omega   = force / horizSpeed;                                // rad/s the velocity realigns

                    // The grip/weathervane only makes sense in the FORWARD hemisphere. The
                    // model assumes the nose should point along the velocity; that's only true
                    // while moving roughly forward. Reverse thrust drives the velocity ~180°
                    // from the nose, where (a) the grip would curl that backward velocity into
                    // a circle and (b) the slip-proportional weathervane would slam the nose
                    // around to "face where it's going" — and at exactly 180° the slip SIGN is
                    // numerically ambiguous, so it picks a side and spins. Real, but wrong for a
                    // ship with reverse thrusters. So fade both by how forward we're actually
                    // moving: cos(slip) = +1 ahead, 0 sideways, ≤0 reversing → no grip, no spin.
                    float forwardness = Vector3.Dot(velDir, horizFwd);                 // cos(slip)
                    float rearFade    = Mathf.Clamp01(forwardness);                    // 1 forward → 0 at/over 90° slip

                    // Active strafe is ALSO commanded velocity, not a slip to correct (it would
                    // otherwise creep forward + stay dead-slow). To the extent strafe is held the
                    // grip neither rotates that lateral velocity onto the nose nor weathervanes
                    // toward it. Release → authority returns, so cornering slip & drift are intact.
                    float strafeHold    = Mathf.Clamp01(Mathf.Abs(intent.localThrust.x));
                    float gripAuthority = (1f - strafeHold) * rearFade;

                    float stepRad = Mathf.Min(beta, omega * dt) * gripAuthority;       // clamp so it never overshoots heading

                    Vector3 steeredHoriz = Quaternion.AngleAxis(
                        stepRad * Mathf.Rad2Deg * Mathf.Sign(slipDeg), Vector3.up) * horizVel;
                    velocity = steeredHoriz + Vector3.up * velocity.y;                 // lateral only → speed-preserving

                    // "Drifting" physically means the tyre has BROKEN LOOSE — slip past the
                    // breakaway angle where the grip force saturates (beta_break =
                    // maxGripForce / lateralGrip). Below breakaway the tyre is still gripping,
                    // i.e. an ordinary cornering slip — and a hard yaw alone already sits right
                    // around breakaway, which is why the old beta/peakSlipAngle read saturated
                    // and tilted the mesh just from YAWING. Measuring commitment as slip BEYOND
                    // breakaway keeps gripping corners at 0 and only leans on a genuine slide.
                    // peakSlipAngle is the slip past breakaway that reads as fully committed.
                    float breakawayBeta = k.maxGripForce / Mathf.Max(1e-4f, k.lateralGrip);
                    float overslip      = beta - breakawayBeta;
                    _commitment = Mathf.Clamp01(overslip / Mathf.Max(1e-4f, k.peakSlipAngle)) * rearFade;
                    _driftDir   = Mathf.Sign(slipDeg);

                    slipForYaw  = slipDeg * gripAuthority;  // commanded (strafe/reverse) slip must NOT weathervane the nose
                    speedForYaw = horizSpeed;
                    gripLive    = true;
                }
                else
                {
                    // Below steering speed / mid-roll / degenerate heading: no slip to read.
                    _commitment = 0f;
                }

                // ── Braking: linear decel toward zero, scaled by pressure ─────────
                if (intent.brakePressure > 0f)
                    velocity = Vector3.MoveTowards(
                        velocity, Vector3.zero, k.brakeForce * intent.brakePressure * dt);

                // ── Drift reward: charge while sliding, burst on exit ──────────────
                // A committed slide (commitment past the arm threshold) stores momentum
                // the grip would otherwise scrub; integrate it into a charge. The moment
                // the tyres hook back up and the slide collapses, spend that charge as a
                // forward burst along the NEW heading: you leave the corner pointing where
                // you steered with MORE speed than you carried in. The burst reuses the
                // brake-boost blend, and the global maxSpeed clamp caps it — so it's a
                // brief, earned overspeed that drag then bleeds back to cruise.
                //
                // Suspended while braking: the brake owns deceleration and has its own
                // release boost, and braking can drive commitment to zero on its own —
                // we don't want that to read as a "drift exit" and fire a forward kick
                // straight into the brake. The charge simply holds until the brake lifts.
                if (intent.braking)
                {
                    /* hold charge; brake release path handles the payout/clear */
                    _wasStrafing = false;   // brake interrupts the slide; don't fire a stale release on lift
                }
                else
                {
                    bool strafing = Mathf.Abs(intent.localThrust.x) > StrafeDeadzone;

                    // Charge while the slide is genuinely committed (however it's steered).
                    if (_commitment > DriftArmCommit)
                    {
                        _driftCharge += _commitment * dt;   // weighted by how hard the slide is
                        _driftArmed   = true;
                    }

                    // PRIMARY exit cue: you LET GO of the strafe you were holding to keep the
                    // slide open. Holding strafe suppresses grip authority, so the release is the
                    // exact moment grip re-engages and swings the still-sideways velocity back
                    // onto the nose — firing the boost there makes it a genuine re-direction, in
                    // lock-step with the physics rather than chasing a decay curve.
                    bool strafeReleased = _wasStrafing && !strafing;
                    // FALLBACK for a slide steered by yaw alone (no strafe to release): pay out
                    // once the slip has essentially closed on its own.
                    bool resolved       = _commitment < DriftReleaseFloor;

                    if (_driftArmed && (strafeReleased || resolved))
                    {
                        if (_driftCharge >= k.driftBoostMinCharge)
                        {
                            float bonus = Mathf.Min(_driftCharge * k.driftBoostGain, k.driftBoostMaxSpeed);
                            // Boost direction is the NOSE (heading), flattened to horizontal —
                            // explicitly NOT the current momentum direction.
                            Vector3 fwd = Vector3.ProjectOnPlane(intent.heading * Vector3.forward, Vector3.up);
                            if (fwd.sqrMagnitude > 1e-4f)
                            {
                                fwd.Normalize();
                                float speedNow = new Vector3(velocity.x, 0f, velocity.z).magnitude;
                                Vector3 target = fwd * (speedNow + bonus) + Vector3.up * velocity.y;
                                StartBoost(velocity, target, k.driftBoostSnap); // redirects onto the nose; clears charge
                            }
                        }
                        _driftCharge = 0f;
                        _driftArmed  = false;
                    }

                    _wasStrafing = strafing;
                }
            }

            // ── Yaw-plane dynamics: τ = I·α, integrated with rotational damping ───
            // Two torques act on the heading's rotational mass:
            //   • steerTau  — the player's commanded yaw rate as a steering torque.
            //   • alignTau  — a directional-stability (weathervane) torque that ALWAYS
            //                 opposes the slip, pulling the nose back toward the velocity.
            // alignTau scales with speed² and is NON-saturating: a real fin's restoring
            // moment grows with dynamic pressure (∝ v²), so the faster you go the harder
            // the airframe fights any yaw that opens a slip angle. (The earlier version
            // tied this to the lateral-grip force, which saturates at maxGripForce — so
            // above a low speed the resistance went flat and fast yaw felt free. Decoupling
            // it and giving it the v² law is what restores "heavy yaw at speed".) Because
            // it opposes slip, the nose can never out-run the velocity → spins are
            // structurally impossible. Runs every tick (at a standstill alignTau is 0, so
            // you can still pivot in place).
            float steerTau = intent.yawCommand * k.steerTorque;
            float alignTau = 0f;
            if (gripLive)
            {
                float speedN = speedForYaw / Mathf.Max(1f, k.turnResistanceMaxSpeed); // 0..~1+ normalized speed
                alignTau = -k.alignTorque * speedN * speedN * slipForYaw;             // v²·slip, opposes the slip
            }
            float yawAccel = (steerTau + alignTau) / Mathf.Max(1e-4f, k.yawInertia);
            _yawRate += yawAccel * dt;
            _yawRate *= Mathf.Exp(-k.yawDamping * dt);

            velocity = Vector3.ClampMagnitude(velocity, k.maxSpeed);
            return new MotionState(velocity, _yawRate, _commitment > 0.5f, _driftDir, _commitment);
        }
    }
}
