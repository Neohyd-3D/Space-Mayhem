// DIAGNOSTIC ONLY — remove before shipping.
// Attach to the ship root AND to the Main Camera separately.
// Compare smoothedDelta values in the Inspector during Play Mode:
//   - Ship smoothedDelta >> Camera smoothedDelta  → ship is jittering
//   - Camera smoothedDelta >> Ship smoothedDelta  → camera is jittering
//   - Both similar and low                        → frame pacing / editor overhead
//   - Camera spikes exactly match ship spikes     → camera is following correctly, jitter is upstream
using UnityEngine;

namespace SpaceMayhem
{
    public class JitterDiagnostic : MonoBehaviour
    {
        [Tooltip("Frame-to-frame position change this frame (metres).")]
        public float currentDelta;

        [Tooltip("Exponentially smoothed average of frame-to-frame delta. " +
                 "Compare between ship and camera to locate the source.")]
        public float smoothedDelta;

        [Tooltip("Largest single-frame delta recorded since last reset.")]
        public float peakDelta;

        Vector3 _prevPosition;
        bool    _initialized;

        void LateUpdate()
        {
            if (!_initialized)
            {
                _prevPosition = transform.position;
                _initialized  = true;
                return;
            }

            // Pure value-type math — zero GC allocations.
            currentDelta  = Vector3.Distance(transform.position, _prevPosition);
            smoothedDelta = Mathf.Lerp(smoothedDelta, currentDelta, 0.15f);
            if (currentDelta > peakDelta) peakDelta = currentDelta;

            _prevPosition = transform.position;
        }

        public void ResetPeak() => peakDelta = 0f;
    }
}
