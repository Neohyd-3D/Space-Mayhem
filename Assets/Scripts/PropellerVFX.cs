using System.Collections.Generic;
using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Drives the propeller / thruster ShaderGraph (Assets/Controller/mat/Propeller.shadergraph,
    /// used by "Shader Graphs_Propeller Speed" and "Shader Graphs_Thrust") from the ship's
    /// actual throttle so the engines spool up when you accelerate and idle when you coast.
    ///
    /// The signal is the FORWARD THRUST COMMAND (SpaceshipController.ThrustInput.z), not raw
    /// speed — a ship gliding at top speed with the stick released shouldn't show full burn.
    /// A small <see cref="speedInfluence"/> term keeps a faint idle glow while moving so the
    /// engines never look dead. Braking scales the output down (the brake suppresses thrust).
    ///
    /// Modulation is RELATIVE to whatever each material's author set: we read the baseline
    /// _Propeler_Fuel / _Speed / _Color at startup and scale around it, so the two materials
    /// keep their distinct character and the art stays the source of truth. Values are pushed
    /// through a MaterialPropertyBlock — the shared .mat asset is never mutated (no leaked
    /// material instances, no dirtying the asset), and only the propeller submeshes are touched.
    ///
    /// Attach to the "space-VFX" / propeller object inside the ship's visual hierarchy. The
    /// controller and the propeller renderers are auto-discovered, so no manual wiring is needed
    /// (a renderer counts as a propeller if its material exposes _Propeler_Fuel).
    /// </summary>
    [DisallowMultipleComponent]
    public class PropellerVFX : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Ship controller to read throttle/speed from. Auto-found on a parent if left null.")]
        public SpaceshipController controller;

        [Tooltip("Renderers using the Propeller shader. Auto-collected from children (any renderer " +
                 "whose material exposes _Propeler_Fuel) if left empty.")]
        public Renderer[] propellerRenderers;

        [Header("Throttle Signal")]
        [Tooltip("Faint idle glow proportional to current speed/maxSpeed, so the engines aren't dead " +
                 "while coasting. 0 = react to throttle only; ~0.25 = a gentle cruising shimmer.")]
        [Range(0f, 1f)] public float speedInfluence = 0.25f;

        [Tooltip("How fast the burn spools up and down (1/s). Higher = snappier throttle response.")]
        public float responseSpeed = 6f;

        [Tooltip("Engine output multiplier while braking. 0 = engines cut on brake, 1 = brake ignored.")]
        [Range(0f, 1f)] public float brakeOutput = 0.1f;

        [Header("Driven Params  (multipliers vs the material's authored value)")]
        [Tooltip("Scale the flame/fuel amount (_Propeler_Fuel) with throttle.")]
        public bool  driveFuel     = true;
        [Tooltip("Fuel multiplier at idle (engine = 0).")]
        public float fuelIdleMul   = 0.35f;
        [Tooltip("Fuel multiplier at full throttle (engine = 1).")]
        public float fuelBoostMul  = 1.35f;

        [Tooltip("Scale the scroll/animation speed (_Speed) with throttle so the effect races faster.")]
        public bool  driveScroll   = true;
        public float scrollIdleMul = 0.5f;
        public float scrollBoostMul = 1.6f;

        [Tooltip("Scale the HDR emission (_Color) with throttle so the burn brightens under power.")]
        public bool  driveEmission   = true;
        public float emissionIdleMul = 0.5f;
        public float emissionBoostMul = 1.7f;

        [Header("Drift-Charge Hue Shift")]
        [Tooltip("Rotate the propeller emission hue as a drift charges, so the flame visibly tells you " +
                 "how much boost is banked. At zero charge the colour is the material's authored hue; at " +
                 "FULL charge (the point where the drift boost caps out) the hue has swept the full " +
                 "maxHueShift. Reads MomentumSystem.DriftCharge via the controller — needs both wired.")]
        public bool driveDriftHue = true;

        [Tooltip("Total hue rotation (degrees) at full charge. 180 = the complementary colour, so a full " +
                 "charge reads as the opposite hue of the authored flame. Brightness/saturation are kept.")]
        public float maxHueShift = 180f;

        [Tooltip("How fast the hue chases the current charge (1/s). Higher = snaps to the charge colour; " +
                 "lower = a smoother sweep, and a gentle fade back after the boost spends the charge.")]
        public float hueResponseSpeed = 8f;

        static readonly int FuelID  = Shader.PropertyToID("_Propeler_Fuel"); // sic — matches the shader
        static readonly int SpeedID = Shader.PropertyToID("_Speed");
        static readonly int ColorID = Shader.PropertyToID("_Color");

        MaterialPropertyBlock _mpb;
        float[]   _baseFuel;
        Vector4[] _baseSpeed;
        Color[]   _baseColor;
        float     _engine;    // smoothed throttle, 0..1
        float     _hueShift01; // smoothed hue offset, 0..1 of the colour wheel (1 = 360°)

        void Reset()
        {
            controller = GetComponentInParent<SpaceshipController>();
        }

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<SpaceshipController>();

            if (propellerRenderers == null || propellerRenderers.Length == 0)
            {
                // A renderer is a propeller iff its material exposes the (uniquely-named) fuel
                // property — keeps body/cockpit submeshes out of it automatically.
                var found = new List<Renderer>();
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m != null && m.HasProperty(FuelID)) found.Add(r);
                }
                propellerRenderers = found.ToArray();
            }

            _mpb = new MaterialPropertyBlock();
            int n = propellerRenderers.Length;
            _baseFuel  = new float[n];
            _baseSpeed = new Vector4[n];
            _baseColor = new Color[n];
            for (int i = 0; i < n; i++)
            {
                var r = propellerRenderers[i];
                var m = r != null ? r.sharedMaterial : null;
                _baseFuel[i]  = (m != null && m.HasProperty(FuelID))  ? m.GetFloat(FuelID)   : 0f;
                _baseSpeed[i] = (m != null && m.HasProperty(SpeedID)) ? m.GetVector(SpeedID) : Vector4.zero;
                _baseColor[i] = (m != null && m.HasProperty(ColorID)) ? m.GetColor(ColorID)  : Color.black;
            }

            if (controller == null)
                Debug.LogWarning("[PropellerVFX] No SpaceshipController found on a parent — engines won't react.", this);
        }

        void LateUpdate()
        {
            if (controller == null || propellerRenderers == null || propellerRenderers.Length == 0) return;

            // Throttle = forward thrust command, plus a faint speed-based idle. Brake scales it down.
            float throttle = Mathf.Clamp01(controller.ThrustInput.z);
            float speedN   = controller.maxSpeed > 1e-3f
                ? controller.currentVelocity.magnitude / controller.maxSpeed
                : 0f;
            float target = Mathf.Max(throttle, speedN * speedInfluence);
            if (controller.isBraking) target *= brakeOutput;

            // Exponential smoothing — frame-rate independent spool up/down.
            _engine = Mathf.Lerp(_engine, Mathf.Clamp01(target),
                                 1f - Mathf.Exp(-responseSpeed * Time.deltaTime));

            float fuelMul   = Mathf.Lerp(fuelIdleMul,     fuelBoostMul,     _engine);
            float scrollMul = Mathf.Lerp(scrollIdleMul,   scrollBoostMul,   _engine);
            float emiMul    = Mathf.Lerp(emissionIdleMul, emissionBoostMul, _engine);

            // ── Drift-charge hue offset ───────────────────────────────────────────
            // Map the banked drift charge (0 → the value where the boost caps out) onto
            // 0 → maxHueShift of the colour wheel, then smooth it so the flame sweeps
            // colour as you hold a slide and eases back once the boost spends the charge.
            float targetHue = 0f;
            if (driveDriftHue && controller.momentum != null && controller.driftBoostMaxSpeed > 1e-4f)
            {
                float maxCharge  = controller.driftBoostMaxSpeed / Mathf.Max(1e-4f, controller.driftBoostGain);
                float chargeNorm = Mathf.Clamp01(controller.momentum.DriftCharge / Mathf.Max(1e-4f, maxCharge));
                targetHue = chargeNorm * (maxHueShift / 360f);
            }
            _hueShift01 = Mathf.Lerp(_hueShift01, targetHue,
                                     1f - Mathf.Exp(-hueResponseSpeed * Time.deltaTime));

            for (int i = 0; i < propellerRenderers.Length; i++)
            {
                var r = propellerRenderers[i];
                if (r == null) continue;

                r.GetPropertyBlock(_mpb);
                if (driveFuel)   _mpb.SetFloat (FuelID,  _baseFuel[i]  * fuelMul);
                if (driveScroll) _mpb.SetVector(SpeedID, _baseSpeed[i] * scrollMul);

                if (driveEmission || driveDriftHue)
                {
                    Color emi = _baseColor[i];
                    if (driveDriftHue && _hueShift01 > 1e-4f) emi = ShiftHueHDR(emi, _hueShift01);
                    if (driveEmission)                         emi *= emiMul;
                    _mpb.SetColor(ColorID, emi);
                }

                r.SetPropertyBlock(_mpb);
            }
        }

        // Rotate an HDR colour's hue by hueDelta01 (1 = full 360°) while preserving its
        // saturation and — crucially — its emission brightness. Color.RGBToHSV clamps to
        // [0,1], which would crush an HDR flame, so we factor the >1 intensity out first,
        // shift the hue on the normalised colour, then multiply the intensity back in.
        static Color ShiftHueHDR(Color c, float hueDelta01)
        {
            float intensity = Mathf.Max(1f, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            Color ldr = new Color(c.r / intensity, c.g / intensity, c.b / intensity, 1f);

            Color.RGBToHSV(ldr, out float h, out float s, out float v);
            h = Mathf.Repeat(h + hueDelta01, 1f);
            Color shifted = Color.HSVToRGB(h, s, v, hdr: true) * intensity;
            shifted.a = c.a;
            return shifted;
        }
    }
}
