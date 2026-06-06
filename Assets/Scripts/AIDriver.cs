using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// AI racer brain. Drives the ship by producing the EXACT same input a player would
    /// (<see cref="SpaceshipController.ApplyInput"/>) — never touching velocity or the transform directly —
    /// so it's multiplayer-safe by construction and handles identically to the player (same grip, inertia,
    /// drift, collisions).
    ///
    /// Phase 1 ("drive clean"): pure-pursuit steering toward a point down the racing line, but with three
    /// things the naive follower lacked —
    ///   • CORNER ANTICIPATION — it reads how much the line bends ahead and sets a target speed, braking
    ///     BEFORE the corner instead of understeering into the wall.
    ///   • PD STEERING — the yaw command is damped by the ship's actual yaw rate, so it stops oscillating
    ///     against the heavy weathervane inertia.
    ///   • STUCK RECOVERY — if it bogs down (wall, corner), it reverses and reorients, then carries on.
    ///
    /// Put this on the ship root in place of <see cref="SpaceshipInput"/> (keep SpaceshipController,
    /// MomentumSystem, RaceParticipant). It honours the countdown lock the same way the player input does.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SpaceshipController))]
    public class AIDriver : MonoBehaviour
    {
        [Header("Racing line")]
        [Tooltip("The centreline (for 'where am I' progress). Auto-found if empty.")]
        public RaceSpline track;
        [Tooltip("Optional apex racing line to FOLLOW. Auto-found if empty; if there's none, the AI just " +
                 "follows the centreline.")]
        public RacingLine racingLine;

        [Header("Look-ahead (pure pursuit, dynamic)")]
        [Min(1f)]
        [Tooltip("Look-ahead on STRAIGHTS (m) — long, so it tracks smoothly without weaving.")]
        public float lookAheadStraight = 40f;
        [Min(1f)]
        [Tooltip("Look-ahead in tight CORNERS (m) — short, so it aims at the apex instead of cutting across it.")]
        public float lookAheadCorner = 14f;
        [Min(0f)]
        [Tooltip("Extra look-ahead per m/s of speed, so it looks a little further when fast.")]
        public float lookAheadPerSpeed = 0.18f;
        [Min(1f)]
        [Tooltip("How far ahead (m) curvature is SENSED. This one signal drives the look-ahead shortening, the " +
                 "corner-speed braking, and the drift trigger — so they all agree on where a corner is.")]
        public float curveProbe = 30f;
        [Min(1f)]
        [Tooltip("Upcoming bend (degrees over the probe) at which look-ahead is fully shortened to the corner value.")]
        public float lookAheadCurveBend = 45f;

        [Header("Steering (PD)")]
        [Tooltip("Yaw rate commanded per degree of heading error (1/s). Higher = sharper turn-in.")]
        public float yawGain = 5.5f;
        [Tooltip("Damps the yaw command by the ship's actual yaw rate — kills oscillation. Higher = calmer, " +
                 "but too high makes it lazy to turn.")]
        public float yawDamping = 0.35f;
        [Tooltip("Cap on commanded yaw rate (deg/s).")]
        public float maxYawRate = 175f;

        [Tooltip("Also chase the line's elevation with pitch. Off = flat-ish tracks (hover holds altitude).")]
        public bool usePitch = false;
        public float pitchGain = 3f;
        public float maxPitchRate = 80f;

        [Header("Corner speed control")]
        [Range(0f, 1f)]
        [Tooltip("Base throttle on a straight. 1 = pinned. Lower for a slower opponent.")]
        public float throttle = 1f;
        [Tooltip("Slowest it will go through the tightest corners (m/s).")]
        public float minCornerSpeed = 90f;
        [Tooltip("Line-bend over the look-ahead (degrees) that calls for the FULL slow-down. Smaller = it " +
                 "treats gentle bends as corners and slows earlier/more.")]
        public float bendForFullSlow = 55f;
        [Range(0f, 1f)]
        [Tooltip("Max REVERSE thrust it pushes (the gradual L2-style slow-down) when well over the corner's " +
                 "target speed. It eases off throttle first, then feeds reverse to bleed speed over time — " +
                 "NOT the L1 handbrake, which is a snappy drift/redirect tool and wrong for cornering.")]
        public float slowdownReverse = 0.8f;

        [Header("Line keeping")]
        [Range(0f, 0.2f)] [Tooltip("Strafe authority used to trim sideways drift back onto the line.")]
        public float lateralTrim = 0.06f;
        [Range(0f, 0.3f)] [Tooltip("Hover authority used to hold the line's height.")]
        public float verticalTrim = 0.1f;

        [Header("Wall avoidance (whiskers)")]
        [Tooltip("Cast feeler rays out the front/sides and steer away from walls — a REACTIVE safety layer ON " +
                 "TOP of the racing-line follow. A good line still clips walls when it overshoots, the corridor " +
                 "narrows, or a rival shoves it wide; this catches that. Reads the world as the AI's 'eyes' → it " +
                 "only ever feeds ApplyInput, never touches physics, so it stays multiplayer-safe.")]
        public bool avoidWalls = true;
        [Tooltip("Which layers count as walls/obstacles. Set to your track's collision layer(s) — and make sure " +
                 "it does NOT include the ships themselves, or they'll swerve off each other (that's Phase 4).")]
        public LayerMask wallMask = ~0;
        [Min(1)]
        [Tooltip("Feeler rays PER SIDE (plus one straight ahead). 3 → 7 rays total. More = smoother sensing.")]
        public int feelersPerSide = 3;
        [Range(5f, 90f)]
        [Tooltip("Angle of the OUTERMOST feeler from straight-ahead (deg). Wider = notices walls more to the side.")]
        public float feelerSpread = 60f;
        [Min(1f)]
        [Tooltip("Base feeler reach (m). Longer = reacts to walls earlier.")]
        public float feelerLength = 45f;
        [Min(0f)]
        [Tooltip("Extra feeler reach per m/s of speed — look further when fast so it turns/brakes in time.")]
        public float feelerSpeedScale = 0.15f;
        [Tooltip("Vertical offset of the feeler origin (m) so rays leave from the hull, not the floor.")]
        public float feelerHeight = 1f;
        [Tooltip("Yaw authority added when steering away from a wall (deg/s per unit proximity). The main escape.")]
        public float avoidYawGain = 140f;
        [Range(0f, 1f)]
        [Tooltip("Strafe added when shoving away from a wall — more immediate than yaw given the heavy yaw inertia.")]
        public float avoidStrafe = 0.7f;
        [Range(0f, 1f)]
        [Tooltip("How much avoidance reacts ONLY to walls it's CLOSING on, vs any nearby wall. 1 = pure closing — " +
                 "it ignores walls it's passing PARALLEL to (e.g. the inside wall at a corner apex), so it stops " +
                 "fighting a clean apex line. 0 = react to any nearby wall (old behavior). THE fix for over-" +
                 "correcting / wobbling through tight corners.")]
        public float avoidApproachOnly = 0.85f;
        [Tooltip("When a wall is dead ahead, scrub speed (L2-style) proportionally so it doesn't plow straight in.")]
        public bool avoidBrakeFront = true;
        [Range(5f, 89f)]
        [Tooltip("Surfaces tilted UP TO this much from flat (deg) are read as a climbable RAMP, not a wall — the " +
                 "feelers pitch the nose UP to drive up them instead of swerving/braking. Steeper than this is a " +
                 "wall. Also lets it ride banked corners. A front feeler hitting a rising ramp ('-/') triggers this.")]
        public float maxClimbAngle = 50f;
        [Tooltip("Pitch-up authority when a feeler hits a climbable ramp ahead (deg/s per unit proximity). Works " +
                 "even with 'usePitch' off — it's a reactive climb, separate from line-elevation pitch following.")]
        public float avoidPitchGain = 90f;

        [Header("Racer awareness (Phase 4)")]
        [Tooltip("Sense the OTHER ships and race them — step aside to pass a slower car, tuck in behind when it " +
                 "can't pass, and don't grind side-by-side. Reads their public position/velocity only → feeds " +
                 "ApplyInput, so it stays multiplayer-safe and treats the human player as just another rival. " +
                 "NOTE: keep the ship layer OUT of wallMask, or the wall feelers would also react to ships and " +
                 "double up with this.")]
        public bool avoidRacers = true;
        [Tooltip("Only ships within this radius (m) are considered at all.")]
        public float racerSenseRadius = 60f;
        [Tooltip("How far AHEAD (m) a car triggers an overtake step-aside. Closer ahead = stronger.")]
        public float overtakeRange = 45f;
        [Tooltip("Lateral 'in my path' tolerance (m) — a car ahead within this side-band counts as blocking.")]
        public float overtakeOffset = 8f;
        [Tooltip("Closing speed (m/s) over the car ahead below which it gives up passing and matches speed instead.")]
        public float overtakeMinClosing = 5f;
        [Tooltip("Distance (m) at which a nearby car triggers a sideways shove so they don't grind together.")]
        public float sideClearance = 7f;
        [Range(0f, 1f)]
        [Tooltip("Strafe authority for racer interactions (both the pass step-aside and the anti-grind shove).")]
        public float racerPushStrafe = 0.6f;
        [Tooltip("If stuck behind a car it can't pass, ease throttle to tuck in behind rather than rear-end it.")]
        public bool matchWhenBlocked = true;

        [Header("Difficulty — skill")]
        [Tooltip("Scale how fast/committed this AI drives. The cheapest way to make a BEATABLE opponent: a lower-" +
                 "skill car genuinely drives slower, so it loses ground you can take back. Toggle off for full pace.")]
        public bool useDifficulty = true;
        [Range(0f, 1f)]
        [Tooltip("1 = full pace (the tuned 'Pro' you dialed in). 0 = beginner — slower top speed and more cautious " +
                 "corners. Set per-car for a mixed grid (one 0.9, one 0.7, one 0.5…).")]
        public float skill = 0.85f;
        [Range(0f, 1f)]
        [Tooltip("Straight-line throttle at skill 0 (beginner). Skill 1 uses the full 'throttle' above.")]
        public float skillMinThrottle = 0.7f;
        [Range(0.3f, 1f)]
        [Tooltip("Corner-speed scale at skill 0 — low skill corners more cautiously/slowly.")]
        public float skillMinCornerScale = 0.8f;

        [Header("Mistakes — imperfection")]
        [Tooltip("Inject human-like errors so the field isn't flawless and you (or a rival) can capitalize. Toggle " +
                 "off for a clean pace car. Each car fumbles on its OWN seeded schedule, so they desync naturally.")]
        public bool useMistakes = true;
        [Min(0.5f)]
        [Tooltip("Average seconds between fumbles (a throttle lift or a late brake). Lower = clumsier.")]
        public float mistakeInterval = 7f;
        [Range(0f, 1f)]
        [Tooltip("How bad each fumble is, AND how much the line constantly wanders. 0 = none, 1 = big lifts / " +
                 "blown apexes / loose line. This is what turns 'perfect clones' into a field with openings.")]
        public float mistakeIntensity = 0.5f;
        [Tooltip("Seed so each car's mistakes differ but stay reproducible. 0 = auto (derived from the instance).")]
        public int mistakeSeed = 0;

        [Header("Stuck recovery")]
        [Tooltip("Below this speed (m/s) it's considered bogged down.")]
        public float stuckSpeed = 14f;
        [Tooltip("Seconds bogged down before it triggers a recovery.")]
        public float stuckTime = 1.1f;
        [Tooltip("Seconds the recovery (reverse + reorient) runs.")]
        public float recoverDuration = 0.9f;
        [Range(0f, 1f)] [Tooltip("Reverse throttle used during recovery.")]
        public float reverseThrottle = 0.7f;

        [Header("Boost pads")]
        [Tooltip("Detour onto nearby speed-boost pads to grab them.")]
        public bool seekBoosts = true;
        [Tooltip("How far AHEAD on the line (metres) to start looking for a pad to grab.")]
        public float boostLookAhead = 55f;
        [Tooltip("Furthest a pad can sit from the line (metres) before it's not worth the detour.")]
        public float boostMaxDetour = 14f;

        [Header("Drift (experimental)")]
        [Tooltip("Let the AI drift tight corners — over-rotate the nose while holding strafe to break grip, " +
                 "then release at the exit to fire the drift-boost. Tunable; turn off if it's slower than a " +
                 "clean line.")]
        public bool enableDrift = false;
        [Tooltip("Line-bend (degrees) over the look-ahead needed to commit to a drift. Higher = only the " +
                 "tightest corners.")]
        public float driftBend = 30f;
        [Tooltip("Won't drift below this speed (m/s) — slow corners are faster taken clean.")]
        public float driftMinSpeed = 100f;
        [Range(0f, 1f)]
        [Tooltip("Strafe magnitude held during the drift — this is what breaks grip and charges the boost. " +
                 "Its RELEASE at the exit is what fires the drift-boost.")]
        public float driftStrafe = 0.85f;
        [Tooltip("Hold the strafe INTO the turn (toward the apex) vs AWAY from it. This is THE knob to find " +
                 "what actually slides on your tuning — into-turn can read as a planted 'carve', away can kick " +
                 "the tail out. Try both.")]
        public bool driftStrafeIntoTurn = true;
        [Tooltip("Yaw multiplier while drifting — over-rotates the nose so the velocity lags into a real slide.")]
        public float driftYawMul = 1.8f;
        [Tooltip("Look-ahead multiplier while drifting, so the nose aims at the corner EXIT.")]
        public float driftLookAheadMul = 1.6f;
        [Tooltip("Release the drift (fire the boost) once the nose is within this many degrees of the exit aim.")]
        public float driftExitYawError = 12f;
        [Tooltip("Safety: bail out of a drift after this long (seconds).")]
        public float driftMaxTime = 1.2f;
        [Tooltip("Minimum gap (seconds) between drifts, so the release boost has room to pay off.")]
        public float driftCooldownTime = 0.8f;

        [Header("Debug visualization")]
        [Tooltip("Master switch for the on-track gizmos below.")]
        public bool drawDebug = true;
        [Tooltip("Draw the point the AI is steering at, plus the ship→aim line (cyan).")]
        public bool drawAim = true;
        [Tooltip("Forward-SIMULATE the pure-pursuit and draw the line it'll trace (yellow). Updates live as " +
                 "you tweak look-ahead / yaw gain — even while paused — so you can shape it to the track. " +
                 "Geometric only (ignores yaw inertia), so the real line lags it a touch.")]
        public bool drawPrediction = true;
        [Min(2)] public int predictSteps = 80;
        [Min(0.005f)] public float predictStep = 0.04f;
        [Min(0.5f)]
        [Tooltip("How fast the PREDICTED heading responds to steering (1/s) — its stand-in for the ship's yaw " +
                 "inertia. Lower = laggier/more overshoot. Match it to your ship's feel so the yellow line's " +
                 "oscillation mirrors the real (green) one; then tuning yellow smooth tunes the ship smooth.")]
        public float predictTurnResponse = 7f;
        [Tooltip("Draw a trail of where the AI ACTUALLY drove (green) — the ground truth, shows oscillation.")]
        public bool drawTrail = true;
        [Min(8)] public int trailLength = 256;
        [Tooltip("Draw the wall-avoidance feelers — green = clear, red = hit (with the hit point). Tune the " +
                 "spread/length until the fan covers the corridor and lights up BEFORE the nose reaches a wall.")]
        public bool drawFeelers = true;
        [Tooltip("Draw a magenta line to each rival ship currently influencing the AI (passing / avoiding).")]
        public bool drawRacers = true;
        [Tooltip("Float a marker over a car mid-fumble — yellow = throttle lift, red = late/missed brake — so " +
                 "you can SEE the mistakes happen (and tune mistakeInterval / mistakeIntensity to taste).")]
        public bool drawMistakes = true;

        SpaceshipController   _controller;
        RaceParticipant       _participant;
        SpaceboostPickup[]    _pads;
        SpaceshipController[]  _others;        // every other ship (rivals + the player), cached at Awake
        float _stuckTimer;
        float _recoverTimer;
        bool  _drifting;
        float _driftTimer;
        float _driftCooldown;
        float _driftSign;       // which way we hold strafe during the current drift

        // Debug viz state (filled in Update, drawn in OnDrawGizmos).
        Vector3   _dbgAim, _dbgLine;
        bool      _dbgHasAim;
        Vector3[] _trail;
        int       _trailHead, _trailCount;

        // Wall-feeler state (filled by SenseWalls, drawn in OnDrawGizmos).
        Vector3   _feelOrigin;
        Vector3[] _feelDir;
        float[]   _feelLen;
        float[]   _feelHitDist;   // per feeler: distance to hit, or -1 = no hit
        bool[]    _feelRamp;      // per feeler: was the hit a climbable ramp (vs a wall)?
        int       _feelN;

        // Racer-awareness debug (rivals influencing the AI this frame).
        Vector3[] _dbgRacers;
        int       _dbgRacerCount;

        // Difficulty / mistakes state.
        System.Random _rng;
        float _noiseSeed;      // Perlin phase for this car's line wander
        float _nextFumble;     // countdown to the next fumble
        float _fumbleTimer;    // >0 = mid-fumble
        int   _fumbleType;     // 0 = throttle lift, 1 = late brake
        int   _dbgFumble;      // 0 none / 1 lift / 2 brake (for the gizmo)

        void Awake()
        {
            _controller  = GetComponent<SpaceshipController>();
            _participant = GetComponent<RaceParticipant>();
            if (track == null) track = FindFirstObjectByType<RaceSpline>();
            if (racingLine == null) racingLine = FindFirstObjectByType<RacingLine>();
            _pads = FindObjectsByType<SpaceboostPickup>(FindObjectsSortMode.None);

            // Cache every OTHER ship (rivals + the player) for racer awareness. Grid is set at race start,
            // so an Awake snapshot is enough; if ships spawn later (e.g. networked join), call RefreshRivals().
            var ships = FindObjectsByType<SpaceshipController>(FindObjectsSortMode.None);
            int c = 0;
            foreach (var s in ships) if (s != null && s != _controller) c++;
            _others = new SpaceshipController[c];
            int k = 0;
            foreach (var s in ships) if (s != null && s != _controller) _others[k++] = s;
            _dbgRacers = new Vector3[Mathf.Max(1, c)];

            // Per-car deterministic RNG for mistakes, so each AI fumbles on its own reproducible schedule.
            int seed = mistakeSeed != 0 ? mistakeSeed : GetInstanceID();
            _rng        = new System.Random(seed);
            _noiseSeed  = (float)(_rng.NextDouble() * 1000.0);
            _nextFumble = NextFumbleGap();
        }

        // Random gap (s) until the next fumble: 0.5×–1.5× the average interval.
        float NextFumbleGap() => mistakeInterval * (0.5f + (float)_rng.NextDouble());

        /// <summary>Re-snapshot the rival ships — call if ships are spawned after Awake (e.g. networked join).</summary>
        public void RefreshRivals()
        {
            var ships = FindObjectsByType<SpaceshipController>(FindObjectsSortMode.None);
            int c = 0;
            foreach (var s in ships) if (s != null && s != _controller) c++;
            _others = new SpaceshipController[c];
            int k = 0;
            foreach (var s in ships) if (s != null && s != _controller) _others[k++] = s;
            _dbgRacers = new Vector3[Mathf.Max(1, c)];
        }

        // The line the AI FOLLOWS — the apex racing line if one exists, otherwise the centreline. Progress
        // ("where am I") always comes from the centreline (track.GetProgress) regardless.
        Vector3 LinePoint(float t01) => racingLine != null && racingLine.IsValid ? racingLine.GetPoint(t01) : track.GetPoint(t01);
        Vector3 LineDir(float t01)   => racingLine != null && racingLine.IsValid ? racingLine.GetDirection(t01) : track.GetDirection(t01);

        // Dynamic look-ahead as a 0..1 progress fraction: long on straights, short in corners (+ a bit per
        // speed). Also hands back `bend` — the upcoming curvature over the probe — which the corner-speed and
        // drift logic reuse, so everything agrees on where the corner is. Shared by Update and the gizmo sim.
        float LookAheadFr(float t, float speed, out float bend)
        {
            float len = Mathf.Max(1f, track.TotalLength);
            bend = Vector3.Angle(LineDir(t), LineDir(Mathf.Repeat(t + curveProbe / len, 1f)));
            float curveT  = Mathf.Clamp01(bend / Mathf.Max(1f, lookAheadCurveBend));
            float lookDist = Mathf.Lerp(lookAheadStraight, lookAheadCorner, curveT) + speed * lookAheadPerSpeed;
            return lookDist / len;
        }

        // The nearest READY pad that sits just ahead on the line and close enough to detour for. null = none
        // worth grabbing right now (so we just stay on the line).
        SpaceboostPickup PickTargetPad(float myT)
        {
            if (_pads == null) return null;
            float lineLen = Mathf.Max(1f, track.TotalLength);
            SpaceboostPickup best = null;
            float bestAhead = float.MaxValue;
            foreach (var pad in _pads)
            {
                if (pad == null || !pad.isActiveAndEnabled || !pad.IsReady) continue;
                Vector3 pp     = pad.transform.position;
                float   padT   = track.GetProgress(pp);
                float   aheadM = Mathf.Repeat(padT - myT, 1f) * lineLen;     // metres ahead around the loop
                if (aheadM < 1f || aheadM > boostLookAhead) continue;        // on it/behind, or too far ahead
                Vector3 lineAtPad = LinePoint(padT);
                float   lateral = Vector3.Distance(new Vector3(pp.x, 0f, pp.z),
                                                   new Vector3(lineAtPad.x, 0f, lineAtPad.z));
                if (lateral > boostMaxDetour) continue;                     // too far off the line to chase
                if (aheadM < bestAhead) { bestAhead = aheadM; best = pad; }
            }
            return best;
        }

        // Decide if we're mid-drift this frame. ENTER on a tight-enough, fast-enough corner; while drifting we
        // over-rotate + hold strafe (set up in Update). EXIT — which RELEASES the strafe and fires the drift-
        // boost — once the nose has swung onto the exit aim, the corner's done, or the safety timer trips.
        void UpdateDrift(float bend, float speed, float yawErr, float dt)
        {
            _driftCooldown = Mathf.Max(0f, _driftCooldown - dt);

            if (_drifting)
            {
                _driftTimer += dt;
                bool aligned    = Mathf.Abs(yawErr) < driftExitYawError;   // nose now points at the exit
                bool cornerDone = bend < driftBend * 0.5f;                  // out of the corner
                bool timeout    = _driftTimer > driftMaxTime;
                if (aligned || cornerDone || timeout)
                {
                    _drifting = false;
                    _driftCooldown = driftCooldownTime;   // hold off re-entering so the boost can pay off
                }
            }
            else if (_driftCooldown <= 0f && bend > driftBend && speed > driftMinSpeed)
            {
                _drifting   = true;
                _driftTimer = 0f;
                _driftSign  = (yawErr >= 0f ? 1f : -1f) * (driftStrafeIntoTurn ? 1f : -1f);   // into / away from turn
            }
        }

        // Whisker perception: cast a symmetric fan of feelers (one straight ahead + feelersPerSide each side)
        // and return a signed STEER command (+ = steer right, away from a wall on the left). `frontBlock`
        // (0..1) reports how blocked it is dead-ahead, for speed-scrubbing. This only READS the world and
        // feeds ApplyInput — it's the AI's eyes, never a physics write — so it stays multiplayer-safe.
        float SenseWalls(Vector3 pos, Vector3 fwdFlat, Vector3 velDir, float speed, out float frontBlock, out float pitchUp)
        {
            frontBlock = 0f;
            pitchUp    = 0f;
            int perSide = Mathf.Max(1, feelersPerSide);
            int n = perSide * 2 + 1;
            if (_feelDir == null || _feelDir.Length != n)
            {
                _feelDir = new Vector3[n]; _feelLen = new float[n];
                _feelHitDist = new float[n]; _feelRamp = new bool[n];
            }
            _feelN = n;

            float   len    = feelerLength + speed * feelerSpeedScale;
            Vector3 origin = pos + Vector3.up * feelerHeight;
            Vector3 fwd    = fwdFlat.sqrMagnitude > 1e-5f ? fwdFlat.normalized : transform.forward;
            _feelOrigin    = origin;

            float steer     = 0f;
            float leftClear = 1f, rightClear = 1f;   // nearest hit fraction (1 = fully open) on each side

            for (int i = 0; i < n; i++)
            {
                int   side = i - perSide;                          // -perSide..+perSide, 0 = straight ahead
                float a    = (float)side / perSide * feelerSpread; // signed angle from forward (deg)
                Vector3 dir = Quaternion.AngleAxis(a, Vector3.up) * fwd;

                _feelDir[i] = dir; _feelLen[i] = len; _feelHitDist[i] = -1f; _feelRamp[i] = false;

                if (!Physics.Raycast(origin, dir, out RaycastHit info, len, wallMask, QueryTriggerInteraction.Ignore))
                    continue;

                _feelHitDist[i] = info.distance;
                float frac = info.distance / len;                  // 0 touching → 1 at max reach
                float prox = 1f - frac;
                prox *= prox;                                      // bite harder the closer the surface is
                float weight = 1f - 0.5f * Mathf.Abs(side) / perSide;   // front rays count for more than side ones

                // Classify by surface normal. A floor-ish tilt is a RAMP to climb, NOT a wall to dodge — a flat
                // feeler hits a rising ramp dead ahead ('-/'), but the right move is to pitch UP and drive up it,
                // not swerve or brake. Steeper than maxClimbAngle = a real wall (also lets it ride banked turns).
                float slope = Vector3.Angle(info.normal, Vector3.up);
                if (slope <= maxClimbAngle)
                {
                    _feelRamp[i] = true;
                    float climbT = Mathf.Clamp01(slope / Mathf.Max(1f, maxClimbAngle));
                    pitchUp = Mathf.Max(pitchUp, prox * climbT * weight);   // nose up, proportional to rise + nearness
                    continue;                                              // ramps never steer or brake
                }

                // Only react to a wall we're CLOSING on — a wall we're passing PARALLEL to (the inside apex
                // wall mid-corner) isn't a threat, and reacting to it shoves us off the racing line. The hit
                // normal points out of the wall toward us, so -dot(velocity, normal) is how head-on we are.
                float closing  = Mathf.Clamp01(-Vector3.Dot(velDir, info.normal));
                float approach = Mathf.Lerp(1f, closing, avoidApproachOnly);
                float react    = prox * approach;

                if (side > 0)      rightClear = Mathf.Min(rightClear, frac);
                else if (side < 0) leftClear  = Mathf.Min(leftClear,  frac);

                if (side == 0) frontBlock = Mathf.Max(frontBlock, react);     // wall dead ahead
                else           steer += -Mathf.Sign(a) * react * weight;      // shove away from the hit side
            }

            // Dead-ahead wall: the angled feelers can't say which way to go, so break the tie toward the
            // side with more room (steer right when the right is clearer).
            if (frontBlock > 0f) steer += (rightClear - leftClear) * frontBlock * 1.5f;

            return steer;
        }

        // Perceive the OTHER ships and return a lateral strafe (step aside to pass + don't-grind shove) plus an
        // optional speed cap (tuck in behind a slower car we can't pass, instead of rear-ending it). Like the
        // wall feelers, this only READS rivals' public state and feeds ApplyInput — never a physics write — so
        // it's multiplayer-safe. Strafe-based on purpose: the ship keeps aiming down the line and translates
        // sideways to pass, which a hover-racer does cleanly without disturbing its heading.
        void SenseRacers(Vector3 pos, Vector3 fwdFlat, float speed, out float racerStrafe, out float speedCap)
        {
            racerStrafe = 0f;
            speedCap    = -1f;
            _dbgRacerCount = 0;
            if (_others == null) return;

            Vector3 fwdN  = fwdFlat.sqrMagnitude > 1e-5f ? fwdFlat.normalized : transform.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwdN);

            foreach (var other in _others)
            {
                if (other == null || !other.isActiveAndEnabled) continue;
                Vector3 delta = other.transform.position - pos;
                delta.y = 0f;
                float dist = delta.magnitude;
                if (dist > racerSenseRadius || dist < 0.01f) continue;

                float forwardComp = Vector3.Dot(delta, fwdN);     // + = ahead of me
                float sideComp    = Vector3.Dot(delta, right);    // + = to my right
                bool  influencing = false;

                // Anti-grind: a car alongside / very close → shove away from it (stronger the closer it is).
                if (dist < sideClearance)
                {
                    float prox = 1f - dist / sideClearance;
                    float sign = sideComp >= 0f ? -1f : 1f;       // car on the right → push left
                    racerStrafe += sign * prox * racerPushStrafe;
                    influencing = true;
                }

                // Overtake / no-rear-end: a car genuinely AHEAD and roughly in my path.
                if (forwardComp > 1f && forwardComp < overtakeRange && Mathf.Abs(sideComp) < overtakeOffset * 1.5f)
                {
                    float otherSpeed = other.currentVelocity.magnitude;
                    float closing    = speed - otherSpeed;
                    float urgency    = 1f - forwardComp / overtakeRange;   // closer ahead = stronger
                    float passSign   = sideComp >= 0f ? -1f : 1f;          // pass on the side it isn't on
                    racerStrafe += passSign * urgency * racerPushStrafe;
                    influencing = true;

                    // Can't realistically pass (not closing) and right behind it → match its speed, don't ram.
                    if (matchWhenBlocked && closing < overtakeMinClosing && Mathf.Abs(sideComp) < overtakeOffset)
                    {
                        float cap = Mathf.Max(minCornerSpeed, otherSpeed);
                        speedCap  = speedCap < 0f ? cap : Mathf.Min(speedCap, cap);
                    }
                }

                if (influencing && _dbgRacers != null && _dbgRacerCount < _dbgRacers.Length)
                    _dbgRacers[_dbgRacerCount++] = other.transform.position;
            }

            racerStrafe = Mathf.Clamp(racerStrafe, -1f, 1f);
        }

        void Update()
        {
            if (_controller == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Countdown freeze — sit dead-still until GO, exactly like SpaceshipInput.
            if (_participant != null && _participant.ControlsLocked)
            {
                _stuckTimer = _recoverTimer = 0f;
                _controller.ApplyInput(Vector3.zero, Vector3.zero, false);
                return;
            }
            if (track == null || !track.IsValid)
            {
                _controller.ApplyInput(new Vector3(0f, 0f, throttle), Vector3.zero, false);
                return;
            }

            Vector3 pos   = transform.position;
            float   speed = _controller.currentVelocity.magnitude;
            float   t     = track.GetProgress(pos);

            // ── Difficulty: scale pace by skill so a lower-skill car genuinely drives slower (beatable). ──
            float paceMul   = useDifficulty ? Mathf.Lerp(skillMinThrottle,    1f, skill) : 1f;
            float cornerMul = useDifficulty ? Mathf.Lerp(skillMinCornerScale, 1f, skill) : 1f;

            // ── Mistakes: a constant gentle line wander + occasional discrete fumbles (lift / late brake), so
            //    the field isn't a set of flawless clones and openings appear. Each car on its own RNG schedule. ──
            float wander = 0f;
            bool  liftOff = false, lateBrake = false;
            _dbgFumble = 0;
            if (useMistakes && mistakeIntensity > 0f && _rng != null)
            {
                wander = (Mathf.PerlinNoise(_noiseSeed, Time.time * 0.4f) - 0.5f) * mistakeIntensity;   // ±0.5·intensity
                _nextFumble -= dt;
                if (_fumbleTimer > 0f)
                {
                    _fumbleTimer -= dt;
                    if (_fumbleType == 0) liftOff = true; else lateBrake = true;
                    _dbgFumble = _fumbleType == 0 ? 1 : 2;
                }
                else if (_nextFumble <= 0f)
                {
                    _fumbleType  = _rng.Next(2);
                    _fumbleTimer = (0.2f + 0.5f * mistakeIntensity) * (0.6f + (float)_rng.NextDouble());
                    _nextFumble  = NextFumbleGap();
                }
            }

            // ── Dynamic look-ahead (short in corners, long on straights) + the upcoming curvature it's based
            //    on, which also feeds the corner-speed control and the drift trigger. ──
            float aheadFr = LookAheadFr(t, speed, out float bend);

            // ── Steering target: aim down the line (further while drifting, so the nose swings to the exit) ──
            Vector3 aim = LinePoint(Mathf.Repeat(t + aheadFr * (_drifting ? driftLookAheadMul : 1f), 1f));

            // Boost pads: detour onto a grabbable pad just ahead (not while drifting — finish the corner first).
            if (seekBoosts && !_drifting)
            {
                SpaceboostPickup pad = PickTargetPad(t);
                if (pad != null) aim = pad.transform.position;
            }

            Vector3 fwd     = transform.forward;
            Vector3 fwdFlat = Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (fwdFlat.sqrMagnitude < 1e-5f) fwdFlat = fwd;
            Vector3 aimFlat = Vector3.ProjectOnPlane(aim - pos, Vector3.up);
            float yawErr    = Vector3.SignedAngle(fwdFlat, aimFlat, Vector3.up);   // + = aim is right

            // ── Drift state machine: decides whether we're mid-drift this frame ──
            if (enableDrift) UpdateDrift(bend, speed, yawErr, dt);

            // ── Yaw (PD) — over-rotated while drifting to make the velocity lag into a real slide ──
            // While drifting we WANT the nose to over-rotate, so cut the damping right down (it's tuned to
            // PREVENT over-rotation — at full strength it would kill the slide before it builds).
            float yawMul    = _drifting ? driftYawMul : 1f;
            float dampMul   = _drifting ? 0.3f : 1f;
            float actualYaw = _controller.momentum != null ? _controller.momentum.YawRate : 0f;
            float yawRate   = Mathf.Clamp(yawErr * yawGain * yawMul - actualYaw * yawDamping * dampMul, -maxYawRate, maxYawRate);

            float pitchDeg = 0f;
            if (usePitch)
            {
                Vector3 toAim = aim - pos;
                float aimPitch  = Mathf.Atan2(toAim.y, Mathf.Max(0.01f, aimFlat.magnitude)) * Mathf.Rad2Deg;
                float shipPitch = Mathf.Atan2(fwd.y,   Mathf.Max(0.01f, fwdFlat.magnitude)) * Mathf.Rad2Deg;
                pitchDeg = Mathf.Clamp((aimPitch - shipPitch) * pitchGain, -maxPitchRate, maxPitchRate) * dt;
            }

            // ── Corner speed: ease throttle → reverse (L2-style) to hold the corner speed. SKIPPED while
            // drifting — the slide carries the corner, and braking/reverse would kill the charge. ──
            // Pace (skill) scales both top speed and corner target; a throttle-lift fumble cuts the drive.
            float driveThrottle = throttle * paceMul * (liftOff ? 1f - 0.8f * mistakeIntensity : 1f);
            float sharp    = Mathf.Clamp01(bend / Mathf.Max(1f, bendForFullSlow));
            float target   = Mathf.Lerp(_controller.maxSpeed * paceMul, minCornerSpeed * cornerMul, sharp);
            float thrustZ  = driveThrottle;
            // Skip the corner-brake while drifting AND for the post-drift cooldown — otherwise it would
            // immediately reverse-thrust away the drift-boost we just fired (boost > corner target → brake).
            // A late-brake fumble also skips it, so the car runs in too hot and washes wide (an exploitable error).
            if (!_drifting && _driftCooldown <= 0f && !lateBrake && speed > target)
            {
                float over = Mathf.Clamp01((speed - target) / Mathf.Max(1f, target * 0.25f));
                thrustZ = Mathf.Lerp(driveThrottle, -slowdownReverse, over);
            }
            const bool braking = false;   // L1 handbrake never used — it interrupts the drift charge

            // ── Line keeping: trim sideways + vertical offset from the line ──
            Vector3 lineHere = LinePoint(t);
            Vector3 off      = pos - lineHere;
            Vector3 right    = Vector3.Cross(Vector3.up, fwdFlat.normalized);
            float   strafeX  = Mathf.Clamp(-Vector3.Dot(off, right) * lateralTrim, -1f, 1f);
            float   hoverY   = Mathf.Clamp(-off.y * verticalTrim, -1f, 1f);

            // Mistake: a slow line wander so it doesn't hold a laser-perfect line (no effect while drifting).
            if (wander != 0f && !_drifting) strafeX = Mathf.Clamp(strafeX + wander, -1f, 1f);

            // While drifting, HOLD strafe into the turn — this is what breaks grip and charges the boost.
            // The frame the drift ENDS, strafe snaps back to line-keeping; that release is what fires it.
            if (_drifting) strafeX = Mathf.Clamp(driftStrafe * _driftSign, -1f, 1f);

            // ── Wall avoidance: REACTIVE layer over the path-follow. Whisker rays steer + strafe away from
            //    walls and scrub speed on a head-on, so a line that would clip geometry gets saved. ──
            if (avoidWalls)
            {
                Vector3 velDir = Vector3.ProjectOnPlane(_controller.currentVelocity, Vector3.up);
                velDir = velDir.sqrMagnitude > 1e-4f ? velDir.normalized : fwdFlat.normalized;
                float avoidSteer = SenseWalls(pos, fwdFlat, velDir, speed, out float frontBlock, out float pitchUp);
                yawRate = Mathf.Clamp(yawRate + avoidSteer * avoidYawGain, -maxYawRate, maxYawRate);
                strafeX = Mathf.Clamp(strafeX + avoidSteer * avoidStrafe, -1f, 1f);
                if (avoidBrakeFront && frontBlock > 0f)
                    thrustZ = Mathf.Min(thrustZ, Mathf.Lerp(driveThrottle, -slowdownReverse, frontBlock));
                // Climbable ramp ahead → pitch the nose up to drive up it (works even with usePitch off).
                if (pitchUp > 0f)
                    pitchDeg = Mathf.Clamp(pitchDeg + pitchUp * avoidPitchGain * dt, -maxPitchRate * dt, maxPitchRate * dt);
            }

            // ── Racer awareness: step aside to pass a slower car, tuck in behind one it can't pass, and don't
            //    grind side-by-side. Skipped mid-drift so it doesn't corrupt the drift's strafe charge. ──
            if (avoidRacers && !_drifting)
            {
                SenseRacers(pos, fwdFlat, speed, out float racerStrafe, out float speedCap);
                strafeX = Mathf.Clamp(strafeX + racerStrafe, -1f, 1f);
                if (speedCap >= 0f && speed > speedCap)
                {
                    float over = Mathf.Clamp01((speed - speedCap) / Mathf.Max(1f, speedCap * 0.25f));
                    thrustZ = Mathf.Min(thrustZ, Mathf.Lerp(driveThrottle, -slowdownReverse, over));
                }
            }

            float yawDeg = yawRate * dt;

            // Debug: remember what we steered at + record the trail of where we actually went.
            _dbgAim = aim; _dbgLine = lineHere; _dbgHasAim = true;
            RecordTrail(pos);

            // ── Stuck recovery: bogged down → reverse + reorient toward the line ──
            if (_recoverTimer > 0f)
            {
                _recoverTimer -= dt;
                // Reverse out, keep steering toward the line so it backs away facing the right way.
                _controller.ApplyInput(new Vector3(strafeX, hoverY, -reverseThrottle),
                                       new Vector3(0f, yawDeg, 0f), false);
                if (_recoverTimer <= 0f) _stuckTimer = 0f;
                return;
            }
            // Don't count "stuck" while drifting — a slide can dip below stuckSpeed for a moment without being
            // bogged down. If it genuinely spins out, the drift times out first, then recovery can trigger.
            _stuckTimer = (speed < stuckSpeed && !_drifting) ? _stuckTimer + dt : 0f;
            if (_stuckTimer > stuckTime) { _recoverTimer = recoverDuration; return; }

            _controller.ApplyInput(new Vector3(strafeX, hoverY, thrustZ),
                                   new Vector3(pitchDeg, yawDeg, 0f), braking);
        }

        // ── Debug visualization ─────────────────────────────────────────────────────────────────────
        void RecordTrail(Vector3 p)
        {
            if (!drawTrail) { _trailCount = 0; return; }
            int n = Mathf.Max(8, trailLength);
            if (_trail == null || _trail.Length != n) { _trail = new Vector3[n]; _trailHead = _trailCount = 0; }
            _trail[_trailHead] = p;
            _trailHead = (_trailHead + 1) % n;
            if (_trailCount < n) _trailCount++;
        }

        // Forward-simulate the pure-pursuit (geometric: aim → yaw toward it → step). Returns the predicted
        // line. It ignores the ship's yaw INERTIA, so the real driven line lags this a little — the GAP
        // between this (yellow) and the actual trail (green) is exactly the inertia/over/understeer to tune out.
        void OnDrawGizmos()
        {
            if (!drawDebug) return;
            if (track == null) track = FindFirstObjectByType<RaceSpline>();
            if (racingLine == null) racingLine = FindFirstObjectByType<RacingLine>();
            if (_controller == null) _controller = GetComponent<SpaceshipController>();
            if (track == null || !track.IsValid) return;

            // Aim (cyan): what it's steering at right now.
            if (drawAim && _dbgHasAim)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(transform.position, _dbgAim);
                Gizmos.DrawWireSphere(_dbgAim, 2f);
                Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
                Gizmos.DrawWireSphere(_dbgLine, 1.2f);   // where the line wants it (white)
            }

            // Actual trail (green): the ground truth path it drove.
            if (drawTrail && _trail != null && _trailCount > 1)
            {
                Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.9f);
                int n = _trail.Length;
                for (int i = 1; i < _trailCount; i++)
                {
                    int a = (_trailHead - i - 1 + n) % n;
                    int b = (_trailHead - i + n) % n;
                    Gizmos.DrawLine(_trail[a], _trail[b]);
                }
            }

            // Wall feelers: green = clear (full reach), red = hit (drawn to the hit point + a marker).
            if (drawFeelers && _feelDir != null && _feelN > 0)
            {
                for (int i = 0; i < _feelN; i++)
                {
                    float hd = _feelHitDist[i];
                    if (hd >= 0f)
                    {
                        Vector3 hit = _feelOrigin + _feelDir[i] * hd;
                        bool ramp = _feelRamp != null && i < _feelRamp.Length && _feelRamp[i];
                        // orange = climbable ramp (pitch up), red = wall (steer/brake away)
                        Gizmos.color = ramp ? new Color(1f, 0.6f, 0.1f, 0.95f) : new Color(1f, 0.25f, 0.2f, 0.95f);
                        Gizmos.DrawLine(_feelOrigin, hit);
                        Gizmos.DrawWireSphere(hit, 0.8f);
                    }
                    else
                    {
                        Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.4f);
                        Gizmos.DrawLine(_feelOrigin, _feelOrigin + _feelDir[i] * _feelLen[i]);
                    }
                }
            }

            // Rivals currently influencing the AI (magenta) — overtaking or avoiding them.
            if (drawRacers && _dbgRacers != null && _dbgRacerCount > 0)
            {
                Gizmos.color = new Color(1f, 0.2f, 0.9f, 0.9f);
                for (int i = 0; i < _dbgRacerCount; i++)
                {
                    Gizmos.DrawLine(transform.position, _dbgRacers[i]);
                    Gizmos.DrawWireSphere(_dbgRacers[i], 2.5f);
                }
            }

            // Mistake marker: float a sphere over the car when it's mid-fumble (yellow lift / red brake).
            if (drawMistakes && _dbgFumble != 0)
            {
                Gizmos.color = _dbgFumble == 1 ? new Color(1f, 0.9f, 0.1f, 0.95f) : new Color(1f, 0.2f, 0.15f, 0.95f);
                Gizmos.DrawWireSphere(transform.position + Vector3.up * 4f, 1.6f);
            }

            // Predicted pursuit line (yellow): live, responds to look-ahead / yaw gain even when paused.
            if (drawPrediction)
            {
                Vector3 sp = transform.position;
                Vector3 sf = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                sf = sf.sqrMagnitude > 1e-5f ? sf.normalized : Vector3.forward;
                float ss = (Application.isPlaying && _controller != null)
                    ? Mathf.Max(20f, _controller.currentVelocity.magnitude)
                    : (_controller != null ? _controller.maxSpeed * 0.6f : 120f);
                // Start from the ship's ACTUAL yaw rate so the prediction continues its current motion.
                float simYaw = (Application.isPlaying && _controller != null && _controller.momentum != null)
                    ? _controller.momentum.YawRate : 0f;

                Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.95f);
                for (int i = 0; i < predictSteps; i++)
                {
                    float t       = track.GetProgress(sp);
                    sp.y          = LinePoint(t).y;                 // follow the track's elevation, not flat
                    float aheadFr = LookAheadFr(t, ss, out _);      // same dynamic look-ahead as the real AI
                    Vector3 aim   = LinePoint(Mathf.Repeat(t + aheadFr, 1f));
                    Vector3 aimF  = Vector3.ProjectOnPlane(aim - sp, Vector3.up);
                    float yawErr  = Vector3.SignedAngle(sf, aimF, Vector3.up);

                    // Same PD command the AI issues, fed through a yaw-INERTIA lag — so the prediction can
                    // over-correct and OSCILLATE just like the real ship, and damp out as you tune.
                    float cmd = Mathf.Clamp(yawErr * yawGain - simYaw * yawDamping, -maxYawRate, maxYawRate);
                    simYaw = Mathf.Lerp(simYaw, cmd, Mathf.Clamp01(predictTurnResponse * predictStep));

                    sf = (Quaternion.AngleAxis(simYaw * predictStep, Vector3.up) * sf).normalized;
                    Vector3 next = sp + sf * ss * predictStep;
                    Gizmos.DrawLine(sp, next);
                    sp = next;
                }
            }
        }
    }
}
