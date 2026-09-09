using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GinkgoHost.Models;

namespace GinkgoHost.Views;

public partial class LogPanel : UserControl
{
    private ObservableCollection<LogEntry> _log = null!;

    public LogPanel()
    {
        InitializeComponent();
    }

    public void Init(ObservableCollection<LogEntry> log)
    {
        _log = log;
        Lst.ItemsSource = log;
        log.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TxtCount.Text = $"{_log.Count} 条";
        if (e.Action == NotifyCollectionChangedAction.Add && Lst.Items.Count > 0)
            Lst.ScrollIntoView(Lst.Items[^1]); // 跟随最新记录
    }

    private void Lst_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Lst.SelectedItem is not LogEntry { Data: { Length: > 0 } } le) return;
        Clipboard.SetText(le.HexGrouped);
        TxtCount.Text = "已复制到剪贴板";
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"GinkgoHost_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("时间,方向,操作,从机地址,结果,耗时(ms),数据(hex)");
        foreach (var entry in _log)
            sb.Append(entry.Time).Append(',')
              .Append(entry.Dir).Append(',')
              .Append(entry.Op).Append(',')
              .Append(entry.Addr).Append(',')
              .Append(entry.RetText).Append(',')
              .Append(entry.Ms.ToString("F1")).Append(',')
              .AppendLine(entry.Hex);
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM 保证 Excel 中文不乱码
    }
}
