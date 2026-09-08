using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RivuletLight.UI;

/// <summary>
/// WinUI/Fluent 风格托盘右键菜单：亚克力毛玻璃、圆角、MDL2 图标菜单项。
/// 失焦自动隐藏，由 <see cref="TrayIcon"/> 负责定位与显示。
/// </summary>
public partial class TrayMenuWindow : Window
{
    /// <summary>点击暂停/恢复（TrayIcon 内部切换状态后再回调）。</summary>
    public Action? OnPauseResume { get; set; }

    /// <summary>点击设置。</summary>
    public Action? OnOpenSettings { get; set; }

    /// <summary>点击重启（测试用：重启进程）。</summary>
    public Action? OnRestart { get; set; }

    /// <summary>点击退出。</summary>
    public Action? OnExit { get; set; }

    /// <summary>是否处于暂停状态（切换菜单项的图标与文本）。</summary>
    public bool IsPaused
    {
        set
        {
            PauseText.Text = value ? "恢复" : "暂停";
            PauseGlyph.Text = value ? "\uE768" : "\uE769"; // E768=Play E769=Pause
        }
    }

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Acrylic.Enable(this, Color.FromArgb(0xD8, 0x24, 0x24, 0x24));
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OnPauseResume?.Invoke();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OnOpenSettings?.Invoke();
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OnRestart?.Invoke();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OnExit?.Invoke();
    }

    /// <summary>点击菜单外任意处（窗口失活）自动隐藏。</summary>
    private void OnDeactivated(object sender, EventArgs e) => Hide();

    /// <summary>Esc 关闭菜单。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }
}
