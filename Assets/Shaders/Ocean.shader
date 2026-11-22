Shader "RacerTN/Ocean"
{
    Properties
    {
        _ShallowColor ("Shallow Color", Color) = (0.25, 0.75, 0.72, 1)
        _DeepColor ("Deep Color", Color) = (0.02, 0.17, 0.30, 1)
        _Absorption ("Color Depth (1 / meters)", Range(0.02, 0.6)) = 0.12
        _ClearDepth ("Seabed Visibility (m)", Range(0.5, 12)) = 3
        _WaveLength ("Main Wavelength (m)", Range(8, 200)) = 40
        _WaveSlope ("Wave Roughness", Range(0, 0.6)) = 0.18
        _Sharpness ("Reflection Sharpness", Range(0, 1)) = 0.92
        _SunGlitter ("Sun Glitter", Range(0, 6)) = 2
        _FoamWidth ("Shore Foam Width (m)", Range(0.05, 3)) = 0.8
        _FoamAmount ("Shore Foam", Range(0, 1)) = 0.55
        _Refraction ("Refraction", Range(0, 0.1)) = 0.02
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half _Absorption;
                half _ClearDepth;
                half _WaveLength;
                half _WaveSlope;
                half _Sharpness;
                half _SunGlitter;
                half _FoamWidth;
                half _FoamAmount;
                half _Refraction;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
                half   fogFactor  : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                OUT.screenPos = ComputeScreenPos(p.positionCS);
                OUT.fogFactor = ComputeFogFactor(p.positionCS.z);
                return OUT;
            }

            float Hash21(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Sum of travelling sine waves: returns the surface slope (dh/dx, dh/dz).
            // Short waves fade out with distance, so nothing shimmers or flickers.
            float2 WaveSlope(float2 pos, float t, float camDist)
            {
                float2 slope = float2(0, 0);
                [unroll]
                for (int i = 0; i < 5; i++)
                {
                    float scale = pow(0.58, i);                       // each wave is 0.58x as long as the previous
                    float L = max(0.5, _WaveLength * scale);
                    float k = 6.2831853 / L;
                    float w = sqrt(9.81 * k);                         // real deep-water wave speed
                    float angle = 0.6 + i * 1.9;                      // a different direction for each wave
                    float2 dir = float2(cos(angle), sin(angle));
                    float phase = k * dot(dir, pos) - w * t;
                    float fade = saturate(1.0 - camDist / (L * 80.0));  // visible up to ~80 wavelengths away
                    float amp = _WaveSlope * pow(0.8, i) * fade;
                    slope += dir * (amp * cos(phase));
                }
                return slope;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 toCam = GetCameraPositionWS() - IN.positionWS;
                float camDist = length(toCam);
                float3 V = toCam / camDist;
                float t = _Time.y;

                float2 slope = WaveSlope(IN.positionWS.xz, t, camDist);
                float3 N = normalize(float3(-slope.x, 1.0, -slope.y));

                // How deep is the water here? (vertical meters above the seabed)
                float2 uv = IN.screenPos.xy / IN.screenPos.w;
                float rawDepth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                    rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
                #endif
                float3 scenePos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float depth = max(0.0, IN.positionWS.y - scenePos.y);

                // Body colour: turquoise in the shallows, deep blue offshore, darker when the sun is low
                Light sun = GetMainLight();
                float daylight = lerp(0.3, 1.0, saturate(sun.direction.y * 2.0));
                float shallowAmount = exp(-depth * _Absorption);
                half3 tint = lerp(_DeepColor.rgb, _ShallowColor.rgb, shallowAmount) * daylight;

                // See the seabed through shallow water, with a little refraction
                half3 seabed = SampleSceneColor(uv + N.xz * _Refraction);
                float clarity = exp(-depth / max(0.1, _ClearDepth));
                half3 body = lerp(tint, seabed * lerp(half3(1, 1, 1), tint * 1.6, 1.0 - clarity), clarity);

                // Sky reflection (stronger at grazing angles) and sun glitter
                float fres = 0.02 + 0.98 * pow(1.0 - saturate(dot(N, V)), 5.0);
                float3 R = reflect(-V, N);
                R.y = abs(R.y);
                half4 enc = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, R, (1.0 - _Sharpness) * 6.0);
                half3 sky = DecodeHDREnvironment(enc, unity_SpecCube0_HDR);

                float3 H = normalize(sun.direction + V);
                float ndh = saturate(dot(N, H));
                float spec = (pow(ndh, 800.0) + pow(ndh, 80.0) * 0.12) * _SunGlitter;
                half3 glitter = sun.color * spec;

                // Soft foam only where the water meets the land
                float foamBand = 1.0 - smoothstep(0.0, _FoamWidth, depth);
                float n = ValueNoise(IN.positionWS.xz * 0.9 + float2(t * 0.15, -t * 0.1)) * 0.6
                        + ValueNoise(IN.positionWS.xz * 2.7 - float2(t * 0.2, t * 0.1)) * 0.4;
                float pulse = 0.5 + 0.5 * sin(t * 0.8 + depth * 6.0);
                float foam = foamBand * smoothstep(0.3, 0.7, n) * _FoamAmount * (0.7 + 0.3 * pulse);

                half3 col = body * (1.0 - fres) + sky * fres + glitter;
                col = lerp(col, half3(0.95, 0.97, 0.97), saturate(foam));
                col = MixFog(col, IN.fogFactor);

                float alpha = saturate(max(depth * 3.0, foam));
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}