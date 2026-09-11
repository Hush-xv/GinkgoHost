using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using GinkgoHost.Models;
using GinkgoHost.Native;

namespace GinkgoHost.Views;

public partial class LogPanel : UserControl
{
    private ObservableCollection<LogEntry> _log = null!;
    private ICollectionView _view = null!;
    private bool _followLatest = true;
    private string _filter = "all";
    private string? _sortProperty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public LogPanel()
    {
        InitializeComponent();
    }

    public void Init(ObservableCollection<LogEntry> log)
    {
        _log = log;
        _view = CollectionViewSource.GetDefaultView(log);
        _view.Filter = MatchesFilter;
        Lst.ItemsSource = _view;
        log.CollectionChanged += OnLogChanged;
        UpdateCount();
        UpdateLogPresentation();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCount();
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
        if (Lst.SelectedItem is not LogEntry entry)
        {
            SelectedRecordPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtFeedback.Text = entry.Data is { Length: > 0 } ? "已选中 · 双击复制数据" : "已选中";
        TxtSelectedSummary.Text = $"{entry.Time} · {entry.Op} · {entry.Addr} · {entry.RetText} · {entry.Ms:F1} ms";
        TxtSelectedData.Text = entry.HexGrouped;
        SelectedRecordPanel.Visibility = Visibility.Visible;
    }

    private void ChkFollow_Changed(object sender, RoutedEventArgs e)
    {
        _followLatest = ChkFollow.IsChecked == true;
        TxtFeedback.Text = _followLatest ? "自动跟随" : "已暂停跟随";
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"确认清空 {_log.Count} 条事务记录？", "确认清空日志",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            TxtFeedback.Text = "已取消清空";
            return;
        }
        Dbg.Log($"LogPanel.BtnClear_Click: clear {_log.Count} entries");
        _log.Clear();
        TxtFeedback.Text = "已清空";
    }

    private void BtnFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string filter }) return;
        _filter = filter;
        _view.Refresh();
        UpdateFilterButtons();
        UpdateCount();
        SelectedRecordPanel.Visibility = Visibility.Collapsed;
        TxtFeedback.Text = $"已筛选：{FilterName(filter)}";
        Dbg.Log($"LogPanel.BtnFilter_Click: filter={filter}");
    }

    private void BtnSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string property }) return;
        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortProperty = property;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(property, _sortDirection));
        TxtFeedback.Text = $"按{property} {(_sortDirection == ListSortDirection.Ascending ? "升序" : "降序")}";
        Dbg.Log($"LogPanel.BtnSort_Click: property={property} direction={_sortDirection}");
    }

    private bool MatchesFilter(object item) => item is LogEntry entry && _filter switch
    {
        "read" => entry.Dir == "RX",
        "write" => entry.Dir == "TX",
        "system" => entry.Dir == "SYS",
        "error" => !entry.Ok,
        _ => true
    };

    private static readonly Brush CountOkBrush = new SolidColorBrush(Color.FromRgb(0x91, 0xD5, 0xA0));
    private static readonly Brush CountErrBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x9B));
    private static readonly Brush CountErrZeroBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));

    /// <summary>头部统计：总数（含筛选态）+ 成功/失败带色计数，一眼看出事务健康度。</summary>
    private void UpdateCount()
    {
        int total = _log.Count, ok = _log.Count(e => e.Ok), err = total - ok;
        int shown = _view.Cast<LogEntry>().Count();
        TxtCount.Inlines.Clear();
        TxtCount.Inlines.Add(new Run(shown == total ? $"共 {total} 条" : $"{shown} / {total} 条"));
        TxtCount.Inlines.Add(new Run($" · 成功 {ok}") { Foreground = CountOkBrush });
        TxtCount.Inlines.Add(new Run($" · 失败 {err}") { Foreground = err > 0 ? CountErrBrush : CountErrZeroBrush });
    }

    private void UpdateFilterButtons()
    {
        BtnFilterAll.Appearance = _filter == "all" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnFilterRead.Appearance = _filter == "read" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnFilterWrite.Appearance = _filter == "write" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnFilterSystem.Appearance = _filter == "system" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnFilterError.Appearance = _filter == "error" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    private static string FilterName(string filter) => filter switch
    {
        "read" => "读取", "write" => "写入", "system" => "系统", "error" => "失败", _ => "全部"
    };

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
        foreach (LogEntry entry in _view.Cast<LogEntry>())
            sb.Append(entry.Time).Append(',')
              .Append(entry.Dir).Append(',')
              .Append(entry.Op).Append(',')
              .Append(entry.Addr).Append(',')
              .Append(entry.RetText).Append(',')
              .Append(entry.Ms.ToString("F1")).Append(',')
              .AppendLine(entry.Hex);
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM 保证 Excel 中文不乱码
        Dbg.Log($"LogPanel.BtnExport_Click: exported {_view.Cast<LogEntry>().Count()} filtered entries to {dlg.FileName}");
        TxtFeedback.Text = "导出完成";
    }
}
