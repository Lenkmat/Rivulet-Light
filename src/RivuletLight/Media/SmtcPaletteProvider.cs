using System.Numerics;
using RivuletLight.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace RivuletLight.Media;

/// <summary>
/// SMTC 封面取色调色板提供者：监听系统媒体会话，从封面提取 4 主色调色板。
/// 封面无法获取（无缩略图/解码失败/SMTC 不可用）时回退到系统强调色（个性化主题色）。
/// 实现 IPaletteProvider 契约。
/// </summary>
public sealed class SmtcPaletteProvider : IPaletteProvider
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private Palette _current = SystemAccentPalette.Get(); // 初始即系统强调色（封面未取得时的回退色）
    private bool _started;
    private bool _disposed;

    // 缓存：避免重复解码同一封面（Title+Artist+AlbumTitle 作为 key）
    private readonly Dictionary<(string? Title, string? Artist, string? AlbumTitle), Palette> _cache = new();
    private (string? Title, string? Artist, string? AlbumTitle) _lastKey;
    private (string? Title, string? Artist, string? AlbumTitle)? _extractingKey; // 正在取色的 key（防重入）
    private long _lastProcessTick;      // 高频触发防抖
    private int _processSeq; // 异步处理序号：仅最新请求可发布结果，防止过期任务覆盖造成调色板闪变
    private readonly object _lock = new();

    public event Action? PaletteChanged;

    public Palette Current
    {
        get { lock (_lock) { return _current; } }
    }

    /// <summary>启动 SMTC 监听。幂等，可多次调用。</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        _ = InitializeAsync();
    }

    /// <summary>异步初始化 SMTC 管理器。</summary>
    private async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;

            SelectActiveSession();
            if (_currentSession != null)
            {
                await ProcessSessionAsync(_currentSession);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("SMTC 初始化失败", ex);
            SetDefaultPalette();
        }
    }

    /// <summary>
    /// 选择活跃会话：优先"正在播放"的会话，避免系统存在多个会话（如后台浏览器视频 + 音乐播放器）
    /// 时 CurrentSessionChanged 交替触发导致调色板来回闪变。
    /// </summary>
    private void SelectActiveSession()
    {
        if (_manager == null) return;

        GlobalSystemMediaTransportControlsSession? selected = null;

        // 优先正在播放的会话
        foreach (var s in _manager.GetSessions())
        {
            try
            {
                if (s.GetPlaybackInfo().PlaybackStatus
                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    selected = s;
                    break;
                }
            }
            catch { /* 单个会话查询失败不影响整体 */ }
        }

        // 无播放中的会话时退回默认策略
        selected ??= _manager.GetCurrentSession();

        if (!ReferenceEquals(selected, _currentSession))
        {
            if (_currentSession != null)
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;

            _currentSession = selected;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _ = ProcessSessionAsync(_currentSession);
            }
        }
        else if (_currentSession != null)
        {
            // 会话未变但状态可能变化（如从暂停恢复），重新处理一次
            _ = ProcessSessionAsync(_currentSession);
        }
    }

    /// <summary>当前会话切换事件。</summary>
    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        try
        {
            SelectActiveSession();
        }
        catch (Exception ex)
        {
            Logger.LogError("SMTC 会话切换失败", ex);
            SetDefaultPalette();
        }
    }

    /// <summary>当前会话媒体属性变化事件。</summary>
    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        _ = ProcessSessionAsync(sender);
    }

    /// <summary>处理会话媒体属性：提取封面 → 降采样 → k-means 取色 → 缓存并发布。</summary>
    private async Task ProcessSessionAsync(GlobalSystemMediaTransportControlsSession session)
    {
        // 高频触发防抖（必须在递增 seq 之前：SMTC 进度更新也会触发 MediaPropertiesChanged，
        // 每次触发都会递增 seq，令进行中的取色结果"永远过期"而无法发布——
        // 表现为调色板滞后一拍，一直显示上一首歌的颜色）
        long nowTick = Environment.TickCount64;
        long lastTick = Interlocked.Read(ref _lastProcessTick);
        if (nowTick - lastTick < 800)
        {
            return;
        }
        Interlocked.Exchange(ref _lastProcessTick, nowTick);

        // 序号竞态保护：取色（解码+k-means）耗时较长，期间可能有新请求；
        // 仅最新请求允许发布，过期结果直接丢弃，防止调色板被旧数据来回覆盖（闪变）
        int seq = Interlocked.Increment(ref _processSeq);
        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties? props;
            try
            {
                props = await session.TryGetMediaPropertiesAsync();
            }
            catch
            {
                props = null;
            }

            if (props == null)
            {
                // 切歌瞬间 TryGetMediaPropertiesAsync 可能短暂失败：
                // 保留当前调色板（重置默认色会造成调色板闪变）
                return;
            }

            var key = (props.Title, props.Artist, props.AlbumTitle ?? "");

            // 缓存命中且与上次不同 → 直接返回缓存。
            // 注意：PaletteChanged 必须在 _lock 之外触发——事件处理器会在 UI 线程上
            // 读取 Current（同样需要 _lock），若在锁内同步触发将造成循环等待死锁
            // （切歌时缓存命中路径即触发此死锁，UI 线程永久冻结）。
            bool publishCached = false;
            bool cacheHit = false;
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached))
                {
                    cacheHit = true;
                    if (!Equals(key, _lastKey))
                    {
                        _lastKey = key;
                        _current = cached;
                        publishCached = true;
                    }
                }
            }
            if (publishCached)
            {
                PaletteChanged?.Invoke();
                return;
            }
            if (cacheHit)
            {
                return; // 同一首歌的重复通知：调色板已在显示，无需处理
            }

            // 取色防重入：同一 key 的取色进行中时直接等待其完成，
            // 避免并发取色相互作废（取色耗时秒级 > MediaPropertiesChanged 间隔）
            lock (_lock)
            {
                if (_extractingKey == key)
                {
                    return;
                }
                _extractingKey = key;
            }

            // 有封面 → 后台取色解码
            if (props.Thumbnail != null)
            {
                try
                {
                    var palette = await Task.Run(() => ExtractPaletteFromThumbnailAsync(props.Thumbnail));

                    // 结果无条件写入缓存：即使 seq 已过期（期间有新触发），缓存命中
                    // 也能让下一次触发直接发布，避免重复解码空转
                    if (palette != null)
                    {
                        lock (_lock)
                        {
                            _cache[key] = palette;
                            _extractingKey = null;
                        }

                        if (seq != Volatile.Read(ref _processSeq)) return; // 过期请求：仅发布作废

                        lock (_lock)
                        {
                            _lastKey = key;
                            _current = palette;
                        }
                        PaletteChanged?.Invoke();
                        return;
                    }
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_extractingKey == key) _extractingKey = null;
                    }
                }
            }

            // 无封面 → 系统强调色回退（用户自定义主题色）
            var fallback = SystemAccentPalette.Get();
            if (seq != Volatile.Read(ref _processSeq)) return; // 过期请求，丢弃

            lock (_lock)
            {
                _cache[key] = fallback;
                _lastKey = key;
                _current = fallback;
            }
            PaletteChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.LogError("SMTC 取色失败", ex);
            SetDefaultPalette();
        }
    }

    /// <summary>从封面缩略图提取调色板（降采样 ~64×64 后 k-means）。</summary>
    private static async Task<Palette?> ExtractPaletteFromThumbnailAsync(
        IRandomAccessStreamReference thumbnailRef)
    {
        try
        {
            using var stream = await thumbnailRef.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // 降采样至 ~64×64
            uint sw = Math.Min(decoder.PixelWidth, 64u);
            uint sh = (uint)Math.Max(1,
                decoder.PixelHeight * sw / (double)decoder.PixelWidth);
            if (sh > 64)
            {
                sh = 64;
                sw = (uint)Math.Max(1,
                    decoder.PixelWidth * sh / (double)decoder.PixelHeight);
            }

            var transform = new BitmapTransform
            {
                ScaledWidth = sw,
                ScaledHeight = sh,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            var pixels = pixelData.DetachPixelData();
            var colors = KMeansPalette.Extract(pixels, (int)sw, (int)sh);

            var palette = new Palette();
            for (int i = 0; i < Palette.ColorCount; i++)
            {
                palette.Colors[i] = i < colors.Length ? colors[i] : Vector4.Zero;
            }
            return palette;
        }
        catch (Exception ex)
        {
            Logger.LogError("封面解码失败", ex);
            return null;
        }
    }

    /// <summary>回退到系统强调色调色板（无会话/取色失败等封面不可用场景）。</summary>
    private void SetDefaultPalette()
    {
        lock (_lock)
        {
            _current = SystemAccentPalette.Get();
        }
        PaletteChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        }

        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        _manager = null;
        _currentSession = null;
    }
}