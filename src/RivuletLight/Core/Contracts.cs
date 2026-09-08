namespace RivuletLight.Core;

/// <summary>频谱帧：96 个对数频段能量（0..1），复用实例避免分配。</summary>
public sealed class SpectrumFrame
{
    public const int BandCount = 96;
    /// <summary>对数分桶后的频段能量，0..1</summary>
    public readonly float[] Bands = new float[BandCount];
    /// <summary>低频能量（20-250Hz 聚合）</summary>
    public float Bass;
    /// <summary>中频能量</summary>
    public float Mid;
    /// <summary>高频能量</summary>
    public float High;
    /// <summary>当前 RMS（0..1 近似）</summary>
    public float Rms;
    /// <summary>音频是否处于激活态（RMS 高于阈值的持续期已满足）</summary>
    public bool Active;
}

/// <summary>音频源契约：WASAPI loopback 采集 + FFT 频谱分析。</summary>
public interface IAudioSource : IDisposable
{
    void Start();
    /// <summary>每次分析完成（约 20ms 周期）触发，参数为复用实例。</summary>
    event Action<SpectrumFrame>? FrameReady;
    /// <summary>音频激活态（供渲染淡入淡出状态机使用）。</summary>
    bool IsAudioActive { get; }
}

/// <summary>调色板（4 色，RGB 各分量 0..1，sRGB 空间）。</summary>
public sealed record Palette
{
    public static readonly int ColorCount = 4;
    public readonly System.Numerics.Vector4[] Colors = new System.Numerics.Vector4[ColorCount];
}

/// <summary>调色板提供者契约：SMTC 封面取色。</summary>
public interface IPaletteProvider : IDisposable
{
    void Start();
    /// <summary>目标调色板变化时触发（渲染端自行做 2s 平滑过渡）。</summary>
    event Action? PaletteChanged;
    /// <summary>当前目标调色板（不含过渡插值）。</summary>
    Palette Current { get; }
}
