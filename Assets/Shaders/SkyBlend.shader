// Sky that blends three HDRI photos (day / sunrise-sunset / night) and adds drifting clouds
// plus a sun and moon disc that follow your light. Controlled by TimeOfDay.cs (Sky Mode = BlendedHdri).
// The textures are normal lat-long HDRI images (.hdr or .exr), the same kind Skybox/Panoramic uses.
Shader "RacerTN/SkyBlend"
{
    Properties
    {
        _TexDay ("Day sky (HDRI)", 2D) = "gray" {}
        _TexDusk ("Sunrise / sunset sky (HDRI)", 2D) = "gray" {}
        _TexNight ("Night sky (HDRI)", 2D) = "black" {}
        _Weights ("Weights (day, dusk, night)", Vector) = (1, 0, 0, 0)
        _Yaws ("Rotation in degrees (day, dusk, night)", Vector) = (0, 0, 0, 0)
        _Exposure ("Exposure", Float) = 1
        [HDR] _SunColor ("Sun / moon disc colour", Color) = (1, 0.95, 0.85, 1)
        _SunDir ("Direction to the sun / moon", Vector) = (0, 1, 0, 0)
        _SunSize ("Disc size (degrees)", Float) = 1.2
        _SunGlow ("Glow around the disc", Float) = 0.25
        [HDR] _CloudColor ("Cloud colour", Color) = (1, 1, 1, 1)
        _CloudAmount ("Cloud amount", Range(0, 1)) = 0.4
        _CloudScale ("Cloud size", Float) = 1.5
        _CloudOpacity ("Cloud opacity", Range(0, 1)) = 0.85
        _CloudOffset ("Cloud offset (set by script)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _TexDay, _TexDusk, _TexNight;
            float4 _Weights, _Yaws, _SunDir, _CloudOffset;
            float4 _SunColor, _CloudColor;
            float _Exposure, _SunSize, _SunGlow, _CloudAmount, _CloudScale, _CloudOpacity;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;          // on a skybox mesh the vertex position is the view direction
                return o;
            }

            // Direction -> position in a lat-long image (same layout as Unity's Skybox/Panoramic)
            float2 LatLong (float3 d, float yaw)
            {
                float u = 0.5 - atan2(d.z, d.x) * (0.5 / UNITY_PI);
                float v = 1.0 - acos(clamp(d.y, -1.0, 1.0)) / UNITY_PI;
                return float2(frac(u + yaw / 360.0), saturate(v));
            }

            float3 SampleSky (sampler2D t, float3 d, float yaw)
            {
                return tex2Dlod(t, float4(LatLong(d, yaw), 0, 0)).rgb;
            }

            float Hash (float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Noise (float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash(i);
                float b = Hash(i + float2(1, 0));
                float c = Hash(i + float2(0, 1));
                float d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float Fbm (float2 p)
            {
                float v = 0.0;
                float a = 0.5;
                for (int i = 0; i < 5; i++)
                {
                    v += a * Noise(p);
                    p = p * 2.03 + 17.3;
                    a *= 0.5;
                }
                return v;
            }

            float4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);

                // The three skies, mixed by the weights the script sets from the time of day
                float3 col = float3(0, 0, 0);
                if (_Weights.x > 0.001) col += SampleSky(_TexDay, d, _Yaws.x) * _Weights.x;
                if (_Weights.y > 0.001) col += SampleSky(_TexDusk, d, _Yaws.y) * _Weights.y;
                if (_Weights.z > 0.001) col += SampleSky(_TexNight, d, _Yaws.z) * _Weights.z;
                col *= _Exposure;

                // Clouds on a flat layer high above, so they get smaller toward the horizon
                float cloudCover = 0.0;
                if (_CloudAmount > 0.001 && d.y > 0.0)
                {
                    float2 uv = d.xz / (d.y + 0.2) * _CloudScale + _CloudOffset.xy;
                    float n = Fbm(uv);
                    cloudCover = smoothstep(1.0 - _CloudAmount, 1.0 - _CloudAmount + 0.3, n);
                    cloudCover *= smoothstep(0.0, 0.25, d.y) * _CloudOpacity;
                    col = lerp(col, _CloudColor.rgb, cloudCover);
                }

                // Sun / moon disc with a soft glow, partly hidden by clouds
                float sd = dot(d, normalize(_SunDir.xyz));
                float cosR = cos(radians(_SunSize));
                float disc = smoothstep(cosR - 0.0004, cosR + 0.0004, sd);
                float glow = pow(saturate(sd), 64.0) * _SunGlow;
                float above = smoothstep(-0.03, 0.03, d.y);
                col += _SunColor.rgb * (disc + glow) * above * (1.0 - cloudCover * 0.85);

                return float4(col, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
