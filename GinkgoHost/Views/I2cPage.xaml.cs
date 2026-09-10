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
/// 复刻 Binho Mission Control 的三段式 I2C Command Panel：
/// Settings / Target Device / Transactions。
/// </summary>
public partial class I2cPage : UserControl
{
    private bool _scanning;
    private bool _operationBusy;
    // XAML 加载期 SelectionChanged 就会触发，_loading 初始为 true，RestoreSettings 完成后才放行
    private bool _loading = true;

    public I2cPage()
    {
        InitializeComponent();
        LogView.Init(App.Log);
        GridReg.ItemsSource = RegTable;
        GridInit.ItemsSource = InitSeq;
        Loaded += (_, _) =>
        {
            RestoreSettings();
            RefreshProfiles();
            App.Bus.StateChanged -= RefreshOperationAvailability;
            App.Bus.StateChanged += RefreshOperationAvailability;
            RefreshOperationAvailability();
        };
        Unloaded += (_, _) => App.Bus.StateChanged -= RefreshOperationAvailability;
        ShowExtSub("reg");
    }

    private void RestoreSettings()
    {
        var s = App.Settings;
        _loading = true;
        CmbCtrlMode.SelectedIndex = s.ControlMode == 2 ? 1 : 0;
        RebuildChannelItems();
        // 非标准频率在预设里则选预设，否则开开关
        int idx = Array.IndexOf(new uint[] { 100000, 400000, 1000000, 1200000 }, s.ClockHz);
        if (idx >= 0) { CmbSpeed.SelectedIndex = idx; TglNonStd.IsChecked = false; }
        else { CmbSpeed.SelectedIndex = 1; TglNonStd.IsChecked = true; TxtCustomHz.Text = s.ClockHz.ToString(); }
        CmbAddrFmt.SelectedIndex = Math.Clamp(s.AddrFmt, 0, 1);
        TxtAddr.Text = s.LastAddr;
        TxtSubAddr.Text = s.LastSubAddr;
        double ratio = Math.Clamp(s.LogPanelRatio, 0.3, 0.8);
        LogRow.Height = new GridLength(ratio, GridUnitType.Star);
        DataRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
        UpdateSpeedControlsEnabled();
        _loading = false;
        ValidateTargetInputs();
    }

    private void SaveSettings()
    {
        if (_loading) return;
        App.Settings.Channel = CurrentChannel();
        App.Settings.ClockHz = CurrentHz();
        App.Settings.ControlMode = CurrentCtrlMode();
        App.Settings.LastAddr = TxtAddr.Text;
        App.Settings.LastSubAddr = TxtSubAddr.Text;
        App.Settings.AddrFmt = CmbAddrFmt.SelectedIndex;
        App.Settings.Save();
    }

    private byte CurrentCtrlMode() =>
        CmbCtrlMode.SelectedIndex == 1 ? GinkgoDriver.VII_SCTL_MODE : GinkgoDriver.VII_HCTL_MODE;

    private int CurrentChannel() =>
        CmbChannel.SelectedItem is ComboBoxItem { Tag: string tag } ? int.Parse(tag) : 0;

    /// <summary>硬件模式通道 0–1，软件 I2C 模式为 GPIO 0–7。</summary>
    private void RebuildChannelItems()
    {
        bool sw = CurrentCtrlMode() == GinkgoDriver.VII_SCTL_MODE;
        int max = sw ? 7 : 1;
        int keep = Math.Clamp(App.Settings.Channel, 0, max);
        CmbChannel.Items.Clear();
        for (int i = 0; i <= max; i++)
            CmbChannel.Items.Add(new ComboBoxItem
            {
                Content = sw ? $"GPIO {i}" : $"通道 {i}",
                Tag = i.ToString()
            });
        ((ComboBoxItem)CmbChannel.Items[keep]).IsSelected = true;
    }

