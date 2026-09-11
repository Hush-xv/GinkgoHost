using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using GinkgoHost.Models;
using GinkgoHost.Native;
using GinkgoHost.Services;
using Wpf.Ui.Appearance;

namespace GinkgoHost;

public partial class App : Application
{
    // 页面级静态定位：四个页面共享同一条总线和同一份日志，无需引入 DI 框架
    public static I2cService Bus { get; } = new();
    public static ObservableCollection<LogEntry> Log { get; } = new();
    public static SettingsService Settings { get; private set; } = null!;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, "Local\\GinkgoHost_USB_I2C", out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            MessageBox.Show("GinkgoHost 已在运行。请切换到现有窗口。", "GinkgoHost",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        Settings = SettingsService.Load();
        ApplicationThemeManager.Apply(
            Settings.Theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dbg.Log("App.OnExit: release I2C session");
        Bus.Dispose();
        if (Settings is not null) Settings.Save();
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
