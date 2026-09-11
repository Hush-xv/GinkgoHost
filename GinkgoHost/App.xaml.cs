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
        HookExceptionLogging();
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
        Dbg.Log($"App start v{typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · log {Dbg.LogDir}");
    }

    /// <summary>三类未处理异常全部落日志：UI 异常弹窗后继续运行，致命/任务异常只留痕。</summary>
    void HookExceptionLogging()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Dbg.Log($"ERROR UI 未处理异常: {e.Exception}");
            MessageBox.Show($"发生未处理异常，详情见日志文件夹中的最新 .log：\n{e.Exception.Message}",
                "GinkgoHost", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Dbg.Log($"ERROR 致命异常 (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Dbg.Log($"ERROR 未观察任务异常: {e.Exception}");
            e.SetObserved();
        };
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
