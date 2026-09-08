using System.Numerics;

namespace RivuletLight.Core;

/// <summary>
/// 系统强调色（Windows 个性化自定义主题色）调色板：
/// 封面无法获取（无缩略图/解码失败/SMTC 不可用）时的统一回退。
/// 由 AccentDark2 → AccentLight1 组成暗→亮四色，结构与封面取色板一致。
/// </summary>
public static class SystemAccentPalette
{
    private const long CacheMs = 5000; // 短缓存：避免高频触发时反复创建 WinRT 对象
    private static Palette? _cached;
    private static long _cacheTick;

    /// <summary>获取强调色四色板（暗→亮）。每次返回独立副本，调用方可安全修改。
    /// 读取失败时使用 Windows 默认强调色梯度。</summary>
    public static Palette Get()
    {
        long now = Environment.TickCount64;
        Palette? cached = _cached;
        if (cached != null && now - Volatile.Read(ref _cacheTick) < CacheMs)
            return Clone(cached);

        var palette = BuildFromSystem();
        _cached = palette;
        Volatile.Write(ref _cacheTick, now);
        return Clone(palette); // 返回副本：缓存实例与调用方实例互不共享
    }

    private static Palette BuildFromSystem()
    {
        var palette = new Palette();
        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            palette.Colors[0] = ToVector(uiSettings.GetColorValue(
                Windows.UI.ViewManagement.UIColorType.AccentDark2));
            palette.Colors[1] = ToVector(uiSettings.GetColorValue(
                Windows.UI.ViewManagement.UIColorType.AccentDark1));
            palette.Colors[2] = ToVector(uiSettings.GetColorValue(
                Windows.UI.ViewManagement.UIColorType.Accent));
            palette.Colors[3] = ToVector(uiSettings.GetColorValue(
                Windows.UI.ViewManagement.UIColorType.AccentLight1));
        }
        catch (Exception ex)
        {
            Logger.LogError("读取系统强调色失败，使用固定回退梯度", ex);
            // Windows 默认强调色 #0078D4 的暗→亮梯度
            palette.Colors[0] = new Vector4(0.00f, 0.20f, 0.42f, 1f);
            palette.Colors[1] = new Vector4(0.00f, 0.35f, 0.63f, 1f);
            palette.Colors[2] = new Vector4(0.00f, 0.47f, 0.83f, 1f);
            palette.Colors[3] = new Vector4(0.30f, 0.66f, 0.91f, 1f);
        }
        return palette;
    }

    private static Vector4 ToVector(Windows.UI.Color c)
        => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);

    private static Palette Clone(Palette p)
    {
        var copy = new Palette();
        Array.Copy(p.Colors, copy.Colors, Palette.ColorCount);
        return copy;
    }
}
