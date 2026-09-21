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
    private readonly System.Windows.Threading.DispatcherTimer _followScrollTimer;
    private readonly System.Windows.Threading.DispatcherTimer _summaryRefreshTimer;
    private bool _followScrollQueued;
    private bool _summaryRefreshQueued;
    private LogEntry? _pendingFollowEntry;
    private LogEntry? _selectedRecord;
    private string _filter = "all"; // 取值即 XAML 里筛选按钮的 Tag
    private string? _sortProperty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private readonly LogCounters _counters = new();
    private string? _lastCountSignature;

    public LogPanel()
    {
        _followScrollTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _followScrollTimer.Tick += (_, _) => FlushFollowLatest();
        _summaryRefreshTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _summaryRefreshTimer.Tick += (_, _) => FlushSummaryRefresh();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            QueueFollowLatest();
            QueueSummaryRefresh();
        };
        Unloaded += (_, _) =>
        {
            _followScrollTimer.Stop();
            _followScrollQueued = false;
            _summaryRefreshTimer.Stop();
            _summaryRefreshQueued = false;
        };
    }

    public void Init(ObservableCollection<LogEntry> log)
    {
        _log = log;
        _view = CollectionViewSource.GetDefaultView(log);
        _view.Filter = MatchesFilter;
        Lst.ItemsSource = _view;
        log.CollectionChanged += OnLogChanged;
        // 智能跟随：滚动位置驱动——贴底自动跟随，向上翻阅自动停跟随（复选框同步显示）
        Lst.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Lst_ScrollChanged));
        _counters.Rebuild(log);
        UpdateFilterButtons();
        UpdateSortPresentation();
        UpdateCount();
    }

    /// <summary>仅纯用户滚动才表达用户意图；周期日志改变 Extent 时 WPF 可能同步修正 VerticalOffset，不能据此关闭跟随。</summary>
    private void Lst_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
        // 新增/淘汰记录、筛选和布局变化都会改变 Extent；即使同时带来 VerticalChange，也不是用户滚动。
        if (Math.Abs(e.ExtentHeightChange) >= double.Epsilon) return;
        if (Math.Abs(e.VerticalChange) < double.Epsilon) return;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 2;
        if (atBottom == _followLatest) return;
        _followLatest = atBottom;
        if (ChkFollow.IsChecked != atBottom) ChkFollow.IsChecked = atBottom;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 增量维护计数：高频轮询下避免每条记录全表扫描
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (LogEntry i in e.NewItems!) _counters.Add(i);
                break;
            case NotifyCollectionChangedAction.Remove:
                // 仅当删除碰到末尾连续失败区间时才需重扫。日志容量淘汰最旧项时，
                // 这只会发生在整段日志均失败的边界，避免高频轮询退化为全表扫描。
                int failureTailStart = _counters.Total - _counters.ConsecutiveFailures;
                bool removedFromFailureTail = _counters.ConsecutiveFailures > 0 &&
                    (e.OldStartingIndex < 0 || e.OldStartingIndex + e.OldItems!.Count > failureTailStart);
                foreach (LogEntry i in e.OldItems!) _counters.Remove(i);
                if (removedFromFailureTail)
                {
                    _counters.RecomputeTail(_log);
#if DEBUG
                    Dbg.Log("LogPanel.OnLogChanged: recalculated failure lens after removing its tail");
#endif
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                _counters.Rebuild(_log);
                if (_selectedRecord is { } selected && _log.Contains(selected) && MatchesFilter(selected))
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_log.Contains(selected) && MatchesFilter(selected)) Lst.SelectedItem = selected;
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                else ClearSelectedRecord();
                break;
            default:
                _counters.Rebuild(_log);
                break;
        }
        QueueSummaryRefresh();
        if (_followLatest && e.Action == NotifyCollectionChangedAction.Add &&
            e.NewItems!.OfType<LogEntry>().LastOrDefault(MatchesFilter) is { } latestVisible)
        {
            _pendingFollowEntry = latestVisible;
            QueueFollowLatest();
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _pendingFollowEntry = null;
        }
    }

    /// <summary>计数、失败透镜和按钮状态按 100 ms 合并刷新，计数器本身仍逐笔更新。</summary>
    private void QueueSummaryRefresh()
    {
        if (!IsLoaded || _summaryRefreshQueued) return;
        _summaryRefreshQueued = true;
        _summaryRefreshTimer.Start();
    }

    private void FlushSummaryRefresh()
    {
        _summaryRefreshTimer.Stop();
        _summaryRefreshQueued = false;
        UpdateCount();
    }

    /// <summary>高频证据流按短间隔合并滚动：所有记录已入集合，只合并昂贵的列表定位。</summary>
    private void QueueFollowLatest()
    {
        if (!IsLoaded || !_followLatest || _followScrollQueued || Lst is null || Lst.Items.Count == 0) return;
        _pendingFollowEntry ??= LatestVisibleEntry();
        if (_pendingFollowEntry is null) return;
        _followScrollQueued = true;
        _followScrollTimer.Start();
    }

    private void FlushFollowLatest()
    {
        _followScrollTimer.Stop();
        _followScrollQueued = false;
        LogEntry? latest = _pendingFollowEntry;
        _pendingFollowEntry = null;
        if (_followLatest && latest is not null && Lst.Items.Contains(latest))
            Lst.ScrollIntoView(latest);
    }

    /// <summary>排序可改变视觉顺序，不能把末行当作最新事务；按时间找当前可见的最新项。</summary>
    private LogEntry? LatestVisibleEntry() => Lst.Items.OfType<LogEntry>().MaxBy(entry => entry.Ts);

    private void Lst_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Lst.SelectedItem is not LogEntry { Data: { Length: > 0 } } le) return;
        try
        {
            Clipboard.SetText(le.HexGrouped);
            TxtFeedback.Text = "已复制数据";
#if DEBUG
            Dbg.Log($"LogPanel.Lst_DoubleClick: copied op={le.Op} addr={le.Addr}");
#endif
        }
        catch (Exception ex)
        {
            _ = ex; // Release 中调试日志剔除后仍保留异常捕获路径。
            TxtFeedback.Text = "复制失败，请稍后重试";
#if DEBUG
            Dbg.Log($"LogPanel.Lst_DoubleClick: clipboard unavailable error={ex.Message}");
#endif
        }
    }

    private void Lst_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key != Key.C) return;
        if (!CopySelectedRecord()) return;
        e.Handled = true;
    }

    private bool CopySelectedRecord()
    {
        if (Lst.SelectedItem is not LogEntry entry) return false;

        try
        {
            Clipboard.SetText($"时间: {entry.Time}{Environment.NewLine}" +
                              $"方向: {entry.Dir}{Environment.NewLine}" +
                              $"操作: {entry.Op}{Environment.NewLine}" +
                              $"从机地址: {entry.Addr}{Environment.NewLine}" +
                              $"结果: {entry.RetText}{Environment.NewLine}" +
                              $"耗时: {entry.Ms:F1} ms{Environment.NewLine}" +
                              $"数据 HEX: {entry.DataDisplay}");
            TxtFeedback.Text = "已复制完整记录";
#if DEBUG
            Dbg.Log($"LogPanel.CopySelectedRecord: op={entry.Op} addr={entry.Addr}");
#endif
            return true;
        }
        catch (Exception ex)
        {
            _ = ex; // Release 中调试日志剔除后仍保留异常捕获路径。
            TxtFeedback.Text = "复制失败，请稍后重试";
#if DEBUG
            Dbg.Log($"LogPanel.CopySelectedRecord: clipboard unavailable error={ex.Message}");
#endif
            return false;
        }
    }

    private void BtnCopySelected_Click(object sender, RoutedEventArgs e) => CopySelectedRecord();

    private void Lst_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        if (current is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Lst_ContextMenuOpened(object sender, RoutedEventArgs e) =>
        MnuCopyRecord.IsEnabled = Lst.SelectedItem is LogEntry;

    private void MnuCopyRecord_Click(object sender, RoutedEventArgs e) => CopySelectedRecord();

    private void Lst_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Lst.SelectedItem is not LogEntry entry)
        {
            SelectedRecordPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _selectedRecord = entry;
        TxtFeedback.Text = "已选中 · Ctrl+C 复制完整记录";
        TxtSelectedSummary.Text = $"{entry.Time} · {entry.Dir} · {entry.Op} · {entry.Addr} · {entry.RetText} · {entry.Ms:F1} ms";
        TxtSelectedSummary.ToolTip = TxtSelectedSummary.Text;
        TxtSelectedData.Text = entry.DataDisplay;
        SelectedRecordPanel.Visibility = Visibility.Visible;
    }

    private void ClearSelectedRecord()
    {
        _selectedRecord = null;
        Lst.SelectedItem = null;
        SelectedRecordPanel.Visibility = Visibility.Collapsed;
        TxtSelectedSummary.Text = string.Empty;
        TxtSelectedData.Text = string.Empty;
    }

    private void ChkFollow_Changed(object sender, RoutedEventArgs e)
    {
        _followLatest = ChkFollow.IsChecked == true;
        if (_followLatest) QueueFollowLatest();
        // 跟随状态由 CheckBox 自身表达，不再写 TxtFeedback 造成「自动跟随/跟随最新」重复
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
        ClearSelectedRecord();
        if (_followLatest) QueueFollowLatest();
        int shown = _counters.CountFor(filter);
        TxtFeedback.Text = shown == 0 ? $"{FilterName(filter)}：没有匹配记录" : $"已筛选：{FilterName(filter)} · {shown} 条";
        Dbg.Log($"LogPanel.BtnFilter_Click: filter={filter}");
    }

    private void BtnResetFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_filter == "all") return;
        _filter = "all";
        _view.Refresh();
        UpdateFilterButtons();
        UpdateCount();
        if (_followLatest) QueueFollowLatest();
        TxtFeedback.Text = _counters.Total == 0 ? "暂无记录" : $"已显示全部记录 · {_counters.Total} 条";
