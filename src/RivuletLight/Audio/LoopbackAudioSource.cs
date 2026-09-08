using System.Runtime.InteropServices;
using NAudio.Wave;
using RivuletLight.Core;

namespace RivuletLight.Audio;

/// <summary>
/// WASAPI Loopback 系统音频采集 + FFT 频谱分析（实现 <see cref="Core.IAudioSource"/>）。
/// 分析在 NAudio 捕获线程的 DataAvailable 回调内同步完成：每累积 1024 个新样本
/// （约 21ms @48kHz）执行一次 FFT，结果写入复用的 <see cref="SpectrumFrame"/>
/// 并触发 <see cref="FrameReady"/>。所有缓冲构造时预分配，稳态运行零托管分配。
/// </summary>
public sealed class LoopbackAudioSource : IAudioSource
{
    // ---- 分析参数 ----
    private const int FftSize = 2048;                 // FFT 窗口长度（环形缓冲同长）
    private const int HopSize = 1024;                 // 分析步进：每 1024 新样本一次（约 21ms @48kHz）
    private const float MinHz = 20f;                 // 对数分桶频率下限
    private const float MaxHz = 16000f;              // 对数分桶频率上限
    private const float DbFloor = -65f;              // dB 映射下限 → 0
    private const float DbCeil = -10f;               // dB 映射上限 → 1
    private const float AttackCoef = 0.5f;           // 包络上升系数（快攻，跟拍）
    private const float ReleaseCoef = 0.07f;         // 包络下降系数（极慢回落，消除频段交替的滚动感）
    private const float SilenceRmsThreshold = 0.004f; // 静音 RMS 阈值
    private const int LoudStreakToActivate = 2;       // 激活去抖：需连续高于阈值的分析块数
    private const long SilenceDeactivateMs = 1000;    // 静音持续该时长后判定失活
    private const int MaxRestartAttempts = 5;         // 采集异常重启的最大尝试次数
    private const long RestartResetMs = 30000;       // 稳定运行该时长后重置重启退避计数

    private readonly Fft _fft = new(FftSize);
    private readonly float[] _ring = new float[FftSize];      // 单声道环形缓冲（始终保存最新 2048 样本）
    private readonly float[] _hann = new float[FftSize];       // 汉宁窗系数
    private readonly float[] _windowed = new float[FftSize];   // 加窗后的分析窗口
    private readonly float[] _re = new float[FftSize];         // FFT 实部（复用）
    private readonly float[] _im = new float[FftSize];         // FFT 虚部（复用）
    private readonly float[] _mag = new float[FftSize / 2];    // 前 1024 个 bin 的幅度
    private readonly int[] _bandFirstBin = new int[SpectrumFrame.BandCount];   // 频段起始 bin
    private readonly int[] _bandLastBin = new int[SpectrumFrame.BandCount];   // 频段结束 bin
    private readonly float[] _bandCenterHz = new float[SpectrumFrame.BandCount]; // 频段几何中心频率

    private readonly SpectrumFrame _frame = new();
    private readonly object _startGate = new(); // 序列化 StartCapture/Dispose，避免释放时泄漏运行中的采集
    private WasapiLoopbackCapture? _capture;
    private int _ringPos;          // 环形缓冲写位置（指向最旧样本）
    private int _pending;          // 距上次分析累积的新样本数
    private int _sampleRate;
    private int _channels;
    private bool _bandsMapped;     // 频段→bin 映射是否已按当前采样率构建
    private bool _formatOk;        // 采集格式是否为可分析的 float32
    private int _loudStreak;      // 连续高于 RMS 阈值的分析块计数
    private long _lastLoudTick;  // 最近一次"有声"分析块的时间戳（ms）
    private volatile bool _active; // 静音检测状态机输出（供 UI 线程读取）
    private int _restartAttempts;
    private long _lastStartTick; // 最近一次启动/重启尝试的时间戳
    private int _restarting;      // 0=空闲 1=重启循环进行中（Interlocked 操作）
    private volatile bool _disposed;
    private bool _started;
    private volatile int _activeBands = SpectrumFrame.BandCount; // 当前有效频段数（16–96，设置可调）

