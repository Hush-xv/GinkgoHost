using System.IO;
using System.Windows.Controls;
using GinkgoHost.Native;

namespace GinkgoHost.Views;

public partial class DevicePage : UserControl
{
    public DevicePage()
    {
        InitializeComponent();
        // 页面被导航切换时 Unloaded 会解挂订阅，必须在每次 Loaded 重新挂上
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
            TxtCount.Text = App.Bus.AdapterCount > 0 ? $"{App.Bus.AdapterCount} 个" : "未检测到";
            TxtDevState.Text = App.Bus.IsOpen
                ? $"已连接 · 通道 {App.Bus.Channel} · {(App.Bus.ControlMode == 2 ? "软件 I2C" : $"{App.Bus.ClockHz / 1000} kHz")}"
                : "未连接";
            try
            {
                string dll = Path.Combine(AppContext.BaseDirectory, "Ginkgo_Driver.dll");
                TxtDriver.Text = File.Exists(dll)
                    ? $"Ginkgo_Driver.dll v{System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion} (x64)"
                    : "未找到";
            }
            catch { TxtDriver.Text = "—"; }
        });
    }
}
