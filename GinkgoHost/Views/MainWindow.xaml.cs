using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private bool _windowReady;
    private bool _navOpen = true;
    private bool _connectionBusy;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        // 位置必须在 HWND 创建后（SourceInitialized）设置：构造期设 Left/Top+Maximized
        // 会被 Win32 初始位置覆盖，最大化会落到默认屏幕而不是目标屏
        SourceInitialized += (_, _) => RestoreWindowPlacement();
        _navOpen = App.Settings.NavOpen;
        ApplyNavState();
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

    /// <summary>窗口定位用的 Win32 物理像素接口：虚拟桌面指标、窗口矩形与还原位置。</summary>
    internal static class Win32Interop
    {
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int ht, uint f);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public int ptMinX, ptMinY, ptMaxX, ptMaxY;
            public int rcNormalLeft, rcNormalTop, rcNormalRight, rcNormalBottom;
        }

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
        if (!App.Bus.IsOpen) return;

        e.Cancel = true;
        SetConnectionBusy(true);
        Dbg.Log("MainWindow.Closing: waiting for I2C session close");
        try
        {
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
        _navOpen = !_navOpen;
        ApplyNavState();
        App.Settings.NavOpen = _navOpen;
        App.Settings.Save();
        Dbg.Log($"MainWindow.BtnNavToggle_Click: navOpen={_navOpen}");
    }

    private void ApplyNavState()
    {
        NavCol.Width = _navOpen ? new GridLength(204) : new GridLength(0);
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
            3 => _settingsPage,
            _ => _i2cPage // 默认落在 I2C
        };
    }

    private void RefreshStatus()
    {
        Dispatcher.Invoke(() =>
        {
            DotWorkspaceState.Fill = App.Bus.IsOpen ? DotOn : DotOff;
            string busTxt = App.Bus.ControlMode == GinkgoDriver.VII_SCTL_MODE
                ? "软件 I2C"
                : $"{App.Bus.ClockHz / 1000} kHz";
            TxtWorkspaceState.Text = App.Bus.IsOpen ? "已连接" : "未连接";
            TxtWorkspaceDetail.Text = App.Bus.IsOpen
                ? $"CH{App.Bus.Channel} · {busTxt}"   // 设备名已由窗口标题表达，状态卡只留通道与速率
                : App.Bus.AdapterCount > 0 ? $"检测到 {App.Bus.AdapterCount} 个适配器" : "未检测到适配器";
            if (!_connectionBusy)
            {
                BtnWorkspaceConnect.Content = "连接";
                BtnWorkspaceConnect.IsEnabled = !App.Bus.IsOpen;
                BtnWorkspaceDisconnect.IsEnabled = App.Bus.IsOpen;
            }
        });
    }

    private void SetConnectionBusy(bool busy)
    {
        _connectionBusy = busy;
        BtnWorkspaceConnect.Content = busy ? "连接中…" : "连接";
        BtnWorkspaceConnect.IsEnabled = !busy && !App.Bus.IsOpen;
        BtnWorkspaceDisconnect.IsEnabled = !busy && App.Bus.IsOpen;
    }

    private async void BtnWorkspaceConnect_Click(object sender, RoutedEventArgs e)
    {
        Dbg.Log($"MainWindow.BtnWorkspaceConnect_Click: ch={App.Settings.Channel} clk={App.Settings.ClockHz} mode={App.Settings.ControlMode}");
        try
        {
            SetConnectionBusy(true);
            var (count, ret) = await App.Bus.ConnectAsync(
                App.Settings.Channel, App.Settings.ClockHz, (byte)App.Settings.ControlMode);
            Dbg.Log($"MainWindow.BtnWorkspaceConnect_Click: count={count} ret={ret}");
            int logRet = count <= 0 ? (count == 0 ? -15 : count) : ret;
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器",
                $"通道{App.Settings.Channel}", logRet, 0,
                logRet == 0 ? null : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(logRet))));
        }
        catch (Exception ex)
        {
            Dbg.Log($"MainWindow.BtnWorkspaceConnect_Click: failed={ex.Message}");
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            RefreshStatus();
        }
        finally { SetConnectionBusy(false); }
    }

    private async void BtnWorkspaceDisconnect_Click(object sender, RoutedEventArgs e)
    {
        Dbg.Log("MainWindow.BtnWorkspaceDisconnect_Click: start");
        try
        {
            SetConnectionBusy(true);
            int ret = await App.Bus.CloseAsync();
            Dbg.Log($"MainWindow.BtnWorkspaceDisconnect_Click: ret={ret}");
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "断开适配器", "—", ret, 0,
                ret == 0 ? null : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
        }
        catch (Exception ex)
        {
            Dbg.Log($"MainWindow.BtnWorkspaceDisconnect_Click: failed={ex.Message}");
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "断开适配器", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            RefreshStatus();
        }
        finally { SetConnectionBusy(false); }
    }

}
