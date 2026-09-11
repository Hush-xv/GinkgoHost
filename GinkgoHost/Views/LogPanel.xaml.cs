using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GinkgoHost.Models;
using GinkgoHost.Native;

namespace GinkgoHost.Views;

public partial class LogPanel : UserControl
{
    private ObservableCollection<LogEntry> _log = null!;
    private bool _followLatest = true;

    public LogPanel()
    {
        InitializeComponent();
    }

    public void Init(ObservableCollection<LogEntry> log)
    {
        _log = log;
        Lst.ItemsSource = log;
        log.CollectionChanged += OnLogChanged;
        UpdateLogPresentation();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TxtCount.Text = $"{_log.Count} 条";
        UpdateLogPresentation();
        if (_followLatest && e.Action == NotifyCollectionChangedAction.Add && Lst.Items.Count > 0)
            Lst.ScrollIntoView(Lst.Items[^1]); // 跟随最新记录
    }

    /// <summary>空日志只占紧凑引导高度，出现记录后再展开为完整工作区。</summary>
    private void UpdateLogPresentation()
    {
        bool empty = _log.Count == 0;
        LogFrame.Height = empty ? 190 : double.NaN;
        LogFrame.VerticalAlignment = empty ? VerticalAlignment.Top : VerticalAlignment.Stretch;
    }

    private void Lst_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Lst.SelectedItem is not LogEntry { Data: { Length: > 0 } } le) return;
        Clipboard.SetText(le.HexGrouped);
        TxtFeedback.Text = "已复制";
    }

    private void Lst_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Lst.SelectedItem is not LogEntry entry) return;
        TxtFeedback.Text = entry.Data is { Length: > 0 } ? "已选中 · 双击复制数据" : "已选中";
    }

    private void ChkFollow_Changed(object sender, RoutedEventArgs e)
    {
        _followLatest = ChkFollow.IsChecked == true;
        TxtFeedback.Text = _followLatest ? "自动跟随" : "已暂停跟随";
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        Dbg.Log($"LogPanel.BtnClear_Click: clear {_log.Count} entries");
        _log.Clear();
        TxtFeedback.Text = "已清空";
    }

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
        Dbg.Log($"LogPanel.BtnExport_Click: exported {_log.Count} entries to {dlg.FileName}");
        TxtFeedback.Text = "导出完成";
    }
}