    private void UpdateSpeedControlsEnabled()
    {
        bool sw = CurrentCtrlMode() == GinkgoDriver.VII_SCTL_MODE;
        CmbSpeed.IsEnabled = !sw && TglNonStd.IsChecked != true;
        TxtCustomHz.IsEnabled = !sw && TglNonStd.IsChecked == true;
        BtnApplyHz.IsEnabled = !sw && TglNonStd.IsChecked == true;
    }

    private uint CurrentHz()
    {
        if (CurrentCtrlMode() == GinkgoDriver.VII_SCTL_MODE)
            return App.Settings.ClockHz; // 软件 I2C 速率由 GPIO 时序决定
        if (TglNonStd.IsChecked == true)
            return uint.TryParse(TxtCustomHz.Text.Trim(), out uint hz) ? hz : App.Settings.ClockHz;
        return CmbSpeed.SelectedItem is ComboBoxItem { Tag: string tag }
            ? uint.Parse(tag, null)
            : App.Settings.ClockHz;
    }

    // ── Settings 段 ──

    // ── 扩展面板（寄存器表 / 初始化序列，行内执行支持按「周期ms」轮询）──

    public ObservableCollection<RegRow> RegTable { get; } = [];
    public ObservableCollection<RegRow> InitSeq { get; } = [];
    /// <summary>正在轮询的行 → 其取消源。键为引用，DataGrid 行对象生命周期内稳定。</summary>
    private readonly Dictionary<RegRow, CancellationTokenSource> _rowLoops = [];

    private void BtnTabMain_Click(object sender, RoutedEventArgs e) => ShowExtTab(false);
    private void BtnTabExt_Click(object sender, RoutedEventArgs e) => ShowExtTab(true);

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
        ControlColumn.Width = ext
            ? new GridLength(Math.Clamp(App.Settings.ExtPanelWidth, 320, 560))
            : new GridLength(0);
        PanelSplitter.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        BtnTabMain.Appearance = ext ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        BtnTabExt.Appearance = ext ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    private byte ExtSlave7() => Hex.ParseByte(TxtSlave.Text);

    private uint ExtParseReg(string s)
    {
        s = s.Trim().Replace("0x", "").Replace("0X", "");
        return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
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
        BtnDelRow.IsEnabled = GridReg.SelectedItem is RegRow;

    private void GridInit_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BtnDelInit.IsEnabled = GridInit.SelectedItem is RegRow;

    /// <summary>行执行：周期ms=0 单次执行（按读写属性）；&gt;0 点击进入周期轮询，再点停止。</summary>
    private void BtnRowExec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not RegRow row) return;

        // 已在轮询 → 本次点击 = 停止
        if (_rowLoops.TryGetValue(row, out var running))
        {
            running.Cancel();
            _rowLoops.Remove(row);
            btn.Content = "执行";
            return;
        }

