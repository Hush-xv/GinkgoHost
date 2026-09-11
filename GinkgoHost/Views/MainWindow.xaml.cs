using System.ComponentModel;
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

    /// <summary>正常关闭时串行释放驱动会话，避免总线操作与 CloseDevice 并发。</summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        _closing = true;
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
                ? $"通道 {App.Bus.Channel} · {busTxt}"
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
