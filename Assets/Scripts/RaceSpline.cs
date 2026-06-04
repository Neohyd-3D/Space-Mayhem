using UnityEngine;
using UnityEngine.Splines;

namespace SpaceMayhem
{
    /// <summary>
    /// Turns a hand-drawn Unity <see cref="SplineContainer"/> into the race's racing line. You draw
    /// the curve with Unity's native Spline tool (closed loop), drag that SplineContainer into
    /// <see cref="spline"/> here, and this analyses it: it measures the true arc length and slices the
    /// loop into <see cref="sectorCount"/> evenly-spaced sectors, with the start/finish line wherever
    /// you put <see cref="startOffset"/>. The RaceDirector reads progress from this — no trigger boxes.
    ///
    /// Why arc length and not the spline's raw parameter: Unity's spline t is not distance-uniform
    /// (it bunches up around tight knots), so sectors placed on raw t would be uneven and lap timing
    /// unfair. We sample the curve densely once and build a distance table, so progress and sector
    /// spacing are true metres travelled. The projection (position → progress) is pure and
    /// deterministic — the multiplayer-safe property the rest of the race relies on.
    ///
    /// The gizmo draws the curve, a green start/finish marker, and an orange marker per sector so you
    /// can see exactly where laps and checkpoints fall as you shape the spline and slide the start.
    /// </summary>
    [DisallowMultipleComponent]
    public class RaceSpline : MonoBehaviour
    {
        [Tooltip("The racing line, drawn with Unity's Spline tool. Add a Spline (SplineContainer) to any " +
                 "object, draw a CLOSED loop along the track centre, and drag it here. Closed so laps connect.")]
        public SplineContainer spline;

        [Min(1)]
        [Tooltip("How many evenly arc-length-spaced sectors to divide the loop into. Sector 0 begins at " +
                 "the start/finish line. More sectors = finer wrong-way / shortcut rejection and splits.")]
        public int sectorCount = 8;

        [Range(0f, 1f)]
        [Tooltip("Where the START/FINISH line sits along the drawn spline, as a fraction of total length " +
                 "(0 = the spline's first knot). Slide this to move the start anywhere without redrawing.")]
        public float startOffset = 0f;

        [Min(64)]
        [Tooltip("Samples used to measure arc length and project positions. Higher = more accurate; built " +
                 "once at Awake so runtime cost is negligible.")]
        public int resolution = 1024;

        [Tooltip("Draw the curve, start line, and sector markers in the Scene view while editing.")]
        public bool drawGizmo = true;

        // Densely-sampled world points around the loop, paired with cumulative arc length — the bridge
        // from Unity's non-uniform spline parameter to even, distance-true progress.
        Vector3[] _samples;
        float[]   _cumLen;
        float     _totalLen;

        public bool  IsValid     => _samples != null && _totalLen > 1e-3f;
        public int   SectorCount => Mathf.Max(1, sectorCount);
        public float TotalLength => _totalLen;

        void Awake()    => Rebuild();
        void OnEnable() => Rebuild();

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            _samples = null; _cumLen = null; _totalLen = 0f;
            if (spline == null || spline.Spline == null || spline.Spline.Count < 2) return;

            int n = Mathf.Max(64, resolution);
            _samples = new Vector3[n + 1];
            _cumLen  = new float[n + 1];

            // SplineContainer.EvaluatePosition returns world space (it composes the transform).
            for (int i = 0; i <= n; i++)
                _samples[i] = (Vector3)spline.EvaluatePosition((float)i / n);

            _cumLen[0] = 0f;
            for (int i = 1; i <= n; i++)
                _cumLen[i] = _cumLen[i - 1] + Vector3.Distance(_samples[i - 1], _samples[i]);
            _totalLen = _cumLen[n];
        }

        /// <summary>Normalised progress (0..1) of the racing line nearest <paramref name="worldPos"/>,
        /// measured from the start/finish line (so 0 = start, advancing in draw direction). Deterministic
        /// nearest-sample search → pure function of position.</summary>
        public float GetProgress(Vector3 worldPos)
        {
            if (!IsValid) return 0f;
            int best = 0; float bestSq = float.MaxValue;
            for (int i = 0; i < _samples.Length; i++)
            {
                float d = (worldPos - _samples[i]).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = i; }
            }
            float raw = _cumLen[best] / _totalLen;          // 0..1 from the spline's own start
            return Mathf.Repeat(raw - startOffset, 1f);     // shift so the start LINE maps to 0
        }

        /// <summary>World point at race-progress t (0..1 from the start line). For the start gate, AI
        /// targets, and respawn placement.</summary>
        public Vector3 GetPoint(float t01)
        {
            if (!IsValid) return transform.position;
            float target = Mathf.Repeat(t01 + startOffset, 1f) * _totalLen;
            for (int i = 1; i < _cumLen.Length; i++)
            {
                if (_cumLen[i] >= target)
                {
                    float span = _cumLen[i] - _cumLen[i - 1];
                    float f    = span > 1e-5f ? (target - _cumLen[i - 1]) / span : 0f;
                    return Vector3.Lerp(_samples[i - 1], _samples[i], f);
                }
            }
            return _samples[_samples.Length - 1];
        }

        /// <summary>Forward (tangent) direction of the racing line at progress t.</summary>
        public Vector3 GetDirection(float t01)
        {
            Vector3 a = GetPoint(t01);
            Vector3 b = GetPoint(Mathf.Repeat(t01 + 0.002f, 1f));
            Vector3 d = b - a;
            return d.sqrMagnitude > 1e-6f ? d.normalized : transform.forward;
        }

        void OnDrawGizmos()
        {
            if (!drawGizmo || spline == null || spline.Spline == null || spline.Spline.Count < 2) return;

            // Keep the markers live while you edit the spline / slide the start (cheap in the editor).
            if (!Application.isPlaying) Rebuild();
            if (!IsValid) return;

            // The curve.
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.9f);
            for (int i = 1; i < _samples.Length; i++)
                Gizmos.DrawLine(_samples[i - 1], _samples[i]);

            // Start/finish (green) and each sector boundary (orange).
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(GetPoint(0f), 4f);
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 1f);
            int s = SectorCount;
            for (int k = 1; k < s; k++)
                Gizmos.DrawSphere(GetPoint((float)k / s), 2.5f);
        }
    }
}
