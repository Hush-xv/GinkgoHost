using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GinkgoHost.Models;
using GinkgoHost.Native;
using Wpf.Ui.Controls;

namespace GinkgoHost.Views;

public partial class MainWindow : FluentWindow
{
    private readonly DevicePage _devicePage = new();
    private readonly I2cPage _i2cPage = new();
    private readonly ConsolePage _consolePage = new();
    private readonly SettingsPage _settingsPage = new();

    private static readonly Brush DotOn = new SolidColorBrush(Color.FromRgb(0x4c, 0xaf, 0x50));
    private static readonly Brush DotOff = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));
    private static readonly Brush DotBusy = new SolidColorBrush(Color.FromRgb(0xff, 0xa7, 0x26));
    // 204px 导航栏展开后，页面仍保留约 950px 的可用宽度。
    private const double NavAutoCollapseWidth = 1180;
    // 收起后需明显变宽才重新展开，避免拖动窗口停在阈值附近时来回跳动。
    private const double NavAutoExpandWidth = 1220;
    private bool _windowReady;
    private bool _navOpen = true;
    private bool _navAutoCollapsed;
    private bool _navManualOverrideAtNarrowWidth;
    private bool _connectionBusy;
    private ConnectionAction _connectionAction = ConnectionAction.Connect;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        // 位置必须在 HWND 创建后（SourceInitialized）设置：构造期设 Left/Top+Maximized
        // 会被 Win32 初始位置覆盖，最大化会落到默认屏幕而不是目标屏
        SourceInitialized += (_, _) => RestoreWindowPlacement();
        _devicePage.ConnectRequested += DevicePage_ConnectRequested;
        _devicePage.WorkspaceRequested += DevicePage_WorkspaceRequested;
        // 控制台不持有第二套总线生命周期：命令走同一条漏斗，否则窗口连接期间
        // _connectionBusy 闸门管不住命令行，用户可以并发发起第二次开合。
        _consolePage.ConnectRequested = () => ConnectAsync("console");
        _consolePage.DisconnectRequested = DisconnectAsync;
        _navOpen = App.Settings.NavOpen;
        ApplyNavState();
        Loaded += async (_, _) =>
        {
            UpdateNavForWindowWidth(ActualWidth);
            // 启动即连：工作台工具的常用姿势；无设备时静默失败（只留日志，不打扰）
            if (App.Settings.AutoConnect && !App.Bus.IsOpen)
                await ConnectAsync("auto-start");
        };
        Nav.SelectedIndex = Math.Clamp(App.Settings.LastPage, 0, 3);
        _windowReady = true;
        App.Bus.StateChanged += RefreshStatus;
        Closing += MainWindow_Closing;
        RefreshStatus();
        ShowPage();
        Dbg.Log($"MainWindow: restored page={Nav.SelectedIndex} navOpen={_navOpen}");
        Closed += (_, _) =>
        {
            App.Bus.StateChanged -= RefreshStatus;
            _devicePage.ConnectRequested -= DevicePage_ConnectRequested;
            _devicePage.WorkspaceRequested -= DevicePage_WorkspaceRequested;
        };
    }

    /// <summary>
    /// 恢复上次关闭时的窗口位置（物理像素，混合 DPI 下往返无损）。
    /// 首次启动或位置不在当前虚拟桌面内（如拔掉扩展屏）时回退系统居中。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var s = App.Settings;
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        if (double.IsNaN(s.WindowLeft) || s.WindowWidth <= 0 || helper.Handle == IntPtr.Zero)
            return; // 首次启动：XAML CenterScreen 已生效
        // 标题栏须有 140x80 物理像素留在虚拟桌面内，防止拔屏后窗口不可见
        bool visible = s.WindowLeft + 140 > Win32Interop.VirtualScreenLeft
                    && s.WindowLeft < Win32Interop.VirtualScreenRight - 140
                    && s.WindowTop + 80 > Win32Interop.VirtualScreenTop
                    && s.WindowTop < Win32Interop.VirtualScreenBottom - 80;
        if (!visible)
        {
            Dbg.Log($"MainWindow.RestoreWindowPlacement: saved pos ({s.WindowLeft:F0},{s.WindowTop:F0}) outside virtual screen, fallback center");
            Win32Interop.SetWindowPos(helper.Handle, IntPtr.Zero,
                (Win32Interop.VirtualScreenLeft + Win32Interop.VirtualScreenRight) / 2 - (int)s.WindowWidth / 2,
                (Win32Interop.VirtualScreenTop + Win32Interop.VirtualScreenBottom) / 2 - (int)s.WindowHeight / 2,
                0, 0, Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER);
            return;
        }
        Win32Interop.SetWindowPos(helper.Handle, IntPtr.Zero,
            (int)s.WindowLeft, (int)s.WindowTop, (int)s.WindowWidth, (int)s.WindowHeight, Win32Interop.SWP_NOZORDER);
        if (s.WindowMaximized)
            Win32Interop.ShowWindow(helper.Handle, Win32Interop.SW_MAXIMIZE);
        Dbg.Log($"MainWindow.RestoreWindowPlacement: ({s.WindowLeft:F0},{s.WindowTop:F0}) {s.WindowWidth:F0}x{s.WindowHeight:F0} max={s.WindowMaximized}");
    }

    private void SaveWindowPlacement()
    {
        var s = App.Settings;
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero) return;
        var p = new Win32Interop.WINDOWPLACEMENT();
        p.length = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.WINDOWPLACEMENT>();
        if (!Win32Interop.GetWindowPlacement(helper.Handle, ref p)) return;
        // rcNormalPosition 为还原态矩形（物理像素），最大化时不丢还原位置
        s.WindowLeft = p.rcNormalLeft;
        s.WindowTop = p.rcNormalTop;
        s.WindowWidth = p.rcNormalRight - p.rcNormalLeft;
        s.WindowHeight = p.rcNormalBottom - p.rcNormalTop;
        s.WindowMaximized = p.showCmd == Win32Interop.SW_SHOWMAXIMIZED;
    }

    /// <summary>窗口定位用的 Win32 物理像素接口：虚拟桌面指标与还原位置。</summary>
    internal static class Win32Interop
    {
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int ht, uint f);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public int ptMinX, ptMinY, ptMaxX, ptMaxY;
            public int rcNormalLeft, rcNormalTop, rcNormalRight, rcNormalBottom;
        }

        // 同值但不同语义：前者是 ShowWindow 的命令参数，后者是 WINDOWPLACEMENT.showCmd 的回报值
        public const int SW_SHOWMAXIMIZED = 3;
        public const int SW_MAXIMIZE = 3;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOSIZE = 0x0008;

        // SM_XVIRTUALSCREEN=76 / SM_YVIRTUALSCREEN=77 / SM_CXVIRTUALSCREEN=78 / SM_CYVIRTUALSCREEN=79
        public static int VirtualScreenLeft => GetSystemMetrics(76);
        public static int VirtualScreenTop => GetSystemMetrics(77);
        public static int VirtualScreenRight => VirtualScreenLeft + GetSystemMetrics(78);
        public static int VirtualScreenBottom => VirtualScreenTop + GetSystemMetrics(79);
    }

    /// <summary>正常关闭时串行释放驱动会话，避免总线操作与 CloseDevice 并发。</summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        SaveWindowPlacement();
        // 连接扫描尚未完成时 IsOpen 仍为 false；同样等待服务锁释放，避免退出时 DLL 调用悬空。
        bool operationPending = _connectionBusy;
        if (!App.Bus.IsOpen && !operationPending) return;

        e.Cancel = true;
        SetConnectionBusy(true, ConnectionAction.Close);
        TxtWorkspaceState.Text = "正在关闭";
        TxtHeaderState.Text = "正在关闭…";
        TxtWorkspaceDetail.Text = "正在结束 I²C 会话…";
        Dbg.Log($"MainWindow.Closing: waiting for I2C session close; connected={App.Bus.IsOpen} busy={operationPending}");
        try
        {
            _i2cPage.CancelPendingOperations("窗口关闭"); // 先中止扫描、轮询和初始化，CloseAsync 只需等待当前原生事务结束
            int ret = await App.Bus.CloseAsync();
            Dbg.Log($"MainWindow.Closing: CloseAsync ret={ret}");
        }
        catch (Exception ex)
        {
            Dbg.Log($"MainWindow.Closing: CloseAsync failed={ex.Message}");
        }
        finally
        {
            SetConnectionBusy(false);
            Close();
        }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowReady) return;
        App.Settings.LastPage = Math.Clamp(Nav.SelectedIndex, 0, 3);
        App.Settings.Save();
        Dbg.Log($"MainWindow.Nav_SelectionChanged: page={App.Settings.LastPage}");
        ShowPage();
    }

    private void BtnNavToggle_Click(object sender, RoutedEventArgs e)
    {
        // 窄窗口自动收起后，菜单按钮可临时展开导航，但不改写用户的持久化偏好。
        if (_navAutoCollapsed)
        {
            _navAutoCollapsed = false;
            _navManualOverrideAtNarrowWidth = true;
            ApplyNavState();
#if DEBUG
            Dbg.Log("MainWindow.BtnNavToggle_Click: manual override opened navigation at narrow width");
#endif
            return;
        }

        _navOpen = !_navOpen;
        _navManualOverrideAtNarrowWidth = _navOpen && ActualWidth < NavAutoCollapseWidth;
        ApplyNavState();
        App.Settings.NavOpen = _navOpen;
        App.Settings.Save();
        Dbg.Log($"MainWindow.BtnNavToggle_Click: navOpen={_navOpen}");
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateNavForWindowWidth(e.NewSize.Width);

    /// <summary>页面级快捷键只在窗口预览阶段处理，避免各页重复注册且不影响文本输入的常用 Ctrl+字母组合。</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        int page = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1
        };
        if (page < 0) return;

        bool pageChanged = Nav.SelectedIndex != page;
        Nav.SelectedIndex = page;
        if (!pageChanged) ShowPage(); // 同页快捷键仍刷新焦点，不重复触发不同页的生命周期。
        FocusKeyboardSelectedPage(page);
        e.Handled = true;
