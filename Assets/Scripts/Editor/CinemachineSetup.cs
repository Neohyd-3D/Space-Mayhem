#if UNITY_EDITOR
using Unity.Cinemachine;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpaceMayhem.Editor
{
    /// <summary>
    /// One-shot scene setup.  Run from  SpaceMayhem → Setup Cinemachine Camera.
    /// Safe to re-run — existing objects are found and reused.
    ///
    /// Architecture
    /// ────────────
    /// Stock Cinemachine 3.x pipeline:
    ///
    ///   CinemachineCamera
    ///   ├── CinemachineThirdPersonFollow   (Body) — position lag + behind-the-ship offset
    ///   ├── CinemachineRotationComposer    (Aim)  — aim damping + screen composition
    ///   └── CinemachineImpulseListener            — additive camera shake
    ///
    /// Both Body and Aim point at the Spaceship transform (Tracking + LookAt).
    /// No custom proxy script — feel is tuned via the CM components in the Inspector.
    ///
    /// Tunable on the VirtualCamera (live in Play Mode):
    ///   CinemachineThirdPersonFollow:
    ///     • CameraDistance      — how far behind the ship
    ///     • VerticalArmLength   — how high above the ship pivot
    ///     • ShoulderOffset      — lateral offset (keep zero for centered chase)
    ///     • CameraSide          — 0.5 = centered
    ///     • Damping (x,y,z)     — position lag per axis
    ///   CinemachineRotationComposer:
    ///     • Composition.ScreenPosition — where the ship sits in frame
    ///     • Damping (x,y)              — aim lag (yaw, pitch)
    /// </summary>
    public static class CinemachineSetup
    {
        const string VCAM_NAME = "VirtualCamera";

        [MenuItem("SpaceMayhem/Setup Cinemachine Camera")]
        public static void Setup()
        {
            // ── 1. Main Camera ────────────────────────────────────────────────
            var mainCamGO = GameObject.FindWithTag("MainCamera");
            if (mainCamGO == null)
            {
                Debug.LogError("[CinemachineSetup] No GameObject tagged 'MainCamera' found.");
                return;
            }

            // Remove legacy CameraFollower — Brain now drives the real camera.
            var oldFollower = mainCamGO.GetComponent<CameraFollower>();
            if (oldFollower != null)
            {
                Undo.DestroyObjectImmediate(oldFollower);
                Debug.Log("[CinemachineSetup] Removed CameraFollower from Main Camera.");
            }

            if (mainCamGO.GetComponent<CinemachineBrain>() == null)
            {
                Undo.AddComponent<CinemachineBrain>(mainCamGO);
                Debug.Log("[CinemachineSetup] Added CinemachineBrain to Main Camera.");
            }

            // ── 2. Ship reference ─────────────────────────────────────────────
            var shipCtrl = Object.FindFirstObjectByType<SpaceshipController>();
            if (shipCtrl == null)
            {
                Debug.LogError("[CinemachineSetup] No SpaceshipController found in scene.");
                return;
            }

            // ── 3. Clean up legacy proxies if present ─────────────────────────
            var oldTarget = GameObject.Find("CameraTarget");
            if (oldTarget != null)
            {
                Undo.DestroyObjectImmediate(oldTarget);
                Debug.Log("[CinemachineSetup] Removed legacy CameraTarget GO.");
            }

            // ── 4. VirtualCamera ──────────────────────────────────────────────
            var vcamGO = GameObject.Find(VCAM_NAME)
                      ?? new GameObject(VCAM_NAME);
            Undo.RegisterCreatedObjectUndo(vcamGO, "Create VirtualCamera");

            var vcam = vcamGO.GetComponent<CinemachineCamera>()
                    ?? Undo.AddComponent<CinemachineCamera>(vcamGO);
            vcam.Follow = shipCtrl.transform;
            vcam.LookAt = shipCtrl.transform;

            // Strip any orphan proxy left from older setups.
            var staleProxy = vcamGO.GetComponent<ShipCameraTarget>();
            if (staleProxy != null) Undo.DestroyObjectImmediate(staleProxy);

            // Strip any non-matching body component (we want ThirdPersonFollow).
            var staleFollow = vcamGO.GetComponent<CinemachineFollow>();
            if (staleFollow != null) Undo.DestroyObjectImmediate(staleFollow);

            // ── 5. Body — CinemachineThirdPersonFollow ────────────────────────
            var tpf = vcamGO.GetComponent<CinemachineThirdPersonFollow>()
                   ?? Undo.AddComponent<CinemachineThirdPersonFollow>(vcamGO);
            tpf.CameraDistance    = 8f;
            tpf.VerticalArmLength = 2.5f;
            tpf.CameraSide        = 0.5f;          // centered
            tpf.ShoulderOffset    = Vector3.zero;  // no lateral offset
            tpf.Damping           = new Vector3(0.1f, 0.3f, 0.3f);

            // ── 6. Aim — CinemachineRotationComposer ──────────────────────────
            var rc = vcamGO.GetComponent<CinemachineRotationComposer>()
                  ?? Undo.AddComponent<CinemachineRotationComposer>(vcamGO);
            rc.Damping = new Vector2(0.1f, 0.1f);
            var comp = rc.Composition;
            comp.ScreenPosition = Vector2.zero;     // ship centered in frame
            rc.Composition = comp;

            // ── 7. Shake-ready ────────────────────────────────────────────────
            if (vcamGO.GetComponent<CinemachineImpulseListener>() == null)
                Undo.AddComponent<CinemachineImpulseListener>(vcamGO);

            EditorUtility.SetDirty(vcamGO);
            EditorUtility.SetDirty(mainCamGO);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                SceneManager.GetActiveScene());

            Debug.Log("[CinemachineSetup] Done.\n"
                + "  • Body: CinemachineThirdPersonFollow (Distance=8, ArmLength=2.5, Damping=(0.1,0.3,0.3))\n"
                + "  • Aim:  CinemachineRotationComposer  (Damping=(0.1,0.1), centered)\n"
                + "  • Tune both components live in Play Mode via the Inspector.\n"
                + "  • Save the scene (Ctrl/Cmd+S).");
        }
    }
}
#endif
