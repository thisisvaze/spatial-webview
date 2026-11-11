Shader "Hidden/SpatialWebView/UnderlayPunchThrough"
{
    SubShader
    {
        // Renders after standard opaque geometry so objects behind the panel are occluded,
        // while objects closer to the user (hands, avatars, controllers, rays) draw over the hole.
        Tags
        {
            "Queue" = "Geometry+10"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "UnderlayPunch"
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend Zero Zero
            ColorMask RGBA

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(0.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
