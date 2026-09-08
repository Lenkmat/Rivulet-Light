using System.Numerics;

namespace RivuletLight.Media;

/// <summary>
/// k-means (k=4) 调色板提取，从 BGRA 像素数组提取 4 种主色。
/// 确定性：初始质心从输入像素固定间隔采样，不依赖随机数。
/// 迭代 ≤10 次或收敛停止；输出按亮度×饱和度加权降序排列。
/// </summary>
public static class KMeansPalette
{
    private const int K = 4;
    private const int MaxIterations = 10;
    private const float ConvergenceThreshold = 0.005f;

    /// <summary>从 BGRA 像素数组提取 4 色调色板。</summary>
    /// <param name="pixels">BGRA 像素数据（B 字节在低地址，G、R、A 依次）</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <returns>按亮度×饱和度降序的 4 色调色板（Vector4，RGB 分量 0..1，W=1）</returns>
    public static Vector4[] Extract(byte[] pixels, int width, int height)
    {
        int pixelCount = width * height;

        // 将 BGRA 字节转换为 Vector3（RGB，0..1）
        var colors = new Vector3[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            float b = pixels[offset] / 255f;
            float g = pixels[offset + 1] / 255f;
            float r = pixels[offset + 2] / 255f;
            colors[i] = new Vector3(r, g, b);
        }

        // 初始质心：固定间隔采样，保证确定性
        var centroids = new Vector3[K];
        int step = pixelCount / K;
        for (int i = 0; i < K; i++)
        {
            int idx = Math.Min(i * step, pixelCount - 1);
            centroids[i] = colors[idx];
        }

        // 迭代分配 + 更新
        var assignments = new int[pixelCount];
        var sums = new Vector3[K];
        var counts = new int[K];

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            // 分配：每个像素归入最近质心
            bool changed = false;
            for (int i = 0; i < pixelCount; i++)
            {
                Vector3 c = colors[i];
                int best = 0;
                float bestDist = float.MaxValue;
                for (int j = 0; j < K; j++)
                {
                    float d = Vector3.DistanceSquared(c, centroids[j]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = j;
                    }
                }
                if (assignments[i] != best)
                {
                    assignments[i] = best;
                    changed = true;
                }
            }

            if (!changed) break;

            // 重新计算质心
            Array.Clear(sums, 0, K);
            Array.Clear(counts, 0, K);
            for (int i = 0; i < pixelCount; i++)
            {
                int cluster = assignments[i];
                sums[cluster] += colors[i];
                counts[cluster]++;
            }

            float maxMovement = 0f;
            for (int j = 0; j < K; j++)
            {
                if (counts[j] > 0)
                {
                    Vector3 newCentroid = sums[j] / counts[j];
                    float movement = Vector3.Distance(newCentroid, centroids[j]);
                    if (movement > maxMovement) maxMovement = movement;
                    centroids[j] = newCentroid;
                }
            }

            if (maxMovement < ConvergenceThreshold) break;
        }

        // 按亮度 × 饱和度降序排列（最鲜艳明亮的颜色在前）
        var indices = new int[K];
        for (int i = 0; i < K; i++) indices[i] = i;
        Array.Sort(indices, (a, b) =>
        {
            float wa = Luminance(centroids[a]) * Saturation(centroids[a]);
            float wb = Luminance(centroids[b]) * Saturation(centroids[b]);
            return wb.CompareTo(wa);
        });

        var result = new Vector4[K];
        for (int i = 0; i < K; i++)
        {
            result[i] = new Vector4(centroids[indices[i]], 1f);
        }
        return result;
    }

    /// <summary>Rec. 601 亮度系数。</summary>
    private static float Luminance(Vector3 rgb)
        => 0.299f * rgb.X + 0.587f * rgb.Y + 0.114f * rgb.Z;

    /// <summary>饱和度（max - min）。</summary>
    private static float Saturation(Vector3 rgb)
    {
        float max = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
        float min = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
        return max - min;
    }
}