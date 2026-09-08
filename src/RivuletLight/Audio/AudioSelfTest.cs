#if DEBUG
using RivuletLight.Core;

namespace RivuletLight.Audio;

/// <summary>
/// 音频模块临时自测入口（仅 Debug 编译，生产代码不调用，未接入 App 启动逻辑）。
/// 1) 用合成正弦波离线验证 FFT 的正确性（峰值 bin 位置与幅度）；
/// 2) 启动 LoopbackAudioSource 收集 200 帧，验证不崩溃、频段值 0..1 且无 NaN/Inf。
/// 注意：本环境可能无法播放声音——WASAPI loopback 在系统无声时不推送数据属已知特性，
/// 届时第 2 步以超时结束，结论以日志为准；编译通过即达到验证目的。
/// </summary>
public static class AudioSelfTest
{
    /// <summary>运行全部自测（可通过日志文件查看结论）。</summary>
    public static async Task RunAsync(int frameCount = 200, int timeoutMs = 30000)
    {
        TestFft();
        await TestCaptureAsync(frameCount, timeoutMs).ConfigureAwait(false);
    }

    /// <summary>合成"频率正好落在整数 bin 上"的正弦做 2048 点 FFT，校验峰值位置与幅度。
    /// 取 48kHz 采样率、目标 bin=42（984.375Hz）、幅度 0.5：
    /// 无泄漏时该 bin 幅度应为 0.5·N/2 = 512（±2%）。</summary>
    private static void TestFft()
    {
        const int n = 2048;
        const float rate = 48000f;
        const int targetBin = 42;
        float freq = rate / n * targetBin;

        var input = new float[n];
        for (int i = 0; i < n; i++)
            input[i] = 0.5f * MathF.Sin(2f * MathF.PI * freq * i / rate);

        var fft = new Fft(n);
        var re = new float[n];
        var im = new float[n];
        fft.Compute(input, re, im, n);

        int peakBin = 0;
        float peakMag = 0f;
        for (int k = 1; k < n / 2; k++)
        {
            float m = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
            if (m > peakMag) { peakMag = m; peakBin = k; }
        }

        bool binOk = peakBin == targetBin;
        bool magOk = MathF.Abs(peakMag - n / 4f) < n / 4f * 0.02f;
        Logger.Log($"[SelfTest][FFT] peakBin={peakBin}(期望 {targetBin}) peakMag={peakMag:F1}(期望≈{n / 4}) → {(binOk && magOk ? "PASS" : "FAIL")}");
    }

    /// <summary>启动真实采集，收集若干帧做合法性与稳定性校验。</summary>
    private static async Task TestCaptureAsync(int frameCount, int timeoutMs)
    {
        int received = 0;
        int invalid = 0;
        int activeFrames = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var source = new LoopbackAudioSource();
        source.FrameReady += f =>
        {
            for (int i = 0; i < SpectrumFrame.BandCount; i++)
            {
                if (Bad(f.Bands[i])) Interlocked.Increment(ref invalid);
            }
            if (Bad(f.Bass) || Bad(f.Mid) || Bad(f.High)
                || float.IsNaN(f.Rms) || float.IsInfinity(f.Rms) || f.Rms < 0f)
                Interlocked.Increment(ref invalid);
            if (f.Active) Interlocked.Increment(ref activeFrames);
            if (Interlocked.Increment(ref received) >= frameCount)
                done.TrySetResult();
        };

        source.Start();
        Logger.Log($"[SelfTest][Capture] 开始收集 {frameCount} 帧（需系统正在播放声音）…");

        var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        bool gotAll = ReferenceEquals(finished, done.Task);
        bool pass = gotAll && Volatile.Read(ref invalid) == 0;
        Logger.Log($"[SelfTest][Capture] 结果={(pass ? "PASS" : "FAIL")}：收到 {Volatile.Read(ref received)}/{frameCount} 帧，" +
                   $"非法值 {Volatile.Read(ref invalid)}，激活帧 {Volatile.Read(ref activeFrames)}，IsAudioActive={source.IsAudioActive}");
    }

    /// <summary>频段/聚合值合法性：0..1 且非 NaN/Inf。</summary>
    private static bool Bad(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0f || v > 1f;
}
#endif