#if DEBUG
        Dbg.Log("LogPanel.BtnResetFilter_Click: filter reset to all");
#endif
    }

    private void BtnSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string property }) return;
        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortProperty = property;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(property, _sortDirection));
        UpdateSortPresentation();
        if (_followLatest) QueueFollowLatest();
        TxtFeedback.Text = $"按{SortName(property)} {(_sortDirection == ListSortDirection.Ascending ? "升序" : "降序")}";
        Dbg.Log($"LogPanel.BtnSort_Click: property={property} direction={_sortDirection}");
    }

    /// <summary>排序方向同时出现在表头与工具栏，避免用户只看到重排结果却不知道当前排序条件。</summary>
    private void UpdateSortPresentation()
    {
        SetSortHeader(BtnSortTime, "Ts", "时间");
        SetSortHeader(BtnSortOp, "Op", "操作");
        SetSortHeader(BtnSortAddress, "Addr", "地址");
        SetSortHeader(BtnSortStatus, "RetText", "状态");
        SetSortHeader(BtnSortData, "HexGrouped", "数据预览");
        TxtSortState.Text = _sortProperty is null
            ? "默认顺序"
            : $"排序：{SortName(_sortProperty)} {(_sortDirection == ListSortDirection.Ascending ? "↑" : "↓")}";
    }

    private void SetSortHeader(Button button, string property, string title)
    {
        bool active = _sortProperty == property;
        button.Content = active ? $"{title} {(_sortDirection == ListSortDirection.Ascending ? "↑" : "↓")}" : title;
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private bool MatchesFilter(object item) => item is LogEntry entry && LogCounters.Matches(_filter, entry);

    /// <summary>头部统计：条数 + 成功/失败带色计数。失败为 0 时保持灰色，只有出现失败才转红。</summary>
    private void UpdateCount()
    {
        int shown = _counters.CountFor(_filter);
        string signature = $"{_filter}|{shown}|{_counters.Total}|{_counters.OkCount}|{_counters.ErrCount}|{_counters.ConsecutiveFailures}|{_counters.LatestFailure}";
        if (string.Equals(_lastCountSignature, signature, StringComparison.Ordinal)) return;
        _lastCountSignature = signature;
        TxtCount.Inlines.Clear();
        TxtCount.Inlines.Add(new Run(shown == _counters.Total ? $"{_counters.Total} 条" : $"{shown} / {_counters.Total} 条"));
        // 正常计数保持辅助文字；仅异常用颜色吸引注意力（主题化错误色，亮色下同样可读）。
        TxtCount.Inlines.Add(new Run($"   成功 {_counters.OkCount}"));
        var failRun = new Run($"   失败 {_counters.ErrCount}");
        failRun.SetResourceReference(TextBlock.ForegroundProperty,
            _counters.ErrCount > 0 ? "StatusErrorBrush" : "TextFillColorTertiaryBrush");
        TxtCount.Inlines.Add(failRun);
        BtnClear.IsEnabled = _counters.Total > 0;
        BtnExport.IsEnabled = shown > 0;
        FailureLens.Visibility = _counters.ConsecutiveFailures > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtFailureLens.Text = _counters.ConsecutiveFailures > 0
            ? $"连续失败 {_counters.ConsecutiveFailures} · 最近：{_counters.LatestFailure ?? "未知错误"}"
            : string.Empty;
    }

    private void UpdateFilterButtons()
    {
        // 弱化为 chip：选中=中性灰高亮（不占用协议语义色），未选中=透明；与一级模式按钮拉开层级
        SetFilterPill(BtnFilterAll, _filter == "all");
        SetFilterPill(BtnFilterRead, _filter == "read");
        SetFilterPill(BtnFilterWrite, _filter == "write");
        SetFilterPill(BtnFilterSystem, _filter == "system");
        SetFilterPill(BtnFilterError, _filter == "error");
        BtnResetFilter.Visibility = _filter == "all" ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetFilterPill(Wpf.Ui.Controls.Button pill, bool active)
    {
        pill.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        // 选中态使用主题表面和文字资源，浅色主题不再依赖固定白字。
        pill.BorderThickness = new Thickness(active ? 1 : 0);
        if (active)
        {
            pill.SetResourceReference(Control.BackgroundProperty, "ControlFillColorSecondaryBrush");
            pill.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            pill.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
        else
        {
            pill.Background = Brushes.Transparent;
            pill.BorderBrush = Brushes.Transparent;
            pill.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
    }

    private static string FilterName(string filter) => filter switch
    {
        "read" => "读取", "write" => "写入", "system" => "系统", "error" => "失败", _ => "全部"
    };

    private static string SortName(string property) => property switch
    {
        "Ts" => "时间", "Op" => "操作", "Addr" => "地址", "RetText" => "状态", "HexGrouped" => "数据", _ => property
    };

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"GinkgoHost_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var entries = _view.Cast<LogEntry>().ToList();
        var sb = new StringBuilder();
        sb.AppendLine("时间,方向,操作,从机地址,结果,耗时(ms),数据");
        foreach (LogEntry entry in entries)
            sb.Append(CsvCell(entry.Time)).Append(',')
              .Append(CsvCell(entry.Dir)).Append(',')
              .Append(CsvCell(entry.Op)).Append(',')
              .Append(CsvCell(entry.Addr)).Append(',')
              .Append(CsvCell(entry.RetText)).Append(',')
              .Append(CsvCell(entry.Ms.ToString("F1"))).Append(',')
              // SYS 的 Data 是文字说明，按 hex 导出会丢掉排障信息；RX/TX 保持无空格 hex 便于脚本解析
              .AppendLine(CsvCell(entry.Dir == "SYS" ? entry.DataDisplay : entry.Hex));

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM 保证 Excel 中文不乱码
            Dbg.Log($"LogPanel.BtnExport_Click: exported {entries.Count} filtered entries to {dlg.FileName}");
            TxtFeedback.Text = $"已导出 {entries.Count} 条 · {Path.GetFileName(dlg.FileName)}";
            TxtFeedback.ToolTip = dlg.FileName;
        }
        catch (Exception ex)
        {
            TxtFeedback.Text = "导出失败，请检查文件是否被占用或目录权限";
            TxtFeedback.ToolTip = ex.Message;
#if DEBUG
            Dbg.Log($"LogPanel.BtnExport_Click: failed path={dlg.FileName} error={ex.Message}");
#endif
        }
    }

    private static string CsvCell(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
}
