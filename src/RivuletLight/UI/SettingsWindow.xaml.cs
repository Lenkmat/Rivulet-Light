using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RivuletLight.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using RadioButton = System.Windows.Controls.RadioButton;

namespace RivuletLight.UI;

/// <summary>
/// WinUI/Fluent 风格设置窗口：DWM 亚克力毛玻璃背景（失焦仍可见）、设置卡片布局、
/// 系统强调色控件。所有参数变化立即持久化并实时作用于渲染管线。
/// </summary>
public partial class SettingsWindow : Window
{
    // ── 预设固定色 ─────────────────────────────────────────────────────
    private static readonly Color[] PresetColors =
    {
        Color.FromRgb(0xFF, 0x44, 0x44), // 红
        Color.FromRgb(0xFF, 0x88, 0x00), // 橙
        Color.FromRgb(0xFF, 0xDD, 0x00), // 黄
        Color.FromRgb(0x44, 0xCC, 0x44), // 绿
        Color.FromRgb(0x44, 0x88, 0xFF), // 蓝
        Color.FromRgb(0xAA, 0x44, 0xFF), // 紫
    };

    private Color _selectedFixedColor = Color.FromRgb(0x44, 0x88, 0xFF);
    private bool _initializing = true;

    // ── 构造 ─────────────────────────────────────────────────────────
    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
        _initializing = false;
    }

    // ── 窗口初始化 ────────────────────────────────────────────────────
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 亚克力毛玻璃（半透明深色底色，失焦仍可见）
        Acrylic.Enable(this, Color.FromArgb(0xD8, 0x20, 0x20, 0x20));

        // 强调色跟随系统主题
        ApplySystemAccent();

        // 生成固定色色块
        GenerateColorSwatches();
    }

    /// <summary>以系统强调色（AccentLight1，暗色界面上更鲜亮）替换默认强调色。</summary>
    private void ApplySystemAccent()
    {
        try
        {
            var accent = SystemAccentPalette.Get();
            var v = accent.Colors[3];
            var c = Color.FromRgb(
                (byte)Math.Clamp(v.X * 255f, 0f, 255f),
                (byte)Math.Clamp(v.Y * 255f, 0f, 255f),
                (byte)Math.Clamp(v.Z * 255f, 0f, 255f));
            Resources["AccentBrush"] = new SolidColorBrush(c);
        }
        catch
        {
            // 保持 XAML 中的默认强调色
        }
    }

    // ── 加载设置 ──────────────────────────────────────────────────────
    private void LoadSettings()
    {
        var s = SettingsManager.Current;

        EffectToggle.IsChecked = s.EffectEnabled;
        SpectrumBandsSlider.Value = Math.Clamp(s.SpectrumBands, 16, 96);
        FallStrengthSlider.Value = Math.Clamp(s.FallStrength, 1, 10);
        PeakHeightSlider.Value = Math.Clamp(s.PeakHeight, 0.5, 2.0);
        BandHeightSlider.Value = Math.Clamp(s.BandHeight, 160, 320);
        IntensitySlider.Value = Math.Clamp(s.OverallIntensity, 0.1, 1.0);

        // 颜色模式分段按钮
        var modeRadio = s.ColorMode switch
        {
            ColorMode.FixedColor => ModeFixed,
            ColorMode.AmbientAuto => ModeAmbient,
            _ => ModeCover,
        };
        modeRadio.IsChecked = true;

        // 固定色
        if (!string.IsNullOrEmpty(s.FixedColorHex))
        {
            try
            {
                _selectedFixedColor = (Color)ColorConverter.ConvertFromString(s.FixedColorHex);
            }
            catch { /* 非法颜色串时保持默认 */ }
        }

        // 帧率分段按钮
        (s.FrameRate == 30 ? Fps30 : Fps60).IsChecked = true;

        AutoStartToggle.IsChecked = s.AutoStart;

        UpdateSliderLabels();
        UpdateFixedColorPanel();
    }

    // ── 固定色色块 ────────────────────────────────────────────────────
    private void GenerateColorSwatches()
    {
        ColorSwatches.Children.Clear();

        foreach (var color in PresetColors)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Tag = color,
                Child = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = color == _selectedFixedColor
                        ? new Thickness(2)
                        : new Thickness(0),
                    BorderBrush = FindResource("AccentBrush") as Brush,
                    Child = new System.Windows.Shapes.Rectangle
                    {
                        Fill = new SolidColorBrush(color),
                        RadiusX = 5,
                        RadiusY = 5,
                    },
                },
            };

            swatch.MouseDown += OnSwatchMouseDown;
            ColorSwatches.Children.Add(swatch);
        }
    }

    private void OnSwatchMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border { Tag: Color color }) return;

        _selectedFixedColor = color;

        // 更新选中描边
        foreach (var child in ColorSwatches.Children)
        {
            if (child is Border { Child: Border inner } swatch && swatch.Tag is Color c)
            {
                inner.BorderThickness = c == color
                    ? new Thickness(2)
                    : new Thickness(0);
            }
        }

        // 立即保存
        var c0 = _selectedFixedColor;
        SettingsManager.Current.FixedColorHex =
            $"#{c0.A:X2}{c0.R:X2}{c0.G:X2}{c0.B:X2}";
        SettingsManager.Save();
    }

    // ── 事件处理 ──────────────────────────────────────────────────────

    /// <summary>标题栏与卡片空白处拖拽窗口（控件与色块交互不触发）。</summary>
    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Control or System.Windows.Shapes.Shape) return;
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { /* 鼠标已释放时 DragMove 抛出，忽略 */ }
        }
    }

    /// <summary>关闭（隐藏实例由 App 侧管理生命周期）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnEffectToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SettingsManager.Current.EffectEnabled = EffectToggle.IsChecked ?? true;
        SettingsManager.Save();
    }

    private void OnSpectrumBandsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SpectrumBandsValue.Text = ((int)e.NewValue).ToString();
        if (_initializing) return;
        SettingsManager.Current.SpectrumBands = (int)e.NewValue;
        SettingsManager.Save();
    }

    private void OnFallStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        FallStrengthValue.Text = ((int)e.NewValue).ToString();
        if (_initializing) return;
        SettingsManager.Current.FallStrength = Math.Round(e.NewValue, 1);
        SettingsManager.Save();
    }

    private void OnPeakHeightChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PeakHeightValue.Text = e.NewValue.ToString("0.00");
        if (_initializing) return;
        SettingsManager.Current.PeakHeight = Math.Round(e.NewValue, 2);
        SettingsManager.Save();
    }

    private void OnBandHeightChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        BandHeightValue.Text = ((int)e.NewValue).ToString();
        if (_initializing) return;
        SettingsManager.Current.BandHeight = (int)e.NewValue;
        SettingsManager.Save();
    }

    private void OnIntensityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IntensityValue.Text = e.NewValue.ToString("0.00");
        if (_initializing) return;
        SettingsManager.Current.OverallIntensity = Math.Round(e.NewValue, 2);
        SettingsManager.Save();
    }

    private void OnColorModeChecked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag }) return;
        if (Enum.TryParse<ColorMode>(tag, out var mode))
        {
            SettingsManager.Current.ColorMode = mode;
            SettingsManager.Save();
            UpdateFixedColorPanel();
        }
    }

    private void OnFrameRateChecked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag }) return;
        if (int.TryParse(tag, out var fps))
        {
            SettingsManager.Current.FrameRate = fps;
            SettingsManager.Save();
        }
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SettingsManager.Current.AutoStart = AutoStartToggle.IsChecked ?? false;
        SettingsManager.Save();
    }

    // ── 辅助 ──────────────────────────────────────────────────────────
    private void UpdateSliderLabels()
    {
        SpectrumBandsValue.Text = ((int)SpectrumBandsSlider.Value).ToString();
        FallStrengthValue.Text = ((int)FallStrengthSlider.Value).ToString();
        PeakHeightValue.Text = PeakHeightSlider.Value.ToString("0.00");
        BandHeightValue.Text = ((int)BandHeightSlider.Value).ToString();
        IntensityValue.Text = IntensitySlider.Value.ToString("0.00");
    }

    private void UpdateFixedColorPanel()
    {
        var isFixed = SettingsManager.Current.ColorMode == ColorMode.FixedColor;
        ColorSwatches.Visibility = isFixed ? Visibility.Visible : Visibility.Collapsed;
    }
}
