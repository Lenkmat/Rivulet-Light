using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;
using RivuletLight.Core;

namespace RivuletLight.Rendering;

/// <summary>
/// D3D11 渲染引擎：管理设备、渲染目标、常量缓冲，通过共享纹理与 D3DImage 互操作。
/// 每帧用波光着色器绘制全屏 quad。
/// 设备上下文可能被 UI 渲染线程与音频回调线程并发访问，全部经 <see cref="_gl"/> 串行化。
/// </summary>
internal sealed class D3D11Engine : IDisposable
{
    // ── D3D9 P/Invoke ───────────────────────────────────────────────
    private const int D3DSdkVersion = 32;
    private const int D3DfmtA8R8G8B8 = 21;
    private const int D3DfmtX8R8G8B8 = 22;
    private const int D3DpoolDefault = 0;
    private const int D3DusageRendertarget = 0x00000001;
    private const int D3DswapeffectDiscard = 1;

    private const int D3DdevtypeHal = 1;
    private const uint D3DcreateSoftwareVertexProcessing = 0x00000020;
    private const uint D3DcreateHardwareVertexProcessing = 0x00000040;
    private const uint D3DcreateFpuPreserve = 0x00000002;
    private const uint D3DcreateMultithreaded = 0x00000004;

    [DllImport("d3d9.dll", CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
    private static extern int Direct3DCreate9Ex(int sdkVersion, out IntPtr d3d9Ex);

    /// <summary>
    /// IDirect3D9 声明式视图（真实 IID），并按 IDirect3D9Ex 的布局追加 Ex 方法到槽 17-21。
    /// Ex 对象 QueryInterface(IID_IDirect3D9) 必然成功，CLR 按方法声明顺序做 vtable 派发，
    /// 从而安全调用 CreateDeviceEx（槽 20），避免手写 vtable 索引的错误。
    /// </summary>
    [ComImport, Guid("81BDCBCA-64D4-426D-AE8D-AD0147F4275C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3D9WithEx
    {
        // ── IDirect3D9（槽 3-16）─────────────────────────────────────
        [PreserveSig] int RegisterSoftwareDevice(IntPtr pInitializeFunction);                    // 3
        [PreserveSig] int GetAdapterCount();                                                     // 4
        [PreserveSig] int GetAdapterIdentifier(uint adapter, uint flags, IntPtr pIdentifier);    // 5
        [PreserveSig] int GetAdapterModeCount(uint adapter, uint format);                        // 6
        [PreserveSig] int EnumAdapterModes(uint adapter, uint format, uint mode, IntPtr pMode);  // 7
        [PreserveSig] int GetAdapterDisplayMode(uint adapter, IntPtr pMode);                     // 8
        [PreserveSig] int CheckDeviceType(uint adapter, int deviceType, uint adapterFormat, uint backBufferFormat, int windowed); // 9
        [PreserveSig] int CheckDeviceFormat(uint adapter, int deviceType, uint adapterFormat, uint usage, int resourceType, uint checkFormat); // 10
        [PreserveSig] int CheckDeviceMultiSampleType(uint adapter, int deviceType, uint adapterFormat, int windowed, int multiSampleType, IntPtr pQualityLevels); // 11
        [PreserveSig] int CheckDepthStencilMatch(uint adapter, int deviceType, uint adapterFormat, uint renderTargetFormat, uint depthStencilFormat); // 12
        [PreserveSig] int CheckDeviceFormatConversion(uint adapter, int deviceType, uint sourceFormat, uint targetFormat); // 13
        [PreserveSig] int GetDeviceCaps(uint adapter, int deviceType, IntPtr pCaps);             // 14
        [PreserveSig] IntPtr GetAdapterMonitor(uint adapter);                                    // 15
        [PreserveSig] int CreateDevice(uint adapter, int deviceType, IntPtr hFocusWindow, uint behaviorFlags, ref D3DPresentParameters presentationParameters, out IntPtr device); // 16

        // ── IDirect3D9Ex 追加（槽 17-21）─────────────────────────────
        [PreserveSig] int GetAdapterModeCountEx(uint adapter, IntPtr pFilter);                   // 17
        [PreserveSig] int EnumAdapterModesEx(uint adapter, IntPtr pFilter, uint mode, IntPtr pMode); // 18
        [PreserveSig] int GetAdapterDisplayModeEx(uint adapter, IntPtr pMode, IntPtr pRotation); // 19
        [PreserveSig] int CreateDeviceEx(uint adapter, int deviceType, IntPtr hFocusWindow, uint behaviorFlags, ref D3DPresentParameters presentationParameters, IntPtr pFullscreenDisplayMode, out IntPtr device); // 20
        [PreserveSig] int GetAdapterLUID(uint adapter, IntPtr pLuid);                            // 21
    }

    /// <summary>
    /// IDirect3DDevice9 声明式视图（真实 IID，槽 3-28，到 CreateRenderTarget 为止）。
    /// Ex 设备对象 QueryInterface(IID_IDirect3DDevice9) 必然成功（前 110+ 槽布局一致）。
    /// </summary>
    [ComImport, Guid("D0223B96-BF7A-43FD-92BD-A43B0D82B9EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDevice9View
    {
        [PreserveSig] int TestCooperativeLevel();                                                // 3
        [PreserveSig] int GetAvailableTextureMem();                                              // 4
        [PreserveSig] int EvictManagedResources();                                               // 5
        [PreserveSig] int GetDirect3D(out IntPtr d3d9);                                          // 6
        [PreserveSig] int GetDeviceCaps(IntPtr pCaps);                                           // 7
        [PreserveSig] int GetDisplayMode(uint swapChain, IntPtr pMode);                          // 8
        [PreserveSig] int GetCreationParameters(uint swapChain, IntPtr pParameters);             // 9
        [PreserveSig] int SetCursorProperties(uint xHotSpot, uint yHotSpot, IntPtr pCursorBitmap); // 10
        [PreserveSig] void SetCursorPosition(int x, int y, uint flags);                          // 11
        [PreserveSig] int ShowCursor(int bShow);                                                 // 12
        [PreserveSig] int CreateAdditionalSwapChain(ref D3DPresentParameters presentationParameters, out IntPtr swapChain); // 13
        [PreserveSig] int GetSwapChain(uint swapChain, out IntPtr outSwapChain);                 // 14
        [PreserveSig] int GetNumberOfSwapChains();                                               // 15
        [PreserveSig] int Reset(ref D3DPresentParameters presentationParameters);                // 16
        [PreserveSig] int Present(IntPtr pSourceRect, IntPtr pDestRect, IntPtr hDestWindowOverride, IntPtr pDirtyRegion); // 17
        [PreserveSig] int GetBackBuffer(uint swapChain, uint backBuffer, int type, out IntPtr surface); // 18
        [PreserveSig] int GetRasterStatus(uint swapChain, IntPtr pRasterStatus);                 // 19
        [PreserveSig] int SetDialogBoxMode(int enableDialogs);                                   // 20
        [PreserveSig] void SetGammaRamp(uint swapChain, uint flags, IntPtr pRamp);               // 21
        [PreserveSig] void GetGammaRamp(uint swapChain, IntPtr pRamp);                           // 22
        [PreserveSig] int CreateTexture(int width, int height, int levels, uint usage, uint format, int pool, out IntPtr texture, IntPtr pSharedHandle); // 23
        [PreserveSig] int CreateVolumeTexture(int width, int height, int depth, int levels, uint usage, uint format, int pool, out IntPtr texture, IntPtr pSharedHandle); // 24
        [PreserveSig] int CreateCubeTexture(int edgeLength, int levels, uint usage, uint format, int pool, out IntPtr texture, IntPtr pSharedHandle); // 25
        [PreserveSig] int CreateVertexBuffer(int length, uint usage, uint fvf, int pool, out IntPtr vertexBuffer, IntPtr pSharedHandle); // 26
        [PreserveSig] int CreateIndexBuffer(int length, uint usage, uint format, int pool, out IntPtr indexBuffer, IntPtr pSharedHandle); // 27
        [PreserveSig] int CreateRenderTarget(int width, int height, uint format, int multiSample, int multiSampleQuality, int lockable, out IntPtr surface, ref IntPtr pSharedHandle); // 28
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPresentParameters
    {
        public int BackBufferWidth;
        public int BackBufferHeight;
        public int BackBufferFormat;
        public int BackBufferCount;
        public int MultiSampleType;
        public int MultiSampleQuality;
        public int SwapEffect;
        public IntPtr HDeviceWindow;
        public int Windowed;
        public int EnableAutoDepthStencil;
        public int AutoDepthStencilFormat;
        public int Flags;
        public int FullScreenRefreshRateInHz;
        public int PresentationInterval;
    }

    // ── 常量缓冲布局 ─────────────────────────────────────────────────
    // float4 _Bands[24];       // 96 频段槽（有效频段数由 _TimeAndLerp.z 指定，尾部补零）
    // float4 _BassMidHighRms;  // x=Bass y=Mid z=High w=Rms
    // float4 _Palette[4];      // 4 色调色板
    // float4 _TimeAndLerp;     // x=time y=lerp z=有效频段数 w=峰值高度系数
    private const int CbFloat4Count = 30; // 24 + 1 + 4 + 1
    private const int CbSize = CbFloat4Count * 16; // 480 bytes

    // ── 顶点结构 ───────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y, Z, W;
        public float U, V;
    }

    // ── 嵌入的 HLSL 后备着色器（必须与 Rendering/WaveShader.hlsl 保持一致）──
    private const string EmbeddedShader = @"
// 屏幕底部波光柔光（随音乐律动）
cbuffer PerFrame : register(b0)
{
    float4 _Bands[24];       // 96 频段
    float4 _BassMidHighRms;  // x=Bass y=Mid z=High w=Rms
    float4 _Palette[4];      // 4 色调色板
    float4 _TimeAndLerp;     // x=time y=lerp z=有效频段数 w=峰值高度系数
};

struct VSOutput
{
    float4 Pos : SV_POSITION;
    float2 UV  : TEXCOORD0;
};

#define DECAY_RATE 3.5
#define TAU 6.28318531

VSOutput VS(float4 pos : POSITION, float2 uv : TEXCOORD0)
{
    VSOutput output;
    output.Pos = pos;
    output.UV = uv;
    return output;
}

float4 PS(VSOutput input) : SV_TARGET
{
    float2 uv = input.UV;
    float time = _TimeAndLerp.x;
    float bass = _BassMidHighRms.x;
    float high = _BassMidHighRms.z;
    float rms = _BassMidHighRms.w;

    // 波形：低速流动（低流速系数与降波数共同压制快速滚动感）
    float flowBase = 0.12 + high * 0.18;

    float wave = 0.0;
    wave += sin(uv.x * 1.2 * TAU + time * flowBase * 0.8) * 0.35;
    wave += sin(uv.x * 2.8 * TAU + time * flowBase * 1.1 + 1.2) * 0.25;
    wave += sin(uv.x * 4.8 * TAU + time * flowBase * 1.4 + 2.5) * 0.15;
    wave += sin(uv.x * 6.8 * TAU + time * flowBase * 1.7 + 0.8) * 0.10;

    wave *= 1.0 + bass * 0.7;
    wave *= lerp(1.0, 0.3, uv.y);

    // 软压缩波形（替代 saturate 硬截断）：波谷不再形成深暗的左右移动边界
    float intensity = saturate(0.35 + 0.65 * smoothstep(-1.2, 1.2, wave));

    // 频谱结构：横向位置映射到有效频段数，相邻频段线性插值消除硬边界
    float bandCount = max(_TimeAndLerp.z, 2.0);
    float fb = saturate(uv.x) * (bandCount - 1.0);
    int bi = min((int)fb, (int)bandCount - 2);
    float ffrac = fb - (float)bi;
    float band = lerp(_Bands[bi >> 2][bi & 3], _Bands[(bi + 1) >> 2][(bi + 1) & 3], ffrac);
    float bandGlow = 0.5 + _TimeAndLerp.w * band * exp(-uv.y * 4.2);

    float decay = exp(-uv.y * DECAY_RATE);
    float waveAlpha = saturate(intensity * bandGlow * (0.55 + rms * 0.75)) * decay;

    // 颜色：两个强调色 sin 平滑空间混合——连续可微、无分段边界、不平移，
    // 避免旧版 4 色分段渐变在色带交界处形成随时间平移的明显竖直边界
    float spatialMix = 0.5 + 0.5 * sin(uv.x * 1.2 + time * 0.08);
    float3 color = lerp(_Palette[1].rgb, _Palette[2].rgb, spatialMix);
    color = pow(max(color, 0.0), 0.6);

    float glow = 1.0 + bass * 0.9 * exp(-uv.y * 2.0);
    float3 outRgb = color * glow;

    // 波峰白色高光
    float crest = pow(intensity, 6.0) * exp(-uv.y * 3.0);
    outRgb += crest * 0.45;

    // 预乘 alpha
    outRgb *= waveAlpha;

    // 8bit 输出抖动：打散低幅度 alpha/亮度渐变在 8bit 渲染目标上的
    // 量化阶梯（从下到上的断层带）。SV_POSITION 在 PS 中即像素坐标
    float dither = frac(sin(dot(input.Pos.xy, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
    outRgb += dither / 255.0;

    float outAlpha = saturate(waveAlpha + dither / 255.0);
    if (outAlpha < 0.004) discard;
    return float4(outRgb, outAlpha);
}
";

    // ── D3D11 资源 ───────────────────────────────────────────────────
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private ID3D11Texture2D _renderTarget = null!;
    private ID3D11RenderTargetView _rtv = null!;
    private ID3D11Buffer _constantBuffer = null!;
    private ID3D11VertexShader _vertexShader = null!;
    private ID3D11PixelShader _pixelShader = null!;
    private ID3D11InputLayout _inputLayout = null!;
    private ID3D11Buffer _vertexBuffer = null!;
    private ID3D11Texture2D? _stagingTexture; // CPU 回读用（fallback 模式）

    // ── D3D9 互操作 ──────────────────────────────────────────────────
    private IntPtr _d3d9Object;
    private IntPtr _d3d9Device;
    private IntPtr _d3d9Texture;
    private IntPtr _d3d9Surface;

    // ── 状态 ─────────────────────────────────────────────────────────
    private readonly D3DImage _d3dImage;
    private readonly WriteableBitmap? _fallbackBitmap;
    private readonly byte[]? _fallbackPixels; // 预分配的回退像素缓冲
    private readonly int _renderWidth;
    private readonly int _renderHeight;
    private readonly float[] _cbData = new float[CbFloat4Count * 4]; // 120 floats
    private readonly object _gl = new(); // 串行化设备上下文访问（UI 渲染线程 / 捕获回调线程）
    private bool _disposed;
    private bool _started;
    private bool _useFallback; // D3D9 互操作失败时启用 WriteableBitmap 回退
    private long _frameCount;  // 渲染帧计数（心跳日志用）

    // ── 属性 ─────────────────────────────────────────────────────────
    public bool IsRunning => _started;
    public bool UseFallback => _useFallback;
    public WriteableBitmap? FallbackBitmap => _fallbackBitmap;
    /// <summary>D3D11 设备（供需要共享设备的模块使用）。</summary>
    public ID3D11Device Device => _device;

    // ── 构造 ─────────────────────────────────────────────────────────
    public D3D11Engine(D3DImage d3dImage, int screenWidth, int overlayHeight)
    {
        _d3dImage = d3dImage;
        _renderWidth = Math.Max(1, screenWidth / 2);
        _renderHeight = Math.Max(1, overlayHeight / 2);

        // 常量缓冲默认值：有效频段数 96、峰值高度系数 2.00（App 加载设置后可能覆盖）
        _cbData[118] = SpectrumFrame.BandCount;
        _cbData[119] = 2.0f;

        InitializeD3D11();

        try
        {
            // 调试开关：RIVULET_FORCE_FALLBACK=1 时跳过 D3D9 互操作（崩溃排查的对照实验）
            if (Environment.GetEnvironmentVariable("RIVULET_FORCE_FALLBACK") == "1")
                throw new InvalidOperationException("调试开关：强制 WriteableBitmap 回退模式");
            InitializeD3D9();
        }
        catch (Exception ex)
        {
            Logger.LogError("[D3D11] D3D9 互操作失败，切换到 WriteableBitmap 回退模式", ex);
            _useFallback = true;

            // 清理已创建的部分 D3D9 资源（Dispose 时也会兜底）
            CleanupD3D9();

            // 创建 WriteableBitmap 作为回退渲染目标
            // Pbgra32 = 预乘 alpha，与着色器输出格式一致
            _fallbackBitmap = new WriteableBitmap(
                _renderWidth, _renderHeight, 96, 96,
                PixelFormats.Pbgra32, null);

            // 非共享渲染目标（回退模式不需要跨设备共享）
            // 注意：必须使用 B8G8R8A8_UNorm —— 回退 WriteableBitmap(Bgra32) 的正确字节序
            var texDesc = new Texture2DDescription
            {
                Width = (uint)_renderWidth,
                Height = (uint)_renderHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            _renderTarget = _device.CreateTexture2D(texDesc);
            _rtv = _device.CreateRenderTargetView(_renderTarget);

            // 创建 staging 纹理用于 CPU 回读
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)_renderWidth,
                Height = (uint)_renderHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
            };
            _stagingTexture = _device.CreateTexture2D(stagingDesc);
            _fallbackPixels = new byte[_renderHeight * _renderWidth * 4];
        }
    }

    /// <summary>释放 D3D9 侧已创建的部分互操作资源（初始化失败时调用）。</summary>
    private void CleanupD3D9()
    {
        if (_d3d9Surface != IntPtr.Zero) { Marshal.Release(_d3d9Surface); _d3d9Surface = IntPtr.Zero; }
        if (_d3d9Texture != IntPtr.Zero) { Marshal.Release(_d3d9Texture); _d3d9Texture = IntPtr.Zero; }
        if (_d3d9Device != IntPtr.Zero) { Marshal.Release(_d3d9Device); _d3d9Device = IntPtr.Zero; }
        if (_d3d9Object != IntPtr.Zero) { Marshal.Release(_d3d9Object); _d3d9Object = IntPtr.Zero; }
    }

    // ── D3D11 初始化 ─────────────────────────────────────────────────
    private void InitializeD3D11()
    {
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out _device,
            out _,
            out _context);

        if (result.Failure)
            throw new InvalidOperationException($"D3D11 设备创建失败: {result}");

        // 渲染目标纹理不在此创建：
        // - 互操作成功路径：由 D3D9Ex 创建共享纹理，D3D11 通过 OpenSharedResource 打开（见 InitializeD3D9）
        // - 回退路径：在构造函数 catch 中创建非共享渲染目标 + WriteableBitmap

        // 创建常量缓冲
        var cbDesc = new BufferDescription
        {
            ByteWidth = (uint)CbSize,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        };
        _device.CreateBuffer(cbDesc, null, out _constantBuffer);

        // ── 编译着色器 ─────────────────────────────────────────────────
        // 一律优先从随包分发的 WaveShader.hlsl 文件编译（CompileFromFile 直读
        // UTF-8 字节）。Compiler.Compile(string) 会把字符串按 ANSI(GBK) marshal，
        // 中文注释的多字节序列被 D3DCompile 按 UTF-8 解释后破坏语法（X3000
        // unexpected end of file），导致 Release 内嵌路径必现编译失败。
        // 内嵌字符串仅在文件缺失时兜底。
        ReadOnlyMemory<byte> vsBytecode = default;
        ReadOnlyMemory<byte> psBytecode = default;
        var shaderFlags = ShaderFlags.EnableStrictness;
#if DEBUG
        shaderFlags |= ShaderFlags.Debug;
        shaderFlags |= ShaderFlags.SkipValidation;
#else
        shaderFlags |= ShaderFlags.OptimizationLevel3;
#endif

        bool compiledFromFile = false;
        // 候选路径：发布形态（输出目录 Rendering\ 子目录 / exe 同级）、开发形态（bin\Debug 回溯源码树）
        var shaderCandidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rendering", "WaveShader.hlsl"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WaveShader.hlsl"),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..\\..\\..\\Rendering\\WaveShader.hlsl")),
        };
        var shaderPath = shaderCandidates.FirstOrDefault(File.Exists);
        if (shaderPath != null)
        {
            try
            {
                vsBytecode = Compiler.CompileFromFile(shaderPath, "VS", "vs_5_0", shaderFlags);
                psBytecode = Compiler.CompileFromFile(shaderPath, "PS", "ps_5_0", shaderFlags);
                compiledFromFile = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("[D3D11] 从文件编译着色器失败，回退到内嵌着色器", ex);
                compiledFromFile = false;
            }
        }

        if (!compiledFromFile)
        {
            try
            {
                vsBytecode = Compiler.Compile(EmbeddedShader, "VS", "WaveShader", "vs_5_0", shaderFlags);
                psBytecode = Compiler.Compile(EmbeddedShader, "PS", "WaveShader", "ps_5_0", shaderFlags);
            }
            catch (Exception ex)
            {
                Logger.LogError("[D3D11] 内嵌着色器编译失败", ex);
                throw;
            }
        }

        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        // ── 创建 InputLayout ────────────────────────────────────────────
        var inputElements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 16, 0),
        };
        _inputLayout = _device.CreateInputLayout(inputElements, vsBytecode.Span);

        // ── 创建 Full-Screen Quad 顶点缓冲 ──────────────────────────────
        // 4 个顶点组成 triangle strip（两个三角形覆盖整个屏幕）
        // UV.y: 0 = 底部, 1 = 顶部
        Vertex[] vertices =
        {
            new() { X = -1, Y = -1, Z = 0, W = 1, U = 0, V = 0 }, // 左下
            new() { X = -1, Y = +1, Z = 0, W = 1, U = 0, V = 1 }, // 左上
            new() { X = +1, Y = -1, Z = 0, W = 1, U = 1, V = 0 }, // 右下
            new() { X = +1, Y = +1, Z = 0, W = 1, U = 1, V = 1 }, // 右上
        };

        var vbDesc = new BufferDescription
        {
            ByteWidth = (uint)(vertices.Length * 24), // 4 * (16 + 8) = 96
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        };
        var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
        try
        {
            var initData = new SubresourceData(handle.AddrOfPinnedObject());
            _vertexBuffer = _device.CreateBuffer(vbDesc, initData);
        }
        finally
        {
            handle.Free();
        }
    }

    // ── D3D9 互操作初始化 ────────────────────────────────────────────
    private void InitializeD3D9()
    {
        // 创建 D3D9Ex 对象（共享句柄资源必须使用 Ex 设备）
        int hrEx = Direct3DCreate9Ex(D3DSdkVersion, out _d3d9Object);
        if (hrEx < 0 || _d3d9Object == IntPtr.Zero)
            throw new InvalidOperationException($"Direct3DCreate9Ex 失败, HRESULT=0x{hrEx:X8}");

        // 包装为声明式 COM 视图（CLR 自动处理 vtable 派发与参数编组）
        var d3d9 = (IDirect3D9WithEx)Marshal.GetObjectForIUnknown(_d3d9Object);
        try
        {
            // 获取窗口句柄（从 D3DImage 所属的 PresentationSource）
            var hwnd = IntPtr.Zero;
            var source = PresentationSource.FromDependencyObject(_d3dImage);
            if (source is HwndSource hwndSource)
                hwnd = hwndSource.Handle;

            // ── 适配器匹配：D3D9 与 DXGI 的适配器枚举顺序可能不一致 ──
            // （本机存在 GameViewer 虚拟显示适配器，错配会导致创建出无效表面）
            int dxgiVendorId = 0, dxgiDeviceId = 0;
            try
            {
                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                using var dxgiAdapter = dxgiDevice.GetAdapter();
                var desc = dxgiAdapter.Description;
                dxgiVendorId = (int)desc.VendorId;
                dxgiDeviceId = (int)desc.DeviceId;
                Logger.Log($"[D3D11] DXGI 适配器：{desc.Description} Ven=0x{desc.VendorId:X4} Dev=0x{desc.DeviceId:X4}");
            }
            catch (Exception ex)
            {
                Logger.LogError("[D3D11] 获取 DXGI 适配器信息失败（将使用 D3D9 适配器 0）", ex);
            }

            int chosenAdapter = -1;
            int adapterCount = d3d9.GetAdapterCount();
            // D3DADAPTER_IDENTIFIER9 布局（MAX_DEVICE_IDENTIFIER_STRING=512）：
            //   char Driver[512]        0..512
            //   char Description[512]   512..1024
            //   char DeviceName[32]     1024..1056
            //   LARGE_INTEGER DriverVersion  1056..1064
            //   DWORD VendorId          1064
            //   DWORD DeviceId          1068
            var identBuffer = Marshal.AllocHGlobal(1152);
            try
            {
                for (uint i = 0; i < adapterCount; i++)
                {
                    if (d3d9.GetAdapterIdentifier(i, 0, identBuffer) < 0) continue;

                    string desc = Marshal.PtrToStringAnsi(identBuffer + 512) ?? string.Empty;
                    int vendorId = Marshal.ReadInt32(identBuffer, 1064);
                    int deviceId = Marshal.ReadInt32(identBuffer, 1068);
                    Logger.Log($"[D3D11] D3D9 适配器 {i}：{desc} Ven=0x{vendorId:X4} Dev=0x{deviceId:X4}");

                    if (chosenAdapter < 0 && vendorId != 0
                        && vendorId == dxgiVendorId && deviceId == dxgiDeviceId)
                        chosenAdapter = (int)i;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(identBuffer);
            }
            // 无匹配时兜底适配器 0（D3D9 适配器 0 通常是主显示适配器）
            if (chosenAdapter < 0)
            {
                Logger.Log("[D3D11] D3D9/DXGI 适配器无 Ven/Dev 匹配，回退 D3D9 适配器 0");
                chosenAdapter = 0;
            }
            Logger.Log($"[D3D11] D3D9 使用适配器 {chosenAdapter}");

            var pp = new D3DPresentParameters
            {
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = D3DfmtX8R8G8B8,
                BackBufferCount = 0,
                SwapEffect = D3DswapeffectDiscard,
                HDeviceWindow = hwnd,
                Windowed = 1,
                EnableAutoDepthStencil = 0,
                PresentationInterval = 0
            };

            // MULTITHREADED + FPU_PRESERVE：D3DImage 互操作的标准要求
            const uint baseFlags = D3DcreateMultithreaded | D3DcreateFpuPreserve;
            // 先尝试硬件顶点处理，失败则回退到软件顶点处理
            hrEx = d3d9.CreateDeviceEx((uint)chosenAdapter, D3DdevtypeHal, hwnd,
                baseFlags | D3DcreateHardwareVertexProcessing,
                ref pp, IntPtr.Zero, out _d3d9Device);
            if (hrEx < 0)
            {
                hrEx = d3d9.CreateDeviceEx((uint)chosenAdapter, D3DdevtypeHal, hwnd,
                    baseFlags | D3DcreateSoftwareVertexProcessing,
                    ref pp, IntPtr.Zero, out _d3d9Device);
            }
            if (hrEx < 0 || _d3d9Device == IntPtr.Zero)
                throw new InvalidOperationException($"D3D9Ex 设备创建失败, HRESULT={hrEx}");
            Logger.Log("[D3D11] D3D9Ex 设备已创建");

            var device = (IDirect3DDevice9View)Marshal.GetObjectForIUnknown(_d3d9Device);
            try
            {
                // ── D3D9Ex 创建共享渲染目标表面（句柄输出方向）──────────
                // CreateRenderTarget 原生返回 IDirect3DSurface9*，无需 GetSurfaceLevel，
                // 传 NULL 句柄则 D3D9Ex 创建共享资源并把句柄写回 sharedHandle
                var sharedHandle = IntPtr.Zero;
                int hr = device.CreateRenderTarget(_renderWidth, _renderHeight,
                    (uint)D3DfmtA8R8G8B8, 0, 0, 0, // MultiSample=None, Quality=0, Lockable=false
                    out _d3d9Surface, ref sharedHandle);
                if (hr < 0 || _d3d9Surface == IntPtr.Zero || sharedHandle == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"D3D9 共享渲染目标创建失败, HRESULT={hr}, surface=0x{_d3d9Surface.ToInt64():X}, handle=0x{sharedHandle.ToInt64():X}");
                Logger.Log($"[D3D11] D3D9 共享渲染目标已创建，surface=0x{_d3d9Surface.ToInt64():X}，句柄=0x{sharedHandle.ToInt64():X}");

                // 预检：QueryInterface 验证表面确实是有效的 IDirect3DSurface9
                // （IID 来自 d3d9helper.h：{0CFBAF3A-9FF6-429A-99B3-A2796AF8B89B}）
                var surfaceIid = new Guid("0CFBAF3A-9FF6-429A-99B3-A2796AF8B89B");
                hr = Marshal.QueryInterface(_d3d9Surface, ref surfaceIid, out var qiSurface);
                if (hr < 0 || qiSurface == IntPtr.Zero)
                    throw new InvalidOperationException($"D3D9 表面 QueryInterface 失败, HRESULT={hr}");
                Marshal.Release(qiSurface);

                // 设置 D3DImage 的后缓冲区
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface);
                _d3dImage.Unlock();

                // ── D3D11 打开共享纹理作为渲染目标 ────────────────────
                // D3D9 的 D3DFMT_A8R8G8B8 渲染目标 ↔ DXGI_FORMAT_B8G8R8A8_UNorm
                _renderTarget = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle)
                    ?? throw new InvalidOperationException("D3D11 OpenSharedResource 返回 null");
                _rtv = _device.CreateRenderTargetView(_renderTarget);
                Logger.Log("[D3D11] D3D9 共享纹理互操作完成：D3DImage 后缓冲与 D3D11 渲染目标已就绪");
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(d3d9);
        }
    }

    // ── 数据上传 ─────────────────────────────────────────────────────
    /// <summary>上传频谱数据到常量缓冲（音频线程调用，与渲染线程互斥）。</summary>
    public void SetSpectrum(SpectrumFrame frame)
    {
        if (_disposed) return;
        lock (_gl)
        {
            if (_disposed) return;
            // 96 频段 -> 24 float4
            for (int i = 0; i < SpectrumFrame.BandCount; i++)
                _cbData[i] = frame.Bands[i];

            // Bass/Mid/High/Rms 到第 25 个 float4
            _cbData[96] = frame.Bass;
            _cbData[97] = frame.Mid;
            _cbData[98] = frame.High;
            _cbData[99] = frame.Rms;
        }
    }

    /// <summary>上传调色板与时间参数到常量缓冲。</summary>
    public void SetPalette(float time, Palette palette, float lerp)
    {
        if (_disposed) return;
        lock (_gl)
        {
            if (_disposed) return;
            // 4 色调色板 -> float4[4]（紧跟 24 个频段 float4 + 1 个聚合 float4 之后）
            for (int i = 0; i < Palette.ColorCount; i++)
            {
                _cbData[100 + i * 4 + 0] = palette.Colors[i].X;
                _cbData[100 + i * 4 + 1] = palette.Colors[i].Y;
                _cbData[100 + i * 4 + 2] = palette.Colors[i].Z;
                _cbData[100 + i * 4 + 3] = palette.Colors[i].W;
            }

            // 第 30 个 float4：x=time y=lerp；z=有效频段数 w=峰值高度系数（SetRenderParams 写入）
            _cbData[116] = time;
            _cbData[117] = lerp;
        }
    }

    /// <summary>上传渲染参数：有效频段数与峰值高度系数（设置变化时调用）。</summary>
    public void SetRenderParams(int bandCount, float peakHeight)
    {
        if (_disposed) return;
        lock (_gl)
        {
            if (_disposed) return;
            _cbData[118] = bandCount;
            _cbData[119] = peakHeight;
        }
    }

    // ── 渲染 ─────────────────────────────────────────────────────────
    /// <summary>执行一帧渲染：用着色器绘制波光，然后通过共享纹理输出到 D3DImage。</summary>
    public void Render(TimeSpan elapsed)
    {
        if (_disposed) return;

        try
        {
            lock (_gl)
            {
                if (_disposed) return;

                // 心跳日志：每 600 帧（约 10s）记录一次，崩溃时可据此定位最后存活环节
                if (++_frameCount % 600 == 0)
                    Logger.Log($"[D3D11] 心跳 frame={_frameCount} fallback={_useFallback}");

                // 更新常量缓冲
                var mapped = _context.Map(_constantBuffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                Marshal.Copy(_cbData, 0, mapped.DataPointer, _cbData.Length);
                _context.Unmap(_constantBuffer, 0);

                // 绑定渲染目标 + 设置视口（必须显式设置，否则 Draw 无输出！）
                _context.OMSetRenderTargets(_rtv, null);
                _context.RSSetViewport(new Viewport(0, 0, _renderWidth, _renderHeight));

                // 清空渲染目标（完全透明，着色器绘制波光）
                _context.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 0f));

                // ── 设置着色器管线 ─────────────────────────────────────────────
                _context.VSSetShader(_vertexShader);
                _context.PSSetShader(_pixelShader);

                _context.VSSetConstantBuffer(0, _constantBuffer);
                _context.PSSetConstantBuffer(0, _constantBuffer);

                _context.IASetInputLayout(_inputLayout);
                _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleStrip);

                // 设置全屏 quad 顶点缓冲
                uint stride = 24; // 4 * 4 + 2 * 4 = 24 bytes
                uint offset = 0;
                _context.IASetVertexBuffers(0, new[] { _vertexBuffer }, new[] { stride }, new[] { offset });

                // 绘制全屏 quad（4 vertices, 2 triangles via strip）
                _context.Draw(4, 0);

                if (_useFallback)
                {
                    RenderFallback();
                }
                else
                {
                    // 冲刷命令队列，确保共享纹理更新对 D3D9/D3DImage 侧可见
                    _context.Flush();

                    // 后缓冲在 InitializeD3D9 中已绑定一次；每帧重复 SetBackBuffer 会
                    // 强制 D3DImage 重新解析 surface（开销 + 闪烁隐患），此处只刷新脏区
                    _d3dImage.Lock();
                    _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _renderWidth, _renderHeight));
                    _d3dImage.Unlock();
                }
            }
        }
        catch (Exception ex)
        {
            // 渲染帧失败（如 D3DImage 前缓冲不可用）不应终止渲染循环；
            // 原生层 Access Violation 无法在此捕获，由心跳日志定位
            Logger.LogError("[D3D11] 渲染帧异常（已跳过该帧）", ex);
        }
    }

    /// <summary>回退模式：将 D3D11 渲染结果通过 CPU 回读写入 WriteableBitmap。</summary>
    private void RenderFallback()
    {
        if (_stagingTexture == null || _fallbackBitmap == null || _fallbackPixels == null) return;
        try
        {
            // 从渲染目标拷贝到 staging（GPU→CPU 回读方向）
            _context.CopyResource(_stagingTexture, _renderTarget);
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            if (mapped.DataPointer != IntPtr.Zero)
            {
                int srcStride = _renderWidth * 4;
                // 逐行复制到预分配缓冲
                for (int y = 0; y < _renderHeight; y++)
                {
                    IntPtr srcRow = new IntPtr(mapped.DataPointer.ToInt64() + y * mapped.RowPitch);
                    Marshal.Copy(srcRow,
                        _fallbackPixels, y * srcStride, srcStride);
                }
                _context.Unmap(_stagingTexture, 0);
                // 写入到 WriteableBitmap
                _fallbackBitmap.WritePixels(
                    new Int32Rect(0, 0, _renderWidth, _renderHeight),
                    _fallbackPixels, srcStride, 0);
            }
        }
        catch
        {
            // 回退模式下的渲染失败不必抛出
        }
    }

    // ── 生命周期 ─────────────────────────────────────────────────────
    public void Start()
    {
        _started = true;
    }

    public void Stop()
    {
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gl)
        {
            // 释放 D3D9 资源
            if (_d3d9Surface != IntPtr.Zero)
                Marshal.Release(_d3d9Surface);
            if (_d3d9Texture != IntPtr.Zero)
                Marshal.Release(_d3d9Texture);
            if (_d3d9Device != IntPtr.Zero)
                Marshal.Release(_d3d9Device);
            if (_d3d9Object != IntPtr.Zero)
                Marshal.Release(_d3d9Object);

            // 释放 D3D11 资源
            _stagingTexture?.Dispose();
            _vertexBuffer?.Dispose();
            _inputLayout?.Dispose();
            _pixelShader?.Dispose();
            _vertexShader?.Dispose();
            _constantBuffer?.Dispose();
            _rtv?.Dispose();
            _renderTarget?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
