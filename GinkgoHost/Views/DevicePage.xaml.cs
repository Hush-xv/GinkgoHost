using System.IO;
using System.Windows;
using System.Windows.Controls;
using GinkgoHost.Native;

namespace GinkgoHost.Views;

public partial class DevicePage : UserControl
{
    private bool? _lastConnected;
    private bool _connectionBusy;
    private ConnectionAction _connectionAction = ConnectionAction.Connect;
    private string? _connectionOutcome;

    private static string? _cachedDriverVersion;
    private static string DriverVersionText => _cachedDriverVersion ??= LoadDriverVersion();

    private static string LoadDriverVersion()
    {
        try
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "Ginkgo_Driver.dll");
            return File.Exists(dll)
                ? $"Ginkgo_Driver.dll v{System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion} (x64)"
                : "未找到";
        }
        catch { return "—"; }
    }

    /// <summary>由窗口承接连接，设备页不持有第二套总线生命周期。</summary>
    public event EventHandler? ConnectRequested;
    public event EventHandler? WorkspaceRequested;

    public DevicePage()
    {
        InitializeComponent();
        // 页面被导航切换时 Unloaded 会解挂订阅，必须在每次 Loaded 重新挂上
        Loaded += (_, _) =>
        {
            App.Bus.StateChanged += Refresh;
            Refresh();
            FocusPrimaryAction();
        };
        Unloaded += (_, _) => App.Bus.StateChanged -= Refresh;
    }

    public void SetConnectionBusy(bool busy, ConnectionAction action = ConnectionAction.Connect)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(() => SetConnectionBusy(busy, action));
            return;
        }
        _connectionBusy = busy;
        if (busy) _connectionAction = action;
        if (busy) _connectionOutcome = null;
        Refresh();
#if DEBUG
        Dbg.Log($"DevicePage.SetConnectionBusy: busy={busy} action={_connectionAction}");
#endif
    }

    public void SetConnectionOutcome(int adapterCount, int ret, string? detail = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(() => SetConnectionOutcome(adapterCount, ret, detail));
            return;
        }
        _connectionOutcome = adapterCount == 0
            ? "未检测到适配器。请检查 USB 连接后重试。"
            : adapterCount < 0
                ? $"连接失败：{detail ?? GinkgoDriver.ErrorName(ret)}。请检查驱动与设备占用情况。"
                : ret == 0
                ? null
                : $"发现 {adapterCount} 个适配器，但初始化失败：{detail ?? GinkgoDriver.ErrorName(ret)}。请检查通道和设备占用情况。";
        Refresh();
#if DEBUG
        Dbg.Log($"DevicePage.SetConnectionOutcome: adapters={adapterCount} ret={ret} outcome={_connectionOutcome ?? "success"}");
#endif
    }

    private void BtnDeviceConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy) return;
        if (App.Bus.IsOpen)
        {
            WorkspaceRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        ConnectRequested?.Invoke(this, EventArgs.Empty);
#if DEBUG
        Dbg.Log("DevicePage.BtnDeviceConnect_Click: connection requested from device page");
#endif
    }

    private void BtnCopyDeviceInfo_Click(object sender, RoutedEventArgs e)
    {
        if (App.Bus.AdapterCount <= 0) return;

        string connection = App.Bus.IsOpen
            ? $"已连接 · CH{App.Bus.Channel} · {App.Bus.ClockHz / 1000} kHz"
            : "未连接";
        string report = $"GinkgoHost 设备信息{Environment.NewLine}" +
                        $"适配器: {TxtModel.Text}{Environment.NewLine}" +
                        $"序列号: {TxtSerial.Text}{Environment.NewLine}" +
                        $"固件: {TxtFirmware.Text}{Environment.NewLine}" +
                        $"驱动: {TxtDriver.Text}{Environment.NewLine}" +
                        $"I²C: {connection}";
        try
        {
            Clipboard.SetText(report);
            TxtDeviceInfoFeedback.Text = "已复制设备信息";
#if DEBUG
            Dbg.Log($"DevicePage.BtnCopyDeviceInfo_Click: copied connected={App.Bus.IsOpen} adapters={App.Bus.AdapterCount}");
#endif
        }
        catch (Exception ex)
        {
            TxtDeviceInfoFeedback.Text = "复制失败，请稍后重试";
#if DEBUG
            Dbg.Log($"DevicePage.BtnCopyDeviceInfo_Click: clipboard unavailable error={ex.Message}");
#else
            _ = ex;
#endif
        }
    }

    public void FocusPrimaryAction()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || !BtnDeviceConnect.IsEnabled) return;
            BtnDeviceConnect.Focus();
