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

        void Update()
        {
            if (controller == null || label == null) return;
            float speed = controller.currentVelocity.magnitude;
            label.text = $"{prefix}{speed.ToString(format)} {unit}";
        }
    }
}
