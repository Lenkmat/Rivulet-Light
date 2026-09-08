using System.Runtime.InteropServices;
using System.Windows.Threading;
// 消除 System.Windows.Forms.Timer 与 System.Threading.Timer 的歧义
using Timer = System.Threading.Timer;

namespace RivuletLight.Rendering;

/// <summary>
/// 渲染循环调度：独立 Timer 线程触发，Dispatcher.Invoke 到 UI 线程更新 D3DImage。
/// 含全屏检测，通过 FullScreenChanged 事件通知外部隐藏/恢复覆盖层。
/// </summary>
public sealed class RenderLoop : IDisposable
{
    // ── P/Invoke：前台窗口几何检测 ─────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    // ── 字段 ─────────────────────────────────────────────────────────
    private readonly Dispatcher _dispatcher;
    private readonly Action<TimeSpan> _onRender;
    private System.Threading.Timer? _timer;
    private int _targetFps = 60;
    private bool _disposed;
    private bool _isRunning;
    private bool _isFullScreen;
    private long _lastTick;
    private long _lastRenderTick;          // 实际执行的上一帧时刻（UI 线程侧计算真实 delta）
    private long _inFlightSince;           // 渲染投递时刻（0=空闲）；超时自恢复防止 lambda 丢失导致永久跳帧
    private int _fullScreenCheckSkip;
    private int _fullScreenStableCount;    // 连续稳定计数，消抖
    private bool _fullScreenPending;        // 待确认的新的全屏状态
    private const int FullScreenStableThreshold = 3; // 连续 3 次确认才触发

    // ── 属性 ─────────────────────────────────────────────────────────
    /// <summary>目标帧率（默认 60）。</summary>
    public int TargetFps
    {
        get => _targetFps;
        set
        {
            _targetFps = Math.Clamp(value, 1, 120);
            if (_isRunning)
                RestartTimer();
        }
    }

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>全屏状态变化事件。true=全屏/演示中，应隐藏覆盖层。</summary>
    public event Action<bool>? FullScreenChanged;

    // ── 构造 ─────────────────────────────────────────────────────────
    public RenderLoop(Dispatcher dispatcher, Action<TimeSpan> onRender)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onRender = onRender ?? throw new ArgumentNullException(nameof(onRender));
    }

    // ── 控制 ─────────────────────────────────────────────────────────
    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _lastTick = Environment.TickCount64;
        _lastRenderTick = _lastTick;
        _timer = new System.Threading.Timer(OnTimerCallback, null, 0, GetIntervalMs());
    }

    public void Stop()
    {
        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
    }

    // ── 定时器逻辑 ───────────────────────────────────────────────────
    private int GetIntervalMs() => Math.Max(1, 1000 / _targetFps);

    private void RestartTimer()
    {
        _timer?.Change(0, GetIntervalMs());
    }

    private void OnTimerCallback(object? state)
    {
        if (!_isRunning || _disposed) return;

        var now = Environment.TickCount64;
        _lastTick = now;

        // 全屏检测（每 60 帧执行一次，避免频繁 P/Invoke）
        _fullScreenCheckSkip++;
        if (_fullScreenCheckSkip >= 60)
        {
            _fullScreenCheckSkip = 0;
            CheckFullScreen();
        }

        // 跳帧 + 超时自恢复：上一次投递尚未执行时跳过；
        // 但若超过 2s 仍未执行（lambda 被 Dispatcher 丢失），强制作废并重新投递，
        // 避免标志永久卡死导致渲染循环静默死亡（画面冻结）。
        long nowTick = Environment.TickCount64;
        long prev = Interlocked.Exchange(ref _inFlightSince, nowTick);
        if (prev != 0)
        {
            if (nowTick - prev < 2000)
            {
                // 仍在执行窗口内：还原标志并跳帧
                Interlocked.Exchange(ref _inFlightSince, prev);
                return;
            }
            RivuletLight.Core.Logger.Log("[RenderLoop] 渲染投递超时，强制恢复渲染循环");
        }

        // 异步投递渲染到 UI 线程（BeginInvoke 不等待）：
        // 同步 Invoke 在 UI 线程繁忙/Dispatcher 关闭时会抛 TaskCanceledException，
        // Timer 线程回调中的未处理异常将直接终止进程（事件日志已证实此崩溃路径）
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _inFlightSince, 0);
            if (!_isRunning || _disposed) return;

            // 在 UI 线程侧计算真实帧间隔（跳帧后仍正确反映渲染节奏）
            var renderNow = Environment.TickCount64;
            var delta = TimeSpan.FromMilliseconds(renderNow - _lastRenderTick);
            _lastRenderTick = renderNow;
            if (delta.TotalMilliseconds < 1) return;

            try
            {
                _onRender(delta);
            }
            catch (Exception ex)
            {
                RivuletLight.Core.Logger.LogError("[RenderLoop] 渲染回调异常（已跳过该帧）", ex);
            }
        }, DispatcherPriority.Render);
    }

    // ── 全屏检测 ─────────────────────────────────────────────────────
    /// <summary>
    /// 通过前台窗口几何判断全屏：前台窗口矩形覆盖整个主屏时视为全屏。
    /// 比 SHQueryUserNotificationState 更可靠（后者在某些环境下持续误报）。
    /// </summary>
    private void CheckFullScreen()
    {
        try
        {
            // 调试开关：设置 RIVULET_NO_FULLSCREEN_HIDE=1 时禁用全屏隐藏（验证渲染效果用）
            if (Environment.GetEnvironmentVariable("RIVULET_NO_FULLSCREEN_HIDE") == "1") return;

            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;

            // 排除桌面 shell 窗口：显示桌面（Win+D）时 Progman/WorkerW 覆盖全屏，
            // 但并非"全屏应用"，不应隐藏覆盖层
            var className = new System.Text.StringBuilder(32);
            GetClassName(fg, className, 32);
            string cls = className.ToString();
            if (cls is "Progman" or "WorkerW") return;

            // 我们自己的覆盖窗口是 WS_EX_NOACTIVATE，永远不会成为前台，
            // 但防御性跳过桌面窗口句柄
            if (!GetWindowRect(fg, out var rect)) return;

            int screenW = GetSystemMetrics(SmCxScreen);
            int screenH = GetSystemMetrics(SmCyScreen);
            bool fullScreen = rect.Left <= 0 && rect.Top <= 0
                           && rect.Right >= screenW && rect.Bottom >= screenH;

            // 消抖：只有连续 FullScreenStableThreshold 次检测结果一致才触发事件
            if (fullScreen == _fullScreenPending)
            {
                _fullScreenStableCount++;
                if (_fullScreenStableCount >= FullScreenStableThreshold && fullScreen != _isFullScreen)
                {
                    _isFullScreen = fullScreen;
                    _fullScreenStableCount = 0;

                    if (fullScreen)
                    {
                        var sb = new System.Text.StringBuilder(64);
                        GetWindowText(fg, sb, 64);
                        RivuletLight.Core.Logger.Log(
                            $"[RenderLoop] 检测到全屏应用：{sb} ({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");
                    }

                    // 通知外部（异步投递到 UI 线程：Timer 线程不得同步等待 UI，
                    // 避免与渲染回调/事件处理器形成等待链）
                    _dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            FullScreenChanged?.Invoke(_isFullScreen);
                        }
                        catch
                        {
                            // 防御性处理：事件订阅方不应抛异常
                        }
                    });
                }
            }
            else
            {
                _fullScreenPending = fullScreen;
                _fullScreenStableCount = 1;
            }
        }
        catch
        {
            // P/Invoke 失败时静默处理
        }
    }

    // ── 清理 ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}