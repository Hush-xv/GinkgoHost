using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    private bool _suppressScan; // chip 点击程序化聚焦地址框时，抑制 GotFocus 的自动扫描
    private List<byte> _lastHits = new();
    private CancellationTokenSource? _extPeriodCts;
    private bool _extPeriodRunning;
    private static readonly Brush DotOn = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush DotOff = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));
    // XAML 加载期 SelectionChanged 就会触发，_loading 初始为 true，RestoreSettings 完成后才放行
    private bool _loading = true;

    public I2cPage()
    {
        InitializeComponent();
        LogView.Init(App.Log);
        GridReg.ItemsSource = RegTable;
        GridInit.ItemsSource = InitSeq;
        App.Bus.StateChanged += RefreshConn;
        Loaded += (_, _) =>
        {
            RefreshConn();
            RestoreSettings();
            RefreshProfiles();
        };
        Unloaded += (_, _) => App.Bus.StateChanged -= RefreshConn;
        ShowExtSub("reg");
    }

    /// <summary>紧凑连接状态刷新（左栏卡片）。</summary>
    private void RefreshConn()
    {
        Dispatcher.Invoke(() =>
        {
            bool on = App.Bus.IsOpen;
            DotConn.Fill = on ? DotOn : DotOff;
            if (on)
            {
                string bus = App.Bus.ControlMode == GinkgoDriver.VII_SCTL_MODE
                    ? "软件 I2C"
                    : $"{App.Bus.ClockHz / 1000} kHz";
                TxtConnMain.Text = "已连接";
                TxtConnSub.Text = $"通道 {App.Bus.Channel} · {bus}";
            }
            else
            {
                TxtConnMain.Text = "未连接";
                TxtConnSub.Text = "点击「连接」";
            }
            BtnConn.IsEnabled = !on;
            BtnDisc.IsEnabled = on;
        });
    }

    private async void BtnConn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (count, ret) = await App.Bus.ConnectAsync(
                App.Settings.Channel, App.Settings.ClockHz, (byte)App.Settings.ControlMode);
            if (count <= 0)
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", ret, 0, null));
            else if (ret != 0)
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", ret, 0,
                    System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
        }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "连接适配器", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    private async void BtnDisc_Click(object sender, RoutedEventArgs e)
    {
        await App.Bus.CloseAsync();
        App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "断开适配器", "—", 0, 0, null));
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
        TxtAddr.Text = s.LastAddr;
        TxtSubAddr.Text = s.LastSubAddr;
        CmbAddrFmt.SelectedIndex = Math.Clamp(s.AddrFmt, 0, 1);
        UpdateSpeedControlsEnabled();
        _loading = false;
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
        MainScroll.Visibility = ext ? Visibility.Collapsed : Visibility.Visible;
        ExtPanel.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
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
        if (GridReg.SelectedItem is RegRow r) RegTable.Remove(r);
    }

    private void BtnDelInit_Click(object sender, RoutedEventArgs e)
    {
        if (GridInit.SelectedItem is RegRow r) InitSeq.Remove(r);
    }

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

    private async void TxtAddr_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_scanning || _suppressScan) return;
        await ScanBusNow();
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e) => await ScanBusNow();

    private async Task ScanBusNow()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var sw = Stopwatch.StartNew();
            ChipPanel.Children.Clear(); // 清掉上一轮的地址 chip
            TxtScanResult.Text = "扫描中… 0/112";
            // 流式刷新：进度实时更新，命中的地址立即出 chip，不等服务结束
            var found = await App.Bus.ScanBusAsync(
                progress: (done, _) => Dispatcher.Invoke(() =>
                    TxtScanResult.Text = $"扫描中… {done}/112"),
                hit: addr => Dispatcher.Invoke(() => ChipPanel.Children.Add(MakeChip(addr))));
            _lastHits = found;
            sw.Stop();
            if (found.Count == 0)
            {
                // 当前通道无命中时自动扫另一通道，避免从机接在别的通道上干等
                int other = 1 - App.Settings.Channel;
                var otherFound = await App.Bus.ScanBusAsync(channel: other);
                TxtScanResult.Text = otherFound.Count == 0
                    ? $"通道 {App.Settings.Channel} 未发现从机。排查：① 从机地址若按 8 位标注（如 0x92）= 7 位 0x49；② 降回 100 kHz；③ 检查上拉与共地"
                    : $"通道 {App.Settings.Channel} 无从机；通道 {other} 上发现 {string.Join(" ", otherFound.Select(a => $"0x{a:X2}"))} —— 把 Channel 切到通道 {other} 后重新扫描";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", $"通道{other}", 0,
                    sw.Elapsed.TotalMilliseconds, otherFound.ToArray()));
            }
            else
            {
                TxtScanResult.Text = $"发现 {found.Count} 个从机，点击选用（{sw.Elapsed.TotalMilliseconds:F0} ms）：";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", "—", 0, sw.Elapsed.TotalMilliseconds, found.ToArray()));
            }
        }
        catch (Exception ex)
        {
            TxtScanResult.Text = ex.Message;
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally { _scanning = false; }
    }

    /// <summary>按当前 Address Format 渲染扫描命中的地址 chip。</summary>
    private void RenderChips()
    {
        ChipPanel.Children.Clear();
        foreach (var addr in _lastHits)
            ChipPanel.Children.Add(MakeChip(addr));
    }

    private RadioButton MakeChip(byte addr7)
    {
        bool fmt8 = CmbAddrFmt.SelectedIndex == 1;
        int disp = fmt8 ? addr7 * 2 : addr7;
        var rb = new RadioButton
        {
            Content = $"0x{disp:X2}",
            GroupName = "busAddr",
            Margin = new Thickness(0, 2, 8, 2),
            Padding = new Thickness(8, 3, 8, 3),
            // 扫描结果按行业惯例以 7 位为准；提示 8 位等价形式，避免 0x92/0x49 之类混淆
            ToolTip = $"7 位 0x{addr7:X2} = 8 位 0x{addr7 * 2:X2}(写) / 0x{addr7 * 2 + 1:X2}(读)"
        };
        rb.Checked += (_, _) =>
        {
            _suppressScan = true; // chip 点击程序化聚焦地址框，不应再次触发扫描
            TxtAddr.Text = $"{disp:X2}";
            TxtAddr.Focus();
            TxtAddr.CaretIndex = TxtAddr.Text.Length;
            _suppressScan = false;
        };
        return rb;
    }

    private void TxtAddr_TextChanged(object sender, TextChangedEventArgs e) => SaveSettings();

    private void CmbAddrFmt_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // 地址文本随格式换算：7 位 v ↔ 8 位 v<<1（8 位含读写位，右移时读写位丢弃）
        if (Hex.TryParseByte(TxtAddr.Text, out var v))
        {
            byte nv = CmbAddrFmt.SelectedIndex == 1 ? (v <= 0x7F ? (byte)(v << 1) : v) : (byte)(v >> 1);
            TxtAddr.Text = $"{nv:X2}";
        }
        RenderChips(); // 扫描结果 chip 按新格式重渲染
        SaveSettings();
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
        try
        {
            byte addr = TargetAddr7() ?? throw new ArgumentException("地址为空");
            byte[] data = Hex.ParseBytes(TxtWriteBuf.Text);
            if (data.Length == 0) throw new ArgumentException("Write Buffer 为空");
            bool hasSub = !string.IsNullOrWhiteSpace(TxtSubAddr.Text);
            var r = hasSub
                ? await App.Bus.WriteRegisterAsync(addr, SubAddr(), data)
                : await App.Bus.RawWriteAsync(addr, data);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "WRITE", $"0x{addr:X2}", r.Ret, r.Ms, data));

            // 写后读取：写入成功后自动读回验证（同长度），结果进数据显示区
            if (r.Ok && ChkWriteRead.IsChecked == true)
            {
                int blen = Math.Min(Math.Max(data.Length, 1), 256);
                var rb = hasSub
                    ? await App.Bus.ReadRegisterAsync(addr, SubAddr(), blen)
                    : await App.Bus.RawReadAsync(addr, blen);
                TxtReadResult.Text = rb.Ok
                    ? $"[写后读 {rb.Ms:F1} ms]  {Grouped(rb.Data!)}"
                    : $"写后读取失败：{GinkgoDriver.ErrorName(rb.Ret)}";
                TxtReadResult.Foreground = rb.Ok ? new SolidColorBrush(Color.FromRgb(0x9a, 0x86, 0xfd)) : new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "写后读", $"0x{addr:X2}", rb.Ret, rb.Ms, rb.Data));
            }
        }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "WRITE", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    private async void BtnRead_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            byte addr = TargetAddr7() ?? throw new ArgumentException("地址为空");
            int len = int.Parse(TxtReadSize.Text.Trim(), null);
            if (len is < 1 or > 256) throw new ArgumentException("Read Size 须在 1–256 之间");
            bool hasSub = !string.IsNullOrWhiteSpace(TxtSubAddr.Text);
            var r = hasSub
                ? await App.Bus.ReadRegisterAsync(addr, SubAddr(), len)
                : await App.Bus.RawReadAsync(addr, len);
            TxtReadResult.Text = r.Ok
                ? $"[{r.Ms:F1} ms]  {Grouped(r.Data!)}"
                : $"读取失败：{GinkgoDriver.ErrorName(r.Ret)}";
            TxtReadResult.Foreground = r.Ok ? new SolidColorBrush(Color.FromRgb(0x9a, 0x86, 0xfd)) : new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            // 读完更新：读到的数据自动回填写入框，便于改几个字节后写回
            if (r.Ok && ChkReadUpdate.IsChecked == true && r.Data is not null)
                TxtWriteBuf.Text = string.Join(" ", Convert.ToHexString(r.Data).Chunk(2).Select(c => new string(c)));
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "READ", $"0x{addr:X2}", r.Ret, r.Ms, r.Data));
        }
        catch (Exception ex)
        {
            TxtReadResult.Text = ex.Message;
            TxtReadResult.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "READ", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }

    internal static string Grouped(byte[] data) =>
        "0x" + string.Join(".", Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));

    /// <summary>双击分隔条：命令面板恢复默认 380px，日志列回到自动占满。</summary>
    private void Splitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.GridSplitter { Parent: Grid grid })
            grid.ColumnDefinitions[0].Width = new GridLength(344);
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
