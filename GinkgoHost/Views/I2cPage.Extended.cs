using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GinkgoHost.Models;
using GinkgoHost.Native;
using GinkgoHost.Services;

namespace GinkgoHost.Views;

/// <summary>
/// I2cPage 的扩展面板部分：寄存器表 / 初始化序列 / 周期轮询 / Profile。
/// 与 I2cPage.xaml.cs 同属一个 partial 类，按功能分文件降低单文件体量，无行为差异。
/// </summary>
public partial class I2cPage
{
    // ── 扩展面板（寄存器表 / 初始化序列，行内执行支持按「周期ms」轮询）──

    public ObservableCollection<RegRow> RegTable { get; } = [];
    public ObservableCollection<RegRow> InitSeq { get; } = [];
    /// <summary>正在轮询的行 → 其取消源。键为引用，DataGrid 行对象生命周期内稳定。</summary>
    private readonly Dictionary<RegRow, CancellationTokenSource> _rowLoops = [];
    private CancellationTokenSource? _extOperationCts;
    private string _extOperationAction = string.Empty;
    private string _extCancellationReason = "用户停止";
    private string? _lastPollSummary;
    private readonly Dictionary<string, string> _registerSnapshot = [];
    private string? _lastReadAllSummary;
    private string? _lastInitSummary;
    private long _lastPollUiRefreshTimestamp;
    private (RegRow Row, int Index)? _lastDeletedRegister;
    private (RegRow Row, int Index)? _lastDeletedInit;

    private void BtnTabMain_Click(object sender, RoutedEventArgs e) => ShowExtTab(false);
    private void BtnTabExt_Click(object sender, RoutedEventArgs e) => ShowExtTab(true);

