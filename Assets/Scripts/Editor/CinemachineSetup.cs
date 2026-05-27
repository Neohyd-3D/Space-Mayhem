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
    /// ShipCameraTarget lives ON the VirtualCamera GameObject.  It writes the
    /// final desired camera position and rotation to its own transform each
    /// LateUpdate (position-lag SmoothDamp, rotation-lag Slerp, roll-free
    /// swing-twist, barrel-roll freeze).
    ///
    /// CinemachineCamera has an EMPTY pipeline (no body, no aim).  A passive
    /// CinemachineCamera reads its own transform.position / transform.rotation
    /// as its state directly — so Cinemachine touches NOTHING position/rotation-wise.
    ///
    /// CinemachineImpulseListener runs as an extension and adds camera shake
    /// purely additively on top of the state.  No interference at all.
    ///
    /// What is created
    /// ───────────────
    ///  VirtualCamera  — CinemachineCamera (passive) + ShipCameraTarget + ImpulseListener
    ///  Main Camera    — CinemachineBrain added; CameraFollower removed
    ///  (No separate CameraTarget proxy GO needed)
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

            // Remove CameraFollower — Brain now drives the real camera.
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

            // ── 3. Clean up old CameraTarget proxy (no longer needed) ─────────
            var oldTarget = GameObject.Find("CameraTarget");
            if (oldTarget != null)
            {
                Undo.DestroyObjectImmediate(oldTarget);
                Debug.Log("[CinemachineSetup] Removed legacy CameraTarget GO.");
            }

            // ── 4. VirtualCamera — passive, pipeline-free ─────────────────────
            var vcamGO = GameObject.Find(VCAM_NAME)
                      ?? new GameObject(VCAM_NAME);
            Undo.RegisterCreatedObjectUndo(vcamGO, "Create VirtualCamera");

            // Passive CinemachineCamera: no body, no aim.
            // It reads its own transform.position/rotation as the camera state.
            // ShipCameraTarget writes to that transform every LateUpdate.
            var vcam = vcamGO.GetComponent<CinemachineCamera>()
                    ?? Undo.AddComponent<CinemachineCamera>(vcamGO);
            vcam.Follow = null;
            vcam.LookAt = null;

            // Remove any stale body/aim components left from a previous setup run.
            var staleFollow = vcamGO.GetComponent<CinemachineFollow>();
            if (staleFollow != null) Undo.DestroyObjectImmediate(staleFollow);

            var staleComposer = vcamGO.GetComponent<CinemachineRotationComposer>();
            if (staleComposer != null) Undo.DestroyObjectImmediate(staleComposer);

            // ── 5. ShipCameraTarget — owns ALL camera-feel logic ──────────────
            var proxy = vcamGO.GetComponent<ShipCameraTarget>()
                     ?? Undo.AddComponent<ShipCameraTarget>(vcamGO);
            proxy.ship       = shipCtrl.transform;
            proxy.controller = shipCtrl;
            EditorUtility.SetDirty(vcamGO);

            // ── 6. CinemachineImpulseListener — shake-ready ───────────────────
            // Adds camera shake additively to the state AFTER ShipCameraTarget
            // has set the transform.  Zero coupling with position/rotation logic.
            if (vcamGO.GetComponent<CinemachineImpulseListener>() == null)
                Undo.AddComponent<CinemachineImpulseListener>(vcamGO);

            // ── Done ──────────────────────────────────────────────────────────
            EditorUtility.SetDirty(mainCamGO);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                SceneManager.GetActiveScene());

            Debug.Log("[CinemachineSetup] Done.\n"
                + "  • Tweak feel via ShipCameraTarget on the VirtualCamera GO:\n"
                + "      positionLag, rotationLag, cameraDistance, cameraHeight, lookAheadDistance\n"
                + "  • All fields update live in Play Mode.\n"
                + "  • Shake: add CinemachineImpulseSource to any GO and call GenerateImpulse().\n"
                + "  • Save the scene (Ctrl/Cmd+S).");
        }
    }
}
#endif
