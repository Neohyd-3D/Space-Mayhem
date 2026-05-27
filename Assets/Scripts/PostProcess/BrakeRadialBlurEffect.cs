using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SpaceMayhem
{
    [Serializable, VolumeComponentMenu("Post-processing/Custom/Brake Radial Blur")]
    public sealed class BrakeRadialBlurEffect : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        [Tooltip("Strength of the radial blur. 0 disables.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);

        [Tooltip("Screen-space UV center of the radial blur.")]
        public Vector2Parameter center = new Vector2Parameter(new Vector2(0.5f, 0.5f));

        [Tooltip("Number of radial samples. More = smoother but more expensive.")]
        public ClampedIntParameter sampleCount = new ClampedIntParameter(8, 2, 24);

        Material _material;
        const string kShaderName = "Hidden/Post Process/BrakeRadialBlur";

        static readonly int _IntensityID = Shader.PropertyToID("_Intensity");
        static readonly int _CenterID = Shader.PropertyToID("_Center");
        static readonly int _SampleCountID = Shader.PropertyToID("_SampleCount");
        static readonly int _MainTexID = Shader.PropertyToID("_MainTex");

        public bool IsActive() => _material != null && intensity.value > 0f;

        public override CustomPostProcessInjectionPoint injectionPoint
            => CustomPostProcessInjectionPoint.AfterPostProcess;

        public override void Setup()
        {
            var shader = Shader.Find(kShaderName);
            if (shader != null)
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            else
                Debug.LogError($"[BrakeRadialBlur] Shader not found: {kShaderName}");
        }

        public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
        {
            if (_material == null) return;
            _material.SetFloat(_IntensityID, intensity.value);
            _material.SetVector(_CenterID, new Vector4(center.value.x, center.value.y, 0, 0));
            _material.SetInt(_SampleCountID, sampleCount.value);
            _material.SetTexture(_MainTexID, source);
            HDUtils.DrawFullScreen(cmd, _material, destination);
        }

        public override void Cleanup()
        {
            if (_material != null) CoreUtils.Destroy(_material);
        }
    }
}