        if (row.PeriodMs > 0)
        {
            var cts = new CancellationTokenSource();
            _rowLoops[row] = cts;
            btn.Content = "停止";
            _ = RunRowLoopAsync(row, cts.Token).ContinueWith(
                _ => Dispatcher.Invoke(() => btn.Content = "执行"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            _ = ExecRowOnceAsync(row);
        }
    }

    /// <summary>单次执行：按行读写属性分派（R 读 / W 写）。</summary>
    private async Task ExecRowOnceAsync(RegRow row)
    {
        if (row.Dir == "W")
        {
            byte[] data = Hex.ParseBytes(string.IsNullOrWhiteSpace(row.Value) ? "00" : row.Value);
            var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
            row.Status = r.Ok ? "OK" : "ERR";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "寄存器写", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, data));
        }
        else
        {
            var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), Math.Max(1, row.Len), ExtRegWidth());
            row.Status = r.Ok ? "OK" : "ERR";
            if (r.Ok && r.Data is not null) row.Value = Convert.ToHexString(r.Data);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "寄存器读", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, r.Data));
        }
    }

    /// <summary>行周期轮询循环：按 row.PeriodMs 节奏执行；读在值变化时进日志，写仅在失败时记录。</summary>
    private async Task RunRowLoopAsync(RegRow row, CancellationToken ct)
    {
        using var timer = new System.Threading.PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, row.PeriodMs)));
        string? lastHex = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (row.Dir == "W")
                {
                    byte[] data = Hex.ParseBytes(string.IsNullOrWhiteSpace(row.Value) ? "00" : row.Value);
                    var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                    row.Status = r.Ok ? "OK" : "ERR";
                    if (!r.Ok)
                        App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "周期写", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, data));
                }
                else
                {
                    var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), Math.Max(1, row.Len), ExtRegWidth());
                    row.Status = r.Ok ? "OK" : "ERR";
                    string hex = r.Ok && r.Data is not null ? Convert.ToHexString(r.Data) : "";
                    if (r.Ok && hex != lastHex)
                        App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "周期读", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, r.Data));
                    lastHex = hex;
                }
            }
            catch (Exception ex)
            {
                row.Status = "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "周期读", "—", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
            try { await timer.WaitForNextTickAsync(ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>初始化行执行：按行读写属性分派（R 读校验 / W 写入）。</summary>
    private async void BtnInitRowExec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        await ExecInitRowAsync(row);
    }

    private async Task ExecInitRowAsync(RegRow row)
    {
        try
        {
            if (row.Dir == "R")
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), Math.Max(1, row.Len), ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                if (r.Ok && r.Data is not null) row.Value = Convert.ToHexString(r.Data);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "初始化读", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, r.Data));
            }
            else
            {
                byte[] data = Hex.ParseBytes(string.IsNullOrWhiteSpace(row.Value) ? "00" : row.Value);
                var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "初始化写", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, data));
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
        // 只读取方向为 R 的行；W 行由行内「执行」显式写入
        foreach (var row in RegTable.Where(r => r.Dir == "R"))
        {
            try
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), Math.Max(1, row.Len), ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                if (r.Ok && r.Data is not null) row.Value = Convert.ToHexString(r.Data);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "读全部", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, r.Data));
            }
            catch (Exception ex)
            {
                row.Status = "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "读全部", "—", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
        }
    }

    private async void BtnRunInit_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in InitSeq)
        {
            if (row.DelayMs > 0)
                await Task.Delay(Math.Min(row.DelayMs, 10_000));
            try
            {
                byte[] data = Hex.ParseBytes(string.IsNullOrWhiteSpace(row.Value) ? "00" : row.Value);
                var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "初始化", $"0x{ExtSlave7():X2}", r.Ret, r.Ms, data));
                if (!r.Ok) break; // 初始化失败即中止，后续行没有意义
            }
            catch (Exception ex)
            {
                row.Status = "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "初始化", "—", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
                break;
            }
        }
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

    // ── Profile ──

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string name = (CmbProfile.Text ?? "").Trim();
        if (name.Length == 0) { MessageBox.Show("输入或选择 Profile 名"); return; }
        ProfileService.Save(name, RegTable, InitSeq);
        RefreshProfiles();
        CmbProfile.Text = name;
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfile.SelectedItem is not string name) { MessageBox.Show("先从下拉选择 Profile"); return; }
        var p = ProfileService.Load(name);
        if (p is null) { MessageBox.Show("Profile 不存在"); return; }
        RegTable.Clear();
        foreach (var r in p.RegTable) RegTable.Add(r);
        InitSeq.Clear();
        foreach (var r in p.InitSequence) InitSeq.Add(r);
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfile.SelectedItem is not string name) return;
        if (MessageBox.Show($"删除 Profile“{name}”？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Dbg.Log($"I2cPage.BtnDelete_Click: profile={name}");
        ProfileService.Delete(name);
        RefreshProfiles();
    }

    private async void CmbCtrlMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RebuildChannelItems();
        UpdateSpeedControlsEnabled();
        SaveSettings();
        await App.Bus.ApplyConfigAsync(App.Settings.Channel, CurrentHz(), CurrentCtrlMode());
    }

    private async void CmbSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SaveSettings();
        await App.Bus.ApplyConfigAsync(App.Settings.Channel, CurrentHz(), CurrentCtrlMode());
    }

    private async void CmbChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SaveSettings();
        await App.Bus.ApplyConfigAsync(App.Settings.Channel, CurrentHz(), CurrentCtrlMode());
    }

    private void TglNonStd_Changed(object sender, RoutedEventArgs e) => UpdateSpeedControlsEnabled();

    private async void BtnApplyHz_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        int ret = await App.Bus.ApplyConfigAsync(App.Settings.Channel, CurrentHz(), CurrentCtrlMode());
        App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", $"速率切换 {CurrentHz() / 1000} kHz", "—", ret, 0,
            ret == 0 ? null : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
    }

    // ── Target Device 段 ──

    private async void BtnScan_Click(object sender, RoutedEventArgs e) => await ScanBusNow();

    private void SetOperationBusy(bool busy, string action = "")
    {
        _operationBusy = busy;
        BtnScanBus.Content = busy && action == "scan" ? "扫描中…" : "扫描总线";
        BtnRead.Content = busy && action == "read" ? "读取中…" : "读取";
        BtnWrite.Content = busy && action == "write" ? "写入中…" : "写入";
        UpdateOperationAvailability();
    }

    private void RefreshOperationAvailability()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshOperationAvailability);
            return;
        }

        UpdateOperationAvailability();
        if (_operationBusy || TxtDataStatus is null) return;
        if (!App.Bus.IsOpen)
        {
            TxtDataStatus.Text = "请先在左侧工作区连接适配器";
            TxtDataStatus.ClearValue(TextBlock.ForegroundProperty);
        }
        else if (TxtDataStatus.Text == "请先在左侧工作区连接适配器")
        {
            TxtDataStatus.Text = "已连接，可以扫描或读写";
        }
        Dbg.Log($"I2cPage.RefreshOperationAvailability: connected={App.Bus.IsOpen}");
    }

    private void UpdateOperationAvailability()
    {
        if (BtnScanBus is null || BtnRead is null || BtnWrite is null) return;
        bool connected = App.Bus.IsOpen && !_operationBusy;
        bool targetValid = IsTargetValid();
        BtnScanBus.IsEnabled = connected;
        BtnRead.IsEnabled = connected && targetValid && IsReadSizeValid();
        BtnWrite.IsEnabled = connected && targetValid && IsBufferValid();
        string? disconnectedTip = App.Bus.IsOpen ? null : "请先在左侧工作区连接适配器";
        BtnScanBus.ToolTip = disconnectedTip ?? "扫描当前通道上的从机地址 (F5)";
        BtnRead.ToolTip = disconnectedTip ?? "读取数据 (Ctrl+R)";
        BtnWrite.ToolTip = disconnectedTip ?? "写入数据 (Ctrl+W)";
    }

    private async Task ScanBusNow()
    {
        if (_scanning || !BtnScanBus.IsEnabled) return;
        _scanning = true;
        SetOperationBusy(true, "scan");
        Dbg.Log($"I2cPage.ScanBusNow: start channel={App.Settings.Channel}");
        try
        {
            var sw = Stopwatch.StartNew();
            var found = await App.Bus.ScanBusAsync();
            sw.Stop();
            if (found.Count == 0)
            {
                // 当前通道无命中时自动扫另一通道，避免从机接在别的通道上干等
                int other = 1 - App.Settings.Channel;
                var otherFound = await App.Bus.ScanBusAsync(channel: other);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", $"通道{other}", 0,
                    sw.Elapsed.TotalMilliseconds, otherFound.ToArray()));
            }
            else
            {
                if (found.Count == 1)
                {
                    int displayAddress = CmbAddrFmt.SelectedIndex == 1 ? found[0] << 1 : found[0];
                    TxtAddr.Text = $"{displayAddress:X2}";
                    Dbg.Log($"I2cPage.ScanBusNow: auto selected 0x{found[0]:X2}");
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", "—", 0, sw.Elapsed.TotalMilliseconds, found.ToArray()));
            }
        }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally
        {
            _scanning = false;
            SetOperationBusy(false);
            Dbg.Log("I2cPage.ScanBusNow: end");
        }
    }

    private void TxtAddr_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateTargetInputs();
        ResetDataHint();
        SaveSettings();
    }

    private void TxtSubAddr_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateTargetInputs();
        ResetDataHint();
        SaveSettings();
    }

    private void CmbAddrFmt_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // 地址文本随格式换算：7 位 v ↔ 8 位 v<<1（8 位含读写位，右移时读写位丢弃）
        if (Hex.TryParseByte(TxtAddr.Text, out var v))
        {
            byte nv = CmbAddrFmt.SelectedIndex == 1 ? (v <= 0x7F ? (byte)(v << 1) : v) : (byte)(v >> 1);
            TxtAddr.Text = $"{nv:X2}";
        }
        ValidateTargetInputs();
        ResetDataHint();
        SaveSettings();
    }

    private bool IsTargetValid()
    {
        bool addressValid = Hex.TryParseByte(TxtAddr.Text, out var value) &&
                            (CmbAddrFmt?.SelectedIndex == 1 || value <= 0x7F);
        bool registerValid = string.IsNullOrWhiteSpace(TxtSubAddr.Text) ||
                             Hex.TryParseByte(TxtSubAddr.Text, out _);
        return addressValid && registerValid;
    }

    private void ValidateTargetInputs()
    {
        if (TxtAddr is null || TxtSubAddr is null) return;
        bool addressValid = Hex.TryParseByte(TxtAddr.Text, out var value) &&
                            (CmbAddrFmt?.SelectedIndex == 1 || value <= 0x7F);
        bool registerValid = string.IsNullOrWhiteSpace(TxtSubAddr.Text) ||
                             Hex.TryParseByte(TxtSubAddr.Text, out _);
        if (addressValid) TxtAddr.ClearValue(Control.BorderBrushProperty);
        else TxtAddr.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
        if (registerValid) TxtSubAddr.ClearValue(Control.BorderBrushProperty);
        else TxtSubAddr.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
        UpdateOperationAvailability();
    }

    private void ResetDataHint()
    {
        if (_loading || _operationBusy || TxtDataStatus is null) return;
        TxtDataStatus.Text = App.Bus.IsOpen
            ? "参数已更新，可以读取或写入"
            : "请先在左侧工作区连接适配器";
        TxtDataStatus.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>用户输入按 Address Format 折算成 7 位地址。8 位输入右移一位。</summary>
    private byte? TargetAddr7()
    {
        byte raw = Hex.ParseByte(TxtAddr.Text);
        return CmbAddrFmt.SelectedIndex == 1 ? (byte)(raw >> 1) : raw;
    }

    private byte SubAddr() => Hex.ParseByte(string.IsNullOrWhiteSpace(TxtSubAddr.Text) ? "00" : TxtSubAddr.Text);

    // ── Transactions 段 ──

    private async void BtnWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || !BtnWrite.IsEnabled) return;
        SetOperationBusy(true, "write");
        Dbg.Log($"I2cPage.BtnWrite_Click: addr={TxtAddr.Text} reg={TxtSubAddr.Text}");
        try
        {
            byte addr = TargetAddr7() ?? throw new ArgumentException("地址为空");
            byte[] data = Hex.ParseBytes(TxtDataBuffer.Text);
            if (data.Length == 0) throw new ArgumentException("数据缓冲区为空");
            bool hasSub = !string.IsNullOrWhiteSpace(TxtSubAddr.Text);
            var r = hasSub
                ? await App.Bus.WriteRegisterAsync(addr, SubAddr(), data)
                : await App.Bus.RawWriteAsync(addr, data);
            TxtDataStatus.Text = r.Ok
                ? $"写入成功 · {data.Length} 字节 · {r.Ms:F1} ms"
                : $"写入失败 · {GinkgoDriver.ErrorName(r.Ret)}";
            TxtDataStatus.Foreground = r.Ok
                ? new SolidColorBrush(Color.FromRgb(0x81, 0xc7, 0x84))
                : new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "WRITE", $"0x{addr:X2}", r.Ret, r.Ms, data));

            // 写后读取：写入成功后自动读回验证（同长度），结果进数据显示区
            if (r.Ok && ChkWriteRead.IsChecked == true)
            {
                int blen = Math.Min(Math.Max(data.Length, 1), 256);
                var rb = hasSub
                    ? await App.Bus.ReadRegisterAsync(addr, SubAddr(), blen)
                    : await App.Bus.RawReadAsync(addr, blen);
                if (rb.Ok && rb.Data is not null)
                    TxtDataBuffer.Text = BufferText(rb.Data);
                TxtDataStatus.Text = rb.Ok
                    ? $"写入并回读成功 · {blen} 字节 · {rb.Ms:F1} ms"
                    : $"写后读取失败 · {GinkgoDriver.ErrorName(rb.Ret)}";
                TxtDataStatus.Foreground = rb.Ok
                    ? new SolidColorBrush(Color.FromRgb(0x81, 0xc7, 0x84))
                    : new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "写后读", $"0x{addr:X2}", rb.Ret, rb.Ms, rb.Data));
            }
        }
        catch (Exception ex)
        {
            TxtDataStatus.Text = ex.Message;
            TxtDataStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "WRITE", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally { SetOperationBusy(false); }
    }

    private async void BtnRead_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || !BtnRead.IsEnabled) return;
        SetOperationBusy(true, "read");
        Dbg.Log($"I2cPage.BtnRead_Click: addr={TxtAddr.Text} reg={TxtSubAddr.Text} len={TxtReadSize.Text}");
        try
        {
            byte addr = TargetAddr7() ?? throw new ArgumentException("地址为空");
            int len = int.Parse(TxtReadSize.Text.Trim(), null);
            if (len is < 1 or > 256) throw new ArgumentException("Read Size 须在 1–256 之间");
            bool hasSub = !string.IsNullOrWhiteSpace(TxtSubAddr.Text);
            var r = hasSub
                ? await App.Bus.ReadRegisterAsync(addr, SubAddr(), len)
                : await App.Bus.RawReadAsync(addr, len);
            if (r.Ok && r.Data is not null)
                TxtDataBuffer.Text = BufferText(r.Data);
            TxtDataStatus.Text = r.Ok
                ? $"读取成功 · {r.Data?.Length ?? 0} 字节 · {r.Ms:F1} ms"
                : $"读取失败 · {GinkgoDriver.ErrorName(r.Ret)}";
            TxtDataStatus.Foreground = r.Ok
                ? new SolidColorBrush(Color.FromRgb(0x81, 0xc7, 0x84))
                : new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "READ", $"0x{addr:X2}", r.Ret, r.Ms, r.Data));
        }
        catch (Exception ex)
        {
            TxtDataStatus.Text = ex.Message;
            TxtDataStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "READ", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally { SetOperationBusy(false); }
    }

    private bool IsBufferValid()
    {
        try { return Hex.ParseBytes(TxtDataBuffer.Text).Length > 0; }
        catch { return false; }
    }

    private bool IsReadSizeValid() =>
        int.TryParse(TxtReadSize.Text, out int len) && len is >= 1 and <= 256;

    private void TxtDataBuffer_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtBufferMeta is null) return;
        try
        {
            int count = Hex.ParseBytes(TxtDataBuffer.Text).Length;
            TxtBufferMeta.Text = $"{count} 字节";
            TxtDataBuffer.ClearValue(Control.BorderBrushProperty);
            if (!_operationBusy && BtnWrite is not null) BtnWrite.IsEnabled = count > 0;
        }
        catch
        {
            TxtBufferMeta.Text = "HEX 格式错误";
            TxtDataBuffer.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            if (!_operationBusy && BtnWrite is not null) BtnWrite.IsEnabled = false;
        }
        ResetDataHint();
        UpdateOperationAvailability();
    }

    private void TxtReadSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool valid = IsReadSizeValid();
        if (valid) TxtReadSize.ClearValue(Control.BorderBrushProperty);
        else TxtReadSize.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
        ResetDataHint();
        UpdateOperationAvailability();
    }

    private void I2cPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_operationBusy) return;
        if (e.Key == Key.F5 && BtnScanBus.IsEnabled)
            BtnScan_Click(BtnScanBus, new RoutedEventArgs());
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R && BtnRead.IsEnabled)
            BtnRead_Click(BtnRead, new RoutedEventArgs());
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W && BtnWrite.IsEnabled)
            BtnWrite_Click(BtnWrite, new RoutedEventArgs());
        else
            return;
        e.Handled = true;
    }

    internal static string BufferText(byte[] data) =>
        string.Join(" ", Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));

    internal static string Grouped(byte[] data) =>
        "0x" + string.Join(".", Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));

    /// <summary>双击分隔条：扩展工具恢复默认宽度。</summary>
    private void Splitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.GridSplitter { Parent: Grid grid })
        {
            grid.ColumnDefinitions[0].Width = new GridLength(480);
            App.Settings.ExtPanelWidth = 480;
            App.Settings.Save();
            Dbg.Log("I2cPage.Splitter_DoubleClick: reset extension width to 480");
        }
    }

    private void LogSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not GridSplitter { Parent: Grid grid }) return;
        grid.RowDefinitions[0].Height = new GridLength(13, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(7, GridUnitType.Star);
        App.Settings.LogPanelRatio = 0.65;
        App.Settings.Save();
        Dbg.Log("I2cPage.LogSplitter_DoubleClick: reset rows to 65/35");
        e.Handled = true;
    }

    private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        App.Settings.ExtPanelWidth = Math.Clamp(ControlColumn.ActualWidth, 320, 560);
        App.Settings.Save();
        Dbg.Log($"I2cPage.PanelSplitter_DragCompleted: width={App.Settings.ExtPanelWidth:F0}");
    }

    private void LogSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        double total = LogRow.ActualHeight + DataRow.ActualHeight;
        if (total <= 0) return;
        App.Settings.LogPanelRatio = Math.Clamp(LogRow.ActualHeight / total, 0.3, 0.8);
        App.Settings.Save();
        Dbg.Log($"I2cPage.LogSplitter_DragCompleted: ratio={App.Settings.LogPanelRatio:F2}");
    }
}

/// <summary>RegRow.Dir("R"/"W") 与 ComboBox 索引互转。</summary>
public sealed class DirIndexConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value as string == "W" ? 1 : 0;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is int i && i == 1 ? "W" : "R";
}

/// <summary>hex 输入解析：地址 "50"/"0x50"，数据 "DE AD"/"DE.AD.BE"，兼容逗号/分号分隔。</summary>
internal static class Hex
{
    public static byte ParseByte(string s)
    {
        s = s.Trim().Replace("0x", "").Replace("0X", "");
        return byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static bool TryParseByte(string s, out byte v)
    {
        s = s.Trim().Replace("0x", "").Replace("0X", "");
        return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    public static byte[] ParseBytes(string s) =>
        s.Split(new[] { ' ', ',', ';', '.', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
         .Select(ParseByte)
         .ToArray();
}
