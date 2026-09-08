namespace RivuletLight.Audio;

/// <summary>
/// 自实现迭代式 radix-2 FFT（Cooley-Tukey 蝶形，实输入按复数处理）。
/// 位反转表与旋转因子表在构造时一次性预计算，Compute 全程复用调用方缓冲、
/// 零托管堆分配，可安全用于音频捕获线程的高频调用（每 ~20ms 一次）。
/// 约定：点数为 2 的幂；输出为标准前向 DFT（e^(-2πi/n)），供幅度谱使用。
/// </summary>
public sealed class Fft
{
    /// <summary>默认 FFT 点数：2048（与频谱分析窗口等长）</summary>
    public const int DefaultSize = 2048;

    private readonly int _n;
    private readonly int[] _bitRev;     // 位反转置换表
    private readonly float[] _cosTable; // cos(2πk/n)
    private readonly float[] _sinTable; // sin(2πk/n)

    /// <param name="n">FFT 点数，必须为 2 的幂且不小于 2</param>
    public Fft(int n = DefaultSize)
    {
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT 点数必须是 2 的幂且不小于 2。", nameof(n));

        _n = n;

        // 预计算位反转索引（levels = n 的二进制位数）
        _bitRev = new int[n];
        int levels = 0;
        while ((1 << levels) < n) levels++;
        for (int i = 0; i < n; i++)
        {
            int v = i, r = 0;
            for (int b = 0; b < levels; b++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }
            _bitRev[i] = r;
        }

        // 预计算旋转因子表
        _cosTable = new float[n];
        _sinTable = new float[n];
        for (int k = 0; k < n; k++)
        {
            double t = 2.0 * Math.PI * k / n;
            _cosTable[k] = (float)Math.Cos(t);
            _sinTable[k] = (float)Math.Sin(t);
        }
    }

    /// <summary>
    /// 执行 n 点 FFT：把 inputRe 视为实信号（虚部按 0 处理），
    /// 结果写入 re/im。re/im 由调用方持有并反复复用（预分配），本方法不做任何分配。
    /// 注意：inputRe 不得与 re/im 引用同一数组。
    /// </summary>
    public void Compute(float[] inputRe, float[] re, float[] im, int n)
    {
        if (n != _n)
            throw new ArgumentOutOfRangeException(nameof(n), "n 必须与构造时指定的点数一致。");
        if (inputRe.Length < n || re.Length < n || im.Length < n)
            throw new ArgumentException("数组长度必须不小于 FFT 点数。");

        // 按位反转顺序散射输入（等价于顺序存放后再做位反转交换，省一遍交换）
        for (int i = 0; i < n; i++)
        {
            int j = _bitRev[i];
            re[j] = inputRe[i];
            im[j] = 0f;
        }

        // 自底向上蝶形运算：size 为当前分组长度
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            int step = n / size; // 旋转因子索引步进
            for (int i = 0; i < n; i += size)
            {
                int k = 0;
                for (int j = i; j < i + half; j++)
                {
                    int l = j + half;
                    float cosT = _cosTable[k];
                    float sinT = _sinTable[k];

                    // t = x[l] * w，其中 w = cosθ - i·sinθ（前向变换约定）
                    float tre = re[l] * cosT + im[l] * sinT;
                    float tim = -re[l] * sinT + im[l] * cosT;

                    re[l] = re[j] - tre;
                    im[l] = im[j] - tim;
                    re[j] += tre;
                    im[j] += tim;

                    k += step;
                }
            }
        }
    }
}
