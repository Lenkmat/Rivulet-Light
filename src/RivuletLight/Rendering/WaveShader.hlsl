// WaveShader.hlsl - 屏幕底部波光柔光（随音乐律动）
// 与 D3D11Engine 的常量缓冲布局匹配
// 常量缓冲 (PerFrame, b0):
//   _Bands[24]       — 96 频段能量 (0..1)
//   _BassMidHighRms  — x=Bass y=Mid z=High w=Rms
//   _Palette[4]      — 4 色调色板 (R,G,B,W)
//   _TimeAndLerp     — x=time（y/z/w 保留占位，布局与引擎常量缓冲兼容）
//
// UV 约定：uv.y = 0 为覆盖层底边，1 为覆盖层顶边。

cbuffer PerFrame : register(b0)
{
    float4 _Bands[24];       // 96 频段
    float4 _BassMidHighRms;  // x=Bass y=Mid z=High w=Rms
    float4 _Palette[4];      // 4 色调色板
    float4 _TimeAndLerp;     // x=time
};

struct VSOutput
{
    float4 Pos : SV_POSITION;
    float2 UV  : TEXCOORD0;
};

#define DECAY_RATE 3.5
#define TAU 6.28318531

// ── Vertex Shader ──────────────────────────────────────────────────────
VSOutput VS(float4 pos : POSITION, float2 uv : TEXCOORD0)
{
    VSOutput output;
    output.Pos = pos;
    output.UV = uv;
    return output;
}

// ── Pixel Shader ────────────────────────────────────────────────────────
// 输出 = 波光柔光（随音乐律动），预乘 alpha 合成。
float4 PS(VSOutput input) : SV_TARGET
{
    float2 uv = input.UV;
    float time = _TimeAndLerp.x;
    float bass = _BassMidHighRms.x;
    float high = _BassMidHighRms.z;
    float rms = _BassMidHighRms.w;

    // 波形：低速流动（流速过低流速系数与降波数共同压制"快速滚动"感）
    float flowBase = 0.12 + high * 0.18;

    float wave = 0.0;
    wave += sin(uv.x * 1.2 * TAU + time * flowBase * 0.8) * 0.35;
    wave += sin(uv.x * 2.8 * TAU + time * flowBase * 1.1 + 1.2) * 0.25;
    wave += sin(uv.x * 4.8 * TAU + time * flowBase * 1.4 + 2.5) * 0.15;
    wave += sin(uv.x * 6.8 * TAU + time * flowBase * 1.7 + 0.8) * 0.10;

    // 低频驱动幅度
    wave *= 1.0 + bass * 0.7;

    // 垂直柔和度梯度：底部锐利，顶部柔和
    wave *= lerp(1.0, 0.3, uv.y);

    // 软压缩波形（替代 saturate 硬截断）：波谷不再形成深暗的左右移动边界
    float intensity = saturate(0.35 + 0.65 * smoothstep(-1.2, 1.2, wave));

    // 频谱结构：横向位置映射到有效频段数，相邻频段线性插值消除硬边界
    float bandCount = max(_TimeAndLerp.z, 2.0);
    float fb = saturate(uv.x) * (bandCount - 1.0);
    int bi = min((int)fb, (int)bandCount - 2);
    float ffrac = fb - (float)bi;
    float band = lerp(_Bands[bi >> 2][bi & 3], _Bands[(bi + 1) >> 2][(bi + 1) & 3], ffrac);
    // 峰值高度可调：系数由设置界面经常量缓冲下发（默认 1.05）
    float bandGlow = 0.5 + _TimeAndLerp.w * band * exp(-uv.y * 4.2);

    // 垂直指数衰减：底部亮，向上消散
    float decay = exp(-uv.y * DECAY_RATE);
    float waveAlpha = saturate(intensity * bandGlow * (0.55 + rms * 0.75)) * decay;

    // 颜色：两个强调色 sin 平滑空间混合——连续可微、无分段边界、不平移
    float spatialMix = 0.5 + 0.5 * sin(uv.x * 1.2 + time * 0.08);
    float3 color = lerp(_Palette[1].rgb, _Palette[2].rgb, spatialMix);
    // 深色封面提亮
    color = pow(max(color, 0.0), 0.6);

    // 低频辉光加成（底部更亮）
    float glow = 1.0 + bass * 0.9 * exp(-uv.y * 2.0);
    float3 outRgb = color * glow;

    // 波峰白色高光（波光"闪"的来源）
    float crest = pow(intensity, 6.0) * exp(-uv.y * 3.0);
    outRgb += crest * 0.45;

    // 预乘 alpha
    outRgb *= waveAlpha;

    // 8bit 输出抖动：打散低幅度 alpha/亮度渐变在 8bit 渲染目标上的
    // 量化阶梯（从下到上的断层带）。SV_POSITION 在 PS 中即像素坐标
    float dither = frac(sin(dot(input.Pos.xy, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
    outRgb += dither / 255.0;

    float outAlpha = saturate(waveAlpha + dither / 255.0);
    if (outAlpha < 0.004) discard;
    return float4(outRgb, outAlpha);
}