#if DEBUG
        Dbg.Log($"MainWindow.MainWindow_PreviewKeyDown: switched page={page} via Ctrl+{page + 1}");
#endif
    }

    private void FocusKeyboardSelectedPage(int page)
    {
        switch (page)
        {
            case 0: _devicePage.FocusPrimaryAction(); break;
            case 1: _i2cPage.FocusTransactionTarget(); break;
            case 2: _consolePage.FocusCommandInput(); break;
            case 3: _settingsPage.FocusThemeSelector(); break;
        }
    }

    private void UpdateNavForWindowWidth(double width)
    {
        if (width <= 0) return;

        bool narrow = _navAutoCollapsed
            ? width < NavAutoExpandWidth
            : width < NavAutoCollapseWidth;
        if (!narrow)
            _navManualOverrideAtNarrowWidth = false;

        bool autoCollapsed = narrow && _navOpen && !_navManualOverrideAtNarrowWidth;
        if (_navAutoCollapsed == autoCollapsed) return;

        _navAutoCollapsed = autoCollapsed;
        ApplyNavState();
#if DEBUG
        Dbg.Log($"MainWindow.UpdateNavForWindowWidth: width={width:F0} autoCollapsed={_navAutoCollapsed} userNavOpen={_navOpen}");
#endif
    }

    private void ApplyNavState()
    {
        bool visible = _navOpen && !_navAutoCollapsed;
        NavCol.Width = visible ? new GridLength(204) : new GridLength(0);
        Nav.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPage()
    {
        // XAML 加载期 ListBoxItem.IsSelected 就会触发事件，此时 PageHost 尚未创建
        if (PageHost is null) return;
        PageHost.Content = Nav.SelectedIndex switch
        {
            0 => _devicePage,
            2 => _consolePage,
            3 => _settingsPage,
            _ => _i2cPage // 默认落在 I2C
        };
    }

    private void RefreshStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(RefreshStatus);
            return;
        }
        if (_closing) return;

        DotWorkspaceState.Fill = App.Bus.IsOpen ? DotOn : DotOff;
        DotHeaderState.Fill = App.Bus.IsOpen ? DotOn : DotOff;
        string busTxt = App.Bus.ControlMode == GinkgoDriver.VII_SCTL_MODE
            ? "软件 I2C"
            : $"{App.Bus.ClockHz / 1000} kHz";
        TxtWorkspaceState.Text = App.Bus.IsOpen ? "已连接" : "未连接";
        TxtHeaderState.Text = App.Bus.IsOpen ? $"已连接 · {busTxt}" : "未连接";
        // 紧凑设备标识：短名 · 通道 · 速率。多 Adapter 时短名来自 Adapter 能力描述，避免长名裁切
        TxtWorkspaceDetail.Text = App.Bus.IsOpen
            ? $"Ginkgo · CH{App.Bus.Channel} · {busTxt}"
            : App.Bus.AdapterCount > 0 ? $"检测到 {App.Bus.AdapterCount} 个适配器" : "未检测到适配器";
        if (!_connectionBusy)
        {
            BtnWorkspaceConnect.Content = "连接";
            BtnWorkspaceConnect.IsEnabled = !App.Bus.IsOpen;
            BtnWorkspaceDisconnect.IsEnabled = App.Bus.IsOpen;
        }
    }

    private void SetConnectionBusy(bool busy, ConnectionAction action = ConnectionAction.Connect)
    {
        _connectionBusy = busy;
        if (busy) _connectionAction = action;
        _devicePage.SetConnectionBusy(busy, _connectionAction);
        _consolePage.SetConnectionBusy(busy);
        bool disconnecting = busy && _connectionAction == ConnectionAction.Disconnect;
        bool closing = busy && _connectionAction == ConnectionAction.Close;
        BtnWorkspaceConnect.Content = busy && !disconnecting && !closing ? "连接中…" : "连接";
        BtnWorkspaceDisconnect.Content = disconnecting ? "断开中…" : "断开";
        BtnWorkspaceConnect.IsEnabled = !busy && !App.Bus.IsOpen;
        BtnWorkspaceDisconnect.IsEnabled = !busy && App.Bus.IsOpen;
        if (busy)
        {
            DotWorkspaceState.Fill = DotBusy;
            DotHeaderState.Fill = DotBusy;
            TxtWorkspaceState.Text = closing ? "正在关闭" : disconnecting ? "正在断开" : "连接中";
            TxtHeaderState.Text = closing ? "正在关闭…" : disconnecting ? "正在断开…" : "连接中…";
            TxtWorkspaceDetail.Text = closing ? "正在结束 I²C 会话…"
                : disconnecting ? "正在安全释放适配器与总线…"
                : "正在扫描适配器并初始化 I²C…";
        }
        else
        {
            RefreshStatus();
        }
#if DEBUG
        Dbg.Log($"MainWindow.SetConnectionBusy: busy={busy} action={_connectionAction}");
#endif
    }

    private async void DevicePage_ConnectRequested(object? sender, EventArgs e) => await ConnectAsync("device-page");

    private void DevicePage_WorkspaceRequested(object? sender, EventArgs e)
    {
        Nav.SelectedIndex = 1;
        _i2cPage.FocusTransactionTarget();
#if DEBUG
        Dbg.Log("MainWindow.DevicePage_WorkspaceRequested: opened I2C workspace");
#endif
    }

    private async void BtnWorkspaceConnect_Click(object sender, RoutedEventArgs e) => await ConnectAsync("workspace");

    /// <summary>
    /// 连接链路的唯一实现：状态卡、设备页、控制台与系统日志都从这里产出，避免出现互不同步的第二套状态。
    /// 返回值供命令行回显；早退分支（已连接或另一处正在连接）只有 auto-start 与页面按钮会命中，它们不看返回值。
    /// </summary>
    private async Task<(int Count, int Ret)> ConnectAsync(string source)
    {
        if (_connectionBusy || App.Bus.IsOpen) return (App.Bus.AdapterCount, 0);
        Dbg.Log($"MainWindow.ConnectAsync: source={source} ch={App.Settings.Channel} clk={App.Settings.ClockHz} mode={App.Settings.ControlMode}");
        try
        {
            SetConnectionBusy(true, ConnectionAction.Connect);
            var (count, ret) = await App.Bus.ConnectAsync(
                App.Settings.Channel, App.Settings.ClockHz, (byte)App.Settings.ControlMode);
            Dbg.Log($"MainWindow.ConnectAsync: source={source} count={count} ret={ret}");
            _devicePage.SetConnectionOutcome(count, ret);
            int logRet = count <= 0 ? (count == 0 ? -15 : count) : ret;
            // SYS 事件不占用事务字段：地址列恒 —，设备/通道/速率详情放数据列（DataDisplay 按 UTF-8 显示），
            // 连接时记录一次硬件上下文，导出日志脱离状态卡也能知道事务属于哪个 Adapter
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", logRet, 0,
                logRet == 0
                    ? System.Text.Encoding.UTF8.GetBytes($"Ginkgo · CH{App.Settings.Channel} · {App.Settings.ClockHz / 1000} kHz")
                    : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(logRet))));
            return (count, ret);
        }
        catch (Exception ex)
        {
            Dbg.Log($"MainWindow.ConnectAsync: source={source} failed={ex.Message}");
            _devicePage.SetConnectionOutcome(-1, -1, ex.Message);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            RefreshStatus();
            return (-1, -1); // 负适配器数即「链路异常」，与控制台回显区分开未检测到设备
        }
        finally { SetConnectionBusy(false); }
    }

    private async void BtnWorkspaceDisconnect_Click(object sender, RoutedEventArgs e) => await DisconnectAsync();

    /// <summary>断开链路的唯一实现，工作台按钮与控制台 disconnect 共用。返回驱动错误码，0=成功。</summary>
    private async Task<int> DisconnectAsync()
    {
        Dbg.Log("MainWindow.DisconnectAsync: start");
        try
        {
            SetConnectionBusy(true, ConnectionAction.Disconnect);
            _i2cPage.CancelPendingOperations("断开设备");
            int ret = await App.Bus.CloseAsync();
            Dbg.Log($"MainWindow.DisconnectAsync: ret={ret}");
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "断开适配器", "—", ret, 0,
                ret == 0 ? null : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
            return ret;
        }
        catch (Exception ex)
        {
            Dbg.Log($"MainWindow.DisconnectAsync: failed={ex.Message}");
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "断开适配器", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            RefreshStatus();
            return -1;
        }
        finally { SetConnectionBusy(false); }
    }

}
