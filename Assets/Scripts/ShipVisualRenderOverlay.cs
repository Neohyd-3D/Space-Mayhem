using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SpaceMayhem
{
    /// <summary>
    /// Redraws ordered local ship visual layers late in the HDRP frame using
    /// a private depth buffer, so walls and track geometry cannot visually cut
    /// into them while each layer still depth-tests correctly against itself.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShipVisualRenderOverlay : MonoBehaviour
    {
        const int MaxOverlayLayers = 16;
        const int TopOverlayRenderingLayerBit = 23;

        static readonly LayerMask EverythingLayerMask = new LayerMask { value = ~0 };

        [System.Serializable]
        public sealed class RenderLayer
        {
            [Tooltip("Optional label. Element 0 renders on top of element 1, element 1 renders on top of element 2, and so on.")]
            public string name;

            [Tooltip("Drag GameObjects or mesh roots here. Child renderers are included when Include Children is on.")]
            public Transform[] roots;

            [Tooltip("Drag individual MeshRenderer / SkinnedMeshRenderer components here.")]
            public Renderer[] renderers;

            [Tooltip("Include renderers under each root, not just a renderer on the root object itself.")]
            public bool includeChildren = true;
        }

        [Header("Source")]
        [Tooltip("Fallback root used when Render Layers is empty. Defaults to SpaceshipController.visualMesh, then this object.")]
        public Transform visualRoot;

        [Tooltip("Fallback include setting used when Render Layers is empty.")]
        public bool includeInactiveRenderers = true;

        [Header("Render Layers")]
        [Tooltip("Ordered overlay slots. Element 0 renders on top of everything else, element 1 just beneath it, then 2, 3, etc. Max 16 slots.")]
        public List<RenderLayer> renderLayers = new List<RenderLayer>();

        static ShipOverlayCustomPass s_Pass;
        static int s_ActiveUsers;
        static readonly int[] s_SlotUseCounts = new int[MaxOverlayLayers];

        readonly Dictionary<Renderer, uint> _originalRenderingLayers = new Dictionary<Renderer, uint>();
        readonly List<Renderer>[] _renderersByLayer = new List<Renderer>[MaxOverlayLayers];
        readonly List<int> _registeredSlots = new List<int>();

        void OnEnable()
        {
            CacheRenderers();
            ApplyOverlayMarker();
            RegisterLayerUsage();
            RegisterPass();
        }

        void OnDisable()
        {
            UnregisterLayerUsage();
            RestoreRenderingLayers();
            UnregisterPass();
        }

        void OnValidate()
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return;
            RefreshOverlayLayers();
        }

        [ContextMenu("Refresh Overlay Layers")]
        public void RefreshOverlayLayers()
        {
            UnregisterLayerUsage();
            RestoreRenderingLayers();
            CacheRenderers();
            ApplyOverlayMarker();
            RegisterLayerUsage();
        }

        void CacheRenderers()
        {
            EnsureLayerLists();

            bool hasCustomLayers = renderLayers != null && renderLayers.Count > 0;
            if (hasCustomLayers)
            {
                int layerCount = Mathf.Min(renderLayers.Count, MaxOverlayLayers);
                for (int i = 0; i < layerCount; i++)
                    CollectLayerRenderers(renderLayers[i], _renderersByLayer[i], includeInactiveRenderers);

                if (renderLayers.Count > MaxOverlayLayers)
                {
                    Debug.LogWarning(
                        $"[ShipVisualRenderOverlay] Only the first {MaxOverlayLayers} render layers are supported. Extra layers are ignored.",
                        this);
                }
                return;
            }

            Transform root = visualRoot;
            if (root == null)
            {
                var controller = GetComponent<SpaceshipController>();
                if (controller != null) root = controller.visualMesh;
            }
            if (root == null) root = transform;

            root.GetComponentsInChildren(includeInactiveRenderers, _renderersByLayer[0]);
        }

        void EnsureLayerLists()
        {
            for (int i = 0; i < _renderersByLayer.Length; i++)
            {
                if (_renderersByLayer[i] == null)
                    _renderersByLayer[i] = new List<Renderer>();
                else
                    _renderersByLayer[i].Clear();
            }
        }

        static void CollectLayerRenderers(RenderLayer layer, List<Renderer> results, bool includeInactive)
        {
            if (layer == null) return;

            var seen = new HashSet<Renderer>();

            if (layer.roots != null)
            {
                foreach (Transform root in layer.roots)
                {
                    if (root == null) continue;

                    if (layer.includeChildren)
                    {
                        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(includeInactive))
                            AddRenderer(renderer, results, seen);
                    }
                    else
                    {
                        AddRenderer(root.GetComponent<Renderer>(), results, seen);
                    }
                }
            }

            if (layer.renderers == null) return;
            foreach (Renderer renderer in layer.renderers)
                AddRenderer(renderer, results, seen);
        }

        static void AddRenderer(Renderer renderer, List<Renderer> results, HashSet<Renderer> seen)
        {
            if (renderer == null || !seen.Add(renderer)) return;
            results.Add(renderer);
        }

        void ApplyOverlayMarker()
        {
            // Walk deepest-to-top so duplicates resolve to the frontmost slot.
            for (int layer = MaxOverlayLayers - 1; layer >= 0; layer--)
            {
                uint slotMask = RenderingLayerMaskForSlot(layer);

                foreach (Renderer renderer in _renderersByLayer[layer])
                {
                    if (renderer == null) continue;

                    if (!_originalRenderingLayers.ContainsKey(renderer))
                        _originalRenderingLayers.Add(renderer, renderer.renderingLayerMask);

                    renderer.renderingLayerMask = (renderer.renderingLayerMask & ~AllOverlayRenderingLayersMask()) | slotMask;
                }
            }
        }

        void RestoreRenderingLayers()
        {
            foreach (var pair in _originalRenderingLayers)
            {
                if (pair.Key != null)
                    pair.Key.renderingLayerMask = pair.Value;
            }
            _originalRenderingLayers.Clear();
        }

        void RegisterLayerUsage()
        {
            _registeredSlots.Clear();
            for (int i = 0; i < MaxOverlayLayers; i++)
            {
                if (_renderersByLayer[i].Count == 0) continue;
                s_SlotUseCounts[i]++;
                _registeredSlots.Add(i);
            }
        }

        void UnregisterLayerUsage()
        {
            foreach (int slot in _registeredSlots)
                s_SlotUseCounts[slot] = Mathf.Max(0, s_SlotUseCounts[slot] - 1);
            _registeredSlots.Clear();
        }

        static void RegisterPass()
        {
            if (s_ActiveUsers++ > 0) return;

            s_Pass = new ShipOverlayCustomPass();
            CustomPassVolume.RegisterGlobalCustomPass(CustomPassInjectionPoint.BeforePostProcess, s_Pass);
        }

        static void UnregisterPass()
        {
            s_ActiveUsers = Mathf.Max(0, s_ActiveUsers - 1);
            if (s_ActiveUsers > 0 || s_Pass == null) return;

            CustomPassVolume.UnregisterGlobalCustomPass(CustomPassInjectionPoint.BeforePostProcess, s_Pass);
            s_Pass = null;
        }

        static uint RenderingLayerMaskForSlot(int slot)
        {
            return 1u << (TopOverlayRenderingLayerBit - slot);
        }

        static uint AllOverlayRenderingLayersMask()
        {
            uint mask = 0u;
            for (int i = 0; i < MaxOverlayLayers; i++)
                mask |= RenderingLayerMaskForSlot(i);
            return mask;
        }

        sealed class ShipOverlayCustomPass : CustomPass
        {
            static readonly RenderStateBlock OpaqueDepthState = new RenderStateBlock(RenderStateMask.Depth)
            {
                depthState = new DepthState(true, CompareFunction.LessEqual),
            };

            static readonly RenderStateBlock TransparentDepthState = new RenderStateBlock(RenderStateMask.Depth)
            {
                depthState = new DepthState(false, CompareFunction.LessEqual),
            };

            public ShipOverlayCustomPass()
            {
                name = "Ship Visual Overlay";
                targetColorBuffer = TargetBuffer.Camera;
                targetDepthBuffer = TargetBuffer.Custom;
                clearFlags = ClearFlag.Depth;
            }

            protected override bool executeInSceneView => false;

            protected override void Execute(CustomPassContext ctx)
            {
                if (ctx.hdCamera.camera.cameraType != CameraType.Game) return;

                for (int slot = MaxOverlayLayers - 1; slot >= 0; slot--)
                {
                    if (s_SlotUseCounts[slot] <= 0) continue;

                    CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.customDepthBuffer.Value, ClearFlag.Depth);

                    uint renderingLayerMask = RenderingLayerMaskForSlot(slot);
                    CustomPassUtils.DrawRenderers(
                        ctx,
                        EverythingLayerMask,
                        CustomPass.RenderQueueType.AllOpaque,
                        overrideMaterial: null,
                        overrideMaterialIndex: 0,
                        overrideRenderState: OpaqueDepthState,
                        sorting: SortingCriteria.CommonOpaque,
                        renderingLayerMask: renderingLayerMask);

                    CustomPassUtils.DrawRenderers(
                        ctx,
                        EverythingLayerMask,
                        CustomPass.RenderQueueType.AllTransparent,
                        overrideMaterial: null,
                        overrideMaterialIndex: 0,
                        overrideRenderState: TransparentDepthState,
                        sorting: SortingCriteria.CommonTransparent,
                        renderingLayerMask: renderingLayerMask);
                }
            }
        }
    }
}
