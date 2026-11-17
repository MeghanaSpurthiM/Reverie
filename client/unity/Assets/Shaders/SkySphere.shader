Shader "Custom/SkySphere"
{
    Properties
    {
        _MainTex ("Panorama", 2D) = "white" {}
        _Exposure ("Exposure", Range(0, 4)) = 1.0
    }
    SubShader
    {
        // Render before all other geometry, no depth write
        Tags { "Queue" = "Background" "RenderType" = "Background" }
        ZWrite Off
        ZTest Always

        // Render both faces — no culling
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float     _Exposure;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 dir    : TEXCOORD0;        // world-space view direction
                UNITY_VERTEX_OUTPUT_STEREO        // required for multipass stereo
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);   // stereo eye index

                o.pos = UnityObjectToClipPos(v.vertex);

                // Use local vertex position as the sample direction
                // (works from inside or outside the sphere)
                o.dir = normalize(mul((float3x3)unity_ObjectToWorld, v.vertex.xyz));
                return o;
            }

            // Convert world direction to equirectangular UV
            float2 DirToUV(float3 dir)
            {
                float u = 0.5 - atan2(dir.z, dir.x) / (2.0 * UNITY_PI);
                float v = 0.5 + asin(clamp(dir.y, -1.0, 1.0)) / UNITY_PI;
                return float2(u, v);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);  // stereo eye index
                float2 uv  = DirToUV(normalize(i.dir));

                // Fix atan2 seam: the U derivative is huge (~1.0) across the ±π
                // discontinuity, making the sampler fetch across the full panorama
                // in a thin triangle. Clamp derivatives so the seam is invisible.
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);
                if (dx.x >  0.5) dx.x -= 1.0;
                if (dx.x < -0.5) dx.x += 1.0;

                fixed4 col = tex2Dgrad(_MainTex, uv, dx, dy);
                col.rgb   *= _Exposure;
                return col;
            }
            ENDCG
        }
    }
}
