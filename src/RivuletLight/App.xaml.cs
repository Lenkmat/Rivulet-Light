using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32; // SystemEvents（电源事件）
using Vortice.Direct3D11;
using RivuletLight.Audio;
using RivuletLight.Core;
using RivuletLight.Media;
using RivuletLight.Rendering;
using RivuletLight.UI;
using Application = System.Windows.Application;

namespace RivuletLight;

/// <summary>
/// 应用入口：集成音频源、调色板提供者、覆盖窗口、D3D11 渲染引擎、
/// DWM 模糊回退和渲染循环，实现完整的端到端屏幕底侧音频律动柔光效果。
/// </summary>
public partial class App : Application
{
    // ── 单实例 ─────────────────────────────────────────────────────────
    private const string MutexName = @"Global\RivuletLight_SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _mutexOwned;

    // ── 模块实例 ───────────────────────────────────────────────────────
    private LoopbackAudioSource? _audioSource;
    private SmtcPaletteProvider? _paletteProvider;
    private OverlayWindow? _overlayWindow;
    private D3D11Engine? _d3dEngine;
    private RenderLoop? _renderLoop;

    // ── 设置与托盘 ─────────────────────────────────────────────────────
    private AppSettings? _appSettings;
    private TrayIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;

    // ── 调色板过渡状态 ─────────────────────────────────────────────────
    // _paletteCurrent: 上一次完成过渡的调色板（状态已稳定）
    // _paletteTarget: 目标调色板（来自 SMTC PaletteChanged 事件）
    // _paletteInterpolated: 每帧插值结果，传入 D3D11Engine
    private readonly Palette _paletteCurrent = CreateDefaultPalette();
    private readonly Palette _paletteTarget = CreateDefaultPalette();
    private readonly Palette _paletteInterpolated = CreateDefaultPalette(); // 初始即为默认调色板（SMTC 无会话时着色器也有颜色可用）
    private float _paletteLerpProgress; // 0 → 2s
    private bool _paletteDirty;         // 调色板过渡进行中

    // ── 音频激活状态 ──────────────────────────────────────────────────
    private bool _audioActive;          // 最近一次从音频源读取的激活状态
    private const double FadeDurationMs = 400; // 淡入淡出时长（WPF DoubleAnimation 平滑执行）

    // ── 全屏 ───────────────────────────────────────────────────────────
    private bool _isFullScreen;

    // ── 计时 ───────────────────────────────────────────────────────────
    private TimeSpan _totalElapsed;     // 着色器累计时间
    private DispatcherTimer? _stateTimer;

    // ═══════════════════════════════════════════════════════════════════
    //  启动 / 退出
    // ═══════════════════════════════════════════════════════════════════

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例检查
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _mutexOwned = createdNew;
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        // 全局异常处理
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        Logger.Log("[App] 启动集成");

