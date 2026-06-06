using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// A smoothed "racing line" derived from the <see cref="RaceSpline"/> centreline: the low-curvature,
    /// out-in-out path that stays within a corridor of ±<see cref="maxOffset"/> metres of the centre. AI
    /// racers follow this instead of the centre so they cut apexes and carry more speed.
    ///
    /// How it's built: start on the centreline, then repeatedly nudge each point toward the CHORD of its
    /// neighbours (Laplacian smoothing). On a corner that pulls the line toward the inside (the apex); on the
    /// approach/exit it runs wide — which is exactly a racing line. The corridor clamp (±maxOffset) keeps it
    /// on the track. Tune that width with the gold gizmo so the line hugs the apexes without clipping walls.
    ///
    /// It exposes the same GetPoint / GetDirection API as RaceSpline, so the AI samples it the same way. The
    /// line is a property of the TRACK, not any one racer — one of these is shared by every AI.
    /// </summary>
    [DisallowMultipleComponent]
    public class RacingLine : MonoBehaviour
    {
        [Tooltip("Centreline to derive from. Auto-found (same object first, then the scene) if empty.")]
        public RaceSpline spline;

        [Min(0f)]
        [Tooltip("Corridor half-width (metres) the line may leave the centre by. Set to ~half the track " +
                 "width minus a margin, so apex cuts stay on the road. Watch the gold gizmo.")]
        public float maxOffset = 10f;

        [Min(16)]
        [Tooltip("Points around the loop. Higher = smoother line, more cost to build (built once).")]
        public int resolution = 256;

        [Min(0)]
        [Tooltip("Smoothing passes. More = straighter/lazier line that cuts apexes harder.")]
        public int smoothIterations = 80;

        [Range(0f, 1f)]
        [Tooltip("How aggressively each pass straightens the line. Higher converges faster.")]
        public float smoothRelax = 0.3f;

        [Tooltip("Draw the racing line (gold) in the Scene view.")]
        public bool drawGizmo = true;

        Vector3[] _pts;   // racing-line world points around the loop; index i ↔ progress i/Count

        public bool IsValid => _pts != null && _pts.Length > 2;

        void Awake()    => Build();
        void OnEnable() => Build();
        void OnValidate() { if (!Application.isPlaying) Build(); }

        [ContextMenu("Rebuild Racing Line")]
        public void Build()
        {
            _pts = null;
            if (spline == null) spline = GetComponent<RaceSpline>();
            if (spline == null) spline = FindFirstObjectByType<RaceSpline>();
            if (spline == null) return;
            if (!spline.IsValid) spline.Rebuild();          // force the centreline to sample itself
            if (!spline.IsValid) return;

            int n = Mathf.Max(16, resolution);
            var center = new Vector3[n];
            var right  = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                center[i] = spline.GetPoint(t);
                Vector3 d = spline.GetDirection(t);
                Vector3 flat = new Vector3(d.x, 0f, d.z);
                Vector3 r = Vector3.Cross(Vector3.up, flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward);
                right[i] = r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
            }

            var pts = (Vector3[])center.Clone();
            var off = new float[n];
            for (int pass = 0; pass < smoothIterations; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = (i - 1 + n) % n, b = (i + 1) % n;
                    Vector3 chord = 0.5f * (pts[a] + pts[b]);          // neighbour midpoint = lower curvature
                    float dRight  = Vector3.Dot(chord - pts[i], right[i]);
                    off[i] = Mathf.Clamp(off[i] + dRight * smoothRelax, -maxOffset, maxOffset);  // stay in the corridor
                    pts[i] = center[i] + right[i] * off[i];
                }
            }
            _pts = pts;
        }

        public Vector3 GetPoint(float t01)
        {
            if (!IsValid) return spline != null ? spline.GetPoint(t01) : transform.position;
            int n = _pts.Length;
            float f = Mathf.Repeat(t01, 1f) * n;
            int i = (int)f % n, j = (i + 1) % n;
            return Vector3.Lerp(_pts[i], _pts[j], f - Mathf.Floor(f));
        }

        public Vector3 GetDirection(float t01)
        {
            Vector3 a = GetPoint(t01);
            Vector3 b = GetPoint(Mathf.Repeat(t01 + 0.003f, 1f));
            Vector3 d = b - a;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        void OnDrawGizmos()
        {
            if (!drawGizmo) return;
            if (!IsValid) Build();
            if (!IsValid) return;
            Gizmos.color = new Color(1f, 0.84f, 0.2f, 0.95f);
            for (int i = 0; i < _pts.Length; i++)
                Gizmos.DrawLine(_pts[i], _pts[(i + 1) % _pts.Length]);
        }
    }
}
