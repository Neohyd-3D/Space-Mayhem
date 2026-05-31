# SpaceMayhem — Controller → Momentum Physics Refactor

> **Status:** design doc / working methodology. No physics code has been changed yet.
> **Branch:** `controller-refactor` (cut from the `f3cff83` checkpoint on `main`).
> **Audience:** whoever (human or agent) picks up the refactor. Read this top-to-bottom before touching `SpaceshipController.cs` or `MomentumSystem.cs`.

---

## 0. The one rule everything else serves

**Game feel must EMERGE from physics. It must not be a hardcoded state machine or a magic "feel" knob.**

A tuning parameter is allowed **only if it is physical** — a grip coefficient, a slip angle, a friction budget, an inertia. The moment a parameter exists purely to "make it feel right" (a latch, a blend-toward-a-target, a gesture detector, a per-state grip swap), it's a smell: we are *authoring the symptom* instead of *modelling the cause*.

The whole point of this refactor is to delete the authored symptoms and let the same behaviour fall out of one small, honest model.

---

## 1. Hard constraints (do not violate, do not "improve")

These are settled. Don't relitigate them in code.

- **NO Unity `Rigidbody`.** Multiplayer is coming after the controller is done; we need deterministic, server-authoritative motion we fully own. All dynamics are hand-rolled.
- **Multiplayer-ready seams stay intact.** `MonoBehaviour → NetworkBehaviour` upgrade path; **no singletons**; `ApplyInput(thrust, rotation, braking)` is the network input seam — input is already decoupled from the controller. Keep it that way.
- **The physics step must be serializable / deterministic.** Pure function of `(state, intent, dt)`. No `Time.deltaTime` reads inside it (pass `dt` in), no transform reads inside it (pass heading in), no allocation.
- **DO NOT TOUCH the camera.** It's Cinemachine and it's tuned. Out of scope, full stop.
- **Brake tuning values are WIP.** Leave `brakeForce`, `brakeThresholdSpeed`, `boostMultiplier`, `brakePenaltyMultiplier`, `boostSnapDuration`, `brakeBuildUp` exactly as they are. The brake **reward/penalty mechanic is a game-design feature, not physics** — it does not get "emergent-ified". It stays as authored logic in the controller.
- **The speed-scaled strafe thrust is intentional.** `strafeThrustForce → maxStrafeThrustForce` lerped by speed is a deliberate design choice. Don't rip it out in the name of purity.
- **Do not commit `MemoryCaptures/`.** It's Unity profiler scratch and it is *not* in `.gitignore`. It will show as untracked — leave it untracked, never stage it.

---

## 2. Where the code is today

### `SpaceshipController.cs` — kinematic, owns everything
- No Rigidbody. State is `Vector3 currentVelocity` (**world space**), integrated in **`Update()`** (render rate, not `FixedUpdate`) so the no-Rigidbody ship doesn't judder.
- `_rotationInput` arrives as **degrees this frame** (already source-scaled by `SpaceshipInput`: mouse pixel-delta vs gamepad stick × dt).
- Rotation is applied with `transform.Rotate(...)` — **instant**. There is no angular velocity, no angular inertia, no torque. The nose teleports to its new heading every frame.
- Velocity is integrated, drag applied, then **momentum-steered** (velocity slerped toward heading), then position updated, then depenetrated.
- Mass is never defined → **implicit mass = 1** → `thrustForce` is really an *acceleration*, not a force. Worth saying out loud because the refactor will make mass/inertia explicit.

### `MomentumSystem.cs` — currently just a redirect interpolator
- Today it only does brake-release boosts: `StartBoost(from, to, duration)` + `Tick(dt)` (smoothstep lerp) + `GetBlendedVelocity()` + `IsRedirecting`. **It does not own velocity.**
- **This is the component we promote into the real physics owner.** The name is already right; the responsibility is wrong.

---

## 3. The physics audit — what we faked

