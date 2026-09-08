using System.IO;

namespace RivuletLight.Core;

/// <summary>
/// 简单文件日志：追加写入 %LOCALAPPDATA%\RivuletLight\log.txt（带时间戳），自动创建目录。
/// 线程安全；写入失败时静默忽略，避免影响主流程。
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();

    /// <summary>日志文件完整路径：%LOCALAPPDATA%\RivuletLight\log.txt</summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RivuletLight",
        "log.txt");

    /// <summary>记录普通日志。</summary>
    public static void Log(string message) => Write("INFO", message);

    /// <summary>记录错误日志。</summary>
    public static void LogError(string message) => Write("ERROR", message);

    /// <summary>记录错误日志（含异常类型、消息与堆栈）。</summary>
    public static void LogError(string message, Exception ex)
        => Write("ERROR", $"{message}{Environment.NewLine}{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    /// <summary>格式化并追加写入一行日志。</summary>
    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                // 自动创建日志目录（已存在时无副作用）
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败时静默：不能因日志问题拖垮主流程
        }
    }
}
