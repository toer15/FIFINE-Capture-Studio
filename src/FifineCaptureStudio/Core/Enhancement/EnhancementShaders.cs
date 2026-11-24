namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>
/// Compute shaders for the enhancement pipeline. Every pass works on linear-ish float RGBA,
/// so the result can be scaled, graded and sharpened without clipping intermediate values.
/// </summary>
internal static class EnhancementShaders
{
    public const string Common = """
        Texture2D<float4> Src  : register(t0);
        RWTexture2D<float4> Dst : register(u0);
        SamplerState Linear : register(s0);

        cbuffer Params : register(b0)
        {
            float4 P0; // brightness, contrast, saturation, vibrance
            float4 P1; // gamma, temperature, blackLevel, shadowDetail
            float4 P2; // highlightRecovery, sharpening, detail, texture
            float4 P3; // edge, aa, deblock, enabled
            uint2  Size;
            float2 Texel;
        };

        float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

        float3 Grading(float3 c)
        {
            // Black level: lift or crush the toe before contrast.
            c = max(c - P1.z * 0.25, 0.0) * (1.0 / max(0.05, 1.0 - P1.z * 0.25));
            c += P0.x * 0.25;                                     // brightness
            c = (c - 0.5) * (1.0 + P0.y) + 0.5;                   // contrast
            c = pow(max(c, 0.0), 1.0 / max(0.2, 1.0 + P1.x));      // gamma
            float l = Luma(c);
            c = lerp(float3(l, l, l), c, 1.0 + P0.z);              // saturation
            float mx = max(c.r, max(c.g, c.b));
            float mn = min(c.r, min(c.g, c.b));
            float sat = mx - mn;
            // Vibrance boosts low-saturation pixels less aggressively than full saturation.
            c += (c - float3(l, l, l)) * P0.w * (1.0 - sat);
            // Temperature / tint (green-magenta) white balance.
            c.r *= 1.0 + P1.y * 0.18;
            c.b *= 1.0 - P1.y * 0.18;
            c.g *= 1.0 + P1.w * 0.10;
            // Shadow detail and highlight recovery, luminance weighted.
            float shadow = saturate(1.0 - l * 2.0);
            c *= 1.0 + P1.w * shadow * 0.35;
            float high = saturate(l * 2.0 - 1.0);
            c = lerp(c, float3(min(l, 0.85), min(l, 0.85), min(l, 0.85)), high * P2.x * 0.55);
            return saturate(c);
        }

        float3 SampleBilinear(float2 uv) { return Src.SampleLevel(Linear, uv, 0).rgb; }
        """;

    /// <summary>Edge-aware 5x5 bilateral denoise + light deblocking of 8x8 compression steps.</summary>
    public const string Denoise = Common + """

        [numthreads(8, 8, 1)]
        void CSDenoise(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= Size.x || id.y >= Size.y) return;
            float2 uv = (id.xy + 0.5) * Texel;
            float3 centre = SampleBilinear(uv);
            float lc = Luma(centre);
            float3 sum = 0;
            float wsum = 0;
            float sigma = max(0.008, P3.x * 0.09);   // P3.x = denoise strength

            [unroll] for (int y = -2; y <= 2; y++)
            {
                [unroll] for (int x = -2; x <= 2; x++)
                {
                    float3 s = SampleBilinear(uv + float2(x, y) * Texel);
                    float d = abs(Luma(s) - lc);
                    float w = exp(-(x * x + y * y) / 8.0) * exp(-(d * d) / (2.0 * sigma * sigma));
                    sum += s * w;
                    wsum += w;
                }
            }

            float3 blur = sum / max(wsum, 0.0001);
            float3 outc = lerp(centre, blur, saturate(P3.z));

            // Deblocking: soften the seam at macroblock boundaries only, keeping real edges.
            if (P3.z > 0.0)
            {
                float2 pixel = id.xy;
                float bx = fmod(pixel.x, 8.0);
                float by = fmod(pixel.y, 8.0);
                if (bx < 1.0 || by < 1.0)
                {
                    float3 n = (SampleBilinear(uv + float2(1, 0) * Texel) + SampleBilinear(uv + float2(-1, 0) * Texel)
                              + SampleBilinear(uv + float2(0, 1) * Texel) + SampleBilinear(uv + float2(0, -1) * Texel)) * 0.25;
                    if (abs(Luma(n) - lc) < 0.06) outc = lerp(outc, n, 0.35 * P3.z);
                }
            }

            Dst[id.xy] = float4(outc, 1.0);
        }
        """;

