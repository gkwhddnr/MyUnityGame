Shader "Unlit/PlayerVision"
{
    Properties
    {
        _Center("Center", Vector) = (0.5,0.5,0,0)
        _Radius("Radius", Float) = 0.2
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Pass
        {
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            float4 _Center;
            float _Radius;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // uv (0~1), center (0~1)
                float d = distance(i.uv, _Center.xy);
                // d < R => 투명, d > R => 검정
                float a = d < _Radius ? 0 : 1;
                return fixed4(0,0,0,a);
            }
            ENDCG
        }
    }
}
