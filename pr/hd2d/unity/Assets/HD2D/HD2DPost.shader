// HD-2D look for FF6 Pixel Remaster (built-in pipeline, Unity 2019.4, gamma colour space).
// The plugin runs these passes on the game's main-game render texture (field, world map
// or battle) before the game composites it with the UI, so menus and text stay sharp.
// Blur, bloom, grade, vignette and dust follow the user's own HD-2D battle look
// (roguejrpg: Hidden/Hd2d/Post): a 13-tap downsample pyramid of four levels, blur picked
// per pixel in pyramid levels, bloom from the two softest levels.
//
//   0 Upscale   sharp-bilinear upscale of the pixel art to about screen size
//   1 Down      13-tap downsample (wide and even, so the pyramid doesn't swim while scrolling)
//   2 Final     tilt-shift blur + haze + bloom + dust + grade + vignette
//   3 Backdrop  battle backdrop only, in place before the characters are drawn: a floor
//               that stays sharp where the units stand and blurs toward the horizon, and a
//               back wall/sky past the horizon, blurred and hazed in its own colour
Shader "Hidden/HD2D/Post"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    Texture2D _MainTex;   float4 _MainTex_TexelSize;
    Texture2D _L1, _L2, _L3, _L4;
    float4 _L1_TexelSize, _L2_TexelSize, _L3_TexelSize, _L4_TexelSize;
    // Inline sampler: the game's render textures are point-filtered; this one always
    // filters linearly and clamps.
    SamplerState hd2d_linear_clamp_sampler;

    float4 _SharpScale;   // xy output pixels per source texel
    float4 _View;         // visible UV range: x ul, y uh, z vl, w vh
    float4 _Focus;        // x centre (0 bottom .. 1 top), y half-width, z falloff above, w falloff below
    float4 _Blur;         // x levels above, y levels below, z level offset (subtracted), w debug view
    float4 _Set;          // battle: x units' near edge, y units' far edge, z horizon (screen y)
    float4 _SetBlur;      // battle: x near levels, y far levels, z wall haze, w 1 = haze takes the wall's colour
    float4 _Haze;         // x amount, y start (screen y)
    float4 _HazeColor;
    float4 _Bloom;        // x threshold, y intensity
    float4 _Motes;        // x strength
    float4 _MotesColor;
    float4 _Shadows;      // rgb added to dark parts
    float4 _Highlights;   // rgb added to bright parts
    float4 _Grade;        // x contrast (S-curve amount), y saturation, z exposure, w vignette
    float4 _Aspect;       // x visible width / height

    half4 S(Texture2D t, float2 uv) { return t.SampleLevel(hd2d_linear_clamp_sampler, uv, 0); }

    // position on the visible screen (the game shows only part of the texture on some
    // aspect ratios), 0..1, y = 0 at the bottom
    float2 Visible(float2 uv)
    {
        return float2((uv.x - _View.x) / max(_View.y - _View.x, 1e-4),
                      (uv.y - _View.z) / max(_View.w - _View.z, 1e-4));
    }

    float3 Tent(Texture2D tex, float4 ts, float2 uv)
    {
        float2 d = ts.xy;
        float3 s = S(tex, uv).rgb * 4.0;
        s += (S(tex, uv + float2(d.x, 0)).rgb + S(tex, uv - float2(d.x, 0)).rgb +
              S(tex, uv + float2(0, d.y)).rgb + S(tex, uv - float2(0, d.y)).rgb) * 2.0;
        s += S(tex, uv + d).rgb + S(tex, uv - d).rgb +
             S(tex, uv + float2(d.x, -d.y)).rgb + S(tex, uv + float2(-d.x, d.y)).rgb;
        return s / 16.0;
    }

    // blur of `coc` pyramid levels: 0 = sharp, 1..4 = L1..L4, fractions blend
    float3 Pyramid(float3 sharp, float2 uv, float coc)
    {
        float3 col = sharp;
        if (coc > 0.0)    col = lerp(col, Tent(_L1, _L1_TexelSize, uv), saturate(coc));
        if (coc > 1.0)    col = lerp(col, Tent(_L2, _L2_TexelSize, uv), saturate(coc - 1.0));
        if (coc > 2.0)    col = lerp(col, Tent(_L3, _L3_TexelSize, uv), saturate(coc - 2.0));
        if (coc > 3.0)    col = lerp(col, Tent(_L4, _L4_TexelSize, uv), saturate(coc - 3.0));
        return col;
    }

    float3 DebugTint(float3 col, float coc)
    {
        return _Blur.w > 0.5 ? lerp(col, float3(1, 0.1, 0.1), saturate(coc / 4.0) * 0.65) : col;
    }

    // ---- 0: upscale ----
    // Pixel art stays crisp at non-integer scales: point sampling inside each source
    // texel, a linear blend only across the output pixel at its edge.
    half4 FragUpscale(v2f_img i) : SV_Target
    {
        float2 size = _MainTex_TexelSize.zw;
        float2 texel = i.uv * size;
        float2 scale = max(_SharpScale.xy, 1);
        float2 range = max(0.5 - 0.5 / scale, 0);
        float2 dist = frac(texel) - 0.5;
        float2 f = (dist - clamp(dist, -range, range)) * scale + 0.5;
        return S(_MainTex, (floor(texel) + f) / size);
    }

    // ---- 1: 13-tap downsample ----
    half4 FragDown(v2f_img i) : SV_Target
    {
        float2 t = _MainTex_TexelSize.xy;
        float2 uv = i.uv;
        float4 a = S(_MainTex, uv + t * float2(-1.0, -1.0));
        float4 b = S(_MainTex, uv + t * float2( 0.0, -1.0));
        float4 c = S(_MainTex, uv + t * float2( 1.0, -1.0));
        float4 d = S(_MainTex, uv + t * float2(-0.5, -0.5));
        float4 e = S(_MainTex, uv + t * float2( 0.5, -0.5));
        float4 f = S(_MainTex, uv + t * float2(-1.0,  0.0));
        float4 g = S(_MainTex, uv);
        float4 h = S(_MainTex, uv + t * float2( 1.0,  0.0));
        float4 k = S(_MainTex, uv + t * float2(-0.5,  0.5));
        float4 l = S(_MainTex, uv + t * float2( 0.5,  0.5));
        float4 m = S(_MainTex, uv + t * float2(-1.0,  1.0));
        float4 n = S(_MainTex, uv + t * float2( 0.0,  1.0));
        float4 o = S(_MainTex, uv + t * float2( 1.0,  1.0));
        float4 sum = (d + e + k + l) * 0.125;
        sum += (a + b + g + f) * 0.03125;
        sum += (b + c + h + g) * 0.03125;
        sum += (f + g + n + m) * 0.03125;
        sum += (g + h + o + n) * 0.03125;
        return sum;
    }

    float Hash21(float2 p)
    {
        p = frac(p * float2(123.34, 456.21));
        p += dot(p, p + 45.32);
        return frac(p.x * p.y);
    }

    // Soft dots on a grid, one in some cells, rising slowly and wandering.
    float Motes(float2 s, float scale, float rise, float radius, float density, float seed)
    {
        float t = _Time.y;
        float2 p = float2(s.x * _Aspect.x, s.y) * scale;
        p += float2(sin(t * 0.11 + seed) * 0.6, -t * rise);
        float2 cell = floor(p);
        float2 f = frac(p);
        float on = step(1.0 - density, Hash21(cell + seed));
        float2 c = float2(Hash21(cell + seed + 1.7), Hash21(cell + seed + 4.3)) * 0.6 + 0.2;
        c += 0.12 * float2(sin(t * (0.7 + c.y) + c.x * 6.28), cos(t * (0.5 + c.x) + c.y * 6.28));
        float d = length(f - c);
        float twinkle = 0.6 + 0.4 * sin(t * (1.0 + Hash21(cell + seed + 9.1) * 2.0) + c.x * 12.0);
        return on * smoothstep(radius, radius * 0.2, d) * twinkle;
    }

    // Interleaved gradient noise: a little dither against banding in the 8-bit output.
    float Noise(float2 px) { return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715)))); }

    // ---- 2: final ----
    half4 FragFinal(v2f_img i) : SV_Target
    {
        float2 uv = i.uv;
        float2 s = Visible(uv);
        half4 src = S(_MainTex, uv);

        // tilt-shift: sharp band around the focus line, blur growing above and below it
        float dy = s.y - _Focus.x;
        float t = dy > 0 ? saturate((dy - _Focus.y) / max(_Focus.z, 1e-4))
                         : saturate((-dy - _Focus.y) / max(_Focus.w, 1e-4));
        t = t * t * (3.0 - 2.0 * t);
        float coc = t * (dy > 0 ? _Blur.x : _Blur.y) - _Blur.z;
        float3 col = Pyramid(src.rgb, uv, coc);

        // haze towards the top of the screen (the far side in a top-down view)
        col = lerp(col, _HazeColor.rgb, _Haze.x * smoothstep(_Haze.y, 1.0, s.y));

        // bloom from the two softest levels
        float3 glow = Tent(_L3, _L3_TexelSize, uv) * 0.55 + Tent(_L4, _L4_TexelSize, uv) * 0.45;
        float bright = max(glow.r, max(glow.g, glow.b));
        col += glow * saturate((bright - _Bloom.x) / max(1e-3, 1.0 - _Bloom.x)) * _Bloom.y;

        // dust: small motes and a few big out-of-focus ones
        if (_Motes.x > 0.0)
        {
            float motes = Motes(s, 14.0, 0.015, 0.06, 0.2, 0.0) * 0.9 +
                          Motes(s, 5.0, 0.008, 0.16, 0.3, 7.0) * 0.3;
            col += motes * _MotesColor.rgb * _Motes.x;
        }

        // grade: cool shadows, warm highlights, a gentle S-curve, saturation
        float l = dot(col, float3(0.299, 0.587, 0.114));
        col += (1.0 - l) * _Shadows.rgb + l * _Highlights.rgb;
        col = saturate(col);
        col = lerp(col, col * col * (3.0 - 2.0 * col), _Grade.x);
        float g = dot(col, float3(0.299, 0.587, 0.114));
        col = g + (col - g) * _Grade.y;
        col *= _Grade.z;

        // vignette
        float2 v = (s - 0.5) * 2.0;
        col *= 1.0 - pow(saturate(length(v) - 0.55), 1.6) * _Grade.w;

        col = DebugTint(col, coc);
        if (_Blur.w > 0.5 && abs(dy) < 0.003) col = float3(1, 1, 0);
        col += (Noise(i.pos.xy) - 0.5) / 255.0;
        return half4(saturate(col), src.a);
    }

    // ---- 3: battle backdrop ----
    half4 FragBackdrop(v2f_img i) : SV_Target
    {
        float2 uv = i.uv;
        float2 s = Visible(uv);
        half4 src = S(_MainTex, uv);

        // floor: sharp between the units' near and far edges; blur grows from their far
        // edge to the horizon (eased, so the floor meets the wall without a crease) and
        // from their near edge to the bottom of the screen; the wall is all far
        float far = saturate((s.y - _Set.y) / max(_Set.z - _Set.y, 1e-4));
        far = far * far * (3.0 - 2.0 * far);
        float near = saturate((_Set.x - s.y) / max(_Set.x, 1e-4));
        float coc = max(far * _SetBlur.y, near * _SetBlur.x) - _Blur.z;
        float3 col = Pyramid(src.rgb, uv, coc);

        // the far wall fades into the air: its own average colour, a third of the way to white
        float3 haze = _HazeColor.rgb;
        if (_SetBlur.w > 0.5)
        {
            float y = lerp(_View.z + _Set.z * (_View.w - _View.z), _View.w, 0.5);
            float3 w = 0;
            for (int k = 0; k < 5; k++)
                w += S(_L4, float2(lerp(_View.x, _View.y, (k + 0.5) / 5.0), y)).rgb;
            haze = lerp(w / 5.0, 1.0, 0.33);
        }
        col = lerp(col, haze, _SetBlur.z * pow(far, 0.8));
        col = DebugTint(col, coc);
        return half4(col, src.a);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass // 0
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragUpscale
            #pragma target 3.5
            ENDCG
        }
        Pass // 1
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragDown
            #pragma target 3.5
            ENDCG
        }
        Pass // 2
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragFinal
            #pragma target 3.5
            ENDCG
        }
        Pass // 3
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragBackdrop
            #pragma target 3.5
            ENDCG
        }
    }
    Fallback Off
}
