using System.IO;
using System.Windows;
using System.Windows.Controls;
using GinkgoHost.Native;
using GinkgoHost.Services;
using Wpf.Ui.Appearance;

namespace GinkgoHost.Views;

public partial class SettingsPage : UserControl
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _loading = true;
            CmbTheme.SelectedIndex = App.Settings.Theme == "Light" ? 1 : 0;
            TglAutoConnect.IsChecked = App.Settings.AutoConnect;
            TxtSettingsPath.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GinkgoHost", "settings.json");
            TxtLogPath.Text = Dbg.LogDir;
            TxtSettingsFeedback.Text = string.Empty;
            TxtLogFeedback.Text = string.Empty;
            TxtAutoConnectFeedback.Text = string.Empty;
            TxtAppVersion.Text = $"当前版本 {UpdateService.CurrentVersion}";
            TglCheckUpdates.IsChecked = App.Settings.CheckUpdates;
            _loading = false;
            await RefreshUpdateStateAsync(force: false);
        };
    }

    /// <summary>启动检查与手动检查共用：拿同一任务的缓存结果，不重复请求。</summary>
    private async Task RefreshUpdateStateAsync(bool force)
    {
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateState.Text = string.Empty;
        Task<UpdateService.UpdateInfo?> task = force
            ? UpdateService.ForceCheckAsync()
            : App.Settings.CheckUpdates
                ? UpdateService.GetOrStartCheckAsync()
                : Task.FromResult<UpdateService.UpdateInfo?>(null);
        var info = await task;
        BtnCheckUpdate.IsEnabled = true;
        if (info is null)
        {
            if (force) SetFeedback(TxtUpdateState, "未发现新版本，或当前离线无法查询", success: true);
            else TxtUpdateState.Text = string.Empty;
            return;
        }
        TxtUpdateState.Text = $"发现新版本 {info.Tag} · ";
        // 打开地址用编译期常量（总是最新发布页），不使用 API 返回的字符串，消除注入面
        var link = new System.Windows.Documents.Hyperlink(
            new System.Windows.Documents.Run("前往下载页"))
        {
            NavigateUri = new Uri($"https://github.com/{UpdateService.Repo}/releases/latest")
        };
        link.RequestNavigate += (_, e) =>
        {
            Dbg.OpenUrl(e.Uri.ToString());
        };
        TxtUpdateState.Inlines.Add(link);
        TxtUpdateState.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessBrush");
    }

    private void BtnCheckUpdate_Click(object sender, RoutedEventArgs e) =>
        _ = RefreshUpdateStateAsync(force: true);

    private void TglCheckUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.CheckUpdates = TglCheckUpdates.IsChecked == true;
        App.Settings.Save();
#if DEBUG
        Dbg.Log($"SettingsPage.TglCheckUpdates_Changed: checkUpdates={App.Settings.CheckUpdates}");
#endif
    }

    /// <summary>键盘切页后的第一步是主题选择；鼠标进入页面时不强制改变焦点。</summary>
    public void FocusThemeSelector()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || !CmbTheme.IsEnabled) return;
            CmbTheme.Focus();
#if DEBUG
            Dbg.Log("SettingsPage.FocusThemeSelector: theme selector focused from keyboard navigation");
#endif
        });
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string theme = CmbTheme.SelectedIndex == 1 ? "Light" : "Dark";
        App.Settings.Theme = theme;
        App.Settings.Save();
        bool light = theme == "Light";
        ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark);
        App.ApplyThemePalette(light); // 状态/语义色随主题整组替换，白底不再配浅字
        SetFeedback(TxtSettingsFeedback, $"已切换为{(theme == "Light" ? "浅色" : "深色")}主题，设置已保存", success: true);
#if DEBUG
        Dbg.Log($"SettingsPage.CmbTheme_SelectionChanged: theme={theme}");
#endif
    }

    private void TglAutoConnect_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoConnect = TglAutoConnect.IsChecked == true;
        App.Settings.Save();
        TxtAutoConnectFeedback.Text = App.Settings.AutoConnect
            ? "已开启：下次启动将自动连接适配器"
            : "已关闭：启动后手动点击连接";
#if DEBUG
        Dbg.Log($"SettingsPage.TglAutoConnect_Changed: autoConnect={App.Settings.AutoConnect}");
#endif
    }

    private void BtnCopySettingsPath_Click(object sender, RoutedEventArgs e) =>
        CopyPath(TxtSettingsPath.Text, "已复制设置文件路径", TxtSettingsFeedback);

    private void BtnCopyLogPath_Click(object sender, RoutedEventArgs e) =>
        CopyPath(TxtLogPath.Text, "已复制日志目录路径", TxtLogFeedback);

    private void CopyPath(string path, string successText, TextBlock feedback)
    {
        try
        {
            Clipboard.SetText(path);
            SetFeedback(feedback, successText, success: true);
#if DEBUG
            Dbg.Log($"SettingsPage.CopyPath: copied={path}");
#endif
        }
        catch (Exception ex)
        {
            _ = ex; // Release 不记录调试日志时仍保留 Clipboard 失败处理。
            SetFeedback(feedback, "复制失败，请手动选择路径", success: false);
#if DEBUG
            Dbg.Log($"SettingsPage.CopyPath: failed={ex.Message}");
#endif
        }
    }

    private void BtnOpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        bool opened = Dbg.OpenLogFolder();
        SetFeedback(TxtLogFeedback, opened ? "已打开日志文件夹" : "无法打开日志文件夹，请复制路径后手动打开", opened);
        Dbg.Log($"SettingsPage.BtnOpenLogDir_Click: opened={opened}");
    }

    private static void SetFeedback(TextBlock feedback, string text, bool success)
    {
        feedback.Text = text;
        feedback.SetResourceReference(TextBlock.ForegroundProperty, success ? "StatusSuccessBrush" : "StatusErrorBrush");
    }
}
