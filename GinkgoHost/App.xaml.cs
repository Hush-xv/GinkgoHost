using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Media;
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
        bool light = Settings.Theme == "Light";
        ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark);
        ApplyThemePalette(light);
        base.OnStartup(e);
        Dbg.Log($"App start v{typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · log {Dbg.LogDir}");
    }

    /// <summary>
    /// 主题化状态/语义色：暗色用浅色文本、亮色用深色文本。
    /// 替换 Application.Resources 中的画刷实例后，所有 DynamicResource 引用即时刷新，
    /// 日志、控制台、设备页的状态着色随主题联动，不再有白底浅字的不可读组合。
    /// </summary>
    public static void ApplyThemePalette(bool light)
    {
        var res = Current.Resources;
        res["StatusSuccessBrush"] = Brush(light ? (0x0E, 0x8A, 0x3E) : (0x91, 0xD5, 0xA0));
        res["StatusErrorBrush"] = Brush(light ? (0xC6, 0x28, 0x28) : (0xFF, 0x9B, 0x9B));
        res["StatusLiveBrush"] = res["StatusSuccessBrush"];
        res["RxAccentBrush"] = Brush(light ? (0x16, 0x68, 0xC1) : (0x7C, 0xBC, 0xFD));
        res["TxAccentBrush"] = Brush(light ? (0xB4, 0x6E, 0x00) : (0xFF, 0xC6, 0x84));
        res["OkAccentBrush"] = Brush(light ? (0x14, 0x8A, 0x46) : (0x58, 0xC6, 0x67));
        res["ErrAccentBrush"] = Brush(light ? (0xD3, 0x2F, 0x2F) : (0xFF, 0x6D, 0x7F));
    }

    static SolidColorBrush Brush((int R, int G, int B) c)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>三类未处理异常全部落日志：UI 异常弹窗后继续运行，致命/任务异常只留痕。</summary>
    void HookExceptionLogging()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Exception root = e.Exception.GetBaseException();
            Dbg.Log($"ERROR UI 未处理异常: {e.Exception}\nROOT {root.GetType().FullName}: {root.Message}");
            MessageBox.Show($"发生未处理异常，详情见日志文件夹中的最新 .log：\n{root.GetType().Name}: {root.Message}",
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
