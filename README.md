# Rivulet Light

Windows 桌面底部音频律动柔光：在屏幕底端（任务栏正上方）渲染一条随系统声音律动的波光带，颜色自动跟随正在播放音乐的封面。点击穿透、不抢焦点、不遮挡任务栏，全屏时自动隐藏。

## 特性

- **系统音频驱动**：WASAPI Loopback 采集所有正在播放的声音，FFT 实时频谱分析（约 21ms 周期）
- **封面自动取色**：监听系统媒体会话（SMTC），k-means 提取封面 4 主色，切歌 2s 平滑过渡；无封面时回退系统强调色
- **多级平滑**：音频端 + 渲染端双重快攻慢释包络，律动跟拍且无抖动/滚动感
- **可调参数**：频段数（16–96）、回落强度、峰值高度、覆盖层高度、整体强度、帧率等，全部实时生效
- **自我修复**：休眠唤醒自动重建渲染引擎与音频采集；采集异常指数退避重试；渲染投递超时自恢复
- **低开销**：D3D9Ex/D3D11 共享纹理零拷贝呈现，半分辨率渲染，稳态运行零托管分配

## 系统要求

- Windows 10 19041+ / Windows 11
- .NET 8 桌面运行时
- 支持 DirectX 11 的 GPU

## 构建

```bash
dotnet build RivuletLight.sln -c Release
```

发布单文件：

```bash
dotnet publish src/RivuletLight -c Release -r win-x64 --self-contained false -o publish
```

## 工作原理

### 端到端数据流

```
WASAPI Loopback (系统混音 float32)
  └─ LoopbackAudioSource：环形缓冲 2048 点 → 汉宁窗 → FFT
     → 对数分桶 96 频段 (20Hz–16kHz) → dB 映射 → 快攻慢释包络
     → Bass/Mid/High/RMS 聚合 + 静音检测状态机
     └─ FrameReady (音频线程, ~21ms)

SMTC 媒体会话 (Windows.Media.Control)
  └─ SmtcPaletteProvider：封面缩略图 → 降采样 64×64
     → k-means (k=4) 主色提取 → 缓存 + 防抖 + 序号竞态保护
     └─ PaletteChanged

App（UI 线程）
  ├─ 频谱二次平滑（快攻慢释，回落强度可调）
  ├─ 调色板 2s Vector4.Lerp 过渡（当前板 → 目标板）
  └─ RenderLoop（Timer → BeginInvoke 投递 UI 线程，跳帧 + 2s 超时自恢复）
     └─ D3D11Engine.Render
        ├─ 更新常量缓冲（96 频段 / 聚合值 / 调色板 / 时间与参数）
        ├─ 像素着色器绘制全屏 quad（WaveShader.hlsl）
        └─ 共享纹理 → D3DImage.AddDirtyRect → WPF 合成显示
```

### 模块说明

| 模块 | 文件 | 职责 |
|------|------|------|
| 音频源 | [LoopbackAudioSource.cs](src/RivuletLight/Audio/LoopbackAudioSource.cs) | WASAPI Loopback 采集 + FFT + 对数分桶 + 静音状态机 + 指数退避重启 |
| FFT | [Fft.cs](src/RivuletLight/Audio/Fft.cs) | 预分配缓冲的 FFT 实现 |
| 封面取色 | [SmtcPaletteProvider.cs](src/RivuletLight/Media/SmtcPaletteProvider.cs) | SMTC 会话监听、封面解码、防抖/防重入/序号竞态保护 |
| k-means | [KMeansPalette.cs](src/RivuletLight/Media/KMeansPalette.cs) | 确定性 4 主色提取（亮度×饱和度加权排序） |
| 系统强调色 | [SystemAccentPalette.cs](src/RivuletLight/Core/SystemAccentPalette.cs) | 封面不可用时的统一回退调色板（5s 缓存，返回副本） |
| 渲染引擎 | [D3D11Engine.cs](src/RivuletLight/Rendering/D3D11Engine.cs) | D3D11 设备/管线、D3D9Ex 共享纹理互操作、常量缓冲、回退模式 |
| 波光着色器 | [WaveShader.hlsl](src/RivuletLight/Rendering/WaveShader.hlsl) | 波形/频谱/颜色/衰减/抖动的全部视觉计算 |
| 覆盖窗口 | [OverlayWindow.cs](src/RivuletLight/Rendering/OverlayWindow.cs) | 透明点击穿透窗口、任务栏下方 z 序维护 |
| 渲染循环 | [RenderLoop.cs](src/RivuletLight/Rendering/RenderLoop.cs) | 帧调度、全屏检测（几何判断 + 消抖）、UI 线程安全投递 |
| 应用集成 | [App.xaml.cs](src/RivuletLight/App.xaml.cs) | 模块连线、调色板插值、淡入淡出、设置应用、休眠唤醒自愈 |
| UI | [SettingsWindow](src/RivuletLight/UI/SettingsWindow.xaml.cs) / [TrayMenuWindow](src/RivuletLight/UI/TrayMenuWindow.xaml.cs) / [Acrylic.cs](src/RivuletLight/UI/Acrylic.cs) | Fluent 风格 WPF 设置界面与托盘菜单（Acrylic 亚克力背景） |

### D3D9Ex / D3D11 互操作（关键机制）

