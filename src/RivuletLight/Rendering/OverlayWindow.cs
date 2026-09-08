using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// 消除 System.Drawing.Brushes 与 System.Windows.Media.Brushes 的歧义
using Brushes = System.Windows.Media.Brushes;
// 消除 System.Drawing.Image 与 System.Windows.Controls.Image 的歧义
using Image = System.Windows.Controls.Image;

namespace RivuletLight.Rendering;

/// <summary>
/// 透明覆盖窗口：全宽、底边对齐屏幕底端、点击穿透、置顶、不抢占焦点。
/// 提供两种显示通道：D3DImage（正常路径）与 WriteableBitmap Image（回退路径）。
/// </summary>
public sealed class OverlayWindow : Window
{
    /// <summary>覆盖条带高度（逻辑像素），后续设置可调。</summary>
    public const int DefaultOverlayHeight = 260;

    // ── P/Invoke ────────────────────────────────────────────────────
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x8000000;
    private const int WsExToolWindow = 0x80;
    private const int WsExTopmost = 0x00000008;

    private const int GwlExStyle = -20;

    private const int SwpNoActivate = 0x0010;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpShowWindow = 0x0040;

    private const uint GwHwndPrev = 3; // z 序中上方相邻窗口

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, int uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    // HWND_TOPMOST：置顶标志（比 -1 语义更明确地置于所有顶侧窗口之上）
    private static readonly IntPtr HwndTopMost = new(-1);

    // ── 字段 ─────────────────────────────────────────────────────────
    private readonly int _overlayHeight;
    private readonly IntPtr _taskbarHwnd;   // 任务栏句柄：覆盖层需插到其正下方
    private readonly D3DImage _d3dImage = new();
    private readonly Image _d3dImageHost = new();   // 承载 D3DImage 的 Image 控件
    private readonly Image _fallbackImage = new(); // 承载 WriteableBitmap 的 Image 控件
    private DispatcherTimer? _zOrderTimer;
    private bool _shown;

    // ── 属性 ─────────────────────────────────────────────────────────
    /// <summary>供渲染引擎写入的 D3DImage。</summary>
    public D3DImage D3DImage => _d3dImage;

    /// <summary>回退模式下的 Image 控件，用于显示 WriteableBitmap。</summary>
    public Image FallbackImage => _fallbackImage;

    /// <summary>切换到回退显示模式（隐藏 D3DImage 通道，显示 WriteableBitmap 通道）。</summary>
    public void EnableFallbackMode()
    {
        _d3dImageHost.Visibility = Visibility.Hidden;
        _fallbackImage.Visibility = Visibility.Visible;
    }

    // ── 构造 ─────────────────────────────────────────────────────────
    /// <param name="taskbarHwnd">任务栏（Shell_TrayWnd）句柄；有效时覆盖层保持在任务栏正下方、
    /// 其他非全屏应用之上，避免遮挡任务栏。</param>
    public OverlayWindow(IntPtr taskbarHwnd = default, int overlayHeight = DefaultOverlayHeight)
    {
        _taskbarHwnd = taskbarHwnd;
        _overlayHeight = overlayHeight;

        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;

        // 初始尺寸，Show 时重新定位
        Width = SystemParameters.PrimaryScreenWidth;
        Height = overlayHeight;
        Top = SystemParameters.PrimaryScreenHeight - overlayHeight;
        Left = 0;

        // 两个显示通道：D3DImage（默认）与 WriteableBitmap 回退（默认隐藏）
        _d3dImageHost.Source = _d3dImage;
        _d3dImageHost.Stretch = Stretch.Fill;
        _fallbackImage.Stretch = Stretch.Fill;
        _fallbackImage.Visibility = Visibility.Hidden;

        Content = new Grid
        {
            Children =
            {
                _d3dImageHost,
                _fallbackImage,
            }
        };
    }

    // ── 生命周期 ─────────────────────────────────────────────────────
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 应用扩展样式：点击穿透 + 不抢焦点 + 不出现在任务栏/Alt-Tab
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle,
            exStyle | WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    public new void Show()
    {
        base.Show();
        if (_shown) return;
        _shown = true;

        // DPI 感知底部对齐
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var screenWidth = (int)(SystemParameters.PrimaryScreenWidth * dpiScale);
        var screenHeight = (int)(SystemParameters.PrimaryScreenHeight * dpiScale);
        var overlayHeight = (int)(_overlayHeight * dpiScale);

        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, InsertAfterTarget(), 0, screenHeight - overlayHeight,
            screenWidth, overlayHeight, SwpNoActivate | SwpShowWindow);

        // 启动 z 序按需重断言定时器（1s，仅在实际丢失位置时才真正 SetWindowPos）
        _zOrderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000),
            DispatcherPriority.Normal,
            OnZOrderTimerTick,
            Dispatcher);
    }

    protected override void OnClosed(EventArgs e)
    {
        _zOrderTimer?.Stop();
        _zOrderTimer = null;
        base.OnClosed(e);
    }

    /// <summary>调整覆盖窗口高度并重新定位到底部。</summary>
    public void Resize(int newHeight)
    {
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var screenWidth = (int)(SystemParameters.PrimaryScreenWidth * dpiScale);
        var screenHeight = (int)(SystemParameters.PrimaryScreenHeight * dpiScale);
        var overlayHeight = (int)(newHeight * dpiScale);

        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, InsertAfterTarget(), 0, screenHeight - overlayHeight,
            screenWidth, overlayHeight, SwpNoActivate | SwpShowWindow);
    }

    /// <summary>
    /// SetWindowPos 的 hWndInsertAfter 目标：任务栏句柄有效时插到任务栏正下方
    /// （z 序 = 任务栏之下、其他 topmost/普通应用之上，不遮挡任务栏）；
    /// 无任务栏句柄时退回 HWND_TOPMOST。
    /// </summary>
    private IntPtr InsertAfterTarget()
        => _taskbarHwnd != IntPtr.Zero ? _taskbarHwnd : HwndTopMost;

    /// <summary>
    /// 按需重断言 z 序：仅当覆盖层上方的相邻窗口不是任务栏时（被其他 topmost 窗口插入），
    /// 才重新插到任务栏正下方。无条件的 SetWindowPos 每次都会强制 DWM 重新合成（闪烁来源）。
    /// </summary>
    public void EnsureZOrderBelowTaskbar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _taskbarHwnd == IntPtr.Zero) return;

        // 保持 topmost 样式（与任务栏同段，插入位置才稳定有效）
        if ((GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) == 0)
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTopmost);

        if (GetWindow(hwnd, GwHwndPrev) == _taskbarHwnd) return; // 已就位

        SetWindowPos(hwnd, _taskbarHwnd, 0, 0, 0, 0,
            SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    private void OnZOrderTimerTick(object? sender, EventArgs e)
    {
        EnsureZOrderBelowTaskbar();
    }
}
