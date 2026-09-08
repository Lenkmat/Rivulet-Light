using System.Drawing;
using System.Windows;
using Point = System.Drawing.Point;

namespace RivuletLight.UI;

/// <summary>
/// 系统托盘图标：WinForms NotifyIcon 提供图标与事件，
/// 右键菜单使用 WinUI 风格的 <see cref="TrayMenuWindow"/>（亚克力 + 圆角 + Fluent 图标）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private TrayMenuWindow? _menuWindow;

    private bool _isPaused;
    private bool _disposed;

    /// <summary>切换暂停/恢复时调用，参数表示是否暂停。</summary>
    public Action<bool>? OnTogglePause { get; set; }

    /// <summary>打开设置窗口时调用。</summary>
    public Action? OnOpenSettings { get; set; }

    /// <summary>重启应用时调用（测试用）。</summary>
    public Action? OnRestart { get; set; }

    /// <summary>退出应用时调用。</summary>
    public Action? OnExit { get; set; }

    /// <summary>当前是否处于暂停状态。</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            if (_menuWindow != null)
                _menuWindow.IsPaused = value;
        }
    }

    public TrayIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Rivulet Light",
            Visible = true,
        };
        _notifyIcon.MouseUp += OnTrayMouseUp;
        _notifyIcon.DoubleClick += (_, _) => OnOpenSettings?.Invoke();
    }

    // ── 菜单 ─────────────────────────────────────────────────────────

    /// <summary>右键抬起时显示 WinUI 风格菜单（右键抬起弹出，避免与托盘按下态冲突）。</summary>
    private void OnTrayMouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button != System.Windows.Forms.MouseButtons.Right || _disposed) return;
        ShowMenu();
    }

    private void ShowMenu()
    {
        if (_disposed) return;

        if (_menuWindow == null)
        {
            _menuWindow = new TrayMenuWindow
            {
                // 菜单回调：切换动作由 TrayIcon 统一翻转状态后转发
                OnPauseResume = () =>
                {
                    IsPaused = !IsPaused;
                    OnTogglePause?.Invoke(_isPaused);
                },
                OnOpenSettings = () => OnOpenSettings?.Invoke(),
                OnRestart = () => OnRestart?.Invoke(),
                OnExit = () => OnExit?.Invoke(),
            };
        }
        _menuWindow.IsPaused = _isPaused;

        // 定位：菜单中心对准托盘图标、底边悬于任务栏上方，钳制在工作区内。
        // WinForms 光标坐标为物理像素，按主屏 DPI 换算为 WPF DIP。
        var pos = Point.Empty;
        try { pos = System.Windows.Forms.Cursor.Position; }
        catch { /* 取不到光标位置时回退工作区右下角 */ }

        double scale;
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            scale = g.DpiX / 96.0;
        }
        catch
        {
            scale = 1.0;
        }

        double xDip = pos.IsEmpty ? 0 : pos.X / scale;
        double yDip = pos.IsEmpty ? 0 : pos.Y / scale;

        var wa = SystemParameters.WorkArea;
        double w = _menuWindow.Width;
        double h = _menuWindow.Height;
        double minLeft = wa.Left + 8;
        double minTop = wa.Top + 8;

        _menuWindow.Left = Math.Clamp(xDip - w / 2, minLeft, Math.Max(minLeft, wa.Right - w - 8));
        _menuWindow.Top = Math.Clamp(yDip - h - 12, minTop, Math.Max(minTop, wa.Bottom - h - 8));

        _menuWindow.Show();
        _menuWindow.Activate();
    }

    // ── 图标 ─────────────────────────────────────────────────────────

    /// <summary>程序化生成一个简单的波形图标。</summary>
    private static Icon CreateIcon()
    {
        int size = 32;
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);

        // 绘制一个简单的波形图案
        using var pen = new Pen(Color.Cyan, 2);
        var points = new Point[5];
        int cx = size / 2, cy = size / 2;
        int amp = 8;

        points[0] = new Point(2, cy);
        points[1] = new Point(cx - 6, cy - amp);
        points[2] = new Point(cx, cy + amp);
        points[3] = new Point(cx + 6, cy - amp);
        points[4] = new Point(size - 2, cy);

        g.DrawCurve(pen, points);

        // 第二层波形（更细、略微偏移）
        using var pen2 = new Pen(Color.FromArgb(180, Color.LightBlue), 1.5f);
        var points2 = new Point[5];
        points2[0] = new Point(2, cy + 2);
        points2[1] = new Point(cx - 6, cy + 2 - amp - 2);
        points2[2] = new Point(cx, cy + 2 + amp + 2);
        points2[3] = new Point(cx + 6, cy + 2 - amp - 2);
        points2[4] = new Point(size - 2, cy + 2);
        g.DrawCurve(pen2, points2);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _menuWindow?.Close();
        _menuWindow = null;

        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