        // 1) 音频源
        try
        {
            _audioSource = new LoopbackAudioSource();
            _audioSource.Start();
            Logger.Log("[App] LoopbackAudioSource 已启动");
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] LoopbackAudioSource 启动失败", ex);
        }

        // 2) 调色板提供者
        try
        {
            _paletteProvider = new SmtcPaletteProvider();
            _paletteProvider.Start();
            Logger.Log("[App] SmtcPaletteProvider 已启动");
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] SmtcPaletteProvider 启动失败", ex);
        }

        // 3) 覆盖窗口（初始隐藏；保持任务栏之下、普通应用之上的 z 序，不遮挡任务栏）
        try
        {
            _overlayWindow = new OverlayWindow(FindWindow("Shell_TrayWnd", null));
            _overlayWindow.Show();
            _overlayWindow.Visibility = Visibility.Hidden;
            _overlayWindow.Opacity = 0.0;
            Logger.Log("[App] OverlayWindow 已创建");
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] OverlayWindow 创建失败", ex);
        }

        if (_overlayWindow == null)
        {
            Logger.LogError("[App] OverlayWindow 创建失败，无法继续");
            base.OnStartup(e);
            return;
        }

        int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        int overlayHeight = OverlayWindow.DefaultOverlayHeight;

        // 4) D3D11 渲染引擎
        try
        {
            _d3dEngine = new D3D11Engine(_overlayWindow.D3DImage, screenWidth, overlayHeight);
            Logger.Log("[App] D3D11Engine 已创建");
            if (_d3dEngine.UseFallback)
            {
                // 回退模式：使用 WriteableBitmap 替代 D3DImage
                Logger.Log("[App] 使用 WriteableBitmap 回退渲染模式");
                _overlayWindow.FallbackImage.Source = _d3dEngine.FallbackBitmap;
                _overlayWindow.EnableFallbackMode();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] D3D11Engine 创建失败", ex);
        }

        // 5) 连线事件
        if (_audioSource != null)
            _audioSource.FrameReady += OnFrameReady;

        if (_paletteProvider != null)
            _paletteProvider.PaletteChanged += OnPaletteChanged;

        // 5.5) 系统电源/前缓冲事件：休眠唤醒后 D3D9Ex 共享表面与 WASAPI 采集
        //      可能整体失效（表现为效果不再显示），统一重建自愈
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _overlayWindow.D3DImage.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;

        // 6) 渲染循环
        _renderLoop = new RenderLoop(Dispatcher, OnRender);
        _renderLoop.FullScreenChanged += OnFullScreenChanged;
        _renderLoop.Start();

        // 7) 设置监听
        SettingsManager.SettingsChanged += OnSettingsChanged;

        // 8) 状态定时器（100ms：音频激活检测）
        _stateTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Normal,
            OnStateTimerTick,
            Dispatcher);
        _stateTimer.Start();

        // 11) 加载设置
        _appSettings = SettingsManager.Current;
        Logger.Log("[App] 设置已加载");

        // 应用设置参数
        if (_appSettings.FrameRate is 30 or 60)
            _renderLoop!.TargetFps = _appSettings.FrameRate;

        // 频谱渲染参数（频段数 / 回落强度 / 峰值高度）与覆盖层高度
        ApplySpectrumParams(_appSettings);
        _overlayWindow.Resize(_appSettings.BandHeight);

        // 12) 创建托盘图标
        _trayIcon = new TrayIcon();
        _trayIcon.OnOpenSettings = () =>
        {
            if (_settingsWindow == null || !_settingsWindow.IsVisible)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.Activate();
            }
        };
        _trayIcon.OnTogglePause = paused =>
        {
            if (paused)
            {
                _d3dEngine?.Stop();
                _renderLoop?.Stop();
                _overlayWindow!.Visibility = Visibility.Hidden;
            }
            else
            {
                _d3dEngine?.Start();
                _renderLoop?.Start();
            }
        };
        _trayIcon.OnRestart = () =>
        {
            // 测试用：以相同 exe 路径启动新进程后关闭当前实例
            Logger.Log("[App] 托盘菜单触发应用重启");
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    System.Diagnostics.Process.Start(exe);
                else
                    Logger.LogError("[App] 重启失败：无法获取可执行文件路径", new InvalidOperationException("Environment.ProcessPath 为空"));
            }
            catch (Exception ex)
            {
                Logger.LogError("[App] 重启失败", ex);
            }
            Shutdown();
        };
        _trayIcon.OnExit = () =>
        {
            Shutdown();
        };
        Logger.Log("[App] 托盘图标已创建");

        Logger.Log("[App] 集成启动完成");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Log("[App] 开始清理");

        // 取消系统/前缓冲事件订阅
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_overlayWindow != null)
            _overlayWindow.D3DImage.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;

        // 停止状态定时器
        _stateTimer?.Stop();
        _stateTimer = null;

        // 停止渲染循环
        _renderLoop?.Dispose();
        _renderLoop = null;

        // 停止并释放 D3D11 引擎
        if (_d3dEngine != null)
        {
            _d3dEngine.Stop();
            _d3dEngine.Dispose();
            _d3dEngine = null;
        }

        // 关闭覆盖窗口
        if (_overlayWindow != null)
        {
            _overlayWindow.Visibility = Visibility.Hidden;
            _overlayWindow.Close();
            _overlayWindow = null;
        }

        // 释放调色板提供者
        _paletteProvider?.Dispose();
        _paletteProvider = null;

        // 释放音频源
        _audioSource?.Dispose();
        _audioSource = null;

        // 保存设置并释放托盘图标
        SettingsManager.Save();
        _trayIcon?.Dispose();
        _trayIcon = null;

        // 释放互斥体
        if (_mutexOwned)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* 当前线程未持有互斥体时忽略 */ }
        }
        _singleInstanceMutex?.Dispose();

        Logger.Log("[App] 清理完成");
        base.OnExit(e);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  事件处理
    // ═══════════════════════════════════════════════════════════════════

    // 频谱诊断节流（每 5s 一条，验证音频→着色器数据流）
    private int _spectrumDiagCount;
    private long _renderDiagLast; // 上一帧渲染执行时刻（UI 阻塞诊断）

    // 频谱时间平滑状态（音频线程独占访问）
    private readonly float[] _smoothedBands = new float[SpectrumFrame.BandCount];
    private float _smoothBass, _smoothMid, _smoothHigh, _smoothRms;

    // ── 频谱时间平滑参数（快攻慢释）────────────────────────────────
    // 上升用大系数保持律动感；回落系数由设置界面的回落强度驱动
    // （回落强度 1–10 → 频段 0.025–0.25 / 标量 0.04–0.40，默认 10 ≈ 0.25/0.40），
    // 相邻频段快速交替时不再上下跳动，从根本上消除滚动/闪烁感
    private const float BandAttackCoef = 0.65f;
    private const float ScalarAttackCoef = 0.65f;
    private float _bandReleaseCoef = 0.25f;
    private float _scalarReleaseCoef = 0.40f;

    /// <summary>音频帧就绪：频谱时间平滑（快攻慢释）后写入引擎常量缓冲。</summary>
    private void OnFrameReady(SpectrumFrame frame)
    {
        // 快攻慢释平滑：上升迅速保持律动感，回落速度由设置控制
        for (int i = 0; i < SpectrumFrame.BandCount; i++)
        {
            float s = _smoothedBands[i];
            float v = frame.Bands[i];
            s = v > s ? s + (v - s) * BandAttackCoef : s + (v - s) * _bandReleaseCoef;
            _smoothedBands[i] = s;
            frame.Bands[i] = s;
        }

        _smoothBass = SmoothFollow(_smoothBass, frame.Bass);
        _smoothMid = SmoothFollow(_smoothMid, frame.Mid);
        _smoothHigh = SmoothFollow(_smoothHigh, frame.High);
        _smoothRms = SmoothFollow(_smoothRms, frame.Rms);
        frame.Bass = _smoothBass;
        frame.Mid = _smoothMid;
        frame.High = _smoothHigh;
        frame.Rms = _smoothRms;

        _d3dEngine?.SetSpectrum(frame);

        // 诊断：每 250 帧（约 5s）记录频谱关键值，验证数据非零且随音频变化
        if (++_spectrumDiagCount % 250 == 0)
        {
            float maxBand = 0f;
            for (int i = 0; i < SpectrumFrame.BandCount; i++)
                maxBand = Math.Max(maxBand, frame.Bands[i]);
            Logger.Log($"[Diag] 频谱 maxBand={maxBand:F3} bass={frame.Bass:F3} " +
                       $"mid={frame.Mid:F3} high={frame.High:F3} rms={frame.Rms:F3} active={frame.Active}");
        }
    }

    /// <summary>单值快攻慢释跟随。</summary>
    private float SmoothFollow(float current, float target)
        => target > current
            ? current + (target - current) * ScalarAttackCoef
            : current + (target - current) * _scalarReleaseCoef;

    /// <summary>调色板变化：在 UI 线程更新目标调色板并重置插值进度。
    /// 用 BeginInvoke 异步投递——SMTC 回调线程不得同步等待 UI 线程，
    /// 否则与 Current 属性的锁交互可能形成循环等待死锁。
    /// 固定色/氛围自动模式下忽略封面调色板，保持用户选择不被覆盖。</summary>
    private void OnPaletteChanged()
    {
        if (_appSettings is { } s && s.ColorMode != ColorMode.CoverPalette) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_paletteProvider == null) return;
            var current = _paletteProvider.Current;
            for (int i = 0; i < Palette.ColorCount; i++)
                _paletteTarget.Colors[i] = current.Colors[i];
            _paletteLerpProgress = 0f;
            _paletteDirty = true;
        });
    }

    /// <summary>全屏状态变化：全屏时隐藏覆盖层并暂停渲染，退出全屏时恢复。</summary>
    private void OnFullScreenChanged(bool fullScreen)
    {
        _isFullScreen = fullScreen;
        if (fullScreen)
        {
            Logger.Log("[App] 全屏应用激活，隐藏覆盖层 + 暂停渲染");
            _overlayWindow!.Visibility = Visibility.Hidden;
            _d3dEngine?.Stop();
            _renderLoop?.Stop();
        }
        else
        {
            Logger.Log("[App] 退出全屏，恢复渲染");
            _d3dEngine?.Start();
            _renderLoop?.Start();
            // 重置音频状态标记，让下一个状态定时器滴答重新评估淡入淡出
            _audioActive = false;
        }
    }

    // ── 休眠/唤醒自愈 ────────────────────────────────────────────────
    // 休眠/睡眠唤醒后 D3D9Ex 设备与共享表面、WASAPI 采集端点可能整体失效，
    // 且失效不一定伴随 RecordingStopped 等回调，表现为"效果不再生效"。
    // 统一策略：Resume（或前缓冲恢复可用）后延迟重建渲染引擎 + 重启音频采集。
    private long _lastEngineRecreateTick; // 重建防抖（多个触发源合并为一次）

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Logger.Log("[App] 系统恢复（睡眠/休眠唤醒）：准备重建渲染引擎 + 重启音频采集");
        // ApplicationIdle：等待唤醒初期显卡/音频端点就绪，避免与恢复中的 UI 工作争抢
        Dispatcher.BeginInvoke(() =>
        {
            try { RecreateRenderEngine(); }
            catch (Exception ex) { Logger.LogError("[App] 唤醒后重建渲染引擎失败", ex); }
            try { _audioSource?.RestartCapture(); }
            catch (Exception ex) { Logger.LogError("[App] 唤醒后重启音频采集失败", ex); }
            // 音频激活状态以重启后的实际检测为准，避免残留激活态导致淡入状态错乱
            _audioActive = false;
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        Logger.Log("[App] D3DImage 前缓冲恢复可用：准备重建渲染引擎");
        Dispatcher.BeginInvoke(() =>
        {
            try { RecreateRenderEngine(); }
            catch (Exception ex) { Logger.LogError("[App] 前缓冲恢复后重建渲染引擎失败", ex); }
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>整体重建 D3D11 渲染引擎（复用覆盖窗口的 D3DImage），用于设备失效后的自愈。</summary>
    private void RecreateRenderEngine()
    {
        if (_overlayWindow == null) return;
        long now = Environment.TickCount64;
        if (now - _lastEngineRecreateTick < 3000) return; // 防抖
        _lastEngineRecreateTick = now;

        var old = _d3dEngine;
        _d3dEngine = null; // 先摘引用：渲染回调与音频线程随即安全跳过
        try
        {
            old?.Stop();
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] 释放旧渲染引擎失败（继续重建）", ex);
        }

        int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        int overlayHeight = OverlayWindow.DefaultOverlayHeight;
        var engine = new D3D11Engine(_overlayWindow.D3DImage, screenWidth, overlayHeight);
        _d3dEngine = engine;
        engine.Start();
        if (engine.UseFallback)
        {
            _overlayWindow.FallbackImage.Source = engine.FallbackBitmap;
            _overlayWindow.EnableFallbackMode();
        }
        // 立即注入当前调色板与频谱参数，避免重建后首帧使用全零颜色（频谱由下一音频帧补上）
        engine.SetPalette((float)_totalElapsed.TotalSeconds, _paletteInterpolated, 1f);
        engine.SetRenderParams(
            Math.Clamp(_appSettings?.SpectrumBands ?? SpectrumFrame.BandCount, 16, SpectrumFrame.BandCount),
            (float)Math.Clamp(_appSettings?.PeakHeight ?? 2.0, 0.5, 2.0));
        Logger.Log("[App] 渲染引擎已重建");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  设置变化回调
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>应用频谱渲染参数：频段数、回落强度、峰值高度（启动与设置变化时调用）。</summary>
    private void ApplySpectrumParams(AppSettings settings)
    {
        // 回落强度 1–10 → 快攻慢释的回落系数（默认 10 ≈ 0.25/0.40）
        float fall = (float)Math.Clamp(settings.FallStrength, 1.0, 10.0);
        _bandReleaseCoef = fall * 0.025f;
        _scalarReleaseCoef = fall * 0.04f;

        // 有效频段数（音频端重建对数分桶映射，渲染端按频段数采样）
        int bands = Math.Clamp(settings.SpectrumBands, 16, SpectrumFrame.BandCount);
        if (_audioSource != null)
            _audioSource.ActiveBandCount = bands;

        // 峰值高度系数（着色器 uniform，与频段数一起经常量缓冲下发）
        float peak = (float)Math.Clamp(settings.PeakHeight, 0.5, 2.0);
        _d3dEngine?.SetRenderParams(bands, peak);
    }

    /// <summary>设置变化时更新运行时参数。</summary>
    private void OnSettingsChanged(AppSettings settings)
    {
        // 频谱渲染参数：频段数 / 回落强度 / 峰值高度
        ApplySpectrumParams(settings);

        // 更新帧率
        if (_renderLoop != null)
            _renderLoop.TargetFps = settings.FrameRate;

        // 更新覆盖窗口高度
        if (_overlayWindow != null)
            _overlayWindow.Resize(settings.BandHeight);

        // 整体强度：若效果正在显示（音频激活中），平滑过渡到新强度
        if (settings.EffectEnabled && _audioActive && _overlayWindow != null && !_isFullScreen)
        {
            var anim = new DoubleAnimation(
                Math.Clamp(settings.OverallIntensity, 0.1, 1.0),
                TimeSpan.FromMilliseconds(200))
            {
                FillBehavior = FillBehavior.HoldEnd
            };
            _overlayWindow.BeginAnimation(Window.OpacityProperty, anim);
        }

        // 颜色模式 → 覆盖调色板
        if (settings.ColorMode == ColorMode.FixedColor)
        {
            ApplyFixedColorPalette(settings.FixedColorHex);
        }
        else if (settings.ColorMode == ColorMode.AmbientAuto)
        {
            // 回退到系统强调色调色板（封面不可用/未取得时的统一回退色）
            var accent = SystemAccentPalette.Get();
            for (int i = 0; i < Palette.ColorCount; i++)
                _paletteTarget.Colors[i] = accent.Colors[i];
            _paletteLerpProgress = 0f;
            _paletteDirty = true;
        }
        else if (_paletteProvider != null)
        {
            // 切回封面取色：立即同步当前调色板，避免等待下一次 SMTC 事件
            var current = _paletteProvider.Current;
            for (int i = 0; i < Palette.ColorCount; i++)
                _paletteTarget.Colors[i] = current.Colors[i];
            _paletteLerpProgress = 0f;
            _paletteDirty = true;
        }

        // 效果开关
        if (!settings.EffectEnabled)
        {
            // 关闭效果：隐藏覆盖层并暂停渲染
            if (_overlayWindow != null)
                _overlayWindow.Visibility = Visibility.Hidden;
            _d3dEngine?.Stop();
            _renderLoop?.Stop();
            Logger.Log("[App] 效果已关闭");
        }
        else
        {
            // 打开效果：恢复渲染（音频激活时自动淡入）
            _d3dEngine?.Start();
            _renderLoop?.Start();
            Logger.Log("[App] 效果已开启");
        }

        // 开机自启
        SetAutoStart(settings.AutoStart);
    }

    /// <summary>将十六进制颜色（#RRGGBB 或 #AARRGGBB）转换为 Palette 并设为目标。</summary>
    private void ApplyFixedColorPalette(string hex)
    {
        try
        {
            if (string.IsNullOrEmpty(hex)) return;
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var v = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
            // 以固定色为基础生成 4 色调和渐变，两端为同一色、中间为亮度变化
            _paletteTarget.Colors[0] = v * 0.6f; // 暗
            _paletteTarget.Colors[1] = v * 0.8f;
            _paletteTarget.Colors[2] = v;
            _paletteTarget.Colors[3] = v * 0.5f; // 更暗
            _paletteLerpProgress = 0f;
            _paletteDirty = true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] 固定色解析失败", ex);
        }
    }

    /// <summary>设置或取消开机自启（注册表 HKCU Run 键）。</summary>
    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue("RivuletLight", $"\"{exePath}\"");
            }
            else
            {
                try { key.DeleteValue("RivuletLight", false); }
                catch { /* 值不存在时忽略 */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] 设置开机自启失败", ex);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  渲染回调（每帧 UI 线程调用）
    // ═══════════════════════════════════════════════════════════════════

    private void OnRender(TimeSpan delta)
    {
        if (_d3dEngine == null) return;

        // 阻塞诊断：本帧与上一帧实际执行的间隔超过 1s 说明 UI 线程曾卡顿
        long now = Environment.TickCount64;
        if (_renderDiagLast != 0 && now - _renderDiagLast > 1000)
            Logger.Log($"[Diag] 渲染间隔异常 {now - _renderDiagLast} ms（UI 线程曾阻塞）");
        _renderDiagLast = now;

        _totalElapsed += delta;

        // 调色板 2s 平滑过渡
        if (_paletteDirty)
        {
            _paletteLerpProgress += (float)delta.TotalSeconds;
            float lerp = Math.Clamp(_paletteLerpProgress / 2.0f, 0f, 1f);

            for (int i = 0; i < Palette.ColorCount; i++)
            {
                _paletteInterpolated.Colors[i] = Vector4.Lerp(
                    _paletteCurrent.Colors[i],
                    _paletteTarget.Colors[i],
                    lerp);
            }

            if (lerp >= 1f)
            {
                _paletteDirty = false;
                for (int i = 0; i < Palette.ColorCount; i++)
                    _paletteCurrent.Colors[i] = _paletteTarget.Colors[i];
            }
        }

        float lerpValue = _paletteDirty
            ? Math.Clamp(_paletteLerpProgress / 2.0f, 0f, 1f)
            : 1f;
        _d3dEngine.SetPalette((float)_totalElapsed.TotalSeconds, _paletteInterpolated, lerpValue);
        _d3dEngine.Render(delta);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  状态定时器（100ms）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 定期检查音频激活状态并触发平滑淡入淡出（WPF DoubleAnimation）。
    /// 全屏时跳过视觉变化，仅跟踪内部状态。
    /// </summary>
    private void OnStateTimerTick(object? sender, EventArgs e)
    {
        if (_audioSource == null) return;

        bool currentActive = _audioSource.IsAudioActive;

        if (_isFullScreen)
        {
            // 全屏时仅跟踪状态，不改变可见性
            _audioActive = currentActive;
            return;
        }

        // 检测激活状态变化，触发平滑淡入/淡出
        if (currentActive != _audioActive)
        {
            _audioActive = currentActive;
            Logger.Log($"[App] 音频激活状态切换：{_audioActive}");
            FadeOverlay(fadeIn: _audioActive);
        }
    }

    /// <summary>
    /// 以 WPF DoubleAnimation 平滑淡入/淡出覆盖窗口（替代旧的 100ms 步进，消除闪烁感）。
    /// 淡出完成后隐藏窗口。
    /// </summary>
    private void FadeOverlay(bool fadeIn)
    {
        if (_overlayWindow == null) return;

        double target = fadeIn
            ? Math.Clamp(_appSettings?.OverallIntensity ?? 1.0, 0.1, 1.0)
            : 0.0;

        if (fadeIn)
        {
            // 重置动画与基础值，确保从 0 开始
            _overlayWindow.BeginAnimation(Window.OpacityProperty, null);
            _overlayWindow.Opacity = 0.0;
            _overlayWindow.Visibility = Visibility.Visible;
        }

        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(FadeDurationMs))
        {
            FillBehavior = FillBehavior.HoldEnd
        };

        if (!fadeIn)
        {
            anim.Completed += (_, _) =>
            {
                if (!_audioActive && _overlayWindow != null)
                {
                    _overlayWindow.Visibility = Visibility.Hidden;
                }
            };
        }

        _overlayWindow.BeginAnimation(Window.OpacityProperty, anim);
    }

    // ── P/Invoke ─────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    // ═══════════════════════════════════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>默认调色板：系统强调色（个性化自定义主题色）——
    /// 封面未取得/不可用时的回退色，SmtcPaletteProvider 初始值一致。</summary>
    private static Palette CreateDefaultPalette() => SystemAccentPalette.Get();

    // ═══════════════════════════════════════════════════════════════════
    //  全局异常处理
    // ═══════════════════════════════════════════════════════════════════

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.LogError("UI 线程未处理异常", e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.LogError("任务未观察异常", e.Exception);
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
            ?? new Exception(e.ExceptionObject?.ToString() ?? "未知异常对象");
        Logger.LogError($"非 UI 线程未处理异常（IsTerminating={e.IsTerminating}）", ex);
    }
}