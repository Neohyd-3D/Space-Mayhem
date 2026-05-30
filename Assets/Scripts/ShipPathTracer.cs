using System.Collections.Generic;
using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// DEBUG ONLY — draws a single, seamless trace of the ship's flight path, colour-graded
    /// by how hard it is drifting (a heatmap from <see cref="cruiseColor"/> to
    /// <see cref="driftColor"/>, driven by SpaceshipController.DriftCommitment).
    ///
    /// Uses Debug.DrawLine, so it needs no material and works under HDRP. The trace shows in
    /// the Scene view always, and in the Game view when the "Gizmos" toggle (top-right of the
    /// Game tab) is enabled. It does NOT render in a build — it is purely an editor aid and the
    /// per-frame work is compiled out of players.
    ///
    /// Attach to the ship (or assign the SpaceshipController), enter Play Mode, and fly.
    /// </summary>
    [DisallowMultipleComponent]
    public class ShipPathTracer : MonoBehaviour
    {
        [Tooltip("Ship to read drift state from. Auto-found on this object or a parent if left null.")]
        public SpaceshipController controller;

        [Tooltip("Master on/off for the trace.")]
        public bool tracing = true;

        [Header("Sampling")]
        [Tooltip("Record a new point only after the ship has moved this far (metres). " +
                 "Smaller = smoother and denser, but heavier.")]
        public float minPointDistance = 0.25f;

        [Tooltip("Max points kept. Visible trail length ≈ maxPoints × minPointDistance. " +
                 "Oldest points drop off so the trace never grows unbounded.")]
        public int maxPoints = 4000;

        [Header("Heatmap")]
        [Tooltip("Colour when NOT drifting (drift commitment 0).")]
        public Color cruiseColor = new Color(0.2f, 0.8f, 1f);   // cyan

        [Tooltip("Colour at full drift commitment (1).")]
        public Color driftColor = new Color(1f, 0.35f, 0.1f);   // orange-red

        struct Sample { public Vector3 pos; public float drift; }
        readonly List<Sample> _samples = new List<Sample>();

        void Reset() => controller = GetComponentInParent<SpaceshipController>();

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<SpaceshipController>();
        }

        void Update()
        {
#if UNITY_EDITOR
            if (!tracing) return;

            float   drift = controller != null ? Mathf.Clamp01(controller.DriftCommitment) : 0f;
            Vector3 pos   = controller != null ? controller.transform.position : transform.position;

            // Append a new sample once the ship has moved far enough (or it's the first point).
            int n = _samples.Count;
            if (n == 0 || (pos - _samples[n - 1].pos).sqrMagnitude >= minPointDistance * minPointDistance)
            {
                _samples.Add(new Sample { pos = pos, drift = drift });
                if (_samples.Count > maxPoints) _samples.RemoveAt(0);
            }

            // Redraw the whole buffered path this frame as ONE continuous, colour-graded curve.
            // Each segment's colour is the heatmap value averaged across its two endpoints, so the
            // drift/non-drift transition reads as a smooth gradient along a single seamless line.
            for (int i = 1; i < _samples.Count; i++)
            {
                Sample a = _samples[i - 1];
                Sample b = _samples[i];
                Color  c = Color.Lerp(cruiseColor, driftColor, 0.5f * (a.drift + b.drift));
                Debug.DrawLine(a.pos, b.pos, c, 0f, depthTest: false); // depthTest off = always visible
            }

            // Keep the tip attached to the ship between samples so the curve never lags the nose.
            // Colour it with the SAME endpoint-averaging the buffered segments use (last stored
            // drift ↔ live drift), so the join is seamless instead of stepping cruise→drift abruptly.
            if (_samples.Count > 0)
            {
                Sample last = _samples[_samples.Count - 1];
                Color  c    = Color.Lerp(cruiseColor, driftColor, 0.5f * (last.drift + drift));
                Debug.DrawLine(last.pos, pos, c, 0f, depthTest: false);
            }
#endif
        }

        /// <summary>Wipe the recorded trace — handy to bind to a key while tuning.</summary>
        public void Clear() => _samples.Clear();
    }
}
