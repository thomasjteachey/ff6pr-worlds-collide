// A character's shadow (after the user's roguejrpg Hidden/Hd2d/Shadow). Two kinds, one
// renderer each, both drawn between the ground and the characters:
//  - cast: the character's current frame (sprite or battle mesh), flipped onto the ground
//    at its feet, squashed and slanted away from the light, softened at the edges and
//    fading with distance from the feet;
//  - contact (_Contact = 1): a soft ellipse on a unit quad under the feet.
// Colour and strength come from _ShadowColor (rgb, a).
Shader "Hidden/HD2D/Shadow"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
        _ShadowColor ("Colour", Color) = (0, 0, 0, 0.5)
        _Ground ("Ground", Vector) = (0, 0, 0, 1)
        _Contact ("Contact", Float) = 0
    }
    SubShader
    {
        // the field draws ground tiles at AlphaTest, characters at AlphaTest+5; the plugin
        // sets each material's queue (AlphaTest+2 on the field, between backdrop and units
        // in battle)
        Tags { "Queue" = "AlphaTest+2" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            // x: the ground's world y, y: slant (x per unit of height),
            // z: squash (shadow length per unit of height), w: the body's height
            float4 _Ground;
            float4 _ShadowColor;
            float _Contact;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float height : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 w = mul(unity_ObjectToWorld, v.vertex).xyz;
                float h = 0;
                if (_Contact < 0.5)
                {
                    h = w.y - _Ground.x;
                    w.x += h * _Ground.y;
                    w.y = _Ground.x - h * _Ground.z;
                }
                o.pos = mul(UNITY_MATRIX_VP, float4(w, 1.0));
                o.uv = v.uv;
                o.height = h / max(_Ground.w, 1e-3);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float a;
                if (_Contact > 0.5)
                {
                    float2 d = i.uv * 2.0 - 1.0;
                    a = pow(saturate(1.0 - dot(d, d)), 1.6);
                }
                else
                {
                    float2 d = _MainTex_TexelSize.xy;
                    a = tex2D(_MainTex, i.uv).a * 0.4;
                    a += tex2D(_MainTex, i.uv + float2(d.x, 0)).a * 0.15;
                    a += tex2D(_MainTex, i.uv - float2(d.x, 0)).a * 0.15;
                    a += tex2D(_MainTex, i.uv + float2(0, d.y)).a * 0.15;
                    a += tex2D(_MainTex, i.uv - float2(0, d.y)).a * 0.15;
                    a *= 1.0 - saturate(i.height) * 0.6;
                }
                return fixed4(_ShadowColor.rgb, a * _ShadowColor.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
