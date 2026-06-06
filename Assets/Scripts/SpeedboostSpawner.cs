using UnityEngine;
using UnityEngine.Splines;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SpaceMayhem
{
    /// <summary>
    /// Test utility: scatters speedboost pickups along the track's spline and lets you art-direct them live
    /// in the editor — even spacing (or biased toward corners/straights), a shared offset/rotation relative
    /// to the curve, and a seeded random jitter. Put this on an empty GameObject, assign the speedboost
    /// prefab, turn on <see cref="livePreview"/>, and tweak — the boosts update in real time. They're real
    /// prefab instances saved with the scene, so they work in Play.
    ///
    /// Spacing is done by true ARC LENGTH (Unity's spline t isn't distance-uniform), and the corner/straight
    /// bias warps that spacing by the curve's local curvature.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpeedboostSpawner : MonoBehaviour
    {
        [Header("Sources")]
        [Tooltip("Track curve. Auto-found (first SplineContainer in the scene) if left empty.")]
        public SplineContainer spline;
        [Tooltip("The speedboost prefab to place.")]
        public GameObject boostPrefab;

        [Header("Placement")]
        [Min(1)] public int count = 12;
        [Range(0f, 1f)]
        [Tooltip("Where the first boost sits along the loop (0 = spline start).")]
        public float startOffset = 0f;
        [Min(64)]
        [Tooltip("Samples used to measure arc length / curvature. Higher = more even on twisty tracks.")]
        public int sampleResolution = 512;

        [Header("Corner / Straight bias")]
        [Range(-1f, 1f)]
        [Tooltip("-1 = pack the boosts onto the STRAIGHTS, 0 = even spacing, +1 = pack them into the CORNERS.")]
        public float curveBias = 0f;

        [Header("Offset (relative to the curve, shared by all)")]
        [Tooltip("Sideways from the racing line (+ = the curve's right).")]
        public float lateralOffset = 0f;
        [Tooltip("Up/down (world up). Use to lift boosts to the height the ship actually flies at.")]
        public float verticalOffset = 0f;
        [Tooltip("Slide every boost forward/back along the track.")]
        public float forwardOffset = 0f;
        [Tooltip("Face each boost down-track before the rotation offset is applied.")]
        public bool alignToTangent = true;
        [Tooltip("Extra rotation (Euler) added to every boost — rotate them all at once.")]
        public Vector3 rotationOffset = Vector3.zero;

        [Header("Randomize (relative to the curve, seeded)")]
        [Tooltip("Change this to reroll the random jitter. Same seed = same layout (stable while you tweak).")]
        public int randomSeed = 0;
        [Tooltip("Max random sideways jitter (± metres).")]    public float randomLateral = 0f;
        [Tooltip("Max random up/down jitter (± metres).")]     public float randomVertical = 0f;
        [Tooltip("Max random along-track jitter (± metres).")] public float randomForward = 0f;
        [Tooltip("Max random rotation jitter per axis (± degrees).")] public Vector3 randomRotation = Vector3.zero;

        [Header("Editor")]
        [Tooltip("Keep the boosts in sync with these settings in real time while editing.")]
        public bool livePreview = true;
        [Tooltip("Also (re)spawn at runtime in Start().")]
        public bool spawnOnStart = false;

        const string ContainerName = "__SpawnedBoosts";

        // Arc-length + curvature-weighted tables, rebuilt each Refresh.
        Vector3[] _pts;
        float[]   _cumWeighted;
        float     _totalWeighted;
        bool      _pendingRefresh;

        void Start()
        {
            if (spawnOnStart) Refresh();
        }

        void OnValidate()
        {
            count            = Mathf.Max(1, count);
            sampleResolution = Mathf.Max(64, sampleResolution);
#if UNITY_EDITOR
            if (!livePreview || Application.isPlaying || _pendingRefresh) return;
            _pendingRefresh = true;
            EditorApplication.delayCall += () =>
            {
                _pendingRefresh = false;
                if (this != null && livePreview && !Application.isPlaying) Refresh();
            };
#endif
        }

        [ContextMenu("Spawn / Refresh Boosts")]
        public void Refresh()
        {
            if (spline == null) spline = FindFirstObjectByType<SplineContainer>();
            if (spline == null || spline.Spline == null || spline.Spline.Count < 2)
            {
                Debug.LogWarning("[SpeedboostSpawner] No usable SplineContainer — draw a spline on the track and assign it.", this);
                return;
            }
            if (boostPrefab == null)
            {
                Debug.LogWarning("[SpeedboostSpawner] Assign the speedboost prefab.", this);
                return;
            }
            if (!BuildTables()) return;

            Transform container = GetOrCreateContainer();
            EnsureChildCount(container, count);
            for (int b = 0; b < count; b++)
                PlaceBoost(container.GetChild(b), b);
        }

        [ContextMenu("Clear Boosts")]
        public void Clear()
        {
            Transform existing = transform.Find(ContainerName);
            if (existing != null) DestroyObject(existing.gameObject);
        }

        [ContextMenu("Reroll Random")]
        public void Reroll()
        {
            randomSeed = Random.Range(int.MinValue, int.MaxValue);
            Refresh();
        }

        // ── Tables: arc length + curvature-weighted cumulative ────────────────────────────────────
        bool BuildTables()
        {
            int n = Mathf.Max(64, sampleResolution);
            _pts = new Vector3[n + 1];
            for (int i = 0; i <= n; i++)
                _pts[i] = (Vector3)spline.EvaluatePosition((float)i / n);   // world space

            // Curvature per sample (degrees turned per metre), normalised to 0..1.
            var curv = new float[n + 1];
            float maxCurv = 0f;
            for (int i = 1; i < n; i++)
            {
                Vector3 a = _pts[i] - _pts[i - 1];
                Vector3 b = _pts[i + 1] - _pts[i];
                float seg = b.magnitude;
                curv[i] = seg > 1e-4f ? Vector3.Angle(a, b) / seg : 0f;
                if (curv[i] > maxCurv) maxCurv = curv[i];
            }
            if (maxCurv > 1e-5f)
                for (int i = 0; i <= n; i++) curv[i] /= maxCurv;

            // Weighted cumulative length: each segment counts more or less by the bias.
            _cumWeighted = new float[n + 1];
            _cumWeighted[0] = 0f;
            for (int i = 1; i <= n; i++)
            {
                float seg = Vector3.Distance(_pts[i - 1], _pts[i]);
                _cumWeighted[i] = _cumWeighted[i - 1] + Weight(curv[i]) * seg;
            }
            _totalWeighted = _cumWeighted[n];
            return _totalWeighted > 1e-4f;
        }

        // 0 bias → uniform; + favours corners (curvature), − favours straights. Floor keeps every region alive.
        float Weight(float curvNorm)
        {
            float favoured = curveBias >= 0f ? curvNorm : 1f - curvNorm;
            return Mathf.Max(0.03f, Mathf.Lerp(1f, favoured, Mathf.Abs(curveBias)));
        }

        // ── Per-boost placement ───────────────────────────────────────────────────────────────────
        void PlaceBoost(Transform tr, int b)
        {
            // Even spacing in WEIGHTED space → corners/straights get proportionally more boosts.
            float targetW = Mathf.Repeat((startOffset + (float)b / count) * _totalWeighted, _totalWeighted);
            int i = 1;
            while (i < _cumWeighted.Length - 1 && _cumWeighted[i] < targetW) i++;
            float span = _cumWeighted[i] - _cumWeighted[i - 1];
            float f = span > 1e-5f ? (targetW - _cumWeighted[i - 1]) / span : 0f;

            Vector3 pos = Vector3.Lerp(_pts[i - 1], _pts[i], f);
            Vector3 fwd = (_pts[i] - _pts[i - 1]);
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;

            // Seeded random jitter — stable per (seed, index) so tweaking offsets doesn't reroll it.
            var rng = new System.Random(unchecked(randomSeed * 73856093 ^ (b + 1) * 19349663));
            float R() => (float)(rng.NextDouble() * 2.0 - 1.0);

            pos += right * (lateralOffset + R() * randomLateral)
                 + Vector3.up * (verticalOffset + R() * randomVertical)
                 + fwd * (forwardOffset + R() * randomForward);

            Quaternion rot = alignToTangent ? Quaternion.LookRotation(fwd, Vector3.up) : Quaternion.identity;
            rot *= Quaternion.Euler(rotationOffset);
            rot *= Quaternion.Euler(R() * randomRotation.x, R() * randomRotation.y, R() * randomRotation.z);

            tr.SetPositionAndRotation(pos, rot);
        }

        // ── Container / instance management ─────────────────────────────────────────────────────────
        Transform GetOrCreateContainer()
        {
            Transform existing = transform.Find(ContainerName);
            if (existing != null) return existing;
            var go = new GameObject(ContainerName);
            go.transform.SetParent(transform, worldPositionStays: false);
            return go.transform;
        }

        void EnsureChildCount(Transform container, int n)
        {
            if (container.childCount == n) return;                 // reuse existing — only reposition
            for (int i = container.childCount - 1; i >= 0; i--)
                DestroyObject(container.GetChild(i).gameObject);
            for (int i = 0; i < n; i++)
            {
                GameObject go;
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    go = (GameObject)PrefabUtility.InstantiatePrefab(boostPrefab, container);
                else
#endif
                    go = Instantiate(boostPrefab, container);
                go.name = $"Boost_{i:00}";
            }
        }

        static void DestroyObject(GameObject go)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) { DestroyImmediate(go); return; }
#endif
            Destroy(go);
        }
    }
}
