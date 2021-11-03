Shader "Unlit/LineShader" {
    Properties {
        _Color ("Color", Color) = (1, 1, 1, 1)
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 100

        Blend SrcAlpha OneMinusSrcAlpha

        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert (appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 _Color;
            float _MaxDistance;
            float _FadeDist;

            float3 hsv2rgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
            }

            fixed4 frag(v2f i) : SV_Target{
                float distFromStart = _MaxDistance - i.uv.x;
                float factor = saturate(1 - distFromStart / _FadeDist);

                float hue = i.uv.x;
                float sat = min(factor, 1 - factor) * 0.8 + 0.2;
                float val = factor * 0.5 + 0.5;
                float alpha = lerp(_Color.a, 1, factor);

                float4 color;
                color.rgb = hsv2rgb(float3(hue, sat, val));
                color.a = alpha;
                return color;
            }
            ENDCG
        }
    }
}
