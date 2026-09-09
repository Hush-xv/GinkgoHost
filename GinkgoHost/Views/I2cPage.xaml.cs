using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    private CancellationTokenSource? _periodRcTs, _periodWcTs;
    private bool _periodRRunning, _periodWRunning;
    // XAML 加载期 SelectionChanged 就会触发，_loading 初始为 true，RestoreSettings 完成后才放行
    private bool _loading = true;

    public I2cPage()
    {
        InitializeComponent();
        LogView.Init(App.Log);
        Loaded += (_, _) => RestoreSettings();
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
        TxtPeriodR.Text = Math.Max(10, s.PeriodReadMs).ToString();
        TxtPeriodW.Text = Math.Max(10, s.PeriodWriteMs).ToString();
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
        App.Settings.PeriodReadMs = int.TryParse(TxtPeriodR.Text, out var pr) ? Math.Max(10, pr) : 500;
        App.Settings.PeriodWriteMs = int.TryParse(TxtPeriodW.Text, out var pw) ? Math.Max(10, pw) : 1000;
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

    // ── 周期任务 ──

    private int ParseInterval(string s)
    {
        int ms = int.Parse(s.Trim(), CultureInfo.InvariantCulture);
        if (ms < 10) throw new ArgumentException("最小间隔 10 ms");
        return Math.Min(ms, 60_000);
    }

    private (byte Addr, byte? Reg) PeriodicTarget()
    {
        byte addr = Hex.ParseByte(TxtAddr.Text);
        byte? reg = string.IsNullOrWhiteSpace(TxtSubAddr.Text) ? null : SubAddr();
        return (addr, reg);
    }

    private async void BtnPeriodR_Click(object sender, RoutedEventArgs e)
    {
        if (_periodRRunning) { _periodRcTs?.Cancel(); return; }
        try
        {
            int ms = ParseInterval(TxtPeriodR.Text);
            (byte addr, byte? reg) = PeriodicTarget();
            int len = int.Parse(TxtReadSize.Text.Trim(), CultureInfo.InvariantCulture);
            if (len is < 1 or > 256) throw new ArgumentException("Read Size 须在 1–256 之间");

            _periodRcTs = new CancellationTokenSource();
            _periodRRunning = true;
            BtnPeriodR.Content = "停止";
            byte[]? prev = null;
            var (done, reason) = await App.Bus.PeriodicReadAsync(ms, addr, reg, len, r =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    TxtLastR.Text = r.Ok
                        ? $"[{DateTime.Now:HH:mm:ss}] {I2cPage.Grouped(r.Data!)}"
                        : $"[{DateTime.Now:HH:mm:ss}] {GinkgoDriver.ErrorName(r.Ret)}";
                    // 高频档（<500ms）只在数值变化或失败时进日志，防刷屏
                    bool changed = prev is null || r.Data is null || !r.Data.SequenceEqual(prev);
                    if (ms >= 500 || !r.Ok || changed)
                        App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "周期读", $"0x{addr:X2}", r.Ret, r.Ms, r.Data));
                    if (r.Ok) prev = r.Data;
                });
            }, _periodRcTs.Token);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", $"周期读结束（{done} 次）", "—", 0, 0,
                reason is null ? null : System.Text.Encoding.UTF8.GetBytes(reason)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "周期读", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally
        {
            _periodRRunning = false;
            BtnPeriodR.Content = "开始";
            BtnPeriodR.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        }
    }

    private async void BtnPeriodW_Click(object sender, RoutedEventArgs e)
    {
        if (_periodWRunning) { _periodWcTs?.Cancel(); return; }
        try
        {
            int ms = ParseInterval(TxtPeriodW.Text);
            (byte addr, byte? reg) = PeriodicTarget();
            byte[] data = Hex.ParseBytes(TxtWriteBuf.Text);
            if (data.Length == 0) throw new ArgumentException("Write Buffer 为空");

            var owner = Window.GetWindow(this);
            if (MessageBox.Show(owner,
                    $"将以 {ms} ms 周期向 0x{addr:X2} 重复写入 {data.Length} 字节。\n对 EEPROM 等器件有磨损风险，确认开始？",
                    "周期写确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _periodWcTs = new CancellationTokenSource();
            _periodWRunning = true;
            BtnPeriodW.Content = "停止";
            int wcount = 0;
            var (done, reason) = await App.Bus.PeriodicWriteAsync(ms, addr, reg, data, r =>
            {
                Interlocked.Increment(ref wcount);
                _ = Dispatcher.InvokeAsync(() => TxtCountW.Text = $"已写 {wcount} 次");
                // 周期写成功不逐条进日志，失败即记，总数由停止时汇总
            }, _periodWcTs.Token);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", $"周期写结束（{done} 次）", "—", 0, 0,
                reason is null ? null : System.Text.Encoding.UTF8.GetBytes(reason)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "周期写", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally
        {
            _periodWRunning = false;
            BtnPeriodW.Content = "开始";
        }
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
            grid.ColumnDefinitions[0].Width = new GridLength(380);
    }
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
