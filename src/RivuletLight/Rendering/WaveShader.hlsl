// WaveShader.hlsl - Bottom-of-screen wave glow (reactive to music)
// Matches the constant buffer layout in D3D11Engine.
// Constant buffer (PerFrame, b0):
//   _Bands[24]       - 96 band energies (0..1)
//   _BassMidHighRms  - x=Bass y=Mid z=High w=Rms
//   _Palette[4]      - 4-color palette (R,G,B,W)
//   _TimeAndLerp     - x=time (y/z/w reserved, layout-compatible with engine CB)
//
// UV convention: uv.y = 0 at overlay bottom edge, 1 at top edge.
// NOTE: keep this file ASCII-only. Compiler.Compile(string) marshals ANSI and
// multi-byte comments get corrupted by D3DCompile (X3000); CompileFromFile is
// safe, but ASCII keeps every compile path bulletproof.

cbuffer PerFrame : register(b0)
{
    float4 _Bands[24];       // 96 bands
    float4 _BassMidHighRms;  // x=Bass y=Mid z=High w=Rms
    float4 _Palette[4];      // 4-color palette
    float4 _TimeAndLerp;     // x=time
};

struct VSOutput
{
    float4 Pos : SV_POSITION;
    float2 UV  : TEXCOORD0;
};

#define DECAY_RATE 3.5
#define TAU 6.28318531

// -- Vertex Shader ------------------------------------------------------
VSOutput VS(float4 pos : POSITION, float2 uv : TEXCOORD0)
{
    VSOutput output;
    output.Pos = pos;
    output.UV = uv;
    return output;
}

// -- Pixel Shader -------------------------------------------------------
// Output = wave glow (reactive to music), premultiplied alpha.
float4 PS(VSOutput input) : SV_TARGET
{
    float2 uv = input.UV;
    float time = _TimeAndLerp.x;
    float bass = _BassMidHighRms.x;
    float high = _BassMidHighRms.z;
    float rms = _BassMidHighRms.w;

    // Waves: low flow speed (reduced flow coefficient + lower wave numbers
    // suppress the fast scrolling feel)
    float flowBase = 0.12 + high * 0.18;

    float wave = 0.0;
    wave += sin(uv.x * 1.2 * TAU + time * flowBase * 0.8) * 0.35;
    wave += sin(uv.x * 2.8 * TAU + time * flowBase * 1.1 + 1.2) * 0.25;
    wave += sin(uv.x * 4.8 * TAU + time * flowBase * 1.4 + 2.5) * 0.15;
    wave += sin(uv.x * 6.8 * TAU + time * flowBase * 1.7 + 0.8) * 0.10;

    // Bass drives amplitude
    wave *= 1.0 + bass * 0.7;

    // Vertical softness gradient: sharp at bottom, soft at top
    wave *= lerp(1.0, 0.3, uv.y);

    // Soft compression (instead of saturate hard clamp): troughs no longer
    // form dark horizontally-moving boundaries
    float intensity = saturate(0.35 + 0.65 * smoothstep(-1.2, 1.2, wave));

    // Spectrum structure: horizontal position maps to active band count;
    // linear interpolation between adjacent bands removes hard edges
    float bandCount = max(_TimeAndLerp.z, 2.0);
    float fb = saturate(uv.x) * (bandCount - 1.0);
    int bi = min((int)fb, (int)bandCount - 2);
    float ffrac = fb - (float)bi;
    float band = lerp(_Bands[bi >> 2][bi & 3], _Bands[(bi + 1) >> 2][(bi + 1) & 3], ffrac);
    // Adjustable peak height: coefficient delivered via CB from settings
    float bandGlow = 0.5 + _TimeAndLerp.w * band * exp(-uv.y * 4.2);

    // Vertical exponential decay: bright at bottom, fades upward
    float decay = exp(-uv.y * DECAY_RATE);
    float waveAlpha = saturate(intensity * bandGlow * (0.55 + rms * 0.75)) * decay;

    // Color: sin-smooth spatial blend between two accent colors - continuous,
    // differentiable, no segmented boundaries, no panning
    float spatialMix = 0.5 + 0.5 * sin(uv.x * 1.2 + time * 0.08);
    float3 color = lerp(_Palette[1].rgb, _Palette[2].rgb, spatialMix);
    // Brighten dark covers
    color = pow(max(color, 0.0), 0.6);

    // Bass glow boost (brighter at bottom)
    float glow = 1.0 + bass * 0.9 * exp(-uv.y * 2.0);
    float3 outRgb = color * glow;

    // White crest highlight (source of the shimmer)
    float crest = pow(intensity, 6.0) * exp(-uv.y * 3.0);
    outRgb += crest * 0.45;

    // Premultiplied alpha
    outRgb *= waveAlpha;

    // 8-bit output dither: breaks up quantization stair-steps of low-amplitude
    // alpha/brightness gradients on 8-bit render targets. SV_POSITION in PS
    // is the pixel coordinate
    float dither = frac(sin(dot(input.Pos.xy, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
    outRgb += dither / 255.0;

    float outAlpha = saturate(waveAlpha + dither / 255.0);
    if (outAlpha < 0.004) discard;
    return float4(outRgb, outAlpha);
}
