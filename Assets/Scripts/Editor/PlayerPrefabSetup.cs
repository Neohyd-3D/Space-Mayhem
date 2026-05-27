#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpaceMayhem.Editor
{
    /// <summary>
    /// One-shot tool.  Run from  SpaceMayhem → Create Player Prefab.
    ///
    /// Hierarchy produced
    /// ──────────────────
    ///   PlayerRoot  (empty transform — move this to reposition the whole player)
    ///   ├── [Ship GameObject]       — SpaceshipController, SpaceshipInput, MomentumSystem …
    ///   └── [VirtualCamera GameObject] — ShipCameraTarget, CinemachineCamera …
    ///
    /// The Main Camera (CinemachineBrain) stays in the scene — it is a scene-level
    /// singleton and must NOT be inside the prefab for multiplayer.
    ///
    /// The prefab is saved to  Assets/Prefabs/Player.prefab.
    /// Re-running is safe: if the prefab already exists it is overwritten.
    /// </summary>
    public static class PlayerPrefabSetup
    {
        const string PREFAB_PATH = "Assets/Prefabs/Player.prefab";

        [MenuItem("SpaceMayhem/Create Player Prefab")]
        public static void CreatePlayerPrefab()
        {
            // ── 1. Find ship ──────────────────────────────────────────────────
            var shipCtrl = Object.FindFirstObjectByType<SpaceshipController>();
            if (shipCtrl == null)
            {
                Debug.LogError("[PlayerPrefabSetup] No SpaceshipController found in scene.");
                return;
            }
            GameObject ship = shipCtrl.gameObject;

            // Walk up to scene root in case the ship is already nested somewhere
            while (ship.transform.parent != null)
                ship = ship.transform.parent.gameObject;

            // ── 2. Find virtual camera ────────────────────────────────────────
            var vcamComp = Object.FindFirstObjectByType<Unity.Cinemachine.CinemachineCamera>();
            if (vcamComp == null)
            {
                Debug.LogError("[PlayerPrefabSetup] No CinemachineCamera found in scene. " +
                               "Run SpaceMayhem → Setup Cinemachine Camera first.");
                return;
            }
            GameObject vcam = vcamComp.gameObject;

            while (vcam.transform.parent != null)
                vcam = vcam.transform.parent.gameObject;

            // ── 3. Create / reuse PlayerRoot ─────────────────────────────────
            GameObject root = GameObject.Find("PlayerRoot");
            if (root == null)
            {
                root = new GameObject("PlayerRoot");
                Undo.RegisterCreatedObjectUndo(root, "Create PlayerRoot");
            }

            // Position root at ship's world position so nothing jumps
            root.transform.SetPositionAndRotation(ship.transform.position, Quaternion.identity);

            // Parent ship and vcam under root, preserving world transforms
            Undo.SetTransformParent(ship.transform,  root.transform, "Parent Ship to PlayerRoot");
            Undo.SetTransformParent(vcam.transform,  root.transform, "Parent VCam to PlayerRoot");

            // ── 4. Save prefab ────────────────────────────────────────────────
            bool prefabSuccess;
            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAssetAndConnect(
                root, PREFAB_PATH, InteractionMode.UserAction, out prefabSuccess);

            if (!prefabSuccess || prefabAsset == null)
            {
                Debug.LogError("[PlayerPrefabSetup] Failed to save prefab to " + PREFAB_PATH);
                return;
            }

            EditorUtility.SetDirty(prefabAsset);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                SceneManager.GetActiveScene());

            Debug.Log("[PlayerPrefabSetup] Done.\n" +
                      "  Prefab saved to: " + PREFAB_PATH + "\n" +
                      "  Hierarchy:\n" +
                      "    PlayerRoot\n" +
                      "    ├── " + ship.name + "\n" +
                      "    └── " + vcam.name + "\n" +
                      "  Move 'PlayerRoot' in the scene to reposition the whole player.\n" +
                      "  Save the scene (Ctrl/Cmd+S).");
        }
    }
}
#endif
