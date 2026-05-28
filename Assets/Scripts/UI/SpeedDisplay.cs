using UnityEngine;
using TMPro;

namespace SpaceMayhem
{
    /// <summary>
    /// Reads the ship's current speed each frame and writes it to a TextMeshPro label.
    /// Attach this to any GameObject that has a TextMeshProUGUI sibling or child,
    /// then wire the references in the Inspector (or via SpaceMayhemSetup menu).
    /// </summary>
    [DisallowMultipleComponent]
    public class SpeedDisplay : MonoBehaviour
    {
        [Tooltip("The SpaceshipController to read speed from.")]
        public SpaceshipController controller;

        [Tooltip("The TMP label to write the speed into.")]
        public TextMeshProUGUI label;

        [Header("Format")]
        [Tooltip("Numeric format string. F0 = whole number, F1 = one decimal place.")]
        public string format = "F0";

        [Tooltip("Unit label appended after the number.")]
        public string unit = "m/s";

        [Tooltip("Optional prefix shown before the number (e.g. \"SPEED  \").")]
        public string prefix = "SPEED  ";

        [Tooltip("How much speed must change (m/s) before the display updates. " +
                 "0.5 matches F0 rounding — avoids per-frame TMP mesh rebuilds.")]
        public float updateThreshold = 0.5f;

        // For F0 (the default) we compare rounded integers — zero allocation in steady state.
        // For other formats we fall back to the float-threshold + string-compare path.
        int    _lastDisplayedInt = int.MinValue;   // used by the F0 fast path
        float  _lastDisplayedSpeed = float.NaN;    // used by the general path
        string _lastText = string.Empty;

        void Update()
        {
            if (controller == null || label == null) return;

            float speed = controller.currentVelocity.magnitude;

            // ── Fast path: F0 (integer display) ─────────────────────────────────────
            // Compares rounded integers — no string allocation unless the displayed
            // digit actually changes. This is the zero-allocation steady-state path.
            if (format == "F0")
            {
                int rounded = Mathf.RoundToInt(speed);
                if (rounded == _lastDisplayedInt) return;
                _lastDisplayedInt = rounded;
                label.text = $"{prefix}{rounded} {unit}";   // 1 allocation only on change
                return;
            }

            // ── General path: other format strings ───────────────────────────────────
            // Still avoids TMP mesh rebuilds unless the formatted string actually changes.
            if (Mathf.Abs(speed - _lastDisplayedSpeed) < updateThreshold) return;
            _lastDisplayedSpeed = speed;
            string newText = $"{prefix}{speed.ToString(format)} {unit}";
            if (newText == _lastText) return;
            _lastText = newText;
            label.text = newText;
        }
    }
}
