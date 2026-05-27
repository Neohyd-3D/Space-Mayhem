#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpaceMayhem.Editor
{
    /// <summary>
    /// Editor utilities for wiring up the SpaceMayhem prototype scene. VFX wireup
    /// (BrakeVolume, custom post-process, TimeDilationEffects) has been removed
    /// for the current iteration — focus is movement + camera. Restore from git
    /// history when VFX pass returns.
    /// </summary>
    public static class SpaceMayhemSetup
    {
        [MenuItem("SpaceMayhem/Wire Camera Follower")]
        public static void WireCameraFollower()
        {
            var cam = GameObject.Find("Main Camera");
            if (cam == null) { Debug.LogError("[SpaceMayhemSetup] 'Main Camera' not found."); return; }
            var follower = cam.GetComponent<SpaceMayhem.CameraFollower>();
            if (follower == null) { Debug.LogError("[SpaceMayhemSetup] CameraFollower not found on Main Camera."); return; }
            var ship = GameObject.Find("Spaceship");
            if (ship == null) { Debug.LogError("[SpaceMayhemSetup] Spaceship not found."); return; }
            var controller = ship.GetComponent<SpaceMayhem.SpaceshipController>();
            if (controller == null) { Debug.LogError("[SpaceMayhemSetup] SpaceshipController not found on Spaceship."); return; }
            follower.target = ship.transform;
            follower.controllerRef = controller;
            cam.tag = "MainCamera";
            EditorUtility.SetDirty(follower);
            EditorUtility.SetDirty(cam);
            Debug.Log("[SpaceMayhemSetup] Wired CameraFollower → Spaceship. Tag = MainCamera.");
        }

        [MenuItem("SpaceMayhem/Wire SpaceshipInput → InputActions Asset")]
        public static void WireInputActionsAsset()
        {
            var ship = GameObject.Find("Spaceship");
            if (ship == null) { Debug.LogError("[SpaceMayhemSetup] Spaceship not found."); return; }
            var inp = ship.GetComponent<SpaceMayhem.SpaceshipInput>();
            if (inp == null) { Debug.LogError("[SpaceMayhemSetup] SpaceshipInput not found on Spaceship."); return; }
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>("Assets/InputActions/SpaceshipInputActions.inputactions");
            if (asset == null) { Debug.LogError("[SpaceMayhemSetup] SpaceshipInputActions.inputactions not found."); return; }
            inp.inputActions = asset;
            EditorUtility.SetDirty(inp);
            Debug.Log("[SpaceMayhemSetup] Wired SpaceshipInput.inputActions → SpaceshipInputActions asset.");
        }

        [MenuItem("SpaceMayhem/Wire ShipVisual on Controller")]
        public static void WireShipVisual()
        {
            var ship = GameObject.Find("Spaceship");
            if (ship == null) { Debug.LogError("[SpaceMayhemSetup] Spaceship not found."); return; }
            var controller = ship.GetComponent<SpaceMayhem.SpaceshipController>();
            if (controller == null) { Debug.LogError("[SpaceMayhemSetup] SpaceshipController not found."); return; }
            var visual = ship.transform.Find("ShipVisual");
            if (visual == null) { Debug.LogError("[SpaceMayhemSetup] 'ShipVisual' child not found under Spaceship."); return; }
            controller.visualMesh = visual;
            EditorUtility.SetDirty(controller);
            Debug.Log("[SpaceMayhemSetup] Wired SpaceshipController.visualMesh → ShipVisual child.");
        }

        [MenuItem("SpaceMayhem/Create Speed UI")]
        public static void CreateSpeedUI()
        {
            // ── Find the ship controller ──────────────────────────────────────
            var ship = GameObject.Find("Spaceship");
            if (ship == null) { Debug.LogError("[SpaceMayhemSetup] Spaceship not found."); return; }
            var controller = ship.GetComponent<SpaceMayhem.SpaceshipController>();
            if (controller == null) { Debug.LogError("[SpaceMayhemSetup] SpaceshipController not found on Spaceship."); return; }

            // ── Reuse or create Canvas ────────────────────────────────────────
            var existingCanvas = Object.FindFirstObjectByType<Canvas>();
            GameObject canvasGO;
            if (existingCanvas != null)
            {
                canvasGO = existingCanvas.gameObject;
                Debug.Log("[SpaceMayhemSetup] Reusing existing Canvas.");
            }
            else
            {
                canvasGO = new GameObject("HUD Canvas");
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 10;
                var scaler = canvasGO.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                canvasGO.AddComponent<GraphicRaycaster>();
            }

            // ── Speed panel (anchor: bottom-left) ────────────────────────────
            var panelGO = new GameObject("SpeedDisplay");
            panelGO.transform.SetParent(canvasGO.transform, false);

            var panelRT = panelGO.AddComponent<RectTransform>();
            // Anchor bottom-left, pivot bottom-left
            panelRT.anchorMin = new Vector2(0f, 0f);
            panelRT.anchorMax = new Vector2(0f, 0f);
            panelRT.pivot     = new Vector2(0f, 0f);
            panelRT.anchoredPosition = new Vector2(40f, 40f);
            panelRT.sizeDelta = new Vector2(320f, 60f);

            // ── TMP label ─────────────────────────────────────────────────────
            var labelGO = new GameObject("SpeedLabel");
            labelGO.transform.SetParent(panelGO.transform, false);

            var labelRT = labelGO.AddComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            var tmp = labelGO.AddComponent<TextMeshProUGUI>();
            tmp.text      = "SPEED  0 m/s";
            tmp.fontSize  = 28f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color     = new Color(1f, 1f, 1f, 0.85f);
            tmp.alignment = TextAlignmentOptions.BottomLeft;

            // ── SpeedDisplay component ────────────────────────────────────────
            var display = panelGO.AddComponent<SpaceMayhem.SpeedDisplay>();
            display.controller = controller;
            display.label      = tmp;

            Undo.RegisterCreatedObjectUndo(canvasGO, "Create Speed UI");
            EditorUtility.SetDirty(canvasGO);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            Debug.Log("[SpaceMayhemSetup] Speed UI created and wired. Run 'Full Scene Wire-up' if ship reference was missing.");
        }

        [MenuItem("SpaceMayhem/Full Scene Wire-up")]
        public static void FullSceneSetup()
        {
            WireCameraFollower();
            WireInputActionsAsset();
            WireShipVisual();
            CreateSpeedUI();
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            Debug.Log("[SpaceMayhemSetup] Full scene wire-up complete (movement + camera only). Scene + assets saved.");
        }
    }
}
#endif
