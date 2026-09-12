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
            // 未检测到硬件时身份字段一律占位：不预设型号，避免静态文案冒充设备信息
            TxtModel.Text = App.Bus.AdapterCount > 0 ? "Ginkgo VTG200A USB-I2C" : "—";

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
            TxtModel.ToolTip = null; // 断开后清掉上一会话的驱动标识 tooltip
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
                        // 驱动内部名（Ginkgo_USB_I2C_Adaptor）只进 tooltip；UI 主字段保持用户友好的产品名
                        string native = GinkgoDriver.Ascii(info.ProductName);
                        TxtModel.ToolTip = native.Length > 0 ? $"驱动标识：{native}" : null;
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
