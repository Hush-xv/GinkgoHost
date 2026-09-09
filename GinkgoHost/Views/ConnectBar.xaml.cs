using System.Windows;
using System.Windows.Controls;
using GinkgoHost.Models;
using GinkgoHost.Native;

namespace GinkgoHost.Views;

/// <summary>适配器连接条，DevicePage 与 I2cPage 共用，操作 App.Bus 单例。</summary>
public partial class ConnectBar : UserControl
{
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
            if (!App.Bus.IsOpen)
                TxtState.Text = "Connect to a Host Adapter";
            else
            {
                string bus = App.Bus.ControlMode == GinkgoDriver.VII_SCTL_MODE
                    ? "软件 I2C"
                    : $"{App.Bus.ClockHz / 1000} kHz";
                TxtState.Text = $"Connected to Ginkgo USB-I2C · 通道 {App.Bus.Channel} · {bus}";
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