#if DEBUG
            Dbg.Log("DevicePage.FocusPrimaryAction: primary device action focused");
#endif
        });
    }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(Refresh);
            return;
        }

        if (App.Bus.IsOpen) _connectionOutcome = null;
        TxtCount.Text = App.Bus.AdapterCount > 0 ? $"{App.Bus.AdapterCount} 个" : "未检测到";
        // 通道/速率运行上下文由左下状态卡承担，这里只表达连接状态与协议（P3 去重）
        bool disconnecting = _connectionBusy && _connectionAction is ConnectionAction.Disconnect or ConnectionAction.Close;
        TxtDevState.Text = _connectionBusy
            ? disconnecting ? "正在断开 · I²C" : "正在连接 · I²C"
            : App.Bus.IsOpen ? "已连接 · I²C" : "未连接";
        TxtConnectionHint.Text = _connectionBusy
            ? disconnecting ? "正在安全释放适配器与当前 I²C 会话。" : "正在扫描适配器并初始化当前 I²C 配置。"
            : App.Bus.IsOpen
                ? "适配器已连接，可前往 I²C 页面执行事务。"
                : _connectionOutcome is not null
                    ? _connectionOutcome
                : App.Bus.AdapterCount > 0
                    ? "已发现适配器，连接后即可开始使用。"
                    : "未发现兼容适配器，请检查 USB 连接。";
        BtnDeviceConnect.Content = _connectionBusy
            ? disconnecting ? "断开中…" : "连接中…"
            : App.Bus.IsOpen ? "前往 I²C" : App.Bus.AdapterCount > 0 ? "连接适配器" : "扫描设备";
        BtnDeviceConnect.IsEnabled = !_connectionBusy;
        BtnDeviceConnect.ToolTip = _connectionBusy
            ? disconnecting ? "正在断开，请稍候" : "正在连接，请稍候"
            : App.Bus.IsOpen ? "打开 I²C 调试工作区"
            : App.Bus.AdapterCount > 0 ? "扫描并连接已检测到的适配器" : "重新扫描 USB 适配器";
        TxtAdapterHint.Text = _connectionBusy
            ? disconnecting ? "正在结束当前会话，请等待设备安全释放。" : "保持窗口打开；连接完成后可直接进入 I²C 工作区。"
            : App.Bus.IsOpen
                ? "连接已建立。选择“前往 I²C”开始事务调试。"
                : _connectionOutcome ?? (App.Bus.AdapterCount > 0
                    ? "已发现适配器，可直接连接；无需展开左侧导航。"
                    : "未检测到适配器。连接 USB 后，选择“扫描设备”重新检测。");
        TxtPinoutTitle.Text = App.Bus.AdapterCount > 0 ? "VTG200A 接口与引脚" : "接口与引脚";
        BtnCopyDeviceInfo.IsEnabled = App.Bus.AdapterCount > 0 && !_connectionBusy;
        TxtDeviceInfoFeedback.Text = App.Bus.AdapterCount > 0
            ? "复制当前识别到的适配器与总线上下文。"
            : "连接后自动读取型号、序列号与固件信息。";
#if DEBUG
        if (_lastConnected != App.Bus.IsOpen)
            Dbg.Log($"DevicePage.Refresh: connection UI changed connected={App.Bus.IsOpen} adapters={App.Bus.AdapterCount}");
#endif
        _lastConnected = App.Bus.IsOpen;
        // 未检测到硬件时身份字段一律占位：不预设型号，避免静态文案冒充设备信息
        TxtModel.Text = App.Bus.AdapterCount > 0 ? "Ginkgo VTG200A USB-I2C" : "—";

        // 设备身份（P9）：读取 BoardInfo 序列号/固件；未检测到时保持占位
        // 驱动版本只读一次文件；DLL 路径在进程内不变，Refresh 会被状态事件反复调用
        TxtDriver.Text = DriverVersionText;

        TxtSerial.Text = "—";
        TxtFirmware.Text = "—";
        TxtModel.ToolTip = null; // 断开后清掉上一会话的驱动标识 tooltip
        if (App.Bus.AdapterInfo is { } info)
        {
            TxtSerial.Text = string.IsNullOrWhiteSpace(info.SerialNumber) ? "—" : info.SerialNumber;
            TxtFirmware.Text = info.FirmwareVersion;
            // 驱动内部名只进 tooltip；UI 主字段保持用户友好的产品名。
            TxtModel.ToolTip = info.NativeProductName is { Length: > 0 } native ? $"驱动标识：{native}" : null;
        }
    }
}