Below are the systems currently living in `SpaceshipController.Update()` that are *really physics we hand-authored*. They all approximate **one** real phenomenon (see §4). Several exist only to paper over the side-effects of the others.

| # | Faked system | Code locus | What it really is | Compensator? |
|---|---|---|---|---|
| 1 | **Momentum steering** — `Slerp(horizVel, horizFwd*speed, 1-exp(-grip·dt))` | grip block | Lateral grip pulling velocity toward heading | — |
| 2 | **Drift latch** — counter-strafe gesture → `_isDrifting` | latch block | Traction breaking when slip exceeds the budget | — |
| 3 | **`turnFactor`** — speed quadratically cuts yaw authority | rotation block | A stand-in for "fast = harder to change direction" (really inertia) | — |
| 4 | **`driftYawBoost`** — lifts yaw authority back up while drifting | rotation block | — | **Yes** — undoes #3 so a drift can still turn |
| 5 | **`strafeGripScale`** — a plain strafe loosens grip | grip block | — | **Yes** — stops #1 folding a sideways dodge into forward motion |
| 6 | **Hemisphere fade** — `grip *= Clamp01(Dot(velDir, fwd))` | grip block | — | **Yes** — dodges the antiparallel singularity in #1's Slerp |
| 7 | **Instant yaw** — `transform.Rotate` every frame | rotation block | Yaw with **zero** inertia (infinite angular acceleration) | — |
| + | **Wash/Fight grip** — `Lerp(driftGrip, driftCounterGrip, counterMag)` × speed | grip block | The shape of the slip→force curve, hand-drawn as two endpoints | — |

