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
            // 通道/速率运行上下文由左下状态卡承担，这里只表达连接状态与协议（P3 去重）
            TxtDevState.Text = App.Bus.IsOpen ? "已连接 · I²C" : "未连接";
            // 未发现适配器时显示行动指引；发现后隐藏
            TxtAdapterHint.Visibility = App.Bus.AdapterCount > 0
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
            TxtPinoutTitle.Text = App.Bus.AdapterCount > 0 ? "VTG200A 接口与引脚" : "接口与引脚";
            TxtModel.Text = App.Bus.AdapterCount > 0
                ? "Ginkgo VTG200A USB-I2C"
                : "Ginkgo VTG200A USB-I2C（型号待确认）";

            // 设备身份（P9）：读取 BoardInfo 序列号/固件；未检测到时保持占位
            try
            {
                string dll = Path.Combine(AppContext.BaseDirectory, "Ginkgo_Driver.dll");
                TxtDriver.Text = File.Exists(dll)
                    ? $"Ginkgo_Driver.dll v{System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion} (x64)"
                    : "未找到";
            }
            catch { TxtDriver.Text = "—"; }

            TxtSerial.Text = "—";
            TxtFirmware.Text = "—";
            if (App.Bus.AdapterCount > 0)
            {
                try
                {
                    var info = new GinkgoDriver.VII_BOARD_INFO
                    {
                        ProductName = new byte[32],
                        FirmwareVersion = new byte[4],
                        HardwareVersion = new byte[4],
                        SerialNumber = new byte[12]
                    };
                    int ret = GinkgoDriver.ReadBoardInfo(0, ref info);
                    Dbg.Log($"DevicePage.Refresh: ReadBoardInfo ret={ret} sn={GinkgoDriver.Ascii(info.SerialNumber)}");
                    if (ret == 0)
                    {
                        string sn = GinkgoDriver.Ascii(info.SerialNumber);
                        TxtSerial.Text = sn.Length > 0 ? sn : GinkgoDriver.Hex(info.SerialNumber);
                        TxtFirmware.Text = $"v{info.FirmwareVersion[0]}.{info.FirmwareVersion[1]}";
                        string product = GinkgoDriver.Ascii(info.ProductName);
                        if (product.Length > 0) TxtModel.Text = product;
                    }
                }
                catch (Exception ex)
                {
                    Dbg.Log($"DevicePage.Refresh: ReadBoardInfo failed={ex.Message}");
                }
            }
        });
    }
}