    /// <summary>当前有效频段数量（16–96）。设置界面实时调整；
    /// 变更后由捕获线程在下一次分析时重建频段→bin 映射。</summary>
    public int ActiveBandCount
    {
        get => _activeBands;
        set
        {
            int v = Math.Clamp(value, 16, SpectrumFrame.BandCount);
            if (v == _activeBands) return;
            _activeBands = v;
            _bandsMapped = false; // 触发 Analyze 重建映射
        }
    }

    /// <inheritdoc />
    public event Action<SpectrumFrame>? FrameReady;

    /// <inheritdoc />
    public bool IsAudioActive => _active;

    public LoopbackAudioSource()
    {
        // 预计算汉宁窗系数：0.5 · (1 - cos(2πi/N))
        for (int i = 0; i < FftSize; i++)
            _hann[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / FftSize));
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        Logger.Log("[Audio] LoopbackAudioSource 启动");
        StartCapture();
    }

    /// <summary>创建并启动 WASAPI loopback 采集（默认输出设备）；失败则安排指数退避重试。</summary>
    private void StartCapture()
    {
        lock (_startGate)
        {
            if (_disposed) return;
            WasapiLoopbackCapture? capture = null;
            try
            {
                capture = new WasapiLoopbackCapture(); // 默认输出设备，共享模式 float32
                var fmt = capture.WaveFormat;
                _sampleRate = fmt.SampleRate;
                _channels = fmt.Channels;
                _formatOk = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
                if (!_formatOk)
                    Logger.LogError($"[Audio] 非预期的采集格式：{fmt.Encoding}/{fmt.BitsPerSample}bit，将跳过分析");
                BuildBandMapping();
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                _capture = capture;
                _lastStartTick = Environment.TickCount64;
                Logger.Log($"[Audio] 采集已启动：{_sampleRate}Hz / {_channels}ch / {fmt.BitsPerSample}bit");
            }
            catch (Exception ex)
            {
                Logger.LogError("[Audio] 启动 WASAPI loopback 采集失败", ex);
                if (capture != null)
                {
                    try { capture.Dispose(); } catch { /* 释放失败可忽略 */ }
                }
                _capture = null;
                ScheduleRestart();
            }
        }
    }

    /// <summary>按当前采样率与有效频段数构建对数频段 → FFT bin 的映射（Bass/Mid 归类用中心频率）。</summary>
    private void BuildBandMapping()
    {
        float binHz = _sampleRate > 0 ? (float)_sampleRate / FftSize : 0f;
        if (binHz <= 0f)
        {
            _bandsMapped = false;
            return;
        }

        int n = _activeBands;
        float ratio = MathF.Pow(MaxHz / MinHz, 1f / n); // 每频段的频率比
        int maxBin = FftSize / 2 - 1;
        for (int i = 0; i < n; i++)
        {
            float f0 = MinHz * MathF.Pow(ratio, i);
            float f1 = MinHz * MathF.Pow(ratio, i + 1);
            // 跳过 DC（bin 0）；低频段可能落在同一 bin 上，属对数分桶的正常现象
            int first = Math.Clamp((int)MathF.Ceiling(f0 / binHz), 1, maxBin);
            int last = Math.Clamp((int)MathF.Floor(f1 / binHz), first, maxBin);
            _bandFirstBin[i] = first;
            _bandLastBin[i] = last;
            _bandCenterHz[i] = MathF.Sqrt(f0 * f1); // 几何中心
        }
        _bandsMapped = true;
    }

    /// <summary>NAudio 捕获线程回调：多声道混单声道、入环形缓冲，满一个 hop 即分析。</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || !_formatOk) return;
        try
        {
            // WASAPI 共享模式混音格式为 32 位 float 交错 PCM；零拷贝取得 float 视图
            var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
            int channels = _channels < 1 ? 1 : _channels;
            int frames = samples.Length / channels;

            // 交错多声道 → 单声道（各声道取平均），追加进环形缓冲
            int pos = _ringPos;
            for (int f = 0; f < frames; f++)
            {
                int idx = f * channels;
                float acc = samples[idx];
                for (int c = 1; c < channels; c++) acc += samples[idx + c];
                _ring[pos] = acc / channels;
                pos = (pos + 1) & (FftSize - 1);
            }
            _ringPos = pos;
            _pending += frames;

            // 每累积满一个 hop（1024 新样本）执行一次分析
            while (_pending >= HopSize)
            {
                _pending -= HopSize;
                Analyze();
            }
        }
        catch (Exception ex)
        {
            // 绝不让异常逃逸出捕获线程（会终止 NAudio 的采集线程）
            Logger.LogError("[Audio] 处理音频数据异常", ex);
        }
    }

    /// <summary>
    /// 频谱分析（仅捕获线程调用）：取最新 2048 样本 → 汉宁窗 → FFT →
    /// 对数分桶 96 频段 → dB 映射 → 快攻慢放包络 → Bass/Mid/High 聚合 →
    /// 静音检测状态机 → 触发 FrameReady（复用实例，无分配）。
    /// </summary>
    private void Analyze()
    {
        if (!_bandsMapped) BuildBandMapping();

        // 1) 环形缓冲 → 分析窗口：加汉宁窗，同时计算原始（未加窗）信号的 RMS
        double rmsSum = 0.0;
        int pos = _ringPos; // 写位置即最旧样本，顺序读出恰好是最新 2048 个
        for (int i = 0; i < FftSize; i++)
        {
            float s = _ring[pos];
            rmsSum += (double)s * s;
            _windowed[i] = s * _hann[i];
            pos = (pos + 1) & (FftSize - 1);
        }
        float rms = (float)Math.Sqrt(rmsSum / FftSize);

        // 2) FFT（全部复用预分配缓冲）
        _fft.Compute(_windowed, _re, _im, FftSize);

        // 3) 前 1024 bin 的幅度（除以 N 归一化：满幅正弦加汉宁窗后约 -12dB）
        for (int k = 0; k < FftSize / 2; k++)
            _mag[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) / FftSize;

        // 4) 对数分桶：桶内取最大幅度，映射 dB 到 0..1，再做包络平滑。
        //    仅前 _activeBands 个频段参与分析，尾部频段清零（对应常量缓冲的固定 96 槽）
        int activeBands = _activeBands;
        float dbRangeInv = 1f / (DbCeil - DbFloor);
        for (int b = 0; b < activeBands; b++)
        {
            float peak = 0f;
            int first = _bandFirstBin[b], last = _bandLastBin[b];
            for (int k = first; k <= last; k++)
            {
                if (_mag[k] > peak) peak = _mag[k];
            }
            float db = 20f * MathF.Log10(MathF.Max(peak, 1e-10f));
            float t = (db - DbFloor) * dbRangeInv;
            // 用 "!(t >= 0)" 而非 "t < 0"：顺带把异常数据（NaN）拦截为 0，自愈脏驱动输出
            if (!(t >= 0f)) t = 0f;
            else if (t > 1f) t = 1f;

            // 快攻慢放：上升用大系数、下降用小系数，消除视觉抖动
            float cur = _frame.Bands[b];
            float coef = t > cur ? AttackCoef : ReleaseCoef;
            _frame.Bands[b] = cur + coef * (t - cur);
        }
        for (int b = activeBands; b < SpectrumFrame.BandCount; b++)
            _frame.Bands[b] = 0f;

        // 5) Bass / Mid / High 聚合：按频段中心频率归类取均值（源自已平滑的频段值）
        float bassSum = 0f, midSum = 0f, highSum = 0f;
        int bassN = 0, midN = 0, highN = 0;
        for (int b = 0; b < activeBands; b++)
        {
            float v = _frame.Bands[b];
            float c = _bandCenterHz[b];
            if (c < 250f) { bassSum += v; bassN++; }
            else if (c < 2000f) { midSum += v; midN++; }
            else { highSum += v; highN++; }
        }
        _frame.Bass = bassN > 0 ? bassSum / bassN : 0f;
        _frame.Mid = midN > 0 ? midSum / midN : 0f;
        _frame.High = highN > 0 ? highSum / highN : 0f;

        // 6) RMS 与静音检测状态机：
        //    连续 2 块高于阈值 → 激活；静音持续 ≥1s → 失活
        _frame.Rms = rms;
        if (rms >= SilenceRmsThreshold)
        {
            _loudStreak++;
            _lastLoudTick = Environment.TickCount64;
            if (!_active && _loudStreak >= LoudStreakToActivate)
                _active = true;
        }
        else
        {
            _loudStreak = 0;
            if (_active && Environment.TickCount64 - _lastLoudTick >= SilenceDeactivateMs)
                _active = false;
        }
        _frame.Active = _active;

        // 7) 通知订阅者（帧实例复用，订阅者应在回调内同步消费）
        FrameReady?.Invoke(_frame);
    }

    /// <summary>系统休眠/睡眠唤醒等场景：清理旧采集并立即重建。
    /// 唤醒后 WASAPI 端点可能已静默失效且不再触发 RecordingStopped，需显式重建。</summary>
    public void RestartCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_startGate)
        {
            if (_disposed) return;
            Logger.Log("[Audio] 显式重启采集（系统恢复后重建）");
            CleanupCapture();
            _restartAttempts = 0;
            _active = false;
            _loudStreak = 0;
            StartCapture();
        }
    }

    /// <summary>采集停止（设备移除/默认设备切换/异常）：记录日志并安排重启。
    /// 注意 NAudio 会把本事件投递到构造时的同步上下文（可能是 UI 线程），处理必须轻量且线程安全。</summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        if (e.Exception != null)
            Logger.LogError("[Audio] 采集因异常停止", e.Exception);
        else
            Logger.Log("[Audio] 采集停止（默认输出设备变更等），准备重启");
        CleanupCapture();
        ScheduleRestart();
    }

    /// <summary>指数退避重启采集（1s,2s,4s,8s,16s；稳定运行 30s 后重置计数；最多 5 次）。</summary>
    private void ScheduleRestart()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0) return; // 已有重启循环在跑

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed)
                {
                    // 上次启动距今超过 30s 说明曾稳定运行，重置退避计数允许重新完整重试
                    if (Environment.TickCount64 - _lastStartTick >= RestartResetMs)
                        _restartAttempts = 0;

                    if (_restartAttempts >= MaxRestartAttempts)
                    {
                        Logger.LogError("[Audio] 采集重启次数已达上限，放弃重试");
                        return;
                    }

                    _lastStartTick = Environment.TickCount64; // 记录本次尝试时间，防止快速失败时计数被重置
                    int delayMs = 1000 << _restartAttempts;    // 1s → 16s 指数退避
                    _restartAttempts++;
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    if (_disposed) return;

                    Logger.Log($"[Audio] 重启采集（第 {_restartAttempts} 次，退避 {delayMs}ms）");
                    StartCapture();
                    if (_capture != null) return; // 启动成功，结束重启循环
                }
            }
            finally
            {
                Volatile.Write(ref _restarting, 0);
            }
        });
    }

    /// <summary>断开事件并释放当前采集实例（各步骤独立 try/catch，绝不抛出）。</summary>
    private void CleanupCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture == null) return;
        try { capture.DataAvailable -= OnDataAvailable; } catch { /* 忽略清理异常 */ }
        try { capture.RecordingStopped -= OnRecordingStopped; } catch { /* 忽略清理异常 */ }
        try { capture.StopRecording(); } catch { /* 已停止时可能抛出，忽略 */ }
        try { capture.Dispose(); } catch { /* 忽略清理异常 */ }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 与 StartCapture 互斥，避免释放期间新采集启动导致泄漏
        lock (_startGate)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            try { CleanupCapture(); }
            catch { /* 释放过程绝不抛出 */ }
        }
    }
}
