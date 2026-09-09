using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GinkgoHost.Models;
using GinkgoHost.Native;

namespace GinkgoHost.Views;

/// <summary>适配器连接条，DevicePage 与 I2cPage 共用，操作 App.Bus 单例。</summary>
public partial class ConnectBar : UserControl
{
    private static readonly Brush DotOn = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush DotOff = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));
    private static readonly Brush BadgeOnBg = new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xAF, 0x50));

    public ConnectBar()
    {
        InitializeComponent();
        // 与 DevicePage 同因：Unloaded 解挂后需在 Loaded 重挂
        Loaded += (_, _) =>
        {
            App.Bus.StateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => App.Bus.StateChanged -= Refresh;
    }

    private void Refresh()
    {
        Dispatcher.Invoke(() =>
        {
            bool on = App.Bus.IsOpen;
            // 圆点与徽章：色彩 + 形状 + 文字三重区分，不依赖单一颜色
            DotState.Fill = on ? DotOn : DotOff;
            BadgeState.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            TxtBadge.Text = "ONLINE";
            BadgeState.Background = BadgeOnBg;

            BtnConnect.IsEnabled = !on; // 已连接后"连接"禁用
            BtnDisconnect.IsEnabled = on; // 未连接时"断开"禁用

            if (on)
            {
                string bus = App.Bus.ControlMode == GinkgoDriver.VII_SCTL_MODE
                    ? "软件 I2C"
                    : $"{App.Bus.ClockHz / 1000} kHz";
                TxtState.Text = "Connected to Ginkgo USB-I2C";
                TxtDetail.Text = $"通道 {App.Bus.Channel} · {bus}";
            }
            else
            {
                TxtState.Text = "Connect to a Host Adapter";
                TxtDetail.Text = "未连接 —— 点击右侧「连接适配器」";
            }
        });
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (count, ret) = await App.Bus.ConnectAsync(
                App.Settings.Channel, App.Settings.ClockHz, (byte)App.Settings.ControlMode);
            if (count <= 0)
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", ret, 0, null));
            else if (ret != 0)
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", ret, 0,
                    System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
        }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        await App.Bus.CloseAsync();
        App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "断开适配器", "—", 0, 0, null));
    }
}