    /// <summary>Colour grading + texture/edge reconstruction at source resolution.</summary>
    public const string Grade = Common + """

        [numthreads(8, 8, 1)]
        void CSGrade(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= Size.x || id.y >= Size.y) return;
            float2 uv = (id.xy + 0.5) * Texel;
            float3 c = SampleBilinear(uv);

            if (P2.z > 0.0 || P2.w > 0.0 || P3.x > 0.0)
            {
                // Local contrast around the pixel; weighted by detail/edge/texture amounts.
                float3 blur = (SampleBilinear(uv + float2(1, 0) * Texel) + SampleBilinear(uv - float2(1, 0) * Texel)
                             + SampleBilinear(uv + float2(0, 1) * Texel) + SampleBilinear(uv - float2(0, 1) * Texel)) * 0.25;
                float3 hi = c - blur;
                float edge = saturate(length(hi) * 6.0);
                // Strong edges get edge clarity; smooth areas get texture reconstruction only.
                c += hi * (P2.z * 0.8 + P3.x * edge * 1.2 + P2.w * (1.0 - edge) * 0.5);
            }

            c = Grading(c);
            Dst[id.xy] = float4(c, 1.0);
        }
        """;

    /// <summary>Catmull-Rom upscale with contrast-adaptive sharpening at the output resolution.</summary>
    public const string Upscale = Common + """

        float3 SampleCatmullRom(float2 uv)
        {
            float2 pos = uv * Size - 0.5;
            float2 i = floor(pos);
            float2 f = pos - i;
            float2 f2 = f * f;
            float2 f3 = f2 * f;
            // Catmull-Rom weights.
            float2 w0 = -0.5 * f3 + f2 - 0.5 * f;
            float2 w1 = 1.5 * f3 - 2.5 * f2 + 1.0;
            float2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
            float2 w3 = 0.5 * f3 - 0.5 * f2;
            float2 s = Texel;
            float3 r = 0;
            [unroll] for (int y = -1; y <= 2; y++)
            {
                [unroll] for (int x = -1; x <= 2; x++)
                {
                    float2 o = float2(x, y);
                    float wx = (x == -1) ? w0.x : (x == 0 ? w1.x : (x == 1 ? w2.x : w3.x));
                    float wy = (y == -1) ? w0.y : (y == 0 ? w1.y : (y == 1 ? w2.y : w3.y));
                    r += Src.SampleLevel(Linear, uv + o * s, 0).rgb * wx * wy;
                }
            }
            return r;
        }

        [numthreads(8, 8, 1)]
        void CSUpscale(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= Size.x || id.y >= Size.y) return;
            float2 uv = (id.xy + 0.5) * Texel;
            float3 c = SampleCatmullRom(uv);
            c = Grading(c);

            // Contrast-adaptive sharpening (CAS-like): sharpen where local contrast is low,
            // back off on strong edges to avoid halos around text and UI.
            if (P2.y > 0.0)
            {
                float3 a = SampleCatmullRom(uv + float2(1, 0) * Texel);
                float3 b = SampleCatmullRom(uv - float2(1, 0) * Texel);
                float3 d = SampleCatmullRom(uv + float2(0, 1) * Texel);
                float3 e = SampleCatmullRom(uv - float2(0, 1) * Texel);
                float3 mn = min(c, min(min(a, b), min(d, e)));
                float3 mx = max(c, max(max(a, b), max(d, e)));
                float adaptive = saturate(1.0 - (mx.r + mx.g + mx.b - mn.r - mn.g - mn.b));
                c += (c - (a + b + d + e) * 0.25) * P2.y * adaptive;
            }

            // Anti-aliasing: blend a 4-tap rotated sample only where gradients are high.
            if (P3.y > 0.0)
            {
                float3 aa = (SampleCatmullRom(uv + float2(0.5, 0.5) * Texel)
                           + SampleCatmullRom(uv + float2(-0.5, 0.5) * Texel)
                           + SampleCatmullRom(uv + float2(0.5, -0.5) * Texel)
                           + SampleCatmullRom(uv + float2(-0.5, -0.5) * Texel)) * 0.25;
                float edge = saturate(length(c - aa) * 8.0);
                c = lerp(c, aa, P3.y * edge * 0.5);
            }

            Dst[id.xy] = float4(saturate(c), 1.0);
        }
        """;

    /// <summary>Simple bilinear copy used when only colour work is requested (no resampling).</summary>
    public const string Copy = Common + """

        [numthreads(8, 8, 1)]
        void CSCopy(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= Size.x || id.y >= Size.y) return;
            float2 uv = (id.xy + 0.5) * Texel;
            Dst[id.xy] = float4(Src.SampleLevel(Linear, uv, 0).rgb, 1.0);
        }
        """;
}
