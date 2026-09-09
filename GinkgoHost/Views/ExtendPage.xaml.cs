using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GinkgoHost.Models;
using GinkgoHost.Native;
using GinkgoHost.Services;

namespace GinkgoHost.Views;

/// <summary>扩展页：寄存器表一键读取、初始化序列、周期触发。参考 JI2C 交互。</summary>
public partial class ExtendPage : UserControl
{
    public ObservableCollection<RegRow> RegTable { get; } = [];
    public ObservableCollection<RegRow> InitSeq { get; } = [];

    private CancellationTokenSource? _periodCts;
    private bool _periodRunning;
    // XAML 加载期事件抑制（同 I2cPage 的教训）
    private bool _loading = true;

    public ExtendPage()
    {
        InitializeComponent();
        GridReg.ItemsSource = RegTable;
        GridInit.ItemsSource = InitSeq;
        Resources["DirIndex"] = new DirIndexConverter();
        Loaded += (_, _) =>
        {
            if (_loading)
            {
                RefreshProfiles();
                TxtSlave.Text = App.Settings.LastAddr;
                _loading = false;
            }
        };
    }

    private byte Slave7() => Hex.ParseByte(TxtSlave.Text);

    private byte RegWidth() =>
        CmbRegWidth.SelectedIndex == 1 ? GinkgoDriver.VII_SUB_ADDR_2BYTE : GinkgoDriver.VII_SUB_ADDR_1BYTE;

    private uint ParseReg(string s)
    {
        s = s.Trim().Replace("0x", "").Replace("0X", "");
        return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private void RefreshProfiles()
    {
        CmbProfile.Items.Clear();
        foreach (var p in ProfileService.List())
            CmbProfile.Items.Add(p);
        if (CmbProfile.Items.Count > 0) CmbProfile.SelectedIndex = 0;
    }

    // ── 表格行操作 ──

    private void BtnAddRow_Click(object sender, RoutedEventArgs e) => RegTable.Add(new RegRow());
    private void BtnAddInit_Click(object sender, RoutedEventArgs e) => InitSeq.Add(new RegRow { Dir = "W" });

    private void BtnDelRow_Click(object sender, RoutedEventArgs e)
    {
        if (GridReg.SelectedItem is RegRow r) RegTable.Remove(r);
    }

    private void BtnDelInit_Click(object sender, RoutedEventArgs e)
    {
        if (GridInit.SelectedItem is RegRow r) InitSeq.Remove(r);
    }

    // ── 单行执行 ──

    private async void BtnRowRead_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        try
        {
            var r = await App.Bus.ReadSubAddrAsync(Slave7(), ParseReg(row.Reg), Math.Max(1, row.Len), RegWidth());
            row.Status = r.Ok ? "OK" : "ERR";
            if (r.Ok && r.Data is not null) row.Value = Convert.ToHexString(r.Data);
        }
        catch (Exception ex) { row.Status = "ERR"; MessageBox.Show(ex.Message); }
    }

    private async void BtnRowWrite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        await WriteRowAsync(row);
    }

    private async void BtnInitRowWrite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        await WriteRowAsync(row);
    }

    private async Task WriteRowAsync(RegRow row)
    {
        try
        {
            byte[] data = Hex.ParseBytes(string.IsNullOrWhiteSpace(row.Value) ? "00" : row.Value);
            var r = await App.Bus.WriteSubAddrAsync(Slave7(), ParseReg(row.Reg), data, RegWidth());
            row.Status = r.Ok ? "OK" : "ERR";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "扩展写", $"0x{Slave7():X2}", r.Ret, r.Ms, data));
        }
        catch (Exception ex) { row.Status = "ERR"; MessageBox.Show(ex.Message); }
    }

    // ── 一键读取全部 ──

    private async void BtnReadAll_Click(object sender, RoutedEventArgs e) => await ReadAllAsync("读全部");

    private async Task<int> ReadAllAsync(string opName)
    {
        int okCount = 0;
        var rows = RegTable.Where(r => r.Dir is "R" or "W").ToList(); // 全部行都读（W 行回读验证）
        foreach (var row in rows)
        {
            try
            {
                var r = await App.Bus.ReadSubAddrAsync(Slave7(), ParseReg(row.Reg), Math.Max(1, row.Len), RegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                if (r.Ok && r.Data is not null) { row.Value = Convert.ToHexString(r.Data); okCount++; }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", opName, $"0x{Slave7():X2}", r.Ret, r.Ms, r.Data));
            }
            catch (Exception ex)
            {
                row.Status = "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", opName, $"0x{Slave7():X2}", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
        }
        return okCount;
    }

    // ── 一键初始化 ──

    private async Task<int> RunInitAsync()
    {
        int okCount = 0;
        foreach (var row in InitSeq)
        {
            if (row.DelayMs > 0)
                await Task.Delay(Math.Min(row.DelayMs, 10_000));
            byte[] data = Hex.ParseBytes(string.IsNullOrWhiteSpace(row.Value) ? "00" : row.Value);
            var r = await App.Bus.WriteSubAddrAsync(Slave7(), ParseReg(row.Reg), data, RegWidth());
            row.Status = r.Ok ? "OK" : "ERR";
            if (r.Ok) okCount++;
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "初始化", $"0x{Slave7():X2}", r.Ret, r.Ms, data));
            if (!r.Ok) break; // 初始化失败即中止，后续行没有意义
        }
        return okCount;
    }

    private async void BtnRunInit_Click(object sender, RoutedEventArgs e)
    {
        try { await RunInitAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    // ── 周期触发 ──

    private async void BtnPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (_periodRunning) { _periodCts?.Cancel(); return; }
        try
        {
            int ms = int.Parse(TxtPeriodMs.Text.Trim(), CultureInfo.InvariantCulture);
            if (ms < 10) throw new ArgumentException("最小间隔 10 ms");
            bool initFirst = ChkInitFirst.IsChecked == true;

            _periodCts = new CancellationTokenSource();
            _periodRunning = true;
            BtnPeriod.Content = "停止";
            int ticks = 0;
            string? lastSnap = null;

            using var timer = new System.Threading.PeriodicTimer(TimeSpan.FromMilliseconds(ms));
            try
            {
                while (await timer.WaitForNextTickAsync(_periodCts.Token))
                {
                    if (initFirst) await RunInitAsync();
                    int ok = await ReadAllAsync("周期读");
                    ticks++;
                    // 高频防刷屏：整表值快照对比，变化才详记
                    string snap = string.Join("|", RegTable.Select(r => $"{r.Reg}={r.Value}"));
                    bool changed = lastSnap is not null && snap != lastSnap;
                    lastSnap = snap;
                    TxtPeriodState.Text = $"第 {ticks} 轮 · OK {ok}/{RegTable.Count}{(changed ? " · 值有变化" : "")}";
                }
            }
            catch (OperationCanceledException) { }
            TxtPeriodState.Text += " · 已停止";
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", $"周期触发结束（{ticks} 轮）", "—", 0, 0, null));
        }
        catch (Exception ex)
        {
            TxtPeriodState.Text = "错误";
            MessageBox.Show(ex.Message);
        }
        finally
        {
            _periodRunning = false;
            BtnPeriod.Content = "开始";
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
}

/// <summary>RegRow.Dir("R"/"W") 与 ComboBox 索引互转。</summary>
public sealed class DirIndexConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value as string == "W" ? 1 : 0;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is int i && i == 1 ? "W" : "R";
}
