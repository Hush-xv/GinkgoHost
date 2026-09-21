using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private bool _compactCommandLayout;
    private bool _compactTargetFields;
    private bool _compactTransferFields;
    private const double InspectorMinWidth = 360;
    private const double InspectorDefaultWidth = 480;
    private const double InspectorAbsoluteMaxWidth = 1200;
    private const double InspectorMainReserve = 520;
    private string _operationAction = string.Empty;
    private bool? _lastBusConnection;
    private string? _lastTransactionText;
    private byte[]? _lastReadData;
    private byte? _lastReadAddress;
    private byte? _lastReadRegister;
    private bool _lastReadHasRegister;
    private readonly System.Windows.Threading.DispatcherTimer _headerLogRefreshTimer;
    private readonly System.Windows.Threading.DispatcherTimer _layoutRefreshTimer;
    private bool _extendedRefreshQueued;
    private bool _suspendExtendedRefresh;
#if DEBUG
    private string? _lastStatusDebugText;
#endif
    // XAML 加载期 SelectionChanged 就会触发，_loading 初始为 true，RestoreSettings 完成后才放行
    private bool _loading = true;

    // 高频路径（每次按键校验）复用的冻结画刷；随主题变化的颜色一律走资源引用（App.ApplyThemePalette 按主题整组替换）
    private static readonly SolidColorBrush ErrorBorderBrush = FrozenBrush(0xEF, 0x53, 0x50);
    private static readonly Brush ConnectedDotBrush = FrozenBrush(0x58, 0xC6, 0x67);   // 饱和绿点，双主题可见
    private static readonly Brush DisconnectedDotBrush = FrozenBrush(0x77, 0x77, 0x77); // 中性灰点，双主题可见

    static SolidColorBrush FrozenBrush(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public I2cPage()
    {
        InitializeComponent();
        _headerLogRefreshTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _headerLogRefreshTimer.Tick += (_, _) =>
        {
            _headerLogRefreshTimer.Stop();
            RefreshHeaderRecentLog();
        };
        _layoutRefreshTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _layoutRefreshTimer.Tick += (_, _) =>
        {
            _layoutRefreshTimer.Stop();
            ApplyResponsiveLayout();
        };
        LogView.Init(App.Log);
        CmbTarget.ItemsSource = Targets;
        GridReg.ItemsSource = RegTable;
        GridInit.ItemsSource = InitSeq;
        SizeChanged += (_, _) => QueueResponsiveLayout();
        RegTable.CollectionChanged += (_, _) => QueueExtendedRefresh();
        InitSeq.CollectionChanged += (_, _) => QueueExtendedRefresh();
        Loaded += (_, _) =>
        {
            RestoreSettings();
            LoadTargets();
            RefreshProfiles();
            App.Bus.StateChanged -= OnBusStateChanged;
            App.Bus.StateChanged += OnBusStateChanged;
            App.Log.CollectionChanged -= OnLogCollectionChanged;
            App.Log.CollectionChanged += OnLogCollectionChanged;
            RefreshOperationAvailability();
            RefreshBusConnectionBadge();
            RefreshExtendedUi();
            RefreshHeaderRecentLog();
            ApplyResponsiveLayout();
        };
        Unloaded += (_, _) =>
        {
            App.Bus.StateChanged -= OnBusStateChanged;
            App.Log.CollectionChanged -= OnLogCollectionChanged;
            _headerLogRefreshTimer.Stop();
            _layoutRefreshTimer.Stop();
            CancelPendingOperations("页面离开");
        };
        ShowExtTab(false); // 初始化模式 Tab 选中样式（XAML 不再硬编码 Primary）
        ShowExtSub("reg");
    }

    /// <summary>窗口拖动期间最多每 60 ms 重算一次布局，连续 SizeChanged 不再堆积布局任务。</summary>
    private void QueueResponsiveLayout()
    {
        if (!IsLoaded) return;
        if (!_layoutRefreshTimer.IsEnabled) _layoutRefreshTimer.Start();
    }

    /// <summary>集合批量变化合并成一次检查器刷新；Profile 加载期间由提交点统一触发。</summary>
    private void QueueExtendedRefresh()
    {
        if (_suspendExtendedRefresh || _extendedRefreshQueued || !IsLoaded) return;
        _extendedRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _extendedRefreshQueued = false;
            if (IsLoaded && !_suspendExtendedRefresh) RefreshExtendedUi(preserveStatus: true);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>中止页面持有的全部长任务；原生事务完成后不再开始下一步。</summary>
    public void CancelPendingOperations(string reason = "用户停止")
    {
        _scanCts?.Cancel();
        CancelExtendedWork(reason);
        if (IsLoaded) RefreshExtendedUi();
#if DEBUG
        Dbg.Log($"I2cPage.CancelPendingOperations: cancellation requested reason={reason}");
#endif
    }

    /// <summary>从设备概览进入工作区时落到目标地址，连接后的下一步无需再次寻找输入位置。</summary>
    public void FocusTransactionTarget()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || TxtAddr is null || !TxtAddr.IsEnabled) return;
            TxtAddr.Focus();
            TxtAddr.SelectAll();
#if DEBUG
            Dbg.Log("I2cPage.FocusTransactionTarget: target address focused from device workflow");
#endif
        });
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
        ApplyWorkspaceRows();
        UpdateSpeedControlsEnabled();
        _loading = false;
        ValidateTargetInputs();
        UpdateExtAddressFormatHint();
    }

    /// <summary>
    /// 工作区上下两行的唯一分配点。比例始终以日志区域占比存储。
    /// 日志为空时不再特殊处理：LogPanel 会画满本行并把占位居中，
    /// 若把本行收成 Auto，省下的两百像素会全部灌进 HEX 编辑器，只剩四个字节的输入框反而更像坏掉的布局。
    /// </summary>
    private void ApplyWorkspaceRows()
    {
        double ratio = Math.Clamp(App.Settings.LogPanelRatio, 0.3, 0.8);
        LogRow.Height = new GridLength(ratio, GridUnitType.Star);
        DataRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
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
        bool busIdle = !_operationBusy && !_extOperationBusy && _rowLoops.Count == 0;
        if (CmbCtrlMode.IsEnabled != busIdle) CmbCtrlMode.IsEnabled = busIdle;
        if (CmbChannel.IsEnabled != busIdle) CmbChannel.IsEnabled = busIdle;
        if (TglNonStd.IsEnabled != busIdle) TglNonStd.IsEnabled = busIdle;
        // 互斥：自定义开关占用速率配置时禁用预设下拉，关掉开关才可改
        bool presetEnabled = busIdle && !sw && TglNonStd.IsChecked != true;
        bool customEnabled = busIdle && !sw && TglNonStd.IsChecked == true;
        if (CmbSpeed.IsEnabled != presetEnabled) CmbSpeed.IsEnabled = presetEnabled;
        if (TxtCustomHz.IsEnabled != customEnabled) TxtCustomHz.IsEnabled = customEnabled;
        if (BtnApplyHz.IsEnabled != customEnabled) BtnApplyHz.IsEnabled = customEnabled;
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

    /// <summary>宽屏优先增加有效工作区，普通窗口保留用户保存的分栏比例。</summary>
    private void ApplyResponsiveLayout()
    {
        if (HeaderContextPanel is not null)
        {
            bool showTarget = ActualWidth >= 1_240;
            bool showRecent = ActualWidth >= 1_580;
            Visibility targetVisibility = showTarget ? Visibility.Visible : Visibility.Collapsed;
            Visibility recentVisibility = showRecent ? Visibility.Visible : Visibility.Collapsed;
            if (HeaderContextPanel.Visibility != targetVisibility) HeaderContextPanel.Visibility = targetVisibility;
            if (HeaderRecentDivider.Visibility != recentVisibility) HeaderRecentDivider.Visibility = recentVisibility;
            if (HeaderRecentGroup.Visibility != recentVisibility) HeaderRecentGroup.Visibility = recentVisibility;
        }

        bool wide = ActualWidth >= 1_800 && ActualHeight >= 900;
        bool densityChanged = wide != _wideLayout;
        _wideLayout = wide;
        if (ExtPanel.Visibility == Visibility.Visible)
            ApplyInspectorWidth();

        if (densityChanged)
        {
            ApplyWorkspaceRows();

            GridReg.FontSize = wide ? 13 : 12;
            GridInit.FontSize = wide ? 13 : 12;
            System.Windows.Documents.TextElement.SetFontSize(DataOperationsCard, wide ? 13 : 12);
            Dbg.Log($"I2cPage.ApplyResponsiveLayout: wide={wide} width={ActualWidth:F0} height={ActualHeight:F0}");
        }
        ApplyCommandLayout();
    }

    /// <summary>检查器只在视口变小时安全收窄；放大窗口不会覆盖用户保存的宽度。</summary>
    private void ApplyInspectorWidth(bool reset = false)
    {
        double maxWidth = InspectorMaxWidth();
        double preferred = reset ? InspectorDefaultWidth : App.Settings.ExtPanelWidth;
        double width = Math.Clamp(preferred, InspectorMinWidth, maxWidth);
        bool widthChanged = !ControlColumn.Width.IsAbsolute || Math.Abs(ControlColumn.Width.Value - width) > 0.5;
        if (Math.Abs(ControlColumn.MinWidth - InspectorMinWidth) > 0.5) ControlColumn.MinWidth = InspectorMinWidth;
        if (Math.Abs(ControlColumn.MaxWidth - maxWidth) > 0.5) ControlColumn.MaxWidth = maxWidth;
        if (widthChanged) ControlColumn.Width = new GridLength(width);
#if DEBUG
        if (widthChanged || reset)
            Dbg.Log($"I2cPage.ApplyInspectorWidth: reset={reset} preferred={preferred:F0} width={width:F0} max={maxWidth:F0}");
#endif
    }

    /// <summary>右侧检查器可扩展，但始终给左侧事务工作区留出可操作宽度。</summary>
    private double InspectorMaxWidth() =>
        Math.Clamp(ActualWidth - InspectorMainReserve, InspectorMinWidth, InspectorAbsoluteMaxWidth);

    /// <summary>全宽时保持单行；检查器挤压单个分组时，字段按预设结构整体换行，避免随机掉队。</summary>
    private void ApplyCommandLayout()
    {
        if (CommandGroups is null || DataTargetGroup is null || DataTransferGroup is null ||
            TargetSavedGroup is null || TransferActions is null) return;
        double sideWidth = ControlColumn.ActualWidth > 0 ? ControlColumn.ActualWidth : ControlColumn.Width.Value;
        double available = ActualWidth - (ExtPanel.Visibility == Visibility.Visible
            ? sideWidth + PanelSplitter.ActualWidth + 12
            : 0);
        // 进入/退出阈值分离，拖动检查器靠近断点时不会来回跳动。
        bool stackGroups = _compactCommandLayout ? available < 1_060 : available < 1_020;
        double groupGap = stackGroups ? 0 : 10;
        double targetWidth = stackGroups ? available : Math.Max(0, (available - groupGap) * 0.60);
        double transferWidth = stackGroups ? available : Math.Max(0, (available - groupGap) * 0.40);
        // DataGroupStyle 左右各有 12 px Padding；断点必须按真实内容宽度判断，否则末端按钮会被裁切。
        double targetContentWidth = Math.Max(0, targetWidth - 24);
        double transferContentWidth = Math.Max(0, transferWidth - 24);
        bool compactTarget = _compactTargetFields ? targetContentWidth < 880 : targetContentWidth < 840;
        bool compactTransfer = _compactTransferFields ? transferContentWidth < 540 : transferContentWidth < 500;
        // 并排卡片必须保持相同节奏：一侧需要双行时另一侧同步切换，避免上下重心错位。
        if (!stackGroups && (compactTarget || compactTransfer))
            compactTarget = compactTransfer = true;
        if (stackGroups == _compactCommandLayout && compactTarget == _compactTargetFields &&
            compactTransfer == _compactTransferFields) return;

        _compactCommandLayout = stackGroups;
        _compactTargetFields = compactTarget;
        _compactTransferFields = compactTransfer;
        Grid.SetColumn(DataTargetGroup, 0);
        Grid.SetRow(DataTargetGroup, 0);
        Grid.SetColumnSpan(DataTargetGroup, stackGroups ? 2 : 1);
        Grid.SetColumn(DataTransferGroup, stackGroups ? 0 : 1);
        Grid.SetRow(DataTransferGroup, stackGroups ? 1 : 0);
        Grid.SetColumnSpan(DataTransferGroup, stackGroups ? 2 : 1);
        DataTransferGroup.Margin = stackGroups ? new Thickness(0, 6, 0, 0) : new Thickness(10, 0, 0, 0);

        PlaceCommandElement(TargetSavedGroup, 0, 0, compactTarget ? 4 : 1);
        PlaceCommandElement(TargetAddressGroup, compactTarget ? 1 : 0, compactTarget ? 0 : 1);
        PlaceCommandElement(TargetFormatGroup, compactTarget ? 1 : 0, compactTarget ? 1 : 2);
        PlaceCommandElement(TargetRegisterGroup, compactTarget ? 1 : 0, compactTarget ? 2 : 3);
        TargetAddressGroup.Margin = compactTarget ? new Thickness(0, 7, 8, 0) : new Thickness(0, 0, 8, 0);
        TargetFormatGroup.Margin = compactTarget ? new Thickness(0, 7, 8, 0) : new Thickness(0, 0, 8, 0);
        TargetRegisterGroup.Margin = compactTarget ? new Thickness(0, 7, 0, 0) : new Thickness(0);

        PlaceCommandElement(TransferHeading, 0, 0);
        PlaceCommandElement(TransferLengthGroup, 0, 1);
        PlaceCommandElement(ChkWriteRead, 0, 2);
        PlaceCommandElement(TransferActionDivider, 0, 3);
        PlaceCommandElement(TransferActions, compactTransfer ? 1 : 0, compactTransfer ? 0 : 4, compactTransfer ? 3 : 1);
        TransferActionDivider.Visibility = compactTransfer ? Visibility.Collapsed : Visibility.Visible;
        TransferActions.Margin = compactTransfer ? new Thickness(0, 7, 0, 0) : new Thickness(0);
#if DEBUG
        Dbg.Log($"I2cPage.ApplyCommandLayout: stack={stackGroups} targetCompact={compactTarget} transferCompact={compactTransfer} available={available:F0} targetContent={targetContentWidth:F0} transferContent={transferContentWidth:F0}");
#endif
    }

    private static void PlaceCommandElement(FrameworkElement element, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private void OnBusStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(OnBusStateChanged);
            return;
        }
        if (!App.Bus.IsOpen)
        {
            _scanCts?.Cancel(); // 断开后扫描无意义，立即中止
            CancelExtendedWork("设备已断开");
        }
        RefreshBusConnectionBadge();
        RefreshOperationAvailability();
        RefreshExtendedUi();
    }

    /// <summary>顶部会话概览只派生现有 UI/任务状态，不持有第二份业务状态。</summary>
    private void RefreshHeaderContext(string? activityOverride = null)
    {
        if (TxtHeaderTarget is null || TxtI2cConnection is null || DotI2cConnection is null ||
            TxtAddr is null || TxtSubAddr is null || TxtReadSize is null || CmbAddrFmt is null || ExtPanel is null) return;

        string target;
        if (TryTargetAddr7() is byte address)
        {
            int displayAddress = CmbAddrFmt.SelectedIndex == 1 ? address << 1 : address;
            if (ExtPanel.Visibility == Visibility.Visible)
                target = $"0x{displayAddress:X2} · 检查 {RegTable.Count} 项";
            else
            {
                string register = string.IsNullOrWhiteSpace(TxtSubAddr.Text)
                    ? "RAW"
                    : Hex.TryParseByte(TxtSubAddr.Text, out byte reg) ? $"Reg 0x{reg:X2}" : "Reg 无效";
                string length = IsReadSizeValid() ? $"{TxtReadSize.Text.Trim()} B" : "长度无效";
                target = $"0x{displayAddress:X2} · {register} · {length}";
            }
        }
        else
        {
            target = "目标参数待修正";
        }
        TxtHeaderTarget.Text = target;
        TxtHeaderTarget.ToolTip = ExtPanel.Visibility == Visibility.Visible
            ? $"当前从机与寄存器检查项 · {target}"
            : $"当前基础事务路径 · {target}";

        bool stopping = _rowLoops.Values.Any(cts => cts.IsCancellationRequested) ||
                        _extOperationCts?.IsCancellationRequested == true;
        bool active = _operationBusy || _extOperationBusy || _rowLoops.Count > 0;
        string activity = activityOverride ?? (!App.Bus.IsOpen ? "未连接"
            : _scanning ? "扫描中"
            : stopping ? "停止中"
            : _extOperationBusy ? string.IsNullOrWhiteSpace(_extOperationAction) ? "任务运行中" : $"{_extOperationAction}中"
            : _operationBusy ? _operationAction switch
            {
                "read" => "读取中",
                "write" => "写入中",
                "scan" => "扫描中",
                "config" => "配置中",
                _ => "忙碌"
            }
            : "空闲");
        // 连接标签只表达稳定的连接事实；任务文本仅放入提示，避免长度变化推动整条工具栏。
        string connectionText = App.Bus.IsOpen ? $"已连接 · CH{App.Bus.Channel}" : "未连接";
        if (TxtI2cConnection.Text != connectionText) TxtI2cConnection.Text = connectionText;
        TxtI2cConnection.ToolTip = active ? "当前任务独占 I²C 总线" : App.Bus.IsOpen ? "总线可以执行新事务" : "连接适配器后可执行事务";
        if (active) TxtI2cConnection.ToolTip = $"{activity} · 当前任务独占 I²C 总线";
        DotI2cConnection.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            !App.Bus.IsOpen ? "TextFillColorSecondaryBrush" : active ? "TxAccentBrush" : "StatusSuccessBrush");
    }

    private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(QueueHeaderRecentRefresh);
            return;
        }
        QueueHeaderRecentRefresh();
    }

    /// <summary>周期轮询可在 10 ms 产生一条记录；顶部摘要按 100 ms 合并刷新，避免布局线程被文本更新占满。</summary>
    private void QueueHeaderRecentRefresh()
    {
        if (!IsLoaded) return;
        if (!_headerLogRefreshTimer.IsEnabled) _headerLogRefreshTimer.Start();
    }

    private void RefreshHeaderRecentLog()
    {
        if (TxtHeaderRecent is null) return;
        if (App.Log.Count == 0)
        {
            TxtHeaderRecent.Text = "尚无事务记录";
            TxtHeaderRecent.ToolTip = null;
            return;
        }

        LogEntry entry = App.Log[^1];
        string direction = entry.Dir is "RX" or "TX" ? $"{entry.Dir} · " : string.Empty;
        string address = entry.Addr == "—" ? string.Empty : $" · {entry.Addr}";
        string elapsed = entry.Ms switch
        {
            >= 1000 => $" · {entry.Ms / 1000:F2} s",
            > 0 => $" · {entry.Ms:F1} ms",
            _ => string.Empty
        };
        string operation = entry.Op == "总线扫描" ? "扫描" : entry.Op;
        TxtHeaderRecent.Text = $"{direction}{operation}{address} · {entry.RetText}{elapsed}";
        TxtHeaderRecent.ToolTip = $"{entry.Time} · {entry.Dir} · {entry.Op} · {entry.Addr} · {entry.RetText}{elapsed}\n{entry.DataDisplay}";
    }

    /// <summary>总线配置条始终显示真实连接状态，避免只靠页面底部状态推断当前会话是否可用。</summary>
    private void RefreshBusConnectionBadge()
    {
        if (TxtI2cConnection is null || DotI2cConnection is null) return;
        bool connected = App.Bus.IsOpen;
        TxtI2cConnection.Text = connected ? $"已连接 · CH{App.Bus.Channel}" : "未连接";
        TxtI2cConnection.SetResourceReference(TextBlock.ForegroundProperty,
            connected ? "StatusSuccessBrush" : "TextFillColorSecondaryBrush");
        DotI2cConnection.Fill = connected ? ConnectedDotBrush : DisconnectedDotBrush;
#if DEBUG
        if (_lastBusConnection != connected)
            Dbg.Log($"I2cPage.RefreshBusConnectionBadge: connected={connected} channel={App.Bus.Channel}");
#endif
        _lastBusConnection = connected;
    }

    // ── 已保存目标 ──

    public ObservableCollection<I2cTarget> Targets { get; } = [];

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

    private I2cTarget UpsertTarget(byte address7, bool persist = true)
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
        if (persist) PersistTargets();
        return target;
    }

    private void AddScannedTargets(IReadOnlyCollection<byte> addresses, bool persist = true)
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
        if (persist) PersistTargets();
        else _scanTargetsChanged = true;
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
            SetLastTransactionStatus("地址格式错误，无法保存目标", "StatusErrorBrush");
            return;
        }
        var target = UpsertTarget(address);
        CmbTarget.SelectedItem = target;
        SetLastTransactionStatus($"已保存目标 {DisplayAddress(address)}", "StatusSuccessBrush");
    }

    private void BtnRemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (CmbTarget.SelectedItem is not I2cTarget target) return;
        Targets.Remove(target);
        CmbTarget.SelectedItem = null;
        RefreshTargetEmptyHint();
        PersistTargets();
        SetLastTransactionStatus($"已移除目标 {DisplayAddress(target.Address7)}", "StatusSuccessBrush");
        Dbg.Log($"I2cPage.BtnRemoveTarget_Click: removed addr={DisplayAddress(target.Address7)}");
    }

    /// <summary>地址/寄存器/长度框内按 Enter 直接读取（缓冲区多行编辑除外，Enter 在那里是换行）。</summary>
    private void FieldBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _operationBusy || !BtnRead.IsEnabled) return;
        e.Handled = true;
        BtnRead_Click(BtnRead, new RoutedEventArgs());
    }

    private async void CmbCtrlMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        RebuildChannelItems();
        UpdateSpeedControlsEnabled();
        _loading = false;
        await ApplyCurrentConfigAsync();
    }

    private async void CmbSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        await ApplyCurrentConfigAsync();
    }

    private async void CmbChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        await ApplyCurrentConfigAsync();
    }

    // 开关切换在驱动接受后持久化，失败时恢复原配置，避免下次启动读取到未生效的速率。
    private async void TglNonStd_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSpeedControlsEnabled();
        if (_loading) return;
        await ApplyCurrentConfigAsync();
    }

    private async void BtnApplyHz_Click(object sender, RoutedEventArgs e)
    {
        uint requestedHz = CurrentHz();
        int ret = await ApplyCurrentConfigAsync();
        App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", $"速率切换 {requestedHz / 1000} kHz", "—", ret, 0,
            ret == 0 ? null : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
    }

    /// <summary>配置先下发后持久化。硬件拒绝新参数时恢复控件，保证显示、设置和驱动三者一致。</summary>
    private async Task<int> ApplyCurrentConfigAsync()
    {
        if (_operationBusy || _extOperationBusy || _rowLoops.Count > 0)
        {
            RestoreConfigControlsFromBus();
            SetLastTransactionStatus("总线忙碌，当前配置未更改");
#if DEBUG
            Dbg.Log("I2cPage.ApplyCurrentConfigAsync: blocked because bus is busy");
#endif
            return -1;
        }

        SetOperationBusy(true, "config");
        try
        {
            int ret = await App.Bus.ApplyConfigAsync(CurrentChannel(), CurrentHz(), CurrentCtrlMode());
            if (ret == 0)
            {
                SaveSettings();
                SetLastTransactionStatus($"总线配置已应用 · 通道 {CurrentChannel()} · {CurrentHz() / 1000.0:F0} kHz", "StatusSuccessBrush");
#if DEBUG
                Dbg.Log($"I2cPage.ApplyCurrentConfigAsync: applied channel={CurrentChannel()} hz={CurrentHz()} mode={CurrentCtrlMode()}");
#endif
                return ret;
            }

            RestoreConfigControlsFromBus();
            SetLastTransactionStatus($"配置切换失败 · {GinkgoDriver.ErrorName(ret)}，已恢复原配置", "StatusErrorBrush");
#if DEBUG
            Dbg.Log($"I2cPage.ApplyCurrentConfigAsync: rejected ret={ret}; controls restored from active bus config");
#endif
            return ret;
        }
        catch (Exception ex)
        {
            RestoreConfigControlsFromBus();
            SetLastTransactionStatus($"配置无效 · {ex.Message}", "StatusErrorBrush");
#if DEBUG
            Dbg.Log($"I2cPage.ApplyCurrentConfigAsync: invalid config error={ex.Message}");
#endif
            return -1;
        }
        finally
        {
            SetOperationBusy(false);
        }
    }

    private void RestoreConfigControlsFromBus()
    {
        _loading = true;
        try
        {
            CmbCtrlMode.SelectedIndex = App.Bus.ControlMode == GinkgoDriver.VII_SCTL_MODE ? 1 : 0;
            RebuildChannelItems();
            CmbChannel.SelectedIndex = Math.Clamp(App.Bus.Channel, 0, CmbChannel.Items.Count - 1);

            uint activeHz = App.Bus.ClockHz;
            int speedIndex = Array.IndexOf(new uint[] { 100000, 400000, 1000000, 1200000 }, activeHz);
            if (speedIndex >= 0)
            {
                TglNonStd.IsChecked = false;
                CmbSpeed.SelectedIndex = speedIndex;
            }
            else
            {
                TglNonStd.IsChecked = true;
                TxtCustomHz.Text = activeHz.ToString();
            }
            UpdateSpeedControlsEnabled();
        }
        finally { _loading = false; }
    }

    // ── Target Device 段 ──

    private void SetOperationBusy(bool busy, string action = "")
    {
        bool scanStateChanged = busy ? action == "scan" : _operationAction == "scan";
        _operationBusy = busy;
        _operationAction = busy ? action : string.Empty;
        Dbg.Log($"I2cPage.SetOperationBusy: busy={busy} action={action}");
        if (busy && TxtDataStatus is not null && action is "scan" or "config")
        {
            TxtDataStatus.Text = action switch
            {
                "scan" => "正在扫描总线…",
                "config" => "正在应用总线配置…",
                _ => "正在执行…"
            };
            TxtDataStatus.ClearValue(TextBlock.ForegroundProperty);
        }
        // 扫描按钮两态：扫描中变「停止」并保持可点击（点击 = 中止），读写忙碌时仍由处理器拦截
        bool scanBusy = busy && action == "scan";
        if (PnlScanNormal is not null && PnlScanCancel is not null)
        {
            PnlScanNormal.Visibility = scanBusy ? Visibility.Hidden : Visibility.Visible;
            PnlScanCancel.Visibility = scanBusy ? Visibility.Visible : Visibility.Hidden;
        }
        UpdateOperationAvailability();
        if (scanStateChanged)
        {
            // 扫描期间只拦截配置输入，不改变控件外观，也不刷新右侧检查器。
            bool inputEnabled = !scanBusy;
            CmbCtrlMode.IsHitTestVisible = inputEnabled;
            CmbChannel.IsHitTestVisible = inputEnabled;
            CmbSpeed.IsHitTestVisible = inputEnabled;
            TglNonStd.IsHitTestVisible = inputEnabled;
            TxtCustomHz.IsHitTestVisible = inputEnabled;
            BtnApplyHz.IsHitTestVisible = inputEnabled;
        }
    }

    private Stopwatch? _scanSw;
    private bool _scanTargetsChanged;
    private long _lastScanUiRefreshTimestamp;

    /// <summary>扫描回调先在线程侧限频，再编组到 UI，避免为每个探测地址排队一次布局。</summary>
    private void UpdateScanProgress(int done, int total)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastScanUiRefreshTimestamp);
        if (done < total && previous != 0 &&
            Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(100)) return;
        Interlocked.Exchange(ref _lastScanUiRefreshTimestamp, now);

        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(() => ApplyScanProgress(done, total),
                System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        ApplyScanProgress(done, total);
    }

    private void ApplyScanProgress(int done, int total)
    {
        if (!_scanning || TxtDataStatus is null) return;
        double secs = _scanSw?.Elapsed.TotalSeconds ?? 0;
        int percent = total > 0 ? (int)Math.Round(done * 100.0 / total) : 0;
        string progress = $"扫描中 {done} / {total} · {percent}% · {secs:F1}s";
        if (TxtDataStatus.Text != progress) TxtDataStatus.Text = progress;
    }

    private void RefreshOperationAvailability()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(RefreshOperationAvailability);
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
        bool extendedBusy = _extOperationBusy || _rowLoops.Count > 0;
        bool readReady = connected && !extendedBusy && targetValid && IsReadSizeValid();
        bool writeReady = connected && !extendedBusy && targetValid && IsBufferValid();
        bool scanReady = connected && !extendedBusy;
        if (BtnScanBus.IsEnabled != scanReady) BtnScanBus.IsEnabled = scanReady;
        if (BtnRead.IsEnabled != readReady) BtnRead.IsEnabled = readReady;
        if (BtnWrite.IsEnabled != writeReady) BtnWrite.IsEnabled = writeReady;
        // 读写是同级协议动作；风险由写入确认表达，不把协议类型固定映射为 Primary。
        BtnWrite.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        // 忙碌时保留启用外观，避免 Wpf.Ui 的禁用/启用动画造成视觉抖动；
        // 点击和快捷键仍由 IsHitTestVisible 与 _operationBusy 双重拦截。
        // 扫描按钮例外：扫描中必须可点击用于中止。
        bool transactionHitTest = !_operationBusy && !extendedBusy;
        if (!BtnScanBus.IsHitTestVisible) BtnScanBus.IsHitTestVisible = true;
        if (BtnRead.IsHitTestVisible != transactionHitTest) BtnRead.IsHitTestVisible = transactionHitTest;
        if (BtnWrite.IsHitTestVisible != transactionHitTest) BtnWrite.IsHitTestVisible = transactionHitTest;
        string? unavailableTip = !connected ? "请先在左侧工作区连接适配器"
            : _operationBusy ? "总线忙碌，请稍候"
            : extendedBusy ? "寄存器任务正在占用总线"
            : null;
        BtnScanBus.ToolTip = unavailableTip ?? "扫描当前通道上的从机地址 (F5)；扫描中按 F5、Esc 或点击可中止";
        BtnRead.ToolTip = unavailableTip ?? (!targetValid ? "请先修正目标地址或寄存器" : !IsReadSizeValid() ? "读取长度应为 1–256" : "读取数据 (Ctrl+R)");
        BtnWrite.ToolTip = unavailableTip ?? (!targetValid ? "请先修正目标地址或寄存器" : !IsBufferValid() ? "请先输入有效 HEX 数据" : "写入数据 (Ctrl+W)");
    }

    private async Task ScanBusNow()
    {
        if (_scanning) return;
        _scanning = true;
        var cts = _scanCts = new CancellationTokenSource();
        bool wasCancelled = false;
        bool scanFailed = false;
        int cancelledHitCount = 0;
        string selectedAddress = string.Empty;
        _scanTargetsChanged = false;
        Interlocked.Exchange(ref _lastScanUiRefreshTimestamp, 0);
        SetOperationBusy(true, "scan");
        // 清除输入焦点但不把焦点交给按钮，避免扫描时出现输入框或按钮的强调色焦点线。
        Keyboard.ClearFocus();
        Dbg.Log($"I2cPage.ScanBusNow: start channel={App.Settings.Channel}");
        try
        {
            var sw = _scanSw = Stopwatch.StartNew();
            // 驱动层仍逐地址采集；界面在一轮结束后批量接收结果，避免下拉列表连续重绘。
            var found = await App.Bus.ScanBusAsync(progress: UpdateScanProgress, ct: cts.Token);
            wasCancelled = cts.IsCancellationRequested;
            cancelledHitCount = found.Count;
            int channel = App.Settings.Channel;
            bool canScanAlternate = !cts.Token.IsCancellationRequested &&
                CurrentCtrlMode() == GinkgoDriver.VII_HCTL_MODE && channel is 0 or 1;
            if (found.Count == 0 && canScanAlternate)
            {
                int other = 1 - channel;
                TxtDataStatus.Text = $"当前通道未命中，正在扫描备用通道 {other}";
                Interlocked.Exchange(ref _lastScanUiRefreshTimestamp, 0);
                var otherFound = await App.Bus.ScanBusAsync(progress: UpdateScanProgress, channel: other, ct: cts.Token);
                wasCancelled = cts.IsCancellationRequested;
                cancelledHitCount += otherFound.Count;
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
                    selectedAddress = DisplayAddress(otherFound[0]);
                    SaveSettings();
                    Dbg.Log($"I2cPage.ScanBusNow: auto selected alternate channel={other} addr=0x{otherFound[0]:X2}");
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描",
                    "—", 0, sw.Elapsed.TotalMilliseconds, ScanHitText(otherFound)));
                AddScannedTargets(otherFound, persist: false);
                Dbg.Log($"I2cPage.ScanBusNow: alternate channel={other} hits={otherFound.Count} totalMs={sw.Elapsed.TotalMilliseconds:F1}");
            }
            else
            {
                sw.Stop();
                if (found.Count == 1)
                {
                    int displayAddress = CmbAddrFmt.SelectedIndex == 1 ? found[0] << 1 : found[0];
                    TxtAddr.Text = $"{displayAddress:X2}";
                    selectedAddress = DisplayAddress(found[0]);
                    SaveSettings();
                    Dbg.Log($"I2cPage.ScanBusNow: auto selected 0x{found[0]:X2}");
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描",
                    "—", 0, sw.Elapsed.TotalMilliseconds, ScanHitText(found)));
                AddScannedTargets(found, persist: false);
            }
            // 状态栏保留扫描结论；快速扫描不能只闪过一次，用户应立即知道下一步。
        }
        catch (Exception ex)
        {
            scanFailed = true;
            App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "总线扫描", "—", -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
            SetLastTransactionStatus("扫描失败，详情见事务日志", "StatusErrorBrush");
        }
        finally
        {
            if (_scanTargetsChanged) PersistTargets();
            _scanning = false;
            Interlocked.Exchange(ref _lastScanUiRefreshTimestamp, 0);
            _scanCts = null;
            cts.Dispose();
            SetOperationBusy(false);
            if (wasCancelled)
                SetLastTransactionStatus($"扫描已停止 · 已发现 {cancelledHitCount} 个地址");
            else if (!scanFailed)
            {
                string summary = cancelledHitCount switch
                {
                    0 => "扫描完成 · 未发现从机，请检查连线、供电、地址和通道",
                    1 => $"扫描完成 · 找到 1 个地址，已设为目标 {selectedAddress}",
                    _ => $"扫描完成 · 找到 {cancelledHitCount} 个地址，可从目标列表切换"
                };
                SetLastTransactionStatus(summary, cancelledHitCount > 0 ? "StatusSuccessBrush" : null);
            }
            Dbg.Log($"I2cPage.ScanBusNow: end cancelled={wasCancelled} failed={scanFailed} hits={cancelledHitCount}");
        }
    }

    private void TxtAddr_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateTargetInputs();
        ResetDataHint();
        if (!_loading) App.Settings.LastAddr = TxtAddr.Text;
        SyncSlaveFromMain();
    }

    // 主面板地址与扩展面板从机地址是同一个目标，双向同步；否则扫描选中后扩展操作会打到旧地址
    private bool _syncingSlaveAddr;

    private void SyncSlaveFromMain()
    {
        if (_syncingSlaveAddr || TxtSlave is null || TxtSlave.Text == TxtAddr.Text) return;
        _syncingSlaveAddr = true;
        try { TxtSlave.Text = TxtAddr.Text; }
        finally { _syncingSlaveAddr = false; }
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
        // 地址文本随格式换算：7 位 v ↔ 8 位 v<<1（8 位含读写位，右移时读写位丢弃）。
        // 只换算主面板地址，TxtSlave 经 SyncSlaveFromMain 自动跟随，避免二次换算。
        if (Hex.TryParseByte(TxtAddr.Text, out var v))
        {
            byte nv = CmbAddrFmt.SelectedIndex == 1 ? (v <= 0x7F ? (byte)(v << 1) : v) : (byte)(v >> 1);
            TxtAddr.Text = $"{nv:X2}";
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
        else TxtAddr.BorderBrush = ErrorBorderBrush;
        if (registerValid) TxtSubAddr.ClearValue(Control.BorderBrushProperty);
        else TxtSubAddr.BorderBrush = ErrorBorderBrush;
        TxtAddr.ToolTip = addressValid
            ? CmbAddrFmt?.SelectedIndex == 1 ? "8-bit 地址有效，例如 92" : "7-bit 地址有效，例如 49"
            : CmbAddrFmt?.SelectedIndex == 1 ? "地址无效：8-bit 地址须为偶数 HEX 值，例如 92" : "地址无效：请输入 00–7F，例如 49";
        TxtSubAddr.ToolTip = registerValid
            ? "寄存器地址有效；留空时为原始读写"
            : "寄存器地址无效：请输入 00–FF，或留空使用原始读写";
        UpdateOperationAvailability();
        RefreshHeaderContext();
    }

    private void ResetDataHint()
    {
        if (_loading || _operationBusy || TxtDataStatus is null) return;
        string? inputError = GetInputError();
        if (inputError is not null)
        {
            SetLastTransactionStatus(inputError, "StatusErrorBrush");
            return;
        }
        if (!IsBufferValid())
        {
            SetLastTransactionStatus("读取就绪 · 数据格式错误将阻止写入");
            return;
        }
        if (!App.Bus.IsOpen)
        {
            SetLastTransactionStatus("请先在左侧工作区连接适配器");
            return;
        }

        string route = string.IsNullOrWhiteSpace(TxtSubAddr.Text)
            ? "原始收发"
            : $"寄存器 0x{TxtSubAddr.Text.Trim().ToUpperInvariant()}";
        string verification = ChkWriteRead.IsChecked == true ? " · 写后读取" : string.Empty;
        SetLastTransactionStatus($"就绪 · {route} · {TxtReadSize.Text.Trim()} B{verification}");
    }

    private void TransactionContext_Changed(object sender, RoutedEventArgs e) => ResetDataHint();

    /// <summary>最近一次事务状态独立于缓冲区内容，避免读写后出现陈旧的缓冲区语义。</summary>
    private void SetLastTransactionStatus(string text, string? brushResource = null)
    {
        if (TxtDataStatus.Text != text) TxtDataStatus.Text = text;
        if (brushResource is null) TxtDataStatus.ClearValue(TextBlock.ForegroundProperty);
        else TxtDataStatus.SetResourceReference(TextBlock.ForegroundProperty, brushResource);
#if DEBUG
        // 输入过程会高频刷新此状态；相同文本不重复写磁盘日志。
        string debugText = $"{text}|{brushResource ?? "default"}";
        if (!string.Equals(_lastStatusDebugText, debugText, StringComparison.Ordinal))
        {
            _lastStatusDebugText = debugText;
            Dbg.Log($"I2cPage.SetLastTransactionStatus: text={text} brush={brushResource ?? "default"}");
        }
#endif
    }

    private void SetLastTransactionSnapshot(string direction, string operation, byte address, bool hasSubAddress,
        int length, OpResult result, byte[]? data, string? outcomeOverride = null)
    {
        string target = hasSubAddress
            ? $"{DisplayAddress(address)}[{TxtSubAddr.Text.Trim().ToUpperInvariant()}]"
            : $"{DisplayAddress(address)}[raw]";
        string outcome = outcomeOverride ?? (result.Ok ? "成功" : $"失败 · {GinkgoDriver.ErrorName(result.Ret)}");
        string payload = data is { Length: > 0 } ? BufferText(data) : "—";
        TxtLastTransaction.Text = $"{direction} · {operation} · {target} · {length} B · {outcome} · {result.Ms:F1} ms";
        _lastTransactionText = $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                               $"方向: {direction}{Environment.NewLine}" +
                               $"操作: {operation}{Environment.NewLine}" +
                               $"目标: {target}{Environment.NewLine}" +
                               $"长度: {length} 字节{Environment.NewLine}" +
                               $"结果: {outcome}{Environment.NewLine}" +
                               $"耗时: {result.Ms:F1} ms{Environment.NewLine}" +
                               $"数据 HEX: {payload}";
        TxtLastTransaction.ToolTip = _lastTransactionText;
        LastTransactionPanel.Visibility = Visibility.Visible;
#if DEBUG
        Dbg.Log($"I2cPage.SetLastTransactionSnapshot: {direction} {operation} target={target} len={length} ret={result.Ret}");
#endif
    }

    private void SetLastTransactionFailure(string operation, string error)
    {
        string target = string.IsNullOrWhiteSpace(TxtSubAddr.Text)
            ? $"{TxtAddr.Text.Trim()}[raw]"
            : $"{TxtAddr.Text.Trim()}[{TxtSubAddr.Text.Trim()}]";
        TxtLastTransaction.Text = $"ERR · {operation} · {target} · {error}";
        _lastTransactionText = $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                               $"操作: {operation}{Environment.NewLine}" +
                               $"目标: {target}{Environment.NewLine}" +
                               $"结果: 异常{Environment.NewLine}" +
                               $"错误: {error}";
        TxtLastTransaction.ToolTip = _lastTransactionText;
        LastTransactionPanel.Visibility = Visibility.Visible;
#if DEBUG
        Dbg.Log($"I2cPage.SetLastTransactionFailure: operation={operation} error={error}");
#endif
    }

    private void BtnCopyLastTransaction_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastTransactionText)) return;
        try
        {
            Clipboard.SetText(_lastTransactionText);
            SetLastTransactionStatus("已复制最近事务", "StatusSuccessBrush");
#if DEBUG
            Dbg.Log("I2cPage.BtnCopyLastTransaction_Click: copied latest transaction");
#endif
        }
        catch
        {
            SetLastTransactionStatus("复制失败，请手动选择事务信息", "StatusErrorBrush");
#if DEBUG
            Dbg.Log("I2cPage.BtnCopyLastTransaction_Click: clipboard unavailable");
#endif
        }
    }

    private string? GetInputError(bool requireWriteData = false)
    {
        bool addressValid = Hex.TryParseByte(TxtAddr.Text, out var address) &&
                            (CmbAddrFmt?.SelectedIndex == 1 ? (address & 1) == 0 : address <= 0x7F);
        if (!addressValid)
            return CmbAddrFmt?.SelectedIndex == 1
                ? "地址格式错误：8-bit 地址须为偶数 HEX 值，例如 92"
                : "地址格式错误：7-bit 地址应为 00–7F";
        if (!string.IsNullOrWhiteSpace(TxtSubAddr.Text) && !Hex.TryParseByte(TxtSubAddr.Text, out _))
            return "寄存器地址格式错误：请输入 00–FF，或留空使用原始读写";
        if (!IsReadSizeValid()) return "读取长度应为 1–256";
        if (!requireWriteData) return null;
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
        // 点击扫描会主动转移焦点，但扫描不是编辑操作，不能借 LostFocus 改写用户尚在准备的数据格式。
        if (sender == TxtDataBuffer && !_scanning && IsBufferValid())
            TxtDataBuffer.Text = BufferText(Hex.ParseBytes(TxtDataBuffer.Text));
        ResetDataHint();
#if DEBUG
        Dbg.Log($"I2cPage.DataInput_LostFocus: source={(sender as FrameworkElement)?.Name} valid={GetInputError(requireWriteData: true) is null}");
#endif
    }

    /// <summary>数值输入获得键盘焦点时选中当前值，方便直接替换，鼠标定位不受影响。
    /// 事务后程序归还焦点的数据框例外：全选会让连续读取时缓冲区闪蓝，只把光标放到末尾。</summary>
    private void Input_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) return; // 保留鼠标点击位置
        if (textBox == TxtDataBuffer && _restoringBufferFocus)
        {
            TxtDataBuffer.CaretIndex = TxtDataBuffer.Text.Length;
            return;
        }
        Dispatcher.BeginInvoke(textBox.SelectAll);
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

    private CancellationTokenSource? _scanCts;

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning)
        {
            _scanCts?.Cancel(); // 扫描中再点 = 中止，返回已命中的部分结果
            return;
        }
        if (_operationBusy || _extOperationBusy || _rowLoops.Count > 0 || !BtnScanBus.IsEnabled) return;
        await ScanBusNow();
    }

    private async void BtnWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _extOperationBusy || _rowLoops.Count > 0 || !BtnWrite.IsEnabled) return;
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
            SetLastTransactionStatus(r.Ok
                ? $"写入成功 · {r.Ms:F1} ms"
                : $"写入失败 · {GinkgoDriver.ErrorName(r.Ret)}",
                r.Ok ? "StatusSuccessBrush" : "StatusErrorBrush");
            SetLastTransactionSnapshot("TX", "写入", addr, hasSub, data.Length, r, data);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "WRITE", DisplayAddress(addr), r.Ret, r.Ms, data));

            // 写后读取：写入成功后自动读回验证（同长度），结果进数据显示区
            if (r.Ok && ChkWriteRead.IsChecked == true)
            {
                int blen = Math.Min(Math.Max(data.Length, 1), 256);
                var rb = hasSub
                    ? await App.Bus.ReadRegisterAsync(addr, SubAddr(), blen)
                    : await App.Bus.RawReadAsync(addr, blen);
                bool verified = rb.Ok && rb.Data is not null && data.SequenceEqual(rb.Data);
                string verificationDetail = rb.Ok && rb.Data is not null ? DescribeReadDelta(data, rb.Data) : string.Empty;
                if (rb.Ok && rb.Data is not null)
                    ShowReadBuffer(rb.Data, addr, hasSub);
                SetLastTransactionStatus(!rb.Ok
                    ? $"写后读取失败 · {GinkgoDriver.ErrorName(rb.Ret)}"
                    : verified
                        ? $"写入并回读一致 · {rb.Ms:F1} ms"
                        : $"写入完成，但回读不一致 · {verificationDetail}",
                    verified ? "StatusSuccessBrush" : "StatusErrorBrush");
                SetLastTransactionSnapshot("RX", verified ? "写后读" : "写后读校验", addr, hasSub, blen, rb, rb.Data,
                    verified ? null : rb.Ok ? $"不一致 · {verificationDetail}" : null);
                App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", verified ? "写后读" : "写后读校验", DisplayAddress(addr), rb.Ret, rb.Ms, rb.Data));
#if DEBUG
                Dbg.Log($"I2cPage.BtnWrite_Click: readback ok={rb.Ok} verified={verified} detail={verificationDetail}");
#endif
            }
        }
        catch (Exception ex)
        {
            SetLastTransactionStatus(ex.Message, "StatusErrorBrush");
            SetLastTransactionFailure("写入", ex.Message);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "TX", "WRITE", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally
        {
            SetOperationBusy(false);
            RestoreTransactionFocus();
        }
    }

    private async void BtnRead_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _extOperationBusy || _rowLoops.Count > 0 || !BtnRead.IsEnabled) return;
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
                ShowReadBuffer(r.Data, addr, hasSub);
            SetLastTransactionStatus(r.Ok
                ? $"读取成功 · {r.Ms:F1} ms"
                : $"读取失败 · {GinkgoDriver.ErrorName(r.Ret)}",
                r.Ok ? "StatusSuccessBrush" : "StatusErrorBrush");
            SetLastTransactionSnapshot("RX", "读取", addr, hasSub, len, r, r.Data);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "READ", DisplayAddress(addr), r.Ret, r.Ms, r.Data));
        }
        catch (Exception ex)
        {
            SetLastTransactionStatus(ex.Message, "StatusErrorBrush");
            SetLastTransactionFailure("读取", ex.Message);
            App.Log.AddCapped(new LogEntry(DateTime.Now, "RX", "READ", TxtAddr.Text, -1, 0,
                System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
        finally
        {
            SetOperationBusy(false);
            RestoreTransactionFocus();
        }
    }

    /// <summary>事务结束后回到数据缓冲区，连续读写无需重新寻找输入位置；页面离开时不抢焦点。
    /// 归还焦点期间抑制全选（GotKeyboardFocus），避免连续读取时缓冲区整片闪蓝。</summary>
    private bool _restoringBufferFocus;

    private void RestoreTransactionFocus()
    {
        if (!IsLoaded || !IsVisible || TxtDataBuffer is null) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_operationBusy || !IsVisible || !TxtDataBuffer.IsEnabled) return;
            _restoringBufferFocus = true;
            TxtDataBuffer.Focus();
            TxtDataBuffer.CaretIndex = TxtDataBuffer.Text.Length;
            // GotKeyboardFocus 里的 SelectAll 以 Input 优先级排队，标志在其之后释放
            Dispatcher.BeginInvoke(() => _restoringBufferFocus = false,
                System.Windows.Threading.DispatcherPriority.Input);
#if DEBUG
            Dbg.Log("I2cPage.RestoreTransactionFocus: data buffer focused after transaction");
#endif
        });
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
        try
        {
            byte[] data = Hex.ParseBytes(TxtDataBuffer.Text);
            TxtDataBuffer.ClearValue(Control.BorderBrushProperty);
            if (!_operationBusy && BtnWrite is not null) BtnWrite.IsEnabled = data.Length > 0;
            UpdateBufferPresentation(data);
        }
        catch
        {
            TxtDataBuffer.BorderBrush = ErrorBorderBrush;
            if (!_operationBusy && BtnWrite is not null) BtnWrite.IsEnabled = false;
            UpdateBufferPresentation(null, invalid: true);
        }
        ResetDataHint();
        UpdateOperationAvailability();
    }

    private void ShowReadBuffer(byte[] data, byte address, bool hasRegister)
    {
        byte? register = hasRegister ? SubAddr() : null;
        bool sameContext = _lastReadData is not null &&
            _lastReadAddress == address &&
            _lastReadHasRegister == hasRegister &&
            _lastReadRegister == register;
        string? comparison = sameContext ? DescribeReadDelta(_lastReadData!, data) : null;
        _lastReadData = (byte[])data.Clone();
        _lastReadAddress = address;
        _lastReadRegister = register;
        _lastReadHasRegister = hasRegister;
        ShowBuffer(data, comparison);
#if DEBUG
        Dbg.Log($"I2cPage.ShowReadBuffer: addr={DisplayAddress(address)} reg={(register.HasValue ? register.Value.ToString("X2") : "—")} bytes={data.Length} comparison={comparison ?? "baseline"}");
#endif
    }

    private void ShowBuffer(byte[] data, string? comparison = null)
    {
        TxtDataBuffer.Text = BufferText(data);
        UpdateBufferPresentation(data, comparison: comparison);
#if DEBUG
        Dbg.Log($"I2cPage.ShowBuffer: bytes={data.Length}");
#endif
    }

    /// <summary>HEX 编辑区旁的只读文本提示；保留原数据，非可打印字节不伪装为 ASCII 字符。</summary>
    private void UpdateBufferPresentation(byte[]? data, bool invalid = false, string? comparison = null)
    {
        if (TxtBufferMeta is null || TxtBufferAscii is null || TxtBufferDelta is null) return;
        if (invalid)
        {
            TxtBufferMeta.Text = "HEX 格式无效";
            TxtBufferAscii.Text = "ASCII · —";
            TxtBufferAscii.ToolTip = "请先修正 HEX 数据";
            TxtBufferDelta.Visibility = Visibility.Collapsed;
            return;
        }

        TxtBufferMeta.Text = $"{data?.Length ?? 0} B";
        string preview = data is { Length: > 0 } ? AsciiPreview(data) : "—";
        TxtBufferAscii.Text = $"ASCII · {preview}";
        TxtBufferAscii.ToolTip = TxtBufferAscii.Text;
        TxtBufferDelta.Text = comparison ?? string.Empty;
        TxtBufferDelta.ToolTip = comparison ?? string.Empty;
        TxtBufferDelta.Visibility = string.IsNullOrEmpty(comparison) ? Visibility.Collapsed : Visibility.Visible;
    }

    public static string AsciiPreview(byte[] data)
    {
        const int maxPreviewBytes = 96;
        int length = Math.Min(data.Length, maxPreviewBytes);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = data[i] is >= 0x20 and <= 0x7E ? (char)data[i] : '·';
        return new string(chars) + (data.Length > maxPreviewBytes ? "…" : string.Empty);
    }

    /// <summary>同一读取上下文的两份数据对比；偏移按零基 HEX 表示，便于直接定位寄存器窗口。</summary>
    public static string DescribeReadDelta(byte[] previous, byte[] current)
    {
        if (previous.Length != current.Length)
            return $"长度由 {previous.Length} B 变为 {current.Length} B";

        var changed = new List<int>();
        for (int i = 0; i < current.Length; i++)
            if (previous[i] != current[i]) changed.Add(i);
        if (changed.Count == 0) return "与上次读取相同";

        const int previewLimit = 6;
        string offsets = string.Join("、", changed.Take(previewLimit).Select(index => $"0x{index:X2}"));
        string suffix = changed.Count > previewLimit ? $" 等 {changed.Count} 处" : string.Empty;
        return $"变化 {changed.Count} B · {offsets}{suffix}";
    }

    private void TxtReadSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool valid = IsReadSizeValid();
        if (valid) TxtReadSize.ClearValue(Control.BorderBrushProperty);
        else TxtReadSize.BorderBrush = ErrorBorderBrush;
        ResetDataHint();
        UpdateOperationAvailability();
        RefreshHeaderContext();
    }

    private void I2cPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 扫描占用总线时仍保留键盘取消路径，避免用户必须切回鼠标点击「停止」。
        if (_scanning && (e.Key is Key.F5 or Key.Escape))
        {
            _scanCts?.Cancel();
            e.Handled = true;
#if DEBUG
            Dbg.Log($"I2cPage.I2cPage_PreviewKeyDown: scan cancel via {e.Key}");
#endif
            return;
        }
        if (e.Key == Key.Escape && (_extOperationCts is not null || _rowLoops.Count > 0))
        {
            CancelExtendedWork("Esc 停止");
            RefreshExtendedUi();
            e.Handled = true;
#if DEBUG
            Dbg.Log("I2cPage.I2cPage_PreviewKeyDown: extended operation cancel via Escape");
#endif
            return;
        }
        if (_operationBusy) return;
        if (e.Key == Key.F5 && BtnScanBus.IsEnabled)
        {
            BtnScan_Click(BtnScanBus, new RoutedEventArgs());
            e.Handled = true;
        }
        // 文本编辑时保留 Ctrl 组合键给输入控件，避免用户修正 HEX 时误发总线操作。
        else if (!IsEditingText() && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R && BtnRead.IsEnabled)
        {
            BtnRead_Click(BtnRead, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (!IsEditingText() && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W && BtnWrite.IsEnabled)
        {
            BtnWrite_Click(BtnWrite, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (!IsEditingOrSelectingData() && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && _lastTransactionText is not null)
        {
            BtnCopyLastTransaction_Click(BtnCopyLastTransaction, new RoutedEventArgs());
            e.Handled = true;
        }
        else
            return;
        e.Handled = true;
    }

    private static bool IsEditingText() =>
        Keyboard.FocusedElement is TextBoxBase or ComboBox;

    /// <summary>表格和日志有自己的 Ctrl+C 语义，页面快捷键不能覆盖用户当前选择。</summary>
    private bool IsEditingOrSelectingData() =>
        IsEditingText() || LogView.IsKeyboardFocusWithin || GridReg.IsKeyboardFocusWithin || GridInit.IsKeyboardFocusWithin;

    internal static string BufferText(byte[] data) =>
        string.Join(" ", Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));

    private static byte ToDisplayAddressByte(byte address7) =>
        App.Settings.AddrFmt == 1 ? (byte)(address7 << 1) : address7;

    /// <summary>扫描 SYS 日志的数据负载：可读文本（当前地址格式），空数组表示未命中。</summary>
    private static byte[] ScanHitText(IReadOnlyCollection<byte> addresses) =>
        addresses.Count == 0
            ? []
            : System.Text.Encoding.UTF8.GetBytes(
                $"命中 {addresses.Count} 个 · " + string.Join("  ", addresses.Select(a => $"0x{ToDisplayAddressByte(a):X2}")));

    internal static string Grouped(byte[] data) =>
        "0x" + string.Join(".", Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));

    /// <summary>双击分隔条：扩展工具恢复默认宽度。</summary>
    private void Splitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.GridSplitter) return;
        App.Settings.ExtPanelWidth = InspectorDefaultWidth;
        App.Settings.Save();
        ApplyInspectorWidth(reset: true);
        ApplyCommandLayout();
        Dbg.Log($"I2cPage.Splitter_DoubleClick: reset right inspector width to {InspectorDefaultWidth:F0}");
        e.Handled = true;
    }

    private void LogSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not GridSplitter { Parent: Grid grid }) return;
        const double logRatio = 0.65;
        grid.RowDefinitions[0].Height = new GridLength(1 - logRatio, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(logRatio, GridUnitType.Star);
        if (!_wideLayout)
        {
            App.Settings.LogPanelRatio = logRatio;
            App.Settings.Save();
        }
        Dbg.Log($"I2cPage.LogSplitter_DoubleClick: reset editor/log ratio={1 - logRatio:F2}/{logRatio:F2} wide={_wideLayout}");
        e.Handled = true;
    }

    private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        double maxWidth = InspectorMaxWidth();
        double width = Math.Clamp(ControlColumn.ActualWidth, InspectorMinWidth, maxWidth);
        ControlColumn.Width = new GridLength(width);
        App.Settings.ExtPanelWidth = width;
        App.Settings.Save();
        ApplyCommandLayout();
        Dbg.Log($"I2cPage.PanelSplitter_DragCompleted: persisted width={width:F0} max={maxWidth:F0}");
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
        s = s.Trim();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        // NumberStyles.HexNumber 会接受 +/- 符号（"-1" 静默变 0xFF），显式拒绝非 hex 字符
        if (s.Length == 0 || !s.All(c => Uri.IsHexDigit(c)))
            throw new FormatException($"非法 HEX 字节：\"{s}\"");
        return byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static bool TryParseByte(string s, out byte v)
    {
        v = 0;
        s = s.Trim();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        if (s.Length == 0 || !s.All(c => Uri.IsHexDigit(c))) return false;
        return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    public static byte[] ParseBytes(string s) =>
        s.Split(new[] { ' ', ',', ';', '.', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
         .Select(ParseByte)
         .ToArray();
}
