namespace RivuletLight.UI;

/// <summary>
/// 颜色模式枚举
/// </summary>
public enum ColorMode
{
    /// <summary>封面取色（SMTC 封面 k-means 调色板）</summary>
    CoverPalette,
    /// <summary>固定色</summary>
    FixedColor,
    /// <summary>氛围自动（回退到系统强调色）</summary>
    AmbientAuto,
}

/// <summary>
/// 应用设置数据模型，与 SettingsManager 配合序列化/反序列化。
/// </summary>
public class AppSettings
{
    /// <summary>效果总开关</summary>
    public bool EffectEnabled { get; set; } = true;

    /// <summary>有效频段数量（16–96），越高横向过渡越细腻</summary>
    public int SpectrumBands { get; set; } = 96;

    /// <summary>回落强度（1–10）：频谱峰值回落速度，越低越顺滑。
    /// 内部映射：频段回落系数 = 值 × 0.025，标量回落系数 = 值 × 0.04</summary>
    public double FallStrength { get; set; } = 10.0;

    /// <summary>频谱峰值高度系数（0.5–2.0），默认 2.00</summary>
    public double PeakHeight { get; set; } = 2.0;

    /// <summary>覆盖层高度（逻辑像素），范围 160–320</summary>
    public int BandHeight { get; set; } = 320;

    /// <summary>整体强度（0.1–1.0，覆盖层淡入目标不透明度）</summary>
    public double OverallIntensity { get; set; } = 1.0;

    /// <summary>颜色模式</summary>
    public ColorMode ColorMode { get; set; } = ColorMode.CoverPalette;

    /// <summary>固定色（仅 ColorMode==FixedColor 时有效），十六进制 ARGB 格式</summary>
    public string FixedColorHex { get; set; } = "#FF4488FF";

    /// <summary>目标帧率（30 或 60）</summary>
    public int FrameRate { get; set; } = 60;

    /// <summary>是否开机自启</summary>
    public bool AutoStart { get; set; } = false;
}
