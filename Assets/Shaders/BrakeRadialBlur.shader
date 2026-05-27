Shader "Hidden/Post Process/BrakeRadialBlur"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 texcoord   : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.texcoord   = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }

    TEXTURE2D_X(_MainTex);

    float  _Intensity;
    float4 _Center;
    int    _SampleCount;

    float4 CustomPostProcess(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv  = input.texcoord;
        float2 dir = uv - _Center.xy;

        // Strength scales how much we contract toward the center per sample step.
        float strength = _Intensity * 0.18;
        int   samples  = max(_SampleCount, 1);
        float invMax   = 1.0 / (float)max(samples - 1, 1);

        float3 color = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i * invMax;
            float scale = 1.0 - strength * t;
            float2 sUV = _Center.xy + dir * scale;
            // RTHandle scaling for HDRP
            float2 rtUV = sUV * _RTHandleScale.xy;
            color += SAMPLE_TEXTURE2D_X_LOD(_MainTex, s_linear_clamp_sampler, rtUV, 0).rgb;
        }
        color /= (float)samples;

        return float4(color, 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "BrakeRadialBlur"
            ZWrite Off
            ZTest  Always
            Blend  Off
            Cull   Off

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment CustomPostProcess
            ENDHLSL
        }
    }

    Fallback Off
}
