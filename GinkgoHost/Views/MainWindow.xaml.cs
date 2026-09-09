using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GinkgoHost.Models;
using Wpf.Ui.Controls;

namespace GinkgoHost.Views;

public partial class MainWindow : FluentWindow
{
    private readonly DevicePage _devicePage = new();
    private readonly I2cPage _i2cPage = new();
    private readonly ConsolePage _consolePage = new();
    private readonly ExtendPage _extendPage = new();
    private readonly SettingsPage _settingsPage = new();

    private static readonly Brush DotOn = new SolidColorBrush(Color.FromRgb(0x4c, 0xaf, 0x50));
    private static readonly Brush DotOff = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));

    public MainWindow()
    {
        InitializeComponent();
        App.Bus.StateChanged += RefreshStatus;
        App.Log.CollectionChanged += OnLogChanged;
        RefreshStatus();
        ShowPage();
        Closed += (_, _) => App.Bus.StateChanged -= RefreshStatus;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPage();

    private bool _navOpen = true;

    private void BtnNavToggle_Click(object sender, RoutedEventArgs e)
    {
        _navOpen = !_navOpen;
        NavCol.Width = _navOpen ? new GridLength(216) : new GridLength(0);
        Nav.Visibility = _navOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPage()
    {
        // XAML 加载期 ListBoxItem.IsSelected 就会触发事件，此时 PageHost 尚未创建
        if (PageHost is null) return;
        PageHost.Content = Nav.SelectedIndex switch
        {
            0 => _devicePage,
            2 => _consolePage,
            3 => _extendPage,
            4 => _settingsPage,
            _ => _i2cPage // 默认落在 I2C
        };
    }

    private void RefreshStatus()
    {
        Dispatcher.Invoke(() =>
        {
            DotState.Fill = App.Bus.IsOpen ? DotOn : DotOff;
            string busTxt = App.Bus.ControlMode == GinkgoHost.Native.GinkgoDriver.VII_SCTL_MODE
                ? "软件 I2C"
                : $"{App.Bus.ClockHz / 1000} kHz";
            TxtState.Text = App.Bus.IsOpen
                ? $"已连接 · 通道 {App.Bus.Channel} · {busTxt}"
                : "Connect to a Host Adapter";
            TxtAdapterInfo.Text = App.Bus.AdapterCount > 0
                ? $"适配器 ×{App.Bus.AdapterCount}"
                : "未检测到适配器";
        });
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not { Count: > 0 }) return;
        var last = (LogEntry)e.NewItems[^1]!;
        Dispatcher.Invoke(() =>
            TxtLastTx.Text = last.Ret == 0
                ? $"最近事务：{last.Op} {last.Addr} · {last.Ms:F1} ms"
                : $"最近事务：{last.Op} {last.Addr} · 失败 ({last.Ret})");
    }
}
