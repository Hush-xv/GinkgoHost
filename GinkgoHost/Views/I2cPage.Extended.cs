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
    private string? _lastPollSummary;
    private readonly Dictionary<string, string> _registerSnapshot = [];
    private string? _lastReadAllSummary;

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
        ExtPanel.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        ControlColumn.MinWidth = ext ? 320 : 0;
        ControlColumn.MaxWidth = _wideLayout ? 720 : 560;
        ControlColumn.Width = ext
            ? new GridLength(Math.Clamp(App.Settings.ExtPanelWidth, 320, ControlColumn.MaxWidth))
            : new GridLength(0);
        PanelSplitter.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        BtnTabMain.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnTabExt.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        // 模式 Tab 用「深色底 + 橙字 + 橙色底边」表达选中，不与写入等执行按钮争抢 Primary
        SetModeTab(BtnTabMain, active: !ext);
        SetModeTab(BtnTabExt, active: ext);
        // 切换扩展面板会改变可用列宽；等待布局计算后再决定命令栏的单/双行。
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

    private byte ExtRegWidth() =>
        CmbRegWidth.SelectedIndex == 1 ? GinkgoDriver.VII_SUB_ADDR_2BYTE : GinkgoDriver.VII_SUB_ADDR_1BYTE;

    private void RefreshProfiles()
    {
        CmbProfile.Items.Clear();
        foreach (var p in ProfileService.List())
            CmbProfile.Items.Add(p);
        if (CmbProfile.Items.Count > 0) CmbProfile.SelectedIndex = 0;
    }

    private void BtnAddRow_Click(object sender, RoutedEventArgs e) => RegTable.Add(new RegRow());
    private void BtnAddInit_Click(object sender, RoutedEventArgs e) => InitSeq.Add(new RegRow());

    private void BtnSaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_extOperationBusy) return;
        CommitGridEdit(GridReg);
        _registerSnapshot.Clear();
        // 快照是一次完整基线：清除所有旧标记，不能让已删值或写入行保留过期高亮。
        foreach (var row in RegTable) row.SnapshotChanged = false;
        foreach (var row in RegTable.Where(row => row.Dir == "R" && row.Status == "OK" && !string.IsNullOrWhiteSpace(row.Value)))
        {
            _registerSnapshot[SnapshotKey(row)] = row.Value;
        }
        TxtExtStatus.Text = _registerSnapshot.Count == 0
            ? "没有可保存的读取值；先执行“一键读取全部”"
            : $"已保存快照 · {_registerSnapshot.Count} 个寄存器";
        App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "保存寄存器快照", "—", 0, 0,
            System.Text.Encoding.UTF8.GetBytes($"{_registerSnapshot.Count} 个寄存器")));
        Dbg.Log($"I2cPage.BtnSaveSnapshot_Click: entries={_registerSnapshot.Count}");
    }

    private static string SnapshotKey(RegRow row) =>
        $"{row.Reg.Trim().ToUpperInvariant()}|{row.Len}|{row.Dir}";

    private void BtnDelRow_Click(object sender, RoutedEventArgs e)
    {
        if (GridReg.SelectedItem is not RegRow r) return;
        Dbg.Log($"I2cPage.BtnDelRow_Click: reg={r.Reg}");
        RegTable.Remove(r);
    }

    private void BtnDelInit_Click(object sender, RoutedEventArgs e)
    {
        if (GridInit.SelectedItem is not RegRow r) return;
        Dbg.Log($"I2cPage.BtnDelInit_Click: reg={r.Reg}");
        InitSeq.Remove(r);
    }

    private void GridReg_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BtnDelRow.IsEnabled = !_extOperationBusy && GridReg.SelectedItem is RegRow;

    private void GridInit_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BtnDelInit.IsEnabled = !_extOperationBusy && GridInit.SelectedItem is RegRow;

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

    /// <summary>行执行：周期ms=0 单次执行（按读写属性）；&gt;0 点击进入周期轮询，再点停止。</summary>
    private async void BtnRowExec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        CommitGridEdit(GridReg);

        // 已在轮询 → 本次点击 = 停止。取消源保留到循环 finally，避免停止过程中再次启动第二个循环。
        if (_rowLoops.TryGetValue(row, out var running))
        {
            running.Cancel();
            row.ExecuteText = "停止中";
            row.ExecuteToolTip = "正在停止此行周期轮询";
            return;
        }

        if (_extOperationBusy) return;
        if (row.PeriodMs > 0)
        {
            var cts = new CancellationTokenSource();
            _rowLoops[row] = cts;
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
            await RunExtendedOperationAsync("行执行", () => ExecRowOnceAsync(row));
            TxtExtStatus.Text = row.Status == "OK"
                ? $"行执行完成 · Reg {row.Reg}"
                : $"行执行失败 · Reg {row.Reg}，请查看事务日志";
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
                row.Status = r.Ok ? "OK" : "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", RegisterOperation("寄存器写", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, data));
            }
            else
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                if (r.Ok && r.Data is not null) row.Value = BufferText(r.Data);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("寄存器读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
            }
        }
        catch (Exception ex)
        {
            // 单行异常必须回写本行，不能把上一轮 OK 留在屏幕上。
            row.Status = "ERR";
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
                    row.Status = r.Ok ? "OK" : "ERR";
                    App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", RegisterOperation($"周期写 #{iteration}", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, data));
                }
                else
                {
                    var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                    row.Status = r.Ok ? "OK" : "ERR";
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
                row.Status = "ERR";
                bool writeRow = row.Dir == "W";
                App.Log.AddCapped(new LogEntry(DateTime.Now, writeRow ? "TX" : "RX",
                    RegisterOperation($"{(writeRow ? "周期写" : "周期读")} #{iteration}", row), "—", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
            row.PollCount = iteration;
            TxtExtStatus.Text = $"周期轮询 · Reg {row.Reg} · 第 {iteration} 次 · {row.Status}";
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
            : $"周期轮询已停止 · Reg {row.Reg} · 共 {row.PollCount} 次 · 最后结果 {row.Status}";
        if (!autoStopped)
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "周期轮询停止", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(_lastPollSummary)));
        Dbg.Log($"I2cPage.RunRowLoopAsync: stopped reg={row.Reg} polls={row.PollCount}");
    }

    /// <summary>初始化行执行：按行读写属性分派（R 读校验 / W 写入）。</summary>
    private async void BtnInitRowExec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        CommitGridEdit(GridInit);
        if (_extOperationBusy) return;
        await RunExtendedOperationAsync("初始化行", () => ExecInitRowAsync(row));
        TxtExtStatus.Text = row.Status == "OK"
            ? $"初始化行完成 · Reg {row.Reg}"
            : $"初始化行失败 · Reg {row.Reg}，请查看事务日志";
    }

    private async Task ExecInitRowAsync(RegRow row)
    {
        try
        {
            if (row.Dir == "R")
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                if (r.Ok && r.Data is not null) row.Value = BufferText(r.Data);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("初始化读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
            }
            else
            {
                byte[] data = ExtWriteData(row);
                var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", RegisterOperation("初始化写", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, data));
            }
        }
        catch (Exception ex)
        {
            row.Status = "ERR";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "初始化", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    private async void BtnReadAll_Click(object sender, RoutedEventArgs e)
    {
        if (_extOperationBusy) return;
        CommitGridEdit(GridReg);
        _lastReadAllSummary = null;
        await RunExtendedOperationAsync("一键读取", ReadAllAsync);
        if (_lastReadAllSummary is not null) TxtExtStatus.Text = _lastReadAllSummary;
    }

    private async Task ReadAllAsync()
    {
        // 只读取方向为 R 的行；W 行由行内「执行」显式写入
        int compared = 0, changed = 0;
        foreach (var row in RegTable.Where(r => r.Dir == "R"))
        {
            try
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                if (r.Ok && r.Data is not null)
                {
                    row.Value = BufferText(r.Data);
                    if (_registerSnapshot.TryGetValue(SnapshotKey(row), out string? baseline))
                    {
                        compared++;
                        row.SnapshotChanged = !string.Equals(baseline, row.Value, StringComparison.OrdinalIgnoreCase);
                        if (row.SnapshotChanged) changed++;
                    }
                    else row.SnapshotChanged = false;
                }
                else
                {
                    // 驱动错误同异常一样没有新数据，不能保留旧轮次的变化结论。
                    row.SnapshotChanged = false;
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("寄存器读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
            }
            catch (Exception ex)
            {
                row.Status = "ERR";
                // 失败没有新数据可比；移除上一轮的变化标记，避免把旧结果当作本轮证据。
                row.SnapshotChanged = false;
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "读全部", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
        }
        if (_registerSnapshot.Count == 0)
            _lastReadAllSummary = "读取完成 · 尚未保存快照";
        else
        {
            _lastReadAllSummary = $"读取完成 · 对比 {compared} 项 · 变化 {changed} 项";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "寄存器快照对比", "—", 0, 0,
                System.Text.Encoding.UTF8.GetBytes(_lastReadAllSummary)));
        }
    }

    private async void BtnRunInit_Click(object sender, RoutedEventArgs e)
    {
        if (_extOperationBusy) return;
        if (MessageBox.Show("初始化序列可能包含写入操作。确认继续执行？", "确认初始化",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            TxtExtStatus.Text = "已取消初始化";
            return;
        }
        await RunExtendedOperationAsync("一键初始化", RunInitSequenceAsync);
    }

    private async Task RunInitSequenceAsync()
    {
        foreach (var row in InitSeq)
        {
            if (row.DelayMs > 0)
                await Task.Delay(Math.Min(row.DelayMs, 10_000));
            await ExecInitRowAsync(row);
            if (row.Status == "ERR") break; // 初始化失败即中止，后续行没有意义
        }
    }

    private async Task RunExtendedOperationAsync(string action, Func<Task> operation)
    {
        SetExtOperationBusy(true, action);
        Dbg.Log($"I2cPage.RunExtendedOperationAsync: start action={action}");
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", action, "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            Dbg.Log($"I2cPage.RunExtendedOperationAsync: failed action={action} error={ex.Message}");
        }
        finally
        {
            SetExtOperationBusy(false);
            Dbg.Log($"I2cPage.RunExtendedOperationAsync: end action={action}");
        }
    }

    private void SetExtOperationBusy(bool busy, string action = "")
    {
        _extOperationBusy = busy;
        BtnAddRow.IsEnabled = !busy;
        BtnReadAll.IsEnabled = !busy;
        BtnSaveSnapshot.IsEnabled = !busy;
        BtnAddInit.IsEnabled = !busy;
        BtnRunInit.IsEnabled = !busy;
        BtnSave.IsEnabled = !busy;
        BtnLoad.IsEnabled = !busy;
        BtnDelete.IsEnabled = !busy;
        BtnDelRow.IsEnabled = !busy && GridReg.SelectedItem is RegRow;
        BtnDelInit.IsEnabled = !busy && GridInit.SelectedItem is RegRow;
        RefreshExtendedUi(action);
        Dbg.Log($"I2cPage.SetExtOperationBusy: busy={busy} action={action}");
    }

    private void RefreshExtendedUi(string action = "")
    {
        if (TxtExtStatus is null) return;
        bool connected = App.Bus.IsOpen;
        bool targetValid = ValidateExtSlave();
        CanExecuteExtended = connected && !_extOperationBusy && targetValid;
        foreach (var row in RegTable)
            row.CanExecute = connected && targetValid && (!_extOperationBusy || row.Polling);
        foreach (var row in InitSeq)
            row.CanExecute = connected && targetValid && !_extOperationBusy;
        BtnReadAll.IsEnabled = CanExecuteExtended;
        BtnRunInit.IsEnabled = CanExecuteExtended;
        BtnStopPolling.Visibility = _rowLoops.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnStopPolling.IsEnabled = _rowLoops.Count > 0;
        GridReg.IsReadOnly = _extOperationBusy;
        GridInit.IsReadOnly = _extOperationBusy;
        TxtRegEmpty.Visibility = RegTable.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtInitEmpty.Visibility = InitSeq.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtExtStatus.Text = !connected ? "未连接：可编辑表格，连接适配器后执行"
            : !targetValid ? "从机地址无效：修正后才能执行扩展操作"
            : _extOperationBusy ? $"正在{action}，表格已锁定" : "已连接：可执行读、写和初始化";
    }

    private void BtnStopPolling_Click(object sender, RoutedEventArgs e)
    {
        CancelExtendedWork();
        Dbg.Log("I2cPage.BtnStopPolling_Click: requested");
    }

    private void CancelExtendedWork()
    {
        foreach (var cts in _rowLoops.Values) cts.Cancel();
        if (_rowLoops.Count > 0)
            Dbg.Log($"I2cPage.CancelExtendedWork: loops={_rowLoops.Count}");
    }

    // ── Profile ──

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string name = (CmbProfile.Text ?? "").Trim();
        if (name.Length == 0) { MessageBox.Show("输入或选择 Profile 名"); return; }
        if (!ProfileService.IsValidName(name)) { MessageBox.Show("Profile 名不能含有文件名禁用字符"); return; }
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
            TxtExtStatus.Text = "保存 Profile 失败，详情见提示";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "保存 Profile", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            MessageBox.Show($"保存 Profile 失败：{ex.Message}");
        }
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfile.SelectedItem is not string name) { MessageBox.Show("先从下拉选择 Profile"); return; }
        try
        {
            var p = ProfileService.Load(name);
            if (p is null) { MessageBox.Show("Profile 不存在"); return; }
            // 先完成 Profile 解析和快照；损坏文件不会触碰当前正在编辑的表格。
            var nextRegisters = p.RegTable.ToList();
            var nextInitSequence = p.InitSequence.ToList();
            bool hadPolling = _rowLoops.Count > 0;
            CancelExtendedWork();
            RegTable.Clear();
            foreach (var r in nextRegisters) RegTable.Add(r);
            InitSeq.Clear();
            foreach (var r in nextInitSequence) InitSeq.Add(r);
            bool hadSnapshot = _registerSnapshot.Count > 0;
            _registerSnapshot.Clear();
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
            TxtExtStatus.Text = "载入 Profile 失败，详情见提示";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "载入 Profile", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            MessageBox.Show($"载入 Profile 失败：{ex.Message}");
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfile.SelectedItem is not string name) return;
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
            TxtExtStatus.Text = "删除 Profile 失败，详情见提示";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "删除 Profile", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            MessageBox.Show($"删除 Profile 失败：{ex.Message}");
        }
    }
}
