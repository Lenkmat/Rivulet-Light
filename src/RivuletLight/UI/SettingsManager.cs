using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace RivuletLight.UI;

/// <summary>
/// 设置管理器：以 JSON 格式持久化 AppSettings 到 %LOCALAPPDATA%\RivuletLight\settings.json。
/// 值变化时自动 Save() 并通过 SettingsChanged 事件通知外部。
/// </summary>
public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RivuletLight",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static AppSettings? _current;

    /// <summary>当前设置实例（延迟加载）。</summary>
    public static AppSettings Current => _current ??= Load();

    /// <summary>设置变化时触发，参数为更新后的 AppSettings 实例。</summary>
    public static event Action<AppSettings>? SettingsChanged;

    /// <summary>从磁盘加载设置，不存在时返回默认值。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    _current = loaded;
                    return _current;
                }
            }
        }
        catch
        {
            // 反序列化失败时静默回退到默认值
        }

        _current = new AppSettings();
        return _current;
    }

    /// <summary>应用开机自启注册表键。</summary>
    private static void ApplyAutoStart(bool autoStart)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "RivuletLight";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key == null) return;

            if (autoStart)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(valueName, exePath);
            }
            else
            {
                if (key.GetValue(valueName) != null)
                    key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表写入失败时静默忽略
        }
    }

    /// <summary>保存当前设置到磁盘并触发 SettingsChanged 事件。</summary>
    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_current ?? new AppSettings(), JsonOptions);
            File.WriteAllText(SettingsPath, json);

            // 同步开机自启注册表
            ApplyAutoStart(_current?.AutoStart ?? false);

            SettingsChanged?.Invoke(_current!);
        }
        catch
        {
            // 写入失败时静默忽略（不影响主流程）
        }
    }
}