WPF 的 `D3DImage` 只接受 D3D9 表面作为后缓冲，而本项目用 D3D11 渲染，因此通过共享纹理桥接：

1. `Direct3DCreate9Ex` 创建 D3D9Ex 对象，以 **[ComImport] 声明式 COM 接口**包装（CLR 自动 vtable 派发，杜绝手写槽位错误）
2. D3D9 与 DXGI 适配器**按 VendorId/DeviceId 匹配**（系统存在虚拟显示适配器时枚举顺序可能不一致）
3. D3D9Ex `CreateRenderTarget` 传出共享句柄 → `D3DImage.SetBackBuffer` 绑定表面
4. D3D11 `OpenSharedResource` 打开同一纹理作为渲染目标 → 渲染结果零拷贝呈现
5. 互操作失败（或设置 `RIVULET_FORCE_FALLBACK=1`）时回退 WriteableBitmap CPU 回读模式

每帧只做 `AddDirtyRect` 刷新脏区（不重复 SetBackBuffer），避免 DWM 重合成闪烁。

### z 序策略

覆盖窗口以 `SetWindowPos` 插到任务栏（`Shell_TrayWnd`）正下方，同时保持 `WS_EX_TOPMOST`，形成"普通应用 < 覆盖层 < 任务栏"的层级——效果可见但不遮挡任务栏。每秒检查一次 `GetWindow(GW_HWNDPREV)`，仅在实际丢失位置时才重新断言（无条件 SetWindowPos 会强制 DWM 重合成导致闪烁）。

## 视觉效果实现（WaveShader.hlsl）

所有视觉计算发生在像素着色器中，输入为常量缓冲中的频谱与调色板数据：

| 效果层 | 实现方式 |
|--------|----------|
| 波形流动 | 4 层 sin 叠加（波数 1.2/2.8/4.8/6.8），流速 = `0.12 + high × 0.18`，低速压制滚动感 |
| 律动驱动 | 低频能量调制波形幅度（`× (1 + bass × 0.7)`），RMS 调制整体亮度 |
| 频谱结构 | 横向 UV 映射到有效频段数，**相邻频段线性插值**消除硬边界；峰值高度系数可调 |
| 垂直衰减 | `exp(-uv.y × 3.5)`，底部亮、向上快速消散 |
| 颜色 | 两个强调色按 `sin` 空间混合——连续可微、无分段边界；`pow(0.6)` 提亮深色封面 |
| 波峰高光 | `pow(intensity, 6)` 白色高光形成波光"闪"感 |
| 软压缩 | `smoothstep` 替代 `saturate` 硬截断，波谷无移动的暗边界 |
| 抗断层 | 逐像素 hash 抖动（±0.5/255）打散 8bit 渲染目标上的渐变量化阶梯 |

平滑策略采用**快攻慢释**：上升系数大（0.65，跟拍），回落系数小（可调，默认频段 0.075/标量 0.12），从根本上消除频段交替时的上下跳动。

## 设置项

通过托盘图标打开设置窗口（实时生效）：

| 设置 | 范围 | 说明 |
|------|------|------|
| 效果开关 | — | 总开关，关闭时暂停渲染并隐藏覆盖层 |
| 频段数 | 16–96 | 越高横向过渡越细腻 |
| 回落强度 | 1–10 | 频谱峰值回落速度，越低越顺滑 |
| 峰值高度 | 0.5–2.0 | 频谱峰值高度系数 |
| 覆盖层高度 | 160–320 px | 逻辑像素 |
| 整体强度 | 0.1–1.0 | 淡入目标不透明度 |
| 颜色模式 | 封面取色/固定色/氛围自动 | 氛围自动使用系统强调色 |
| 帧率 | 30/60 | 渲染循环目标帧率 |
| 开机自启 | — | 写入 HKCU Run 注册表 |

## 故障排除

日志写入 `%LOCALAPPDATA%\RivuletLight\logs`（含渲染心跳、频谱诊断、崩溃前的最后存活环节）。

| 环境变量 | 作用 |
|----------|------|
| `RIVULET_FORCE_FALLBACK=1` | 跳过 D3D9 互操作，强制 WriteableBitmap 回退（排查崩溃） |
| `RIVULET_NO_FULLSCREEN_HIDE=1` | 禁用全屏自动隐藏（验证渲染效果用） |

常见问题：

- **唤醒后效果消失**：已内置自愈（Resume/前缓冲恢复 → 重建渲染引擎 + 重启采集）；若仍复现请查看日志
- **D3D9 互操作失败**：自动回退 WriteableBitmap 模式，功能不受影响，仅 CPU 开销略增
- **调色板不变化**：检查颜色模式是否为"封面取色"，以及播放器是否支持 SMTC（浏览器标签页播放可能被后台会话抢占，本应用优先选择"正在播放"的会话）

## 项目结构

```
Rivulet Light/
├── RivuletLight.sln
├── src/RivuletLight/
│   ├── Audio/          # WASAPI 采集与 FFT
│   ├── Media/          # SMTC 封面取色与 k-means
│   ├── Rendering/      # D3D11 引擎、着色器、覆盖窗口、渲染循环
│   ├── Core/           # 契约、日志、系统强调色
│   ├── UI/             # 设置窗口、托盘、Acrylic
│   └── App.xaml.cs     # 集成与协调
└── publish/            # 发布输出
```
