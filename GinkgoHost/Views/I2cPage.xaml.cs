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
    public static readonly DependencyProperty CanExecuteExtendedProperty =
        DependencyProperty.Register(nameof(CanExecuteExtended), typeof(bool), typeof(I2cPage), new PropertyMetadata(false));

    public bool CanExecuteExtended
    {
        get => (bool)GetValue(CanExecuteExtendedProperty);
        private set => SetValue(CanExecuteExtendedProperty, value);
    }

    private bool _scanning;
    private bool _operationBusy;
    private bool _extOperationBusy;
    private bool _wideLayout;
    private bool _settingResultBuffer;
    // XAML 加载期 SelectionChanged 就会触发，_loading 初始为 true，RestoreSettings 完成后才放行
    private bool _loading = true;

    public I2cPage()
    {
        InitializeComponent();
        LogView.Init(App.Log);
        GridReg.ItemsSource = RegTable;
        GridInit.ItemsSource = InitSeq;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        RegTable.CollectionChanged += (_, _) => RefreshExtendedUi();
        InitSeq.CollectionChanged += (_, _) => RefreshExtendedUi();
        Loaded += (_, _) =>
        {
            RestoreSettings();
            LoadTargets();
            RefreshProfiles();
            App.Bus.StateChanged -= OnBusStateChanged;
            App.Bus.StateChanged += OnBusStateChanged;
            RefreshOperationAvailability();
            RefreshExtendedUi();
            ApplyResponsiveLayout();
        };
        Unloaded += (_, _) =>
        {
            App.Bus.StateChanged -= OnBusStateChanged;
            CancelExtendedWork();
        };
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
        // 扩展地址未单独持久化，初始值以 7-bit 写在 XAML 中；加载 8-bit 设置时同步转为显示值。
        if (CmbAddrFmt.SelectedIndex == 1 && Hex.TryParseByte(TxtSlave.Text, out var extAddress) && extAddress <= 0x7F)
            TxtSlave.Text = $"{extAddress << 1:X2}";
        double ratio = Math.Clamp(s.LogPanelRatio, 0.3, 0.8);
        LogRow.Height = new GridLength(ratio, GridUnitType.Star);
        DataRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
        UpdateSpeedControlsEnabled();
        _loading = false;
        ValidateTargetInputs();
        UpdateExtAddressFormatHint();
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

    /// <summary>日志展示跟随用户地址格式；驱动调用始终使用 7-bit 地址。</summary>
    internal static string DisplayAddress(byte address7)
    {
        int displayAddress = App.Settings.AddrFmt == 1 ? address7 << 1 : address7;
        return $"0x{displayAddress:X2}";
    }

    // ── Settings 段 ──

    // ── 扩展面板（寄存器表 / 初始化序列，行内执行支持按「周期ms」轮询）──

    public ObservableCollection<RegRow> RegTable { get; } = [];
    public ObservableCollection<RegRow> InitSeq { get; } = [];
    public ObservableCollection<I2cTarget> Targets { get; } = [];
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
        ControlColumn.MaxWidth = _wideLayout ? 720 : 560;
        ControlColumn.Width = ext
            ? new GridLength(Math.Clamp(App.Settings.ExtPanelWidth, 320, ControlColumn.MaxWidth))
            : new GridLength(0);
        PanelSplitter.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        BtnTabMain.Appearance = ext ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        BtnTabExt.Appearance = ext ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    /// <summary>宽屏优先增加有效工作区，普通窗口保留用户保存的分栏比例。</summary>
    private void ApplyResponsiveLayout()
    {
        bool wide = ActualWidth >= 1_800 && ActualHeight >= 900;
        if (wide == _wideLayout) return;

        _wideLayout = wide;
        ControlColumn.MaxWidth = wide ? 720 : 560;
        if (ExtPanel.Visibility == Visibility.Visible)
        {
            double width = wide
                ? Math.Max(App.Settings.ExtPanelWidth, Math.Clamp(ActualWidth * 0.30, 480, 720))
                : Math.Clamp(App.Settings.ExtPanelWidth, 320, 560);
            ControlColumn.Width = new GridLength(Math.Min(width, ControlColumn.MaxWidth));
        }

        if (wide)
        {
            // 数据卡按实际内容高度排布，所有余量只交给日志区，避免超宽屏出现中间空白带。
            LogRow.Height = new GridLength(1, GridUnitType.Star);
            DataRow.Height = GridLength.Auto;
        }
        else
        {
            double ratio = Math.Clamp(App.Settings.LogPanelRatio, 0.3, 0.8);
            LogRow.Height = new GridLength(ratio, GridUnitType.Star);
            DataRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
        }

        GridReg.FontSize = wide ? 13 : 12;
        GridInit.FontSize = wide ? 13 : 12;
        DataOperationsCard.FontSize = wide ? 13 : 12;
        Dbg.Log($"I2cPage.ApplyResponsiveLayout: wide={wide} width={ActualWidth:F0} height={ActualHeight:F0}");
    }

    private void OnBusStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnBusStateChanged);
            return;
        }
        if (!App.Bus.IsOpen) CancelExtendedWork();
        RefreshOperationAvailability();
        RefreshExtendedUi();
    }

    private void TxtSlave_TextChanged(object sender, TextChangedEventArgs e) => ValidateExtSlave();

    private bool ValidateExtSlave()
    {
        // XAML 按声明顺序创建控件；TxtSlave 的初始 TextChanged 会早于 CmbAddrFmt。
        bool eightBit = CmbAddrFmt?.SelectedIndex == 1;
        bool valid = Hex.TryParseByte(TxtSlave.Text, out var address) &&
                     (eightBit ? (address & 1) == 0 : address <= 0x7F);
        TxtSlave.ToolTip = valid
            ? eightBit ? "8 位地址（hex），如 0x92" : "7 位地址（hex），如 0x49"
            : eightBit ? "8 位地址须为偶数 hex 值，如 0x92" : "地址须为 00–7F 的十六进制数";
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

    // ── 已保存目标 ──

    private void LoadTargets()
    {
        Targets.Clear();
        foreach (var target in (App.Settings.I2cTargets ?? []).Where(t => t.Address7 <= 0x7F))
            Targets.Add(new I2cTarget { Address7 = target.Address7, Name = target.Name });
        RefreshTargetDisplays();
        RefreshTargetEmptyHint();

        byte? current = TryTargetAddr7();
        CmbTarget.SelectedItem = current is byte address
            ? Targets.FirstOrDefault(t => t.Address7 == address)
            : null;
        RefreshTargetEmptyHint();
        Dbg.Log($"I2cPage.LoadTargets: count={Targets.Count}");
    }

    private void RefreshTargetDisplays()
    {
        foreach (var target in Targets)
        {
            string address = DisplayAddress(target.Address7);
            target.Display = string.IsNullOrWhiteSpace(target.Name) ? address : $"{address} · {target.Name}";
        }
    }

    private void RefreshTargetEmptyHint()
    {
        // XAML 加载期下拉框的 SelectionChanged 可能早于占位文本创建。
        if (TxtTargetEmpty is null || CmbTarget is null) return;
        bool noSavedTarget = Targets.Count == 0;
        TxtTargetEmpty.Text = noSavedTarget ? "暂无保存目标" : "选择已保存目标";
        TxtTargetEmpty.Visibility = noSavedTarget || CmbTarget.SelectedItem is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        Dbg.Log($"I2cPage.RefreshTargetEmptyHint: count={Targets.Count} selected={CmbTarget.SelectedItem is not null}");
    }

    private void PersistTargets()
    {
        App.Settings.I2cTargets = Targets
            .Select(t => new I2cTarget { Address7 = t.Address7, Name = t.Name })
            .ToList();
        App.Settings.Save();
    }

    private I2cTarget UpsertTarget(byte address7)
    {
        var target = Targets.FirstOrDefault(t => t.Address7 == address7);
        if (target is null)
        {
            target = new I2cTarget { Address7 = address7, Name = $"设备 {DisplayAddress(address7)}" };
            Targets.Add(target);
            Dbg.Log($"I2cPage.UpsertTarget: added addr={DisplayAddress(address7)}");
        }
        RefreshTargetDisplays();
        RefreshTargetEmptyHint();
        PersistTargets();
        return target;
    }

    private void AddScannedTargets(IReadOnlyCollection<byte> addresses)
    {
        bool changed = false;
        foreach (byte address in addresses)
        {
            if (Targets.Any(t => t.Address7 == address)) continue;
            Targets.Add(new I2cTarget { Address7 = address, Name = $"设备 {DisplayAddress(address)}" });
            changed = true;
        }
        if (!changed) return;
        RefreshTargetDisplays();
        RefreshTargetEmptyHint();
        PersistTargets();
        Dbg.Log($"I2cPage.AddScannedTargets: added count={addresses.Count}");
    }

    private void SelectTarget(I2cTarget target)
    {
        int address = CmbAddrFmt.SelectedIndex == 1 ? target.Address7 << 1 : target.Address7;
        TxtAddr.Text = $"{address:X2}";
        TxtSlave.Text = TxtAddr.Text;
        CmbTarget.SelectedItem = target;
        SaveSettings();
        ResetDataHint();
        Dbg.Log($"I2cPage.SelectTarget: addr={DisplayAddress(target.Address7)} name={target.Name}");
    }

    private void CmbTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshTargetEmptyHint();
        if (_loading || CmbTarget.SelectedItem is not I2cTarget target) return;
        SelectTarget(target);
    }

    private void BtnSaveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TryTargetAddr7() is not byte address)
        {
            TxtDataStatus.Text = "地址格式错误，无法保存目标";
            TxtDataStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            return;
        }
        var target = UpsertTarget(address);
        CmbTarget.SelectedItem = target;
        TxtDataStatus.Text = $"已保存目标 {DisplayAddress(address)}";
        TxtDataStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xc7, 0x84));
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
        if (sender is not Button btn || btn.DataContext is not RegRow row) return;
        CommitGridEdit(GridReg);

        // 已在轮询 → 本次点击 = 停止
        if (_rowLoops.TryGetValue(row, out var running))
        {
            running.Cancel();
            _rowLoops.Remove(row);
            btn.Content = "执行";
            row.Polling = false;
            return;
        }

        if (_extOperationBusy) return;
        if (row.PeriodMs > 0)
        {
            var cts = new CancellationTokenSource();
            _rowLoops[row] = cts;
            row.Polling = true;
            row.PollCount = 0;
            btn.Content = "停止·0";
            SetExtOperationBusy(true, "周期轮询");
            try { await RunRowLoopAsync(row, btn, cts.Token); }
            finally
            {
                _rowLoops.Remove(row);
                btn.Content = "执行";
                row.Polling = false;
                SetExtOperationBusy(false);
            }
        }
        else
        {
            await RunExtendedOperationAsync("行执行", () => ExecRowOnceAsync(row));
        }
    }

    /// <summary>单次执行：按行读写属性分派（R 读 / W 写）。</summary>
    private async Task ExecRowOnceAsync(RegRow row)
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

    /// <summary>行周期轮询循环：每次读都回填数据并记录事务，等待间隔支持取消。</summary>
    private async Task RunRowLoopAsync(RegRow row, System.Windows.Controls.Button execBtn, CancellationToken ct)
    {
        Dbg.Log($"I2cPage.RunRowLoopAsync: start reg={row.Reg} periodMs={row.PeriodMs} dir={row.Dir}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (row.Dir == "W")
                {
                    byte[] data = ExtWriteData(row);
                    var r = await App.Bus.WriteSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), data, ExtRegWidth());
                    row.Status = r.Ok ? "OK" : "ERR";
                    if (!r.Ok)
                        App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", RegisterOperation("周期写", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, data));
                }
                else
                {
                    var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                    row.Status = r.Ok ? "OK" : "ERR";
                    if (r.Ok)
                    {
                        // 成功事务必须回填，即使值与上一轮相同，也让表格反映本次读到的结果。
                        row.Value = r.Data is not null ? BufferText(r.Data) : "—";
                        App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("周期读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
                    }
                }
            }
            catch (Exception ex)
            {
                row.Status = "ERR";
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "周期读", "—", -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            }
            row.PollCount++;
            execBtn.Content = $"停止·{row.PollCount}";
            try { await Task.Delay(Math.Max(10, row.PeriodMs), ct); }
            catch (OperationCanceledException) { break; }
        }
        Dbg.Log($"I2cPage.RunRowLoopAsync: stopped reg={row.Reg} polls={row.PollCount}");
    }

    /// <summary>初始化行执行：按行读写属性分派（R 读校验 / W 写入）。</summary>
    private async void BtnInitRowExec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RegRow row }) return;
        CommitGridEdit(GridInit);
        if (_extOperationBusy) return;
        await RunExtendedOperationAsync("初始化行", () => ExecInitRowAsync(row));
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
        await RunExtendedOperationAsync("一键读取", ReadAllAsync);
    }

    private async Task ReadAllAsync()
    {
        // 只读取方向为 R 的行；W 行由行内「执行」显式写入
        foreach (var row in RegTable.Where(r => r.Dir == "R"))
        {
            try
            {
                var r = await App.Bus.ReadSubAddrAsync(ExtSlave7(), ExtParseReg(row.Reg), ExtLength(row), ExtRegWidth());
                row.Status = r.Ok ? "OK" : "ERR";
                if (r.Ok && r.Data is not null) row.Value = BufferText(r.Data);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", RegisterOperation("寄存器读", row), DisplayAddress(ExtSlave7()), r.Ret, r.Ms, r.Data));
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
        CanExecuteExtended = connected && !_extOperationBusy;
        BtnReadAll.IsEnabled = CanExecuteExtended;
        BtnRunInit.IsEnabled = CanExecuteExtended;
        BtnStopPolling.Visibility = _rowLoops.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnStopPolling.IsEnabled = _rowLoops.Count > 0;
        GridReg.IsReadOnly = _extOperationBusy;
        GridInit.IsReadOnly = _extOperationBusy;
        TxtRegEmpty.Visibility = RegTable.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtInitEmpty.Visibility = InitSeq.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtExtStatus.Text = !connected ? "未连接：可编辑表格，连接适配器后执行"
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
            Dbg.Log($"I2cPage.BtnSave_Click: profile={name}");
        }
        catch (Exception ex)
        {
            Dbg.Log($"I2cPage.BtnSave_Click: failed profile={name} error={ex.Message}");
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
            RegTable.Clear();
            foreach (var r in p.RegTable) RegTable.Add(r);
            InitSeq.Clear();
            foreach (var r in p.InitSequence) InitSeq.Add(r);
            Dbg.Log($"I2cPage.BtnLoad_Click: profile={name}");
        }
        catch (Exception ex)
        {
            Dbg.Log($"I2cPage.BtnLoad_Click: failed profile={name} error={ex.Message}");
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
        }
        catch (Exception ex)
        {
            Dbg.Log($"I2cPage.BtnDelete_Click: failed profile={name} error={ex.Message}");
            MessageBox.Show($"删除 Profile 失败：{ex.Message}");
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

    private async void BtnScan_Click(object sender, RoutedEventArgs e) => await ScanBusNow();

    private void SetOperationBusy(bool busy, string action = "")
    {
        _operationBusy = busy;
        Dbg.Log($"I2cPage.SetOperationBusy: busy={busy} action={action}");
        if (busy && TxtDataStatus is not null)
        {
            TxtDataStatus.Text = action switch
            {
                "scan" => "正在扫描总线…",
                "read" => "正在读取数据…",
                "write" => "正在写入数据…",
                _ => "正在执行…"
            };
            TxtDataStatus.ClearValue(TextBlock.ForegroundProperty);
        }
        UpdateOperationAvailability();
    }

    private Stopwatch? _scanSw;

    /// <summary>扫描回调来自后台线程；只更新界面，不写高频调试日志。</summary>
    private void UpdateScanProgress(int done, int total)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateScanProgress(done, total));
            return;
        }
        if (!_scanning || TxtDataStatus is null) return;
        double secs = _scanSw?.Elapsed.TotalSeconds ?? 0;
        TxtDataStatus.Text = $"扫描中 {done} / {total} · {secs:F1}s";
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
        bool connected = App.Bus.IsOpen;
        bool targetValid = IsTargetValid();
        BtnScanBus.IsEnabled = connected;
        BtnRead.IsEnabled = connected && targetValid && IsReadSizeValid();
        BtnWrite.IsEnabled = connected && targetValid && IsBufferValid();
        // 忙碌时保留启用外观，避免 Wpf.Ui 的禁用/启用动画造成视觉抖动；
        // 点击和快捷键仍由 IsHitTestVisible 与 _operationBusy 双重拦截。
        BtnScanBus.IsHitTestVisible = !_operationBusy;
        BtnRead.IsHitTestVisible = !_operationBusy;
        BtnWrite.IsHitTestVisible = !_operationBusy;
        string? unavailableTip = !connected ? "请先在左侧工作区连接适配器"
            : _operationBusy ? "总线忙碌，请稍候"
            : null;
        BtnScanBus.ToolTip = unavailableTip ?? "扫描当前通道上的从机地址 (F5)";
        BtnRead.ToolTip = unavailableTip ?? "读取数据 (Ctrl+R)";
        BtnWrite.ToolTip = unavailableTip ?? "写入数据 (Ctrl+W)";
    }

    private async Task ScanBusNow()
    {
        if (_scanning || !BtnScanBus.IsEnabled) return;
        _scanning = true;
        SetOperationBusy(true, "scan");
        Dbg.Log($"I2cPage.ScanBusNow: start channel={App.Settings.Channel}");
        try
        {
            var sw = _scanSw = Stopwatch.StartNew();
            var found = await App.Bus.ScanBusAsync(progress: UpdateScanProgress);
            int channel = App.Settings.Channel;
            bool canScanAlternate = CurrentCtrlMode() == GinkgoDriver.VII_HCTL_MODE && channel is 0 or 1;
            if (found.Count == 0 && canScanAlternate)
            {
                int other = 1 - channel;
                TxtDataStatus.Text = $"当前通道未命中，正在扫描备用通道 {other}";
                var otherFound = await App.Bus.ScanBusAsync(progress: UpdateScanProgress, channel: other);
                sw.Stop();
                if (otherFound.Count == 1)
                {
                    // 备用通道命中后必须同步当前通道，否则后续读写仍会走旧通道。
                    _loading = true;
                    CmbChannel.SelectedIndex = other;
                    _loading = false;
                    App.Settings.Channel = other;
                    int configRet = await App.Bus.ApplyConfigAsync(other, CurrentHz(), CurrentCtrlMode());
                    if (configRet != 0) throw new InvalidOperationException($"切换到通道 {other} 失败：{GinkgoDriver.ErrorName(configRet)}");
                    int displayAddress = CmbAddrFmt.SelectedIndex == 1 ? otherFound[0] << 1 : otherFound[0];
                    TxtAddr.Text = $"{displayAddress:X2}";
                    SaveSettings();
                    Dbg.Log($"I2cPage.ScanBusNow: auto selected alternate channel={other} addr=0x{otherFound[0]:X2}");
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描",
                    $"通道{channel}→{other}", 0, sw.Elapsed.TotalMilliseconds, otherFound.Select(ToDisplayAddressByte).ToArray()));
                AddScannedTargets(otherFound);
                Dbg.Log($"I2cPage.ScanBusNow: alternate channel={other} hits={otherFound.Count} totalMs={sw.Elapsed.TotalMilliseconds:F1}");
                TxtDataStatus.Text = otherFound.Count == 0
                    ? "扫描完成，未发现从机"
                    : otherFound.Count == 1
                        ? $"扫描完成，已选中 {DisplayAddress(otherFound[0])}"
                        : $"扫描完成，发现 {otherFound.Count} 个从机，结果见事务日志";
            }
            else
            {
                sw.Stop();
                if (found.Count == 1)
                {
                    int displayAddress = CmbAddrFmt.SelectedIndex == 1 ? found[0] << 1 : found[0];
                    TxtAddr.Text = $"{displayAddress:X2}";
                    SaveSettings();
                    Dbg.Log($"I2cPage.ScanBusNow: auto selected 0x{found[0]:X2}");
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描",
                    $"通道{channel}", 0, sw.Elapsed.TotalMilliseconds, found.Select(ToDisplayAddressByte).ToArray()));
                AddScannedTargets(found);
            }
            if (found.Count != 0)
                TxtDataStatus.Text = found.Count == 1
                    ? $"扫描完成，已选中 {DisplayAddress(found[0])}"
                    : $"扫描完成，发现 {found.Count} 个从机，结果见事务日志";
            TxtDataStatus.ClearValue(TextBlock.ForegroundProperty);
        }
        catch (Exception ex)
        {
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            TxtDataStatus.Text = "扫描失败，详情见事务日志";
            TxtDataStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
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
        if (!_loading) App.Settings.LastAddr = TxtAddr.Text;
    }

    private void TxtSubAddr_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateTargetInputs();
        ResetDataHint();
        if (!_loading) App.Settings.LastSubAddr = TxtSubAddr.Text;
    }

    private void TargetInput_LostFocus(object sender, RoutedEventArgs e)
    {
        NormalizeHexTextBox(TxtAddr);
        NormalizeHexTextBox(TxtSubAddr, allowEmpty: true);
        SaveSettings();
        Dbg.Log($"I2cPage.TargetInput_LostFocus: addr={TxtAddr.Text} reg={TxtSubAddr.Text}");
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
        if (Hex.TryParseByte(TxtSlave.Text, out var extAddress))
        {
            byte nv = CmbAddrFmt.SelectedIndex == 1
                ? (extAddress <= 0x7F ? (byte)(extAddress << 1) : extAddress)
                : (byte)(extAddress >> 1);
            TxtSlave.Text = $"{nv:X2}";
        }
        UpdateExtAddressFormatHint();
        RefreshTargetDisplays();
        ValidateTargetInputs();
        ResetDataHint();
        SaveSettings();
    }

    private bool IsTargetValid()
    {
        bool addressValid = Hex.TryParseByte(TxtAddr.Text, out var value) &&
                            (CmbAddrFmt?.SelectedIndex == 1 ? (value & 1) == 0 : value <= 0x7F);
        bool registerValid = string.IsNullOrWhiteSpace(TxtSubAddr.Text) ||
                             Hex.TryParseByte(TxtSubAddr.Text, out _);
        return addressValid && registerValid;
    }

    private void ValidateTargetInputs()
    {
        if (TxtAddr is null || TxtSubAddr is null) return;
        bool addressValid = Hex.TryParseByte(TxtAddr.Text, out var value) &&
                            (CmbAddrFmt?.SelectedIndex == 1 ? (value & 1) == 0 : value <= 0x7F);
        bool registerValid = string.IsNullOrWhiteSpace(TxtSubAddr.Text) ||
                             Hex.TryParseByte(TxtSubAddr.Text, out _);
        if (addressValid) TxtAddr.ClearValue(Control.BorderBrushProperty);
        else TxtAddr.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
        if (registerValid) TxtSubAddr.ClearValue(Control.BorderBrushProperty);
        else TxtSubAddr.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
        TxtAddr.ToolTip = CmbAddrFmt?.SelectedIndex == 1
            ? "8-bit 地址：00–FF，例如 92"
            : "7-bit 地址：00–7F，例如 49";
        UpdateOperationAvailability();
    }

    private void ResetDataHint()
    {
        if (_loading || _operationBusy || TxtDataStatus is null) return;
        string? inputError = GetInputError();
        if (inputError is not null)
        {
            TxtDataStatus.Text = inputError;
            TxtDataStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            return;
        }
        TxtDataStatus.Text = App.Bus.IsOpen
            ? "参数已更新，可以读取或写入"
            : "请先在左侧工作区连接适配器";
        TxtDataStatus.ClearValue(TextBlock.ForegroundProperty);
    }

    private string? GetInputError()
    {
        bool addressValid = Hex.TryParseByte(TxtAddr.Text, out var address) &&
                            (CmbAddrFmt?.SelectedIndex == 1 || address <= 0x7F);
        if (!addressValid)
            return CmbAddrFmt?.SelectedIndex == 1
                ? "地址格式错误：8-bit 地址应为 00–FF"
                : "地址格式错误：7-bit 地址应为 00–7F";
        if (!string.IsNullOrWhiteSpace(TxtSubAddr.Text) && !Hex.TryParseByte(TxtSubAddr.Text, out _))
            return "寄存器地址格式错误：请输入 00–FF，或留空使用原始读写";
        if (!IsReadSizeValid()) return "读取长度应为 1–256";
        try
        {
            Hex.ParseBytes(TxtDataBuffer.Text);
            return null;
        }
        catch { return "数据格式错误：请输入以空格分隔的 HEX 字节"; }
    }

    private static void NormalizeHexTextBox(TextBox textBox, bool allowEmpty = false)
    {
        if (allowEmpty && string.IsNullOrWhiteSpace(textBox.Text)) return;
        if (Hex.TryParseByte(textBox.Text, out var value)) textBox.Text = $"{value:X2}";
    }

    private void DataInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender == TxtReadSize && int.TryParse(TxtReadSize.Text, out int length))
            TxtReadSize.Text = length.ToString();
        if (sender == TxtDataBuffer && IsBufferValid())
            TxtDataBuffer.Text = BufferText(Hex.ParseBytes(TxtDataBuffer.Text));
        ResetDataHint();
    }

    /// <summary>用户输入按 Address Format 折算成 7 位地址。8 位输入右移一位。</summary>
    private byte? TargetAddr7()
    {
        byte raw = Hex.ParseByte(TxtAddr.Text);
        return CmbAddrFmt.SelectedIndex == 1 ? (byte)(raw >> 1) : raw;
    }

    private byte? TryTargetAddr7() => IsTargetValid() ? TargetAddr7() : null;

    private byte SubAddr() => Hex.ParseByte(string.IsNullOrWhiteSpace(TxtSubAddr.Text) ? "00" : TxtSubAddr.Text);

    // ── Transactions 段 ──

    private async void BtnWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || !BtnWrite.IsEnabled) return;
        if (MessageBox.Show($"确认向目标 {TxtAddr.Text} 写入当前 HEX 数据？", "确认写入",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            TxtDataStatus.Text = "已取消写入";
            return;
        }
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
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "WRITE", DisplayAddress(addr), r.Ret, r.Ms, data));

            // 写后读取：写入成功后自动读回验证（同长度），结果进数据显示区
            if (r.Ok && ChkWriteRead.IsChecked == true)
            {
                int blen = Math.Min(Math.Max(data.Length, 1), 256);
                var rb = hasSub
                    ? await App.Bus.ReadRegisterAsync(addr, SubAddr(), blen)
                    : await App.Bus.RawReadAsync(addr, blen);
                if (rb.Ok && rb.Data is not null)
                    ShowBuffer(rb.Data, "写后读结果");
                TxtDataStatus.Text = rb.Ok
                    ? $"写入并回读成功 · {blen} 字节 · {rb.Ms:F1} ms"
                    : $"写后读取失败 · {GinkgoDriver.ErrorName(rb.Ret)}";
                TxtDataStatus.Foreground = rb.Ok
                    ? new SolidColorBrush(Color.FromRgb(0x81, 0xc7, 0x84))
                    : new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "写后读", DisplayAddress(addr), rb.Ret, rb.Ms, rb.Data));
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
                ShowBuffer(r.Data, "读取结果");
            TxtDataStatus.Text = r.Ok
                ? $"读取成功 · {r.Data?.Length ?? 0} 字节 · {r.Ms:F1} ms"
                : $"读取失败 · {GinkgoDriver.ErrorName(r.Ret)}";
            TxtDataStatus.Foreground = r.Ok
                ? new SolidColorBrush(Color.FromRgb(0x81, 0xc7, 0x84))
                : new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "READ", DisplayAddress(addr), r.Ret, r.Ms, r.Data));
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
            TxtBufferMeta.Text = _settingResultBuffer ? $"{count} 字节 · 读取结果" : $"{count} 字节 · 待写数据";
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

    private void ShowBuffer(byte[] data, string purpose)
    {
        _settingResultBuffer = true;
        TxtDataBuffer.Text = BufferText(data);
        TxtBufferMeta.Text = $"{data.Length} 字节 · {purpose}";
        _settingResultBuffer = false;
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

    private static byte ToDisplayAddressByte(byte address7) =>
        App.Settings.AddrFmt == 1 ? (byte)(address7 << 1) : address7;

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
        grid.RowDefinitions[0].Height = new GridLength(_wideLayout ? 1 : 13, GridUnitType.Star);
        grid.RowDefinitions[2].Height = _wideLayout ? GridLength.Auto : new GridLength(7, GridUnitType.Star);
        if (!_wideLayout)
        {
            App.Settings.LogPanelRatio = 0.65;
            App.Settings.Save();
        }
        Dbg.Log($"I2cPage.LogSplitter_DoubleClick: reset wide={_wideLayout}");
        e.Handled = true;
    }

    private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        double width = Math.Clamp(ControlColumn.ActualWidth, 320, _wideLayout ? 720 : 560);
        if (!_wideLayout)
        {
            App.Settings.ExtPanelWidth = width;
            App.Settings.Save();
        }
        Dbg.Log($"I2cPage.PanelSplitter_DragCompleted: width={width:F0} wide={_wideLayout}");
    }

    private void LogSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        double total = LogRow.ActualHeight + DataRow.ActualHeight;
        if (total <= 0 || _wideLayout) return;
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
