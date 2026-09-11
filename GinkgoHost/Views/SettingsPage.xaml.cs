using System.IO;
using System.Windows;
using System.Windows.Controls;
using GinkgoHost.Native;
using Wpf.Ui.Appearance;

namespace GinkgoHost.Views;

public partial class SettingsPage : UserControl
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            CmbTheme.SelectedIndex = App.Settings.Theme == "Light" ? 1 : 0;
            TxtSettingsPath.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GinkgoHost", "settings.json");
            TxtLogPath.Text = Dbg.LogDir;
            _loading = false;
        };
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string theme = CmbTheme.SelectedIndex == 1 ? "Light" : "Dark";
        App.Settings.Theme = theme;
        App.Settings.Save();
        ApplicationThemeManager.Apply(theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }

    private void BtnOpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        Dbg.OpenLogFolder();
        Dbg.Log("SettingsPage.BtnOpenLogDir_Click: open log folder");
    }
}
