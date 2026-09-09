using System.Collections.ObjectModel;
using System.Windows;
using GinkgoHost.Models;
using GinkgoHost.Services;
using Wpf.Ui.Appearance;

namespace GinkgoHost;

public partial class App : Application
{
    // 页面级静态定位：四个页面共享同一条总线和同一份日志，无需引入 DI 框架
    public static I2cService Bus { get; } = new();
    public static ObservableCollection<LogEntry> Log { get; } = new();
    public static SettingsService Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = SettingsService.Load();
        ApplicationThemeManager.Apply(
            Settings.Theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Bus.Dispose();
        Settings.Save();
        base.OnExit(e);
    }
}