**Read that compensator column.** Three of the seven systems (#4, #5, #6) do no useful work on their own — they exist purely to cancel artefacts of #1 and #3. That's the tell that we're stacking patches on a model that doesn't quite hold. A correct model makes all three unnecessary.

---

## 4. The one real thing all of that approximates

A drift is not a state you enter. It's what happens when **lateral demand exceeds the traction budget.**

- A tyre (or our ship's lateral grip) makes a sideways force that depends on **slip angle** — the angle between where the nose points and where the ship is *actually moving*.
- That force **rises** with slip angle, **peaks**, then **falls** (saturation). Below the peak the ship is *planted*. Push past the peak and grip *drops* → the back end washes out → that's the slide.
- **The counter-strafe is not a special gesture — it's just a way to shove the slip angle past the peak.** Strafe thrust adds lateral velocity → increases slip angle → past peak → traction breaks → drift *emerges*. No latch needed. No gesture detector needed.
- "Fast corners are a real fight, slow ones trivial" falls out for free: at high speed a given heading change demands more lateral force, so you reach the peak sooner and have less margin. Nobody has to script that.

So the entire grip / latch / wash / fight / hemisphere / strafeGripScale / driftYawBoost pile collapses into: **one lateral force as a function of slip angle, capped by a friction budget.**

---

## 5. Target architecture

```
SpaceshipInput ──ApplyInput──▶ SpaceshipController ──MotionIntent──▶ MomentumSystem.Step()
   (unchanged)                  (orchestration only)                   (THE physics owner)
                                       ▲                                      │
                                       └──────────── MotionState ◀────────────┘
                                  applies transform.position (+ yaw in Variant 2)
```

### `MomentumSystem` becomes the pure physics owner
```csharp
public MotionState Step(in MotionIntent intent, float dt);   // pure, deterministic, no allocation
```
- **Owns** `Velocity` (world) and — in Variant 2 — `YawRate`.
- **Owns** the physical params: `lateralGrip`, `peakSlipAngle`, `maxGripForce`, (Variant 2: `yawInertia`, `frontOffset`, `rearOffset`, `steerAuthority`).
- **Writes no transforms, reads no globals.** Heading and `dt` come in via `MotionIntent`. This is what makes it serializable / networkable / testable.
- The brake-release boost (`StartBoost`/redirect) folds in here as part of the velocity it returns.

### `MotionIntent` / `MotionState` (shape, not gospel)
```csharp
struct MotionIntent {
    Vector3    localThrust;   // [-1,1] per axis, ship-local (forward/strafe/hover)
    float      yawCommand;    // V1: degrees this frame. V2: normalized steer intent [-1,1]
    Quaternion heading;       // ship world rotation, so Step can resolve local↔world
    bool       braking;
    float      brakePressure; // 0..1, authored brake feature stays in the controller
}
struct MotionState {
    Vector3 velocity;   // world
    float   yawRate;    // deg/s — V2 only; 0 / unused in V1
    float   slipAngle;  // EMERGENT — feeds DriftCommitment, visual lean, audio
}
```
- `DriftCommitment` stops being a latched `_driftBlend` and becomes **derived**: it measures slip *past* breakaway — `Clamp01((slipAngle − breakawayAngle) / peakSlipAngle)`, where `breakawayAngle = maxGripForce / lateralGrip` (where the tyre lets go). Below breakaway the ship is gripping → commitment 0, so an ordinary yaw-corner does NOT read as a drift (otherwise a hard yaw, which sits right at breakaway, tilted the mesh on its own). The path tracer, visual yaw/lean, and engine audio all read this emergent value — they don't change.

### `SpaceshipController` drops to orchestration
Keeps only what is genuinely **not** yaw-plane dynamics:
- Build `MotionIntent` from input + current transform; call `Step`; apply `transform.position += state.velocity * dt`; (Variant 2 only) `transform.Rotate(up, state.yawRate * dt)`.
- **Stays as authored logic:** brake reward/penalty mechanic; barrel roll; auto-level + horizon reset; **pitch & roll** (kinematic — only *yaw* becomes dynamic); visual tilt in `LateUpdate`; collision depenetration.

### Scope boundary
**The dynamics model is yaw-plane only.** Pitch and roll stay kinematic/cosmetic exactly as now. We are not building a full 6-DOF flight sim — we're making the *cornering* honest.

---

## 6. The fork — decide before writing Phase A's force curve

Both variants delete the *same* fakes (#1, #2, #4, #5, #6 and the wash/fight pile). They differ in what replaces yaw.

### Variant 1 — emergent slide, **commanded yaw** (recommended first target)
- Yaw is still **commanded directly** (player yaw → heading changes now, as today). What becomes emergent is *velocity-follows-heading*, via the slip→force curve.
- **No yaw inertia ⇒ the nose can never overshoot the velocity ⇒ structurally impossible to spin.** Understeer is the only failure mode.
- This **is** Mario Kart, and it matches the stated feel target ("I've never oversteered and done a 360° spin in MK").
- Feel change: **moderate.** Risk: **low.** Most current tuning intuition still transfers.

### Variant 2 — full bicycle model, **dynamic yaw** (only if V1 isn't enough)
- Yaw becomes dynamic: `yawCommand` → front-tyre steer angle → lateral force → **torque about the ship's centre** → integrate `yawRate` (with `yawInertia`) → integrate heading. The **rear** generates its own lateral force; when the rear **saturates**, the tail steps out and the ship can rotate *faster* than the velocity turns → **oversteer → real spin.**
- Gives genuine counter-steer and tank-slappers. Much richer, **much** riskier.
- Feel change: **large** — full re-tune from scratch. Requires an input-semantics tweak: **yaw stops being pre-scaled degrees and becomes a normalized steer intent** (the controller no longer owns the turn rate).
- The user explicitly said MK has **no** oversteer/spin. So V2 is *more physical* but *less on-target* unless we deliberately want skill-ceiling spinouts.

> **Mapping to phases:** Variant 1 ≈ the endpoint of **Phase A**. Variant 2 ≈ the endpoint of **Phase B**. So we don't actually choose up front — we ship A, *feel it*, and only commit to B if the corner fight demands real oversteer.

---

## 7. Phased migration

### Phase 0 — behaviour-preserving extraction (safety net, zero feel change)
Move the **current** velocity logic verbatim into `MomentumSystem.Step()` and have the controller call it. No model change. Same Slerp, same latch, same compensators — just relocated behind the `Step` seam with `MotionIntent`/`MotionState`.
- **Success = the ship flies and drifts *identically* to `main`.** If feel changed, Phase 0 is wrong; fix it before going on.
- This proves the seam (intent in, state out, transforms applied outside) without touching behaviour. It's the rollback anchor.

### Phase A — emergent lateral grip (the real refactor)
Delete the Slerp grip, the drift latch, wash/fight, hemisphere fade, `strafeGripScale`, and `driftYawBoost`. Replace with **one lateral force from the slip→force curve, capped by `maxGripForce`.** Derive `slipAngle` and expose it on `MotionState`; derive `DriftCommitment` from it.
- `turnResistanceMaxSpeed` / `minTurnFactor` are no longer authored knobs — the "fast = harder to turn" feel now **emerges** from needing more lateral force at speed.
- Counter-strafe drift must still appear — but now because strafe thrust pushes slip past the peak, *not* because a gesture flips a bool.
- **This is Variant 1.** Stop here, play it, tune the three physical params.

### Phase B — yaw inertia, **no-spin** (DONE — the chosen path)
Phase A made yaw instant (zero angular inertia), so the "fast = harder to turn" weight vanished — the player felt it immediately. Phase B gives the **heading rotational mass** and drives it with two torques, integrated as `τ = I·α` with rotational damping:
- **Steering torque** — the player's commanded yaw rate (still rate-based input, mouse + gamepad unchanged) × `steerTorque`.
- **Weathervane (directional-stability) torque** — a restoring torque that ALWAYS opposes the slip, pulling the nose back toward the velocity, × `alignTorque`. Its magnitude scales with **speed²·slip** (∝ dynamic pressure on a tail fin) and is **non-saturating**: `alignTau = -alignTorque · (speed/turnResistanceMaxSpeed)² · slipDeg`.

The weathervane torque is the whole trick: because it always opposes slip, **the nose can never out-run the velocity → spins are structurally impossible** (matches the "MK never 360s me" target). And because it grows with speed², **fast yaw is genuinely heavy while slow yaw stays free** — at high speed it fights any nose-yaw that opens a slip angle, forcing you to strafe/drift through the corner instead of just turning. Both feels fall out of one term. Pitch & roll stay kinematic; only yaw became dynamic. `MomentumSystem.Step` returns `yawRate`; the controller applies `transform.Rotate(up, yawRate·dt)`.

> **Why v², not the grip force.** The first cut tied the aligning torque to the Phase-A lateral-grip force, which **saturates at `maxGripForce`** — so above a low speed the yaw resistance went flat and fast turns felt free (the player caught this immediately: *"the yaw hardness isn't there… I don't have to use strafe at all, I just turn."*). Decoupling the restoring torque from grip and giving it the v² fin law is what restores the speed-dependent weight without a magic "feel" knob — it's the physical dynamic-pressure law.

> This is the "yaw inertia, no-spin" variant — deliberately **not** the full front/rear bicycle model (§6 Variant 2). We did not take the steer-angle input change or the spin-capable rear-tyre model; the single weathervane term restores the missing weight while staying on the no-spin feel target. Full Variant 2 remains available later only if catchable spinouts are ever wanted.

Top yaw rate (straight line, zero slip) ≈ `steerTorque × commandedRate / (yawInertia × yawDamping)`; cornering subtracts the v²-scaled weathervane load, so turns get steadily heavier with speed. Tune `alignTorque` for how heavy/planted fast yaw feels, `yawInertia`+`yawDamping` for how snappy.

---

## 8. Parameter migration — out → in

### Deleted (authored symptoms — gone after Phase A)
`steeringGripLow`, `steeringGripHigh`, `driftGrip`, `driftCounterGrip`, `driftYawBoost`, `strafeGripScale`.
`turnResistanceMaxSpeed` and `minTurnFactor` stop being inputs — their effect becomes **emergent**.
The fixed gates `DriftMinSpeed` / `DriftInputDeadzone` and the `_isDrifting` / `_driftDir` / `_driftBlend` latch state all disappear.

### Born (physical — every one names a real quantity)
- `lateralGrip` — cornering stiffness: how much lateral force per unit slip angle, below the peak.
- `peakSlipAngle` — slip *past* breakaway that reads as a fully committed drift (breakaway itself is emergent: `maxGripForce/lateralGrip`).
- `maxGripForce` — the friction budget / traction circle radius (force ceiling).
- **Phase B (no-spin yaw inertia) adds:** `yawInertia` (rotational mass), `steerTorque` (steering torque per unit commanded yaw rate), `alignTorque` (weathervane torque gain — restoring moment ∝ speed²·slip; the no-spin stabilizer + "heavy at speed" dial), `yawDamping` (rotational drag).
  - *(The full Variant 2 set — `frontOffset`, `rearOffset`, `steerAuthority` + the steer-angle input change — was NOT taken; see §7 Phase B.)*

### Untouched (not yaw-plane dynamics — leave them all alone)
`thrustForce`, `strafeThrustForce`, `maxStrafeThrustForce`, `strafeDrag`, `hoverThrustForce`, `hoverDrag`, `linearDrag`, `maxSpeed`; the entire **Brake** block; **Barrel Roll**; **Horizon Reset / Auto-Level**; **Visual Tilt**; **Collision**.

---

## 9. Verification (run at the end of every phase)

1. **Cruise:** WASD + mouse, smooth 6-DOF, velocity cap holds, no judder.
2. **Plain strafe stays lateral** — a sideways thrust dodges sideways, it does **not** curve forward. (This was a regression once; it must hold without `strafeGripScale`.)
3. **Drift emerges from counter-strafe** — and *releases responsively* (no grace timer; the user rejected even 0.12 s).
4. **Fast corner fights, slow corner is trivial** — the speed-scaled difficulty, now emergent.
5. **No spin in Variant 1** — you cannot do a 360° spinout. (In Variant 2 you *can* — that's the point, and it must be *catchable*.)
6. **Path tracer heatmap** still grades cyan→orange-red off `DriftCommitment` (now slip-derived).
7. **Brake, barrel roll, auto-level, tilt, collision, audio** all behave exactly as on `main` — the refactor must not perturb them.
8. **Determinism smoke test:** same `(state, intent, dt)` sequence → identical `MotionState`. No frame-rate dependence beyond the integrator.

---

## 10. Open decisions (resolve with the human before Phase A force-curve code)

1. **Sequencing:** Phase 0 → Phase A (recommended — safety net first), *or* skip 0 and go straight to A?
2. **Variant:** ship **Variant 1** (Phase A) and reassess, *or* commit now to **Variant 2** (Phase B) with the input-semantics change and full re-tune?

> Recommendation: **Phase 0 → Phase A → feel it → then decide B.** It de-risks the seam, preserves a rollback, and reaches an on-target (MK-like, no-spin) feel before betting on the much larger Variant 2 rewrite.

---

## 11. Working principles (apply on every edit)

- **Model the cause, not the symptom.** If you're about to add a bool, a latch, a per-state swap, or a "blend toward the value that feels right" — stop. Find the force that produces that behaviour and add *that*.
- **Every new tunable must name a physical quantity.** If you can't, it doesn't belong.
- **Keep `Step` pure.** No globals, no transforms, no allocation, `dt` and heading passed in. This is the multiplayer contract.
- **One phase at a time, verified against `main` feel before moving on.** Phase 0 is your rollback anchor; don't skip the comparison.
- **Don't touch the off-limits list** (camera, brake values, speed-scaled strafe, the untouched params, `MemoryCaptures/`).