    private static void SetModeTab(System.Windows.Controls.Button tab, bool active)
    {
        // 分段控件选中态：内层浮起为主表面 + 主文字；未选中透明 + 辅助文字。双主题走资源。
        if (active) tab.SetResourceReference(Control.BackgroundProperty, "ControlFillColorSecondaryBrush");
        else tab.Background = Brushes.Transparent;
        tab.SetResourceReference(Control.ForegroundProperty,
            active ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        tab.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void BtnSubReg_Click(object sender, RoutedEventArgs e) => ShowExtSub("reg");
    private void BtnSubInit_Click(object sender, RoutedEventArgs e) => ShowExtSub("init");

    private void ShowExtSub(string which)
    {
        RegHost.Visibility = which == "reg" ? Visibility.Visible : Visibility.Collapsed;
        InitHost.Visibility = which == "init" ? Visibility.Visible : Visibility.Collapsed;
        BtnSubReg.Appearance = which == "reg" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnSubInit.Appearance = which == "init" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    private void ShowExtTab(bool ext)
    {
        if (!ext && (_rowLoops.Count > 0 || _extOperationCts is not null))
            CancelExtendedWork("离开寄存器检查器");
        if (ext)
        {
            ApplyInspectorWidth();
            ExtPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ExtPanel.Visibility = Visibility.Collapsed;
            ControlColumn.MinWidth = 0;
            ControlColumn.Width = new GridLength(0);
        }
        PanelSplitter.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        BtnTabMain.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnTabExt.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        // 模式 Tab 用「深色底 + 橙字 + 橙色底边」表达选中，不与写入等执行按钮争抢 Primary
        SetModeTab(BtnTabMain, active: !ext);
        SetModeTab(BtnTabExt, active: ext);
        // 切换扩展面板会改变可用列宽；等待布局计算后再决定命令栏的单/双行。
        RefreshHeaderContext();
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(ApplyCommandLayout);
    }

    private void TxtSlave_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 双向同步：扩展面板改地址时主面板跟随（_syncingSlaveAddr 防止回环）
        if (!_syncingSlaveAddr && TxtAddr is not null && TxtAddr.Text != TxtSlave.Text)
        {
            _syncingSlaveAddr = true;
            try { TxtAddr.Text = TxtSlave.Text; }
            finally { _syncingSlaveAddr = false; }
        }
        ValidateExtSlave();
        RefreshExtendedUi();
    }

    private bool ValidateExtSlave()
    {
        // XAML 按声明顺序创建控件；TxtSlave 的初始 TextChanged 会早于 CmbAddrFmt。
        bool eightBit = CmbAddrFmt?.SelectedIndex == 1;
        bool valid = Hex.TryParseByte(TxtSlave.Text, out var address) &&
                     (eightBit ? (address & 1) == 0 : address <= 0x7F);
        if (valid) TxtSlave.ClearValue(Control.BorderBrushProperty);
        else TxtSlave.BorderBrush = ErrorBorderBrush;
        TxtSlave.ToolTip = valid
            ? eightBit ? "8 位地址（hex），如 0x92" : "7 位地址（hex），如 0x49"
            : eightBit ? "8 位地址须为偶数 hex 值，如 0x92" : "地址须为 00–7F 的十六进制数";
        if (TxtExtAddressFeedback is not null)
        {
            TxtExtAddressFeedback.Text = valid ? string.Empty : TxtSlave.ToolTip.ToString();
            TxtExtAddressFeedback.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        }
        return valid;
    }

    private byte ExtSlave7()
    {
        if (!ValidateExtSlave()) throw new ArgumentException("从机地址格式无效");
        byte raw = Hex.ParseByte(TxtSlave.Text);
        return CmbAddrFmt.SelectedIndex == 1 ? (byte)(raw >> 1) : raw;
    }

    private void UpdateExtAddressFormatHint()
    {
        bool eightBit = CmbAddrFmt.SelectedIndex == 1;
        TxtExtSlaveLabel.Text = eightBit ? "从机地址 · 8-bit" : "从机地址 · 7-bit";
        ValidateExtSlave();
    }

    private uint ExtParseReg(string s)
    {
        s = s.Trim().Replace("0x", "").Replace("0X", "");
        uint value = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        uint max = ExtRegWidth() == GinkgoDriver.VII_SUB_ADDR_2BYTE ? 0xFFFFu : 0xFFu;
        if (value > max) throw new ArgumentException($"寄存器地址超出 {max:X} 范围");
        return value;
    }

    private static int ExtLength(RegRow row)
    {
        if (row.Len is < 1 or > 256) throw new ArgumentException("读取长度须在 1–256 之间");
        return row.Len;
    }

    private static byte[] ExtWriteData(RegRow row)
    {
        byte[] data = Hex.ParseBytes(string.IsNullOrWhiteSpace(row.Value) ? "00" : row.Value);
        if (data.Length is < 1 or > 256) throw new ArgumentException("写入数据须在 1–256 字节之间");
        return data;
    }

    private static void SetRowResult(RegRow row, OpResult result)
    {
        row.LastDurationMs = result.Ms;
        row.StatusDetail = result.Ok
            ? "事务执行成功"
            : $"{GinkgoDriver.ErrorName(result.Ret)} ({result.Ret})";
        row.Status = result.Ok ? "OK" : "ERR";
    }

    private static void SetRowError(RegRow row, string detail, string status = "ERR")
    {
        row.LastDurationMs = null;
        row.StatusDetail = detail;
        row.Status = status;
    }

    private bool ValidateRowForFeedback(RegRow row, bool initialization)
    {
        try
        {
            _ = ExtParseReg(row.Reg);
            _ = ExtLength(row);
            if (row.Dir is not ("R" or "W")) throw new ArgumentException("读写方向须为 R 或 W");
            if (row.PeriodMs < 0) throw new ArgumentException("周期不能小于 0 ms");
            if (initialization && row.DelayMs is < 0 or > 10_000)
                throw new ArgumentException("初始化延时须在 0–10000 ms 之间");
            if (row.Dir == "W") _ = ExtWriteData(row);
            if (row.Status == "INPUT")
            {
                row.Status = string.Empty;
                row.StatusDetail = string.Empty;
                row.LastDurationMs = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            SetRowError(row, ex.Message, "INPUT");
#if DEBUG
            Dbg.Log($"I2cPage.ValidateRowForFeedback: invalid table={(initialization ? "init" : "register")} reg={row.Reg} error={ex.Message}");
#endif
            return false;
        }
    }

    private static void FocusRow(DataGrid grid, RegRow row)
    {
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
        grid.Focus();
    }

    private byte ExtRegWidth() =>
        CmbRegWidth.SelectedIndex == 1 ? GinkgoDriver.VII_SUB_ADDR_2BYTE : GinkgoDriver.VII_SUB_ADDR_1BYTE;

    private void RefreshProfiles()
    {
        CmbProfile.Items.Clear();
        foreach (var p in ProfileService.List())
            CmbProfile.Items.Add(p);
        if (CmbProfile.Items.Count > 0) CmbProfile.SelectedIndex = 0;
    }

    private void BtnAddRow_Click(object sender, RoutedEventArgs e)
    {
        if (_extOperationBusy) return;
        var row = new RegRow();
        RegTable.Add(row);
        BeginEditNewRow(GridReg, row, 0);
        Dbg.Log($"I2cPage.BtnAddRow_Click: added index={RegTable.Count - 1}");
    }

    private void BtnAddInit_Click(object sender, RoutedEventArgs e)
    {
        if (_extOperationBusy) return;
        var row = new RegRow();
        InitSeq.Add(row);
        BeginEditNewRow(GridInit, row, 1);
        Dbg.Log($"I2cPage.BtnAddInit_Click: added index={InitSeq.Count - 1}");
    }

    private static RegRow CloneRow(RegRow source) => new()
    {
        Reg = source.Reg,
        Len = source.Len,
        PeriodMs = source.PeriodMs,
        Dir = source.Dir,
        Value = source.Value,
        DelayMs = source.DelayMs,
        Note = source.Note
    };

    /// <summary>新增后直接进入首个关键字段，省去重新寻找新行和双击单元格。</summary>
    private void BeginEditNewRow(DataGrid grid, RegRow row, int columnIndex)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || grid.IsReadOnly || columnIndex >= grid.Columns.Count) return;
            grid.SelectedItem = row;
            grid.ScrollIntoView(row);
            grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[columnIndex]);
            grid.Focus();
            grid.BeginEdit();
#if DEBUG
            Dbg.Log($"I2cPage.BeginEditNewRow: grid={grid.Name} column={columnIndex}");
#endif
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BtnSaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _extOperationBusy) return;
        CommitGridEdit(GridReg);
        _registerSnapshot.Clear();
        // 快照是一次完整基线：清除所有旧标记，不能让已删值或写入行保留过期高亮。
        foreach (var row in RegTable)
        {
            row.SnapshotChanged = false;
            row.SnapshotBaseline = null;
        }
        foreach (var row in RegTable.Where(row => row.Dir == "R" && row.Status == "OK" && !string.IsNullOrWhiteSpace(row.Value)))
        {
            _registerSnapshot[SnapshotKey(row)] = row.Value;
            row.SnapshotBaseline = row.Value;
        }
        TxtExtStatus.Text = _registerSnapshot.Count == 0
            ? "没有可保存的读取值；先执行“一键读取全部”"
            : $"已保存快照 · {_registerSnapshot.Count} 个寄存器";
        TxtSnapshotState.Text = _registerSnapshot.Count == 0
            ? "无有效快照"
            : $"基线 {_registerSnapshot.Count} 项";
        App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "保存寄存器快照", "—", 0, 0,
            System.Text.Encoding.UTF8.GetBytes($"{_registerSnapshot.Count} 个寄存器")));
        Dbg.Log($"I2cPage.BtnSaveSnapshot_Click: entries={_registerSnapshot.Count}");
    }

    private static string SnapshotKey(RegRow row) =>
        $"{row.Reg.Trim().ToUpperInvariant()}|{row.Len}|{row.Dir}";

    private void BtnDelRow_Click(object sender, RoutedEventArgs e)
    {
        if (GridReg.SelectedItem is not RegRow r) return;
        int index = RegTable.IndexOf(r);
        _lastDeletedRegister = (r, index);
        Dbg.Log($"I2cPage.BtnDelRow_Click: reg={r.Reg} index={index}");
        RegTable.Remove(r);
        if (RegTable.Count > 0) GridReg.SelectedIndex = Math.Min(index, RegTable.Count - 1);
        UpdateRowActionAvailability();
    }

    private void BtnDelInit_Click(object sender, RoutedEventArgs e)
    {
        if (GridInit.SelectedItem is not RegRow r) return;
        int index = InitSeq.IndexOf(r);
        _lastDeletedInit = (r, index);
        Dbg.Log($"I2cPage.BtnDelInit_Click: reg={r.Reg} index={index}");
        InitSeq.Remove(r);
        if (InitSeq.Count > 0) GridInit.SelectedIndex = Math.Min(index, InitSeq.Count - 1);
        UpdateRowActionAvailability();
    }

    private void GridReg_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRowActionAvailability();

    private void GridInit_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRowActionAvailability();

    private async void GridReg_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers == ModifierKeys.Control;
        if (!control && Keyboard.FocusedElement is TextBoxBase or ComboBox) return;

        if (!control && e.Key == Key.Delete)
            BtnDelRow_Click(GridReg, new RoutedEventArgs());
        else if (control && e.Key == Key.D)
            BtnDuplicateRow_Click(GridReg, new RoutedEventArgs());
        else if (control && e.Key == Key.Z)
            BtnUndoRow_Click(GridReg, new RoutedEventArgs());
        else if (control && e.Key == Key.Up)
            BtnMoveRowUp_Click(GridReg, new RoutedEventArgs());
        else if (control && e.Key == Key.Down)
            BtnMoveRowDown_Click(GridReg, new RoutedEventArgs());
        else if (control && e.Key == Key.Enter && GridReg.SelectedItem is RegRow row)
        {
            e.Handled = true;
            await ExecuteRegisterRowAsync(row);
            return;
        }
        else
            return;

        e.Handled = true;
    }

    private async void GridInit_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers == ModifierKeys.Control;
        if (!control && Keyboard.FocusedElement is TextBoxBase or ComboBox) return;

        if (!control && e.Key == Key.Delete)
            BtnDelInit_Click(GridInit, new RoutedEventArgs());
        else if (control && e.Key == Key.D)
            BtnDuplicateInit_Click(GridInit, new RoutedEventArgs());
        else if (control && e.Key == Key.Z)
            BtnUndoInit_Click(GridInit, new RoutedEventArgs());
        else if (control && e.Key == Key.Up)
            BtnMoveInitUp_Click(GridInit, new RoutedEventArgs());
        else if (control && e.Key == Key.Down)
            BtnMoveInitDown_Click(GridInit, new RoutedEventArgs());
        else if (control && e.Key == Key.Enter && GridInit.SelectedItem is RegRow row)
        {
            e.Handled = true;
            await ExecuteInitRowAsync(row);
            return;
        }
        else
            return;

        e.Handled = true;
    }

    private void UpdateRowActionAvailability()
    {
        bool editable = !_operationBusy && !_extOperationBusy;
        int regIndex = GridReg.SelectedIndex;
        int initIndex = GridInit.SelectedIndex;
        bool regSelected = editable && regIndex >= 0;
        bool initSelected = editable && initIndex >= 0;
        BtnDelRow.IsEnabled = regSelected;
        BtnDuplicateRow.IsEnabled = regSelected;
        BtnMoveRowUp.IsEnabled = regSelected && regIndex > 0;
        BtnMoveRowDown.IsEnabled = regSelected && regIndex < RegTable.Count - 1;
        BtnUndoRow.IsEnabled = editable && _lastDeletedRegister is not null;
        BtnDelInit.IsEnabled = initSelected;
        BtnDuplicateInit.IsEnabled = initSelected;
        BtnMoveInitUp.IsEnabled = initSelected && initIndex > 0;
        BtnMoveInitDown.IsEnabled = initSelected && initIndex < InitSeq.Count - 1;
        BtnUndoInit.IsEnabled = editable && _lastDeletedInit is not null;
    }

    private void BtnDuplicateRow_Click(object sender, RoutedEventArgs e) => DuplicateSelectedRow(GridReg, RegTable, isInit: false);
    private void BtnDuplicateInit_Click(object sender, RoutedEventArgs e) => DuplicateSelectedRow(GridInit, InitSeq, isInit: true);

    private void DuplicateSelectedRow(DataGrid grid, ObservableCollection<RegRow> rows, bool isInit)
    {
        if (_operationBusy || _extOperationBusy || grid.SelectedItem is not RegRow source) return;
        int index = rows.IndexOf(source) + 1;
        RegRow copy = CloneRow(source);
        rows.Insert(index, copy);
        grid.SelectedItem = copy;
        grid.ScrollIntoView(copy);
        UpdateRowActionAvailability();
#if DEBUG
        Dbg.Log($"I2cPage.DuplicateSelectedRow: table={(isInit ? "init" : "register")} index={index} reg={copy.Reg}");
#endif
    }

    private void BtnMoveRowUp_Click(object sender, RoutedEventArgs e) => MoveSelectedRow(GridReg, RegTable, -1);
    private void BtnMoveRowDown_Click(object sender, RoutedEventArgs e) => MoveSelectedRow(GridReg, RegTable, 1);
    private void BtnMoveInitUp_Click(object sender, RoutedEventArgs e) => MoveSelectedRow(GridInit, InitSeq, -1);
    private void BtnMoveInitDown_Click(object sender, RoutedEventArgs e) => MoveSelectedRow(GridInit, InitSeq, 1);

    private void MoveSelectedRow(DataGrid grid, ObservableCollection<RegRow> rows, int offset)
    {
        if (_operationBusy || _extOperationBusy || grid.SelectedItem is not RegRow row) return;
        int from = rows.IndexOf(row);
        int to = from + offset;
        if (to < 0 || to >= rows.Count) return;
        rows.Move(from, to);
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
        UpdateRowActionAvailability();
#if DEBUG
        Dbg.Log($"I2cPage.MoveSelectedRow: grid={grid.Name} from={from} to={to} reg={row.Reg}");
#endif
    }

    private void BtnUndoRow_Click(object sender, RoutedEventArgs e) => RestoreDeletedRow(GridReg, RegTable, ref _lastDeletedRegister);
    private void BtnUndoInit_Click(object sender, RoutedEventArgs e) => RestoreDeletedRow(GridInit, InitSeq, ref _lastDeletedInit);

    private void RestoreDeletedRow(DataGrid grid, ObservableCollection<RegRow> rows, ref (RegRow Row, int Index)? deleted)
    {
        if (_operationBusy || _extOperationBusy || deleted is not { } item) return;
        int index = Math.Clamp(item.Index, 0, rows.Count);
        rows.Insert(index, item.Row);
        deleted = null;
        grid.SelectedItem = item.Row;
        grid.ScrollIntoView(item.Row);
        UpdateRowActionAvailability();
#if DEBUG
        Dbg.Log($"I2cPage.RestoreDeletedRow: grid={grid.Name} index={index} reg={item.Row.Reg}");
#endif
    }

    /// <summary>按钮执行前提交 DataGrid 当前编辑，避免刚切换的读写方向仍沿用旧值。</summary>
    private static void CommitGridEdit(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        Dbg.Log($"I2cPage.CommitGridEdit: grid={grid.Name}");
    }

    private static string RegisterOperation(string action, RegRow row) => $"{action} · Reg {row.Reg}";

    /// <summary>进入行内编辑后直接覆盖旧值，避免先删除默认地址或长度。</summary>
    private void Grid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is not TextBox editor) return;
        Dispatcher.BeginInvoke(editor.SelectAll, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not RegRow row || sender is not DataGrid grid) return;
        bool initialization = ReferenceEquals(grid, GridInit);
        Dispatcher.BeginInvoke(() =>
        {
            ValidateRowForFeedback(row, initialization);
            UpdateInspectorSummaries();
        },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>行执行：周期ms=0 单次执行（按读写属性）；&gt;0 点击进入周期轮询，再点停止。</summary>
    private async void BtnRowExec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        await ExecuteRegisterRowAsync(row);
    }

    private async Task ExecuteRegisterRowAsync(RegRow row)
    {
        CommitGridEdit(GridReg);

        // 已在轮询 → 本次点击 = 停止。取消源保留到循环 finally，避免停止过程中再次启动第二个循环。
        if (_rowLoops.TryGetValue(row, out var running))
        {
            running.Cancel();
            row.ExecuteText = "停止中";
            row.ExecuteToolTip = "正在停止此行周期轮询";
            RefreshExtendedUi();
            return;
        }

        if (_operationBusy || _extOperationBusy) return;
        if (!ValidateRowForFeedback(row, initialization: false))
        {
            FocusRow(GridReg, row);
            TxtExtStatus.Text = $"输入有误 · Reg {row.Reg} · {row.StatusDetail}";
            return;
        }
        if (row.PeriodMs > 0)
        {
            var cts = new CancellationTokenSource();
            _rowLoops[row] = cts;
            _extCancellationReason = "用户停止";
            _lastPollSummary = null;
            row.Polling = true;
            row.PollCount = 0;
            row.ExecuteText = "停止";
            row.ExecuteToolTip = "停止此行周期轮询";
            SetExtOperationBusy(true, "周期轮询");
            try { await RunRowLoopAsync(row, cts.Token); }
            finally
            {
                _rowLoops.Remove(row);
                row.Polling = false;
                row.ExecuteText = "执行";
                row.ExecuteToolTip = "执行此行";
                SetExtOperationBusy(false);
                if (_lastPollSummary is not null) TxtExtStatus.Text = _lastPollSummary;
            }
        }
        else
        {
            bool completed = await RunLocalRowOperationAsync("行执行", row, () => ExecRowOnceAsync(row));
            if (completed)
            {
                TxtExtStatus.Text = row.Status == "OK"
                    ? $"行执行完成 · Reg {row.Reg}"
                    : $"行执行失败 · Reg {row.Reg}，请查看事务日志";
                if (row.Status == "ERR") FocusRow(GridReg, row);
            }
        }
    }

    /// <summary>单次执行：按行读写属性分派（R 读 / W 写）。</summary>
    private async Task ExecRowOnceAsync(RegRow row)
    {
        try
        {
            if (row.Dir == "W")
            {
                byte[] data = ExtWriteData(row);
                var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                SetRowResult(row, r);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", RegisterOperation("寄存器写", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, data));
            }
            else
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                SetRowResult(row, r);
                if (r.Ok && r.Data is not null) row.Value = BufferText(r.Data);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("寄存器读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
            }
        }
        catch (Exception ex)
        {
            // 单行异常必须回写本行，不能把上一轮 OK 留在屏幕上。
            SetRowError(row, ex.Message);
            row.SnapshotChanged = false;
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "行执行", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            Dbg.Log($"I2cPage.ExecRowOnceAsync: failed reg={row.Reg} dir={row.Dir} error={ex.Message}");
        }
    }

    /// <summary>行周期轮询循环：每一轮读写都写入证据流，序号与行内计数一致。
    /// 连续 5 次失败自动停止（README 承诺的安全行为），避免拔线后空转。</summary>
    private async Task RunRowLoopAsync(RegRow row, CancellationToken ct)
    {
        Dbg.Log($"I2cPage.RunRowLoopAsync: start reg={row.Reg} periodMs={row.PeriodMs} dir={row.Dir}");
        const int failLimit = 5;
        int fails = 0;
        bool autoStopped = false;
        while (!ct.IsCancellationRequested)
        {
            int iteration = row.PollCount + 1;
            try
            {
                if (row.Dir == "W")
                {
                    byte[] data = ExtWriteData(row);
                    var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                    SetRowResult(row, r);
                    App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", RegisterOperation($"周期写 #{iteration}", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, data));
                }
                else
                {
                    var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                    SetRowResult(row, r);
                    if (r.Ok)
                    {
                        // 成功事务必须回填，即使值与上一轮相同，也让表格反映本次读到的结果。
                        string newValue = r.Data is not null ? BufferText(r.Data) : "—";
                        row.Value = newValue;
                    }
                    App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation($"周期读 #{iteration}", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
                }
            }
            catch (Exception ex)
            {
                SetRowError(row, ex.Message);
                bool writeRow = row.Dir == "W";
                App.Log.AddCapped(new LogEntry(DateTime.Now, writeRow ? "TX" : "RX",
                    RegisterOperation($"{(writeRow ? "周期写" : "周期读")} #{iteration}", row), "—", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
            row.PollCount = iteration;
            // 总线证据逐笔保留；摘要限频，避免高速轮询持续占用布局线程。
            long now = Stopwatch.GetTimestamp();
            if (_lastPollUiRefreshTimestamp == 0 ||
                Stopwatch.GetElapsedTime(_lastPollUiRefreshTimestamp, now) >= TimeSpan.FromMilliseconds(100))
            {
                _lastPollUiRefreshTimestamp = now;
                string summary = $"周期轮询 · Reg {row.Reg} · 第 {iteration} 次 · {row.Status}";
                if (TxtExtStatus.Text != summary) TxtExtStatus.Text = summary;
                row.ExecuteToolTip = $"已执行 {iteration} 次 · 点击停止周期轮询";
                UpdateInspectorSummaries();
            }
            bool lastOk = row.Status == "OK";
            if (lastOk) fails = 0;
            else if (++fails >= failLimit)
            {
                string reason = $"连续 {fails} 次失败，已自动停止（{row.Note}行 Reg {row.Reg}）";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "周期轮询", "—", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(reason)));
                Dbg.Log($"I2cPage.RunRowLoopAsync: auto-stopped reg={row.Reg} fails={fails}");
                autoStopped = true;
                break;
            }
            try { await Task.Delay(Math.Max(10, row.PeriodMs), ct); }
            catch (OperationCanceledException) { break; }
        }
        _lastPollSummary = autoStopped
            ? $"周期轮询自动停止 · Reg {row.Reg} · 连续 {fails} 次失败 · 共 {row.PollCount} 次"
            : $"周期轮询已停止 · Reg {row.Reg} · 共 {row.PollCount} 次 · {_extCancellationReason} · 最后结果 {row.Status}";
        _lastPollUiRefreshTimestamp = 0;
        if (!autoStopped)
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "周期轮询停止", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(_lastPollSummary)));
        Dbg.Log($"I2cPage.RunRowLoopAsync: stopped reg={row.Reg} polls={row.PollCount}");
    }

    /// <summary>初始化行执行：按行读写属性分派（R 读校验 / W 写入）。</summary>
    private async void BtnInitRowExec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        await ExecuteInitRowAsync(row);
    }

    private async Task ExecuteInitRowAsync(RegRow row)
    {
        CommitGridEdit(GridInit);
        if (_operationBusy || _extOperationBusy) return;
        if (!ValidateRowForFeedback(row, initialization: true))
        {
            FocusRow(GridInit, row);
            TxtExtStatus.Text = $"输入有误 · Reg {row.Reg} · {row.StatusDetail}";
            return;
        }
        bool completed = await RunLocalRowOperationAsync("初始化行", row, () => ExecInitRowAsync(row));
        if (completed)
        {
            TxtExtStatus.Text = row.Status == "OK"
                ? $"初始化行完成 · Reg {row.Reg}"
                : $"初始化行失败 · Reg {row.Reg}，请查看事务日志";
            if (row.Status == "ERR") FocusRow(GridInit, row);
        }
    }

    private async Task ExecInitRowAsync(RegRow row)
    {
        try
        {
            if (row.Dir == "R")
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                SetRowResult(row, r);
                if (r.Ok && r.Data is not null) row.Value = BufferText(r.Data);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("初始化读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
            }
            else
            {
                byte[] data = ExtWriteData(row);
                var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                SetRowResult(row, r);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", RegisterOperation("初始化写", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, data));
            }
        }
        catch (Exception ex)
        {
            SetRowError(row, ex.Message);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "初始化", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    private async void BtnReadAll_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _extOperationBusy) return;
        CommitGridEdit(GridReg);
        _lastReadAllSummary = null;
        bool completed = await RunExtendedOperationAsync("一键读取", ReadAllAsync);
        if (completed && _lastReadAllSummary is not null) TxtExtStatus.Text = _lastReadAllSummary;
    }

    private async Task ReadAllAsync(CancellationToken ct)
    {
        // 只读取方向为 R 的行；W 行由行内「执行」显式写入
        int compared = 0, changed = 0, failed = 0, invalid = 0;
        RegRow? firstChanged = null;
        RegRow? firstAttention = null;
        foreach (var row in RegTable.Where(r => r.Dir == "R"))
        {
            ct.ThrowIfCancellationRequested();
            if (!ValidateRowForFeedback(row, initialization: false))
            {
                invalid++;
                firstAttention ??= row;
                continue;
            }
            try
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                SetRowResult(row, r);
                if (r.Ok && r.Data is not null)
                {
                    row.Value = BufferText(r.Data);
                    if (_registerSnapshot.TryGetValue(SnapshotKey(row), out string? baseline))
                    {
                        compared++;
                        row.SnapshotBaseline = baseline;
                        row.SnapshotChanged = !string.Equals(baseline, row.Value, StringComparison.OrdinalIgnoreCase);
                        if (row.SnapshotChanged)
                        {
                            changed++;
                            firstChanged ??= row;
                        }
                    }
                    else
                    {
                        row.SnapshotBaseline = null;
                        row.SnapshotChanged = false;
                    }
                }
                else
                {
                    failed++;
                    firstAttention ??= row;
                    // 驱动错误同异常一样没有新数据，不能保留旧轮次的变化结论。
                    row.SnapshotChanged = false;
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("寄存器读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SetRowError(row, ex.Message);
                failed++;
                firstAttention ??= row;
                // 失败没有新数据可比；移除上一轮的变化标记，避免把旧结果当作本轮证据。
                row.SnapshotChanged = false;
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "读全部", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
            ct.ThrowIfCancellationRequested();
        }
        if ((firstChanged ?? firstAttention) is { } focusRow) FocusRow(GridReg, focusRow);
        if (_registerSnapshot.Count == 0)
            _lastReadAllSummary = $"读取完成 · 尚未保存快照 · 失败 {failed} · 输入错误 {invalid}";
        else
        {
            _lastReadAllSummary = $"读取完成 · 对比 {compared} 项 · 变化 {changed} 项 · 失败 {failed} · 输入错误 {invalid}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "寄存器快照对比", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(_lastReadAllSummary)));
        }
    }

    private async void BtnRunInit_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _extOperationBusy) return;
        CommitGridEdit(GridInit);
        _lastInitSummary = null;
        Dbg.Log($"I2cPage.BtnRunInit_Click: direct start steps={InitSeq.Count}");
        bool completed = await RunExtendedOperationAsync("一键初始化", RunInitSequenceAsync);
        if (completed && _lastInitSummary is not null) TxtExtStatus.Text = _lastInitSummary;
    }

    private async Task RunInitSequenceAsync(CancellationToken ct)
    {
        int completed = 0;
        foreach (var row in InitSeq)
        {
            ct.ThrowIfCancellationRequested();
            if (!ValidateRowForFeedback(row, initialization: true))
            {
                FocusRow(GridInit, row);
                _lastInitSummary = $"初始化中止 · 第 {completed + 1} 步输入有误 · {row.StatusDetail}";
                return;
            }
            if (row.DelayMs > 0)
                await Task.Delay(Math.Min(row.DelayMs, 10_000), ct);
            await ExecInitRowAsync(row);
            ct.ThrowIfCancellationRequested();
            completed++;
            RefreshHeaderContext($"初始化 · {completed}/{InitSeq.Count}");
            if (row.Status == "ERR")
            {
                FocusRow(GridInit, row);
                _lastInitSummary = $"初始化中止 · 第 {completed} 步失败 · Reg {row.Reg}";
                return; // 初始化失败即中止，后续行没有意义
            }
        }
        _lastInitSummary = $"初始化完成 · {completed} 步";
    }

    private async Task<bool> RunExtendedOperationAsync(string action, Func<CancellationToken, Task> operation)
    {
        var cts = new CancellationTokenSource();
        _extOperationCts = cts;
        _extCancellationReason = "用户停止";
        string? finalStatus = null;
        bool completed = false;
        SetExtOperationBusy(true, action);
        Dbg.Log($"I2cPage.RunExtendedOperationAsync: start action={action}");
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            await operation(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            completed = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            finalStatus = $"{action}已停止 · {_extCancellationReason}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", $"{action}停止", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(_extCancellationReason)));
            Dbg.Log($"I2cPage.RunExtendedOperationAsync: cancelled action={action}");
        }
        catch (Exception ex)
        {
            finalStatus = $"{action}失败 · {ex.Message}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", action, "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            Dbg.Log($"I2cPage.RunExtendedOperationAsync: failed action={action} error={ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_extOperationCts, cts)) _extOperationCts = null;
            cts.Dispose();
            SetExtOperationBusy(false);
            if (finalStatus is not null) TxtExtStatus.Text = finalStatus;
            Dbg.Log($"I2cPage.RunExtendedOperationAsync: end action={action}");
        }
        return completed;
    }

    /// <summary>
    /// 单行事务只锁定当前行。_extOperationBusy 仍作为总线互斥锁，但不触发整页可用性与布局刷新。
    /// </summary>
    private async Task<bool> RunLocalRowOperationAsync(string action, RegRow row, Func<Task> operation)
    {
        _extOperationBusy = true;
        _extOperationAction = action;
#if DEBUG
        Dbg.Log($"I2cPage.RunLocalRowOperationAsync: start action={action} reg={row.Reg}");
#endif
        try
        {
            await operation();
            return true;
        }
        catch (Exception ex)
        {
            SetRowError(row, ex.Message);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", action, "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
#if DEBUG
            Dbg.Log($"I2cPage.RunLocalRowOperationAsync: failed action={action} reg={row.Reg} error={ex.Message}");
#endif
            return false;
        }
        finally
        {
            _extOperationBusy = false;
            _extOperationAction = string.Empty;
            // 正常路径只恢复命中测试，不改视觉属性；若事务期间发生连接/输入变化，则同步真实可用状态。
            UpdateInspectorSummaries();
            UpdateOperationAvailability();
            UpdateSpeedControlsEnabled();
#if DEBUG
            Dbg.Log($"I2cPage.RunLocalRowOperationAsync: end action={action} reg={row.Reg}");
#endif
        }
    }

    private void SetExtOperationBusy(bool busy, string action = "")
    {
        _extOperationBusy = busy;
        if (busy && !string.IsNullOrWhiteSpace(action)) _extOperationAction = action;
        else if (!busy) _extOperationAction = string.Empty;
        if (BtnAddRow.IsEnabled == busy) BtnAddRow.IsEnabled = !busy;
        if (BtnSaveSnapshot.IsEnabled == busy) BtnSaveSnapshot.IsEnabled = !busy;
        if (BtnAddInit.IsEnabled == busy) BtnAddInit.IsEnabled = !busy;
        if (BtnSave.IsEnabled == busy) BtnSave.IsEnabled = !busy;
        if (BtnLoad.IsEnabled == busy) BtnLoad.IsEnabled = !busy;
        if (BtnDelete.IsEnabled == busy) BtnDelete.IsEnabled = !busy;
        bool canDeleteReg = !busy && GridReg.SelectedItem is RegRow;
        bool canDeleteInit = !busy && GridInit.SelectedItem is RegRow;
        if (BtnDelRow.IsEnabled != canDeleteReg) BtnDelRow.IsEnabled = canDeleteReg;
        if (BtnDelInit.IsEnabled != canDeleteInit) BtnDelInit.IsEnabled = canDeleteInit;
        RefreshExtendedUi(action);
        UpdateOperationAvailability();
        UpdateSpeedControlsEnabled();
        Dbg.Log($"I2cPage.SetExtOperationBusy: busy={busy} action={action}");
    }

    private void RefreshExtendedUi(string action = "", bool preserveStatus = false)
    {
        if (TxtExtStatus is null) return;
        bool connected = App.Bus.IsOpen;
        bool targetValid = ValidateExtSlave();
        bool rowTaskExists = _rowLoops.Count > 0;
        bool extTaskExists = _extOperationCts is not null;
        bool stopping = _rowLoops.Values.Any(cts => cts.IsCancellationRequested) ||
                        _extOperationCts?.IsCancellationRequested == true;
        string activeAction = !string.IsNullOrWhiteSpace(action) ? action : _extOperationAction;
        bool canExecuteExtended = connected && !_operationBusy && !_extOperationBusy && targetValid;
        if (CanExecuteExtended != canExecuteExtended) CanExecuteExtended = canExecuteExtended;
        foreach (var row in RegTable)
            row.CanExecute = connected && !_operationBusy && targetValid && (!_extOperationBusy || row.Polling);
        foreach (var row in InitSeq)
            row.CanExecute = connected && !_operationBusy && targetValid && !_extOperationBusy;
        if (BtnReadAll.IsEnabled != CanExecuteExtended) BtnReadAll.IsEnabled = CanExecuteExtended;
        if (BtnRunInit.IsEnabled != CanExecuteExtended) BtnRunInit.IsEnabled = CanExecuteExtended;
        Visibility stopVisibility = rowTaskExists || extTaskExists ? Visibility.Visible : Visibility.Hidden;
        bool canStop = (rowTaskExists || extTaskExists) && !stopping;
        string stopText = stopping ? "停止中…" : rowTaskExists ? "停止轮询" : "停止任务";
        if (BtnStopPolling.Visibility != stopVisibility) BtnStopPolling.Visibility = stopVisibility;
        if (BtnStopPolling.IsEnabled != canStop) BtnStopPolling.IsEnabled = canStop;
        if (!Equals(BtnStopPolling.Content, stopText)) BtnStopPolling.Content = stopText;
        string stopToolTip = stopping ? "正在等待当前 I²C 事务结束" : "停止当前任务 (Esc)";
        if (!Equals(BtnStopPolling.ToolTip, stopToolTip)) BtnStopPolling.ToolTip = stopToolTip;
        if (BtnStopInit.Visibility != stopVisibility) BtnStopInit.Visibility = stopVisibility;
        if (BtnStopInit.IsEnabled != canStop) BtnStopInit.IsEnabled = canStop;
        if (!Equals(BtnStopInit.Content, stopText)) BtnStopInit.Content = stopText;
        if (!Equals(BtnStopInit.ToolTip, stopToolTip)) BtnStopInit.ToolTip = stopToolTip;
        bool tablesReadOnly = _operationBusy || _extOperationBusy;
        if (GridReg.IsReadOnly != tablesReadOnly) GridReg.IsReadOnly = tablesReadOnly;
        if (GridInit.IsReadOnly != tablesReadOnly) GridInit.IsReadOnly = tablesReadOnly;
        Visibility regEmpty = RegTable.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility initEmpty = InitSeq.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (TxtRegEmpty.Visibility != regEmpty) TxtRegEmpty.Visibility = regEmpty;
        if (TxtInitEmpty.Visibility != initEmpty) TxtInitEmpty.Visibility = initEmpty;
        UpdateInspectorSummaries();
        UpdateRowActionAvailability();
        string status = !connected ? "未连接：可编辑表格，连接适配器后执行"
            : !targetValid ? "从机地址无效：修正后才能执行扩展操作"
            : stopping ? $"正在停止{activeAction}…"
            : _operationBusy ? "基础事务正在占用总线，请稍候"
            : _extOperationBusy ? $"正在{activeAction}，表格已锁定" : "已连接：可执行读、写和初始化";
        if (!preserveStatus && TxtExtStatus.Text != status) TxtExtStatus.Text = status;
        RefreshHeaderContext();
    }

    private void UpdateInspectorSummaries()
    {
        int pollingCount = RegTable.Count(row => row.Polling);
        int failedRegisters = RegTable.Count(row => row.Status == "ERR");
        int failedInit = InitSeq.Count(row => row.Status == "ERR");
        int invalidRegisters = RegTable.Count(row => row.Status == "INPUT");
        int invalidInit = InitSeq.Count(row => row.Status == "INPUT");
        int changedRegisters = RegTable.Count(row => row.SnapshotChanged);
        string registerSummary = $"寄存器列表 · {RegTable.Count} 项" +
                                 (pollingCount > 0 ? $" · 轮询 {pollingCount}" : string.Empty) +
                                 (changedRegisters > 0 ? $" · 变化 {changedRegisters}" : string.Empty) +
                                 (failedRegisters > 0 ? $" · 失败 {failedRegisters}" : string.Empty) +
                                 (invalidRegisters > 0 ? $" · 输入错误 {invalidRegisters}" : string.Empty);
        string initSummary = $"初始化步骤 · {InitSeq.Count} 项" +
                             (failedInit > 0 ? $" · 失败 {failedInit}" : string.Empty) +
                             (invalidInit > 0 ? $" · 输入错误 {invalidInit}" : string.Empty);
        if (TxtRegSummary.Text != registerSummary) TxtRegSummary.Text = registerSummary;
        if (TxtInitSummary.Text != initSummary) TxtInitSummary.Text = initSummary;
    }

    private void BtnStopPolling_Click(object sender, RoutedEventArgs e)
    {
        CancelExtendedWork();
        RefreshExtendedUi();
        Dbg.Log("I2cPage.BtnStopPolling_Click: requested");
    }

    private void CancelExtendedWork(string reason = "用户停止")
    {
        bool active = _rowLoops.Count > 0 || _extOperationCts is not null;
        bool alreadyRequested = _rowLoops.Values.Any(cts => cts.IsCancellationRequested) ||
                                _extOperationCts?.IsCancellationRequested == true;
        if (active && !alreadyRequested)
            _extCancellationReason = reason;
        foreach (var cts in _rowLoops.Values) cts.Cancel();
        _extOperationCts?.Cancel();
        if (active)
            Dbg.Log($"I2cPage.CancelExtendedWork: loops={_rowLoops.Count} operation={_extOperationAction} reason={reason}");
    }

    // ── Profile ──

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string name = (CmbProfile.Text ?? "").Trim();
        if (name.Length == 0)
        {
            TxtExtStatus.Text = "请输入 Profile 名称后再保存";
            CmbProfile.Focus();
            return;
        }
        if (!ProfileService.IsValidName(name))
        {
            TxtExtStatus.Text = "Profile 名称不能包含文件名禁用字符";
            CmbProfile.Focus();
            return;
        }
        try
        {
            ProfileService.Save(name, RegTable, InitSeq);
            RefreshProfiles();
            CmbProfile.Text = name;
            TxtExtStatus.Text = $"已保存 Profile · {name}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "保存 Profile", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(name)));
            Dbg.Log($"I2cPage.BtnSave_Click: profile={name}");
        }
        catch (Exception ex)
        {
            Dbg.Log($"I2cPage.BtnSave_Click: failed profile={name} error={ex.Message}");
            TxtExtStatus.Text = $"保存 Profile 失败 · {ex.Message}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "保存 Profile", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        string name = (CmbProfile.Text ?? "").Trim();
        if (name.Length == 0)
        {
            TxtExtStatus.Text = "请选择或输入要载入的 Profile 名称";
            CmbProfile.Focus();
            return;
        }
        try
        {
            var p = ProfileService.Load(name);
            if (p is null)
            {
                TxtExtStatus.Text = $"Profile 不存在 · {name}";
                CmbProfile.Focus();
                return;
            }
            // 先完成 Profile 解析和快照；损坏文件不会触碰当前正在编辑的表格。
            var nextRegisters = p.RegTable.ToList();
            var nextInitSequence = p.InitSequence.ToList();
            bool hadPolling = _rowLoops.Count > 0;
            CancelExtendedWork();
            _suspendExtendedRefresh = true;
            try
            {
                RegTable.Clear();
                foreach (var r in nextRegisters) RegTable.Add(r);
                InitSeq.Clear();
                foreach (var r in nextInitSequence) InitSeq.Add(r);
            }
            finally
            {
                _suspendExtendedRefresh = false;
                QueueExtendedRefresh();
            }
            bool hadSnapshot = _registerSnapshot.Count > 0;
            _registerSnapshot.Clear();
            _lastDeletedRegister = null;
            _lastDeletedInit = null;
            TxtSnapshotState.Text = "未保存快照";
            TxtExtStatus.Text = hadPolling
                ? $"已载入 Profile · {name}；已停止之前的周期任务{(hadSnapshot ? "；快照已清除" : string.Empty)}"
                : $"已载入 Profile · {name}{(hadSnapshot ? "；快照已清除" : string.Empty)}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "载入 Profile", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(name)));
            Dbg.Log($"I2cPage.BtnLoad_Click: profile={name} registers={nextRegisters.Count} init={nextInitSequence.Count} stoppedLoops={hadPolling} clearedSnapshot={hadSnapshot}");
        }
        catch (Exception ex)
        {
            Dbg.Log($"I2cPage.BtnLoad_Click: failed profile={name} error={ex.Message}");
            TxtExtStatus.Text = $"载入 Profile 失败 · {ex.Message}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "载入 Profile", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        string name = (CmbProfile.Text ?? "").Trim();
        if (name.Length == 0)
        {
            TxtExtStatus.Text = "请选择要删除的 Profile";
            return;
        }
        if (!CmbProfile.Items.Cast<object>().Any(item => string.Equals(item?.ToString(), name, StringComparison.Ordinal)))
        {
            TxtExtStatus.Text = $"Profile 不存在 · {name}";
            return;
        }
        if (MessageBox.Show($"删除 Profile“{name}”？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Dbg.Log($"I2cPage.BtnDelete_Click: profile={name}");
        try
        {
            ProfileService.Delete(name);
            RefreshProfiles();
            TxtExtStatus.Text = $"已删除 Profile · {name}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "删除 Profile", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(name)));
        }
        catch (Exception ex)
        {
            Dbg.Log($"I2cPage.BtnDelete_Click: failed profile={name} error={ex.Message}");
            TxtExtStatus.Text = $"删除 Profile 失败 · {ex.Message}";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "删除 Profile", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }
}
