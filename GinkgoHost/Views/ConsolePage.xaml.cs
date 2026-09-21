using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GinkgoHost.Models;
using GinkgoHost.Native;
using GinkgoHost.Services;

namespace GinkgoHost.Views;

public partial class ConsolePage : UserControl
{
    private const int MaxHistoryCount = 200;

    /// <summary>
    ///  connect/disconnect 走宿主窗口注入的连接链路。控制台此前自行开关总线，等于持有第二套
    /// 生命周期：窗口正在连接时命令行仍可并发发起，SYS 日志也会与状态卡、设备页脱节。
    /// </summary>
    public Func<Task<(int Count, int Ret)>>? ConnectRequested { get; set; }
    public Func<Task<int>>? DisconnectRequested { get; set; }

    private bool _connectionBusy;

    public sealed record ConsoleLine(string Line, Brush Brush);

    private readonly ConsoleBuffer _out = new();
    private readonly List<string> _history = new();
    private int _historyIdx;
    private string _historyDraft = string.Empty;
    private bool _executing;
    private bool _followOutput = true;
    private readonly System.Windows.Threading.DispatcherTimer _followOutputTimer;
    private bool _followOutputQueued;
    private CancellationTokenSource? _readLoopCts;
    private CancellationTokenSource? _scriptCts;
    private CancellationTokenSource? _sleepCts;
#if DEBUG
    private string? _lastCommandStateDebugText;
#endif

    // 控制台输出色：选亮/暗两种底色都可读的中间色值（画刷随行记录绑定，不随主题动态切换）
    private static readonly Brush Cyan = new SolidColorBrush(Color.FromRgb(0x4B, 0xA3, 0xE3));
    private static readonly Brush Purple = new SolidColorBrush(Color.FromRgb(0x7C, 0x68, 0xD8));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xE8, 0x96, 0x0C));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE2, 0x3A, 0x37));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    private sealed class ConsoleBuffer : ObservableCollection<ConsoleLine>
    {
        private const int Cap = 1000;
        private const int TrimBatch = 64;

        public void AddCapped(ConsoleLine line)
        {
            Add(line);
            if (Count <= Cap) return;
            int removeCount = Math.Min(TrimBatch, Count);
            for (int i = 0; i < removeCount; i++) Items.RemoveAt(0);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
#if DEBUG
            Dbg.Log($"ConsolePage.ConsoleBuffer.AddCapped: batchTrim={removeCount} retained={Count}");
#endif
        }
    }

    public ConsolePage()
    {
        _followOutputTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _followOutputTimer.Tick += (_, _) => FlushFollowOutput();
        InitializeComponent();
        LstOut.ItemsSource = _out;
        LstOut.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LstOut_ScrollChanged));
        // ↑/↓ 会被 TextBox 类处理器标记为已处理（光标移动），必须用 handledEventsToo 才能收到
        TxtIn.AddHandler(KeyDownEvent, new KeyEventHandler(TxtIn_KeyDown), handledEventsToo: true);
        Print("GinkgoHost 控制台。输入 help 查看命令。", Gray);
        TxtIn.GotKeyboardFocus += (_, _) => UpdateCommandHint();
        Loaded += (_, _) =>
        {
            App.Bus.StateChanged += OnBusStateChanged;
            // Esc 中止挂窗口级：脚本运行期间 TxtIn 禁用、焦点可能离开页面，页面级监听收不到
            var host = Window.GetWindow(this);
            if (host is not null) host.PreviewKeyDown += Page_EscKeyDown;
            UpdateCommandHint();
            UpdateCommandState();
            UpdateReadLoopActionState();
            RefreshConsoleSession();
            QueueFollowOutput();
            FocusCommandInput(); // 切入页面后可直接输入命令
        };
        Unloaded += (_, _) =>
        {
            App.Bus.StateChanged -= OnBusStateChanged;
            var host = Window.GetWindow(this);
            if (host is not null) host.PreviewKeyDown -= Page_EscKeyDown;
            StopReadLoop(false);
            StopScript(false);
            _sleepCts?.Cancel();
            _followOutputTimer.Stop();
            _followOutputQueued = false;
        };
    }

    /// <summary>窗口级快捷切页后进入命令输入，不打断鼠标切页的当前焦点。</summary>
    public void FocusCommandInput()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || !TxtIn.IsEnabled) return;
            TxtIn.Focus();
#if DEBUG
            Dbg.Log("ConsolePage.FocusCommandInput: command input focused from keyboard navigation");
#endif
        });
    }

    private void Print(string text, Brush brush)
    {
        _out.AddCapped(new ConsoleLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}", brush));
        QueueFollowOutput();
    }

    /// <summary>周期输出只合并滚动请求，数据仍逐条保留；避免每行都触发一次测量和布局。</summary>
    private void QueueFollowOutput()
    {
        if (!IsLoaded || !_followOutput || _followOutputQueued || LstOut is null || LstOut.Items.Count == 0) return;
        _followOutputQueued = true;
        _followOutputTimer.Start();
    }

    private void FlushFollowOutput()
    {
        _followOutputTimer.Stop();
        _followOutputQueued = false;
        if (_followOutput && LstOut.Items.Count > 0)
            LstOut.ScrollIntoView(LstOut.Items[^1]);
    }

    /// <summary>内容增删引起的尺寸和偏移变化不代表用户意图；纯滚动离底时才暂停跟随。</summary>
    private void LstOut_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
        if (Math.Abs(e.ExtentHeightChange) >= double.Epsilon || Math.Abs(e.VerticalChange) < double.Epsilon) return;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 2;
        if (atBottom == _followOutput) return;
        _followOutput = atBottom;
        if (ChkFollowOutput.IsChecked != atBottom) ChkFollowOutput.IsChecked = atBottom;
#if DEBUG
        Dbg.Log($"ConsolePage.LstOut_ScrollChanged: follow={atBottom} offset={sv.VerticalOffset:F0}/{sv.ScrollableHeight:F0}");
#endif
    }

    private void BtnClearOutput_Click(object sender, RoutedEventArgs e)
    {
        Dbg.Log($"ConsolePage.BtnClearOutput_Click: clear {_out.Count} lines");
        ClearOutput();
    }

    private void BtnCopyOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_out.Count == 0)
        {
            TxtConsoleState.Text = "没有可复制的输出";
            TxtConsoleState.Foreground = Gray;
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _out.Select(line => line.Line)));
            TxtConsoleState.Text = $"已复制 {_out.Count} 行输出";
            TxtConsoleState.Foreground = Purple;
#if DEBUG
            Dbg.Log($"ConsolePage.BtnCopyOutput_Click: copied {_out.Count} lines");
#endif
        }
        catch
        {
            TxtConsoleState.Text = "复制失败，请手动选择输出";
            TxtConsoleState.Foreground = Red;
#if DEBUG
            Dbg.Log("ConsolePage.BtnCopyOutput_Click: clipboard unavailable");
#endif
        }
    }

    private void BtnStopRead_Click(object sender, RoutedEventArgs e)
    {
        StopReadLoop(true);
        TxtIn.Focus();
#if DEBUG
        Dbg.Log("ConsolePage.BtnStopRead_Click: stop requested from visible action");
#endif
    }

    private void ClearOutput()
    {
        _out.Clear();
        Print("输出已清空。输入 help 查看命令。", Gray);
        TxtIn.Focus();
    }

    private void OnBusStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(OnBusStateChanged);
            return;
        }
        if (!App.Bus.IsOpen) StopReadLoop(false);
        RefreshConsoleSession();
    }

    private void Page_EscKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (_readLoopCts is null && _scriptCts is null && _sleepCts is null)) return;
        if (_readLoopCts is not null) StopReadLoop(true);
        if (_scriptCts is not null) StopScript(true);
        _sleepCts?.Cancel();
        e.Handled = true;
    }

    private async void TxtIn_KeyDown(object sender, KeyEventArgs e)
    {
        if (_executing) return;
        // Esc 由页面级 PreviewKeyDown 统一处理（脚本运行时输入框可能禁用）
        if (e.Key == Key.Up)
        {
            if (_history.Count == 0) return;
            if (_historyIdx == _history.Count) _historyDraft = TxtIn.Text;
            _historyIdx = Math.Max(0, _historyIdx - 1);
            TxtIn.Text = _history[_historyIdx];
            TxtIn.CaretIndex = TxtIn.Text.Length;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down)
        {
            if (_history.Count == 0) return;
            _historyIdx = Math.Min(_history.Count, _historyIdx + 1);
            TxtIn.Text = _historyIdx < _history.Count ? _history[_historyIdx] : _historyDraft;
            TxtIn.CaretIndex = TxtIn.Text.Length;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunInputAsync();
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e) => await RunInputAsync();

    private void TxtIn_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_executing) UpdateCommandState();
    }

    private void ChkFollowOutput_Changed(object sender, RoutedEventArgs e)
    {
        _followOutput = sender is CheckBox checkBox && checkBox.IsChecked == true;
        // XAML 设置 IsChecked 时此事件早于 LstOut 字段赋值；加载期只记住状态，待列表创建后再滚动。
        if (_followOutput) QueueFollowOutput();
#if DEBUG
        Dbg.Log($"ConsolePage.ChkFollowOutput_Changed: follow={_followOutput}");
#endif
    }

    /// <summary>常用命令直接填入并执行，保持与手工输入完全相同的调用链。</summary>
    private async void BtnQuickCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string command }) return;
        TxtIn.Text = command;
        Dbg.Log($"ConsolePage.BtnQuickCommand_Click: command={command}");
        await RunInputAsync();
    }

    private async Task RunInputAsync()
    {
        if (_executing) return;
        string line = TxtIn.Text;
        if (string.IsNullOrWhiteSpace(line))
        {
            UpdateCommandState();
            return;
        }

        var (cmd, err) = CommandParser.Parse(line);
        if (cmd is null)
        {
            Print(err ?? "解析失败", Red);
            UpdateCommandState();
#if DEBUG
            Dbg.Log($"ConsolePage.RunInputAsync: invalid command rejected; error={err ?? "解析失败"}");
#endif
            return;
        }

        _executing = true;
        TxtIn.IsEnabled = false;
        BtnRun.IsEnabled = false;
        TxtConsoleState.Text = "执行中…";
        TxtConsoleState.Foreground = Orange;
        TxtConsoleState.ToolTip = "命令正在执行";
        TxtIn.Clear();
        _history.Add(line);
        if (_history.Count > MaxHistoryCount)
        {
            _history.RemoveAt(0);
#if DEBUG
            Dbg.Log($"ConsolePage.RunInputAsync: command history capped at {MaxHistoryCount}");
#endif
        }
        _historyIdx = _history.Count;
        _historyDraft = string.Empty;
        RefreshConsoleSession();
        Print($"> {line}", Cyan);
        Dbg.Log($"ConsolePage.TxtIn_KeyDown: execute {cmd.GetType().Name}");
        try
        {
            await Exec(cmd);
        }
        catch (OperationCanceledException)
        {
            Print("延时已取消", Gray);
        }
        catch (Exception ex)
        {
            Print(ex.Message, Red);
        }
        finally
        {
            _executing = false;
            TxtIn.IsEnabled = true;
            UpdateCommandState();
            TxtIn.Focus();
            Dbg.Log($"ConsolePage.TxtIn_KeyDown: completed {cmd.GetType().Name}");
        }
    }

    private async Task Exec(Cmd cmd)
    {
        switch (cmd)
        {
            case HelpCmd:
            {
                Print("connect                       连接适配器", Gray);
                Print("disconnect                    断开适配器", Gray);
                Print("adapters                      刷新并列出适配器数量", Gray);
                Print("status                        显示当前连接状态", Gray);
                Print("config                        显示当前 I²C 配置", Gray);
                Print("speed <kHz>                   修改速率，如 speed 400", Gray);
                string addressFormat = App.Settings.AddrFmt == 1 ? "8-bit" : "7-bit";
                string exampleAddress = App.Settings.AddrFmt == 1 ? "92" : "49";
                Print($"scan                          扫描从机地址（当前 {addressFormat} 显示）", Gray);
                Print($"read <addr> [reg] <len>       读，如 read {exampleAddress} 00 8", Gray);
                Print($"write <addr> [reg] <b...>     写，如 write {exampleAddress} 00 de ad", Gray);
                Print($"readloop <addr> [reg] <len> <ms>  周期读，如 readloop {exampleAddress} 00 8 500", Gray);
                Print("stop                          停止周期读", Gray);
                Print("run <文件>                    执行脚本，# 注释、出错即停、Esc 中止", Gray);
                Print("sleep <ms>                    延时，如 sleep 200（脚本编排用）", Gray);
                Print("clear                         清屏", Gray);
                break;
            }

            case ClearCmd:
                ClearOutput();
                break;

            case StatusCmd:
                string identity = App.Bus.AdapterInfo is { } device
                    ? $" · SN {device.SerialNumber} · {device.FirmwareVersion}"
                    : string.Empty;
                Print(App.Bus.IsOpen
                    ? $"已连接 · 通道 {App.Bus.Channel} · {App.Bus.ClockHz / 1000} kHz{identity}"
                    : "未连接适配器", App.Bus.IsOpen ? Purple : Orange);
                break;

            case ConfigCmd:
            {
                string mode = App.Settings.ControlMode == GinkgoDriver.VII_SCTL_MODE ? "软件 I²C" : "硬件 I²C";
                string addressFormat = App.Settings.AddrFmt == 1 ? "8-bit" : "7-bit";
                Print($"配置 · {mode} · 通道 {App.Settings.Channel} · {App.Settings.ClockHz / 1000} kHz · {addressFormat} 地址", Gray);
                break;
            }

            case ConnectCmd:
            {
                // 已连接时重走连接链路会先关设备，失败还会丢掉现有会话——直接提示
                if (App.Bus.IsOpen)
                {
                    Print($"已连接 · 通道 {App.Bus.Channel} · {App.Bus.ClockHz / 1000} kHz，重连请先输入 disconnect", Orange);
                    break;
                }
                if (ConnectRequested is null || _connectionBusy)
                {
                    Print("连接正在处理中，请稍候", Orange);
                    break;
                }
                var (count, ret) = await ConnectRequested();
                Print(count < 0
                        ? "连接异常，详情见设备页状态卡与日志"
                    : count == 0
                        ? "未检测到 Ginkgo 适配器"
                    : ret == 0
                        ? $"已连接 · 通道 {App.Settings.Channel} · {App.Settings.ClockHz / 1000} kHz"
                        : $"打开失败：{GinkgoDriver.ErrorName(ret)}",
                    count <= 0 ? Orange : ret == 0 ? Purple : Red);
#if DEBUG
                Dbg.Log($"ConsolePage.Exec.ConnectCmd: adapters={count} ret={ret}");
#endif
                break;
            }

            case DisconnectCmd:
            {
                if (DisconnectRequested is null || _connectionBusy)
                {
                    Print("断开正在处理中，请稍候", Orange);
                    break;
                }
                int ret = await DisconnectRequested();
                Print(ret == 0 ? "已断开" : $"断开失败：{GinkgoDriver.ErrorName(ret)}", ret == 0 ? Gray : Red);
#if DEBUG
                Dbg.Log($"ConsolePage.Exec.DisconnectCmd: ret={ret}");
#endif
                break;
            }

            case ScanAdapterCmd:
            {
                int count = await App.Bus.ScanAdaptersAsync();
                string adapterIdentity = App.Bus.AdapterInfo is { } adapter
                    ? $" · SN {adapter.SerialNumber} · {adapter.FirmwareVersion}"
                    : string.Empty;
                Print($"适配器数量：{count}{adapterIdentity}{(App.Bus.IsOpen ? "（当前会话）" : "（已刷新）")}", Gray);
#if DEBUG
                Dbg.Log($"ConsolePage.Exec.ScanAdapterCmd: count={count} connected={App.Bus.IsOpen}");
#endif
                break;
            }

            case SpeedCmd s:
            {
                int ret = await App.Bus.ApplyConfigAsync(
                    App.Settings.Channel, s.KHz * 1000, (byte)App.Settings.ControlMode);
                if (ret == 0)
                {
                    App.Settings.ClockHz = s.KHz * 1000;
                    App.Settings.Save();
                }
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", $"控制台速率 {s.KHz} kHz", "—", ret, 0,
                    ret == 0 ? null : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
                Print(ret == 0 ? $"速率已切换 {s.KHz} kHz" : $"切换失败：{GinkgoDriver.ErrorName(ret)}", ret == 0 ? Purple : Red);
#if DEBUG
                Dbg.Log($"ConsolePage.Exec.SpeedCmd: kHz={s.KHz} ret={ret}");
#endif
                break;
            }

            case ScanBusCmd:
            {
                var found = await App.Bus.ScanBusAsync();
                // 数据负载写可读文本；原始地址字节会被 SYS 行按 UTF-8 解码成控制符乱码
                byte[] payload = found.Count == 0
                    ? []
                    : System.Text.Encoding.UTF8.GetBytes(
                        $"命中 {found.Count} 个 · " + string.Join("  ", found.Select(I2cPage.DisplayAddress)));
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "控制台总线扫描", "—", 0, 0, payload));
                Print(found.Count == 0
                    ? "总线空闲，未发现从机"
                    : "命中：" + string.Join("  ", found.Select(I2cPage.DisplayAddress)),
                    found.Count == 0 ? Orange : Purple);
#if DEBUG
                Dbg.Log($"ConsolePage.Exec.ScanBusCmd: hits={found.Count}");
#endif
                break;
            }

            case ReadCmd r:
            {
                byte address = ConsoleAddr7(r.Addr);
                var res = r.Reg is byte reg
                    ? await App.Bus.ReadRegisterAsync(address, reg, r.Len)
                    : await App.Bus.RawReadAsync(address, r.Len);
                AddConsoleTransaction("RX", "控制台读", address, res);
                Print(res.Ok
                    ? $"RX {I2cPage.DisplayAddress(address)} len={r.Len}  {I2cPage.Grouped(res.Data!)}  [{res.Ms:F1} ms]"
                    : $"读取失败：{GinkgoDriver.ErrorName(res.Ret)}", res.Ok ? Purple : Red);
                break;
            }

            case WriteCmd w:
            {
                byte address = ConsoleAddr7(w.Addr);
                var res = w.Reg is byte reg
                    ? await App.Bus.WriteRegisterAsync(address, reg, w.Data)
                    : await App.Bus.RawWriteAsync(address, w.Data);
                AddConsoleTransaction("TX", "控制台写", address, res, w.Data);
                Print(res.Ok
                    ? $"TX {I2cPage.DisplayAddress(address)} len={w.Data.Length}  {I2cPage.Grouped(w.Data)}  [{res.Ms:F1} ms]"
                    : $"写入失败：{GinkgoDriver.ErrorName(res.Ret)}", res.Ok ? Orange : Red);
                break;
            }

            case ReadLoopCmd loop:
                // 先验证地址，失败时不创建取消源，避免控制台进入无法停止的伪运行状态。
                byte loopAddress = ConsoleAddr7(loop.Addr);
                if (_readLoopCts is not null)
                {
                    Print("已有周期读正在运行；输入 stop 后再启动新的周期读", Orange);
                    break;
                }
                _readLoopCts = new CancellationTokenSource();
                UpdateReadLoopActionState();
                _ = RunReadLoopAsync(loop, _readLoopCts);
                Print($"周期读已启动 · {I2cPage.DisplayAddress(loopAddress)} · {loop.PeriodMs} ms · 输入 stop 停止", Purple);
                break;

            case StopCmd:
                StopReadLoop(true);
                if (_scriptCts is not null) StopScript(true);
                _sleepCts?.Cancel();
                break;

            case SleepCmd sleep:
            {
                // 脚本内随脚本中止；手输时 Esc / stop 也能打断，避免长延时锁死输入
                CancellationTokenSource cts;
                if (_scriptCts is not null)
                {
                    cts = _scriptCts;
                }
                else
                {
                    _sleepCts = new CancellationTokenSource();
                    cts = _sleepCts;
                }
                try
                {
                    await Task.Delay(sleep.Ms, cts.Token);
                }
                finally
                {
                    if (ReferenceEquals(_sleepCts, cts)) _sleepCts = null;
                }
                break;
            }

            case RunScriptCmd script:
                await RunScriptAsync(script.Path);
                break;
        }
    }

    private async Task RunReadLoopAsync(ReadLoopCmd loop, CancellationTokenSource cts)
    {
        byte address = ConsoleAddr7(loop.Addr);
        Dbg.Log($"ConsolePage.RunReadLoopAsync: start addr={I2cPage.DisplayAddress(address)} len={loop.Len} periodMs={loop.PeriodMs}");
        // 与寄存器行轮询一致的停止策略：连续 5 次失败自动停止，拔线后不再空转刷屏
        const int failLimit = 5;
        int fails = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var res = loop.Reg is byte reg
                    ? await App.Bus.ReadRegisterAsync(address, reg, loop.Len)
                    : await App.Bus.RawReadAsync(address, loop.Len);
                // 停止请求可能在驱动调用期间到达；此时不再输出已取消的这一轮结果。
                if (cts.IsCancellationRequested) break;
                AddConsoleTransaction("RX", "控制台周期读", address, res);
                Print(res.Ok
                    ? $"RX {I2cPage.DisplayAddress(address)} len={loop.Len}  {I2cPage.Grouped(res.Data!)}  [{res.Ms:F1} ms]"
                    : $"周期读失败：{GinkgoDriver.ErrorName(res.Ret)}", res.Ok ? Purple : Red);
                if (res.Ok) fails = 0;
                else if (++fails >= failLimit)
                {
                    Print($"连续 {fails} 次失败，周期读已自动停止（可先检查接线再重新启动）", Orange);
                    break;
                }
                await Task.Delay(loop.PeriodMs, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Print($"周期读已中断：{ex.Message}", Red);
            Dbg.Log($"ConsolePage.RunReadLoopAsync: failed={ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_readLoopCts, cts)) _readLoopCts = null;
            UpdateReadLoopActionState();
            cts.Dispose();
            Print("周期读已停止", Gray);
            Dbg.Log("ConsolePage.RunReadLoopAsync: stopped");
        }
    }

    private void StopReadLoop(bool announce)
    {
        if (_readLoopCts is null)
        {
            if (announce) Print("没有正在运行的周期读", Orange);
            return;
        }
        _readLoopCts.Cancel();
        UpdateReadLoopActionState();
        Dbg.Log("ConsolePage.StopReadLoop: cancellation requested");
        if (announce) Print("正在停止周期读…", Gray);
    }

    private void StopScript(bool announce)
    {
        if (_scriptCts is null)
        {
            if (announce) Print("没有正在运行的脚本", Orange);
            return;
        }
        _scriptCts.Cancel();
        Dbg.Log("ConsolePage.StopScript: cancellation requested");
        if (announce) Print("正在中止脚本…", Gray);
    }

    /// <summary>
    /// run 脚本：按行走与手工输入相同的 Parse→Exec 链路。# 注释与空行跳过；
    /// 任一行解析或执行失败即停，避免初始化序列半途而废继续跑后续步骤。
    /// </summary>
    private async Task RunScriptAsync(string rawPath)
    {
        if (_scriptCts is not null)
        {
            Print("已有脚本在运行（Esc 中止后再试）；脚本内不支持嵌套 run", Orange);
            return;
        }
        string path = ResolveScriptPath(rawPath);
        if (!File.Exists(path))
        {
            Print($"找不到脚本文件：{path}", Red);
            return;
        }
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex)
        {
            Print($"读取脚本失败：{ex.Message}", Red);
            return;
        }

        _scriptCts = new CancellationTokenSource();
        Print($"run {path} · {lines.Length} 行", Purple);
        Dbg.Log($"ConsolePage.RunScriptAsync: start path={path} lines={lines.Length}");
        int lineno = 0;
        bool aborted = false;
        try
        {
            foreach (string raw in lines)
            {
                lineno++;
                if (_scriptCts.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var (cmd, err) = CommandParser.Parse(line);
                if (cmd is null)
                {
                    Print($"脚本第 {lineno} 行：{err ?? "解析失败"}", Red);
                    aborted = true;
                    break;
                }
                _history.Add(line);
                Print($"> {line}", Cyan);
                try
                {
                    await Exec(cmd);
                }
                catch (OperationCanceledException)
                {
                    aborted = true;
                    break;
                }
                catch (Exception ex)
                {
                    Print($"脚本第 {lineno} 行执行失败：{ex.Message}", Red);
                    aborted = true;
                    break;
                }
            }
        }
        finally
        {
            _scriptCts.Dispose();
            _scriptCts = null;
            Print(aborted ? $"脚本已中止（第 {lineno} 行）" : $"脚本结束 · 共 {lineno} 行", aborted ? Orange : Gray);
            Dbg.Log($"ConsolePage.RunScriptAsync: ended line={lineno} aborted={aborted}");
            RefreshConsoleSession();
        }
    }

    /// <summary>裸文件名优先到 %APPDATA%\GinkgoHost\scripts\ 下找；绝对路径原样返回。</summary>
    private static string ResolveScriptPath(string p)
    {
        if (Path.IsPathRooted(p)) return p;
        if (File.Exists(p)) return Path.GetFullPath(p);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GinkgoHost", "scripts", p);
    }

    private void UpdateReadLoopActionState()
    {
        if (BtnStopRead is null) return;
        bool active = _readLoopCts is not null;
        BtnStopRead.IsEnabled = active;
        // 空闲时直接收起：置灰的 Danger 按钮带亮边框，看起来像可点的高亮项
        BtnStopRead.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        BtnStopRead.ToolTip = active ? "停止正在运行的周期读" : "没有正在运行的周期读";
        RefreshConsoleSession();
    }

    /// <summary>镜像窗口的连接闸门：漏斗繁忙时命令行不重复发起开合。</summary>
    public void SetConnectionBusy(bool busy)
    {
        _connectionBusy = busy;
        RefreshConsoleSession();
    }

    /// <summary>控制台头部仅呈现会话摘要，命令校验仍由底部状态栏负责，避免信息重复。</summary>
    private void RefreshConsoleSession()
    {
        if (TxtConsoleSession is null) return;
        string connection = App.Bus.IsOpen
            ? $"CH{App.Bus.Channel} · {App.Bus.ClockHz / 1000} kHz"
            : _connectionBusy ? "连接处理中" : "未连接";
        string loop = _readLoopCts switch
        {
            { IsCancellationRequested: true } => "停止中",
            not null => "轮询中",
            _ => "空闲"
        };
        TxtConsoleSession.Text = $"{connection} · {loop} · 历史 {_history.Count}";
        TxtConsoleSession.Foreground = App.Bus.IsOpen ? Purple : _connectionBusy ? Orange : Gray;
#if DEBUG
        Dbg.Log($"ConsolePage.RefreshConsoleSession: connected={App.Bus.IsOpen} loop={loop} history={_history.Count}");
#endif
    }

    /// <summary>控制台输入跟随全局地址格式，底层驱动始终接收 7-bit 地址。</summary>
    private static byte ConsoleAddr7(byte address)
    {
        if (App.Settings.AddrFmt != 1)
        {
            if (address > 0x7F) throw new ArgumentException("7-bit 地址须在 00–7F 之间");
            return address;
        }
        if ((address & 1) != 0) throw new ArgumentException("8-bit 地址须为偶数写地址，例如 92");
        return (byte)(address >> 1);
    }

    private void UpdateCommandHint() => TxtIn.PlaceholderText = $"输入命令，例如：{CommandExample()}";

    /// <summary>输入阶段只反馈解析结果；实际执行前仍会重新解析，避免状态与执行脱节。</summary>
    private void UpdateCommandState()
    {
        if (_executing) return;

        if (string.IsNullOrWhiteSpace(TxtIn.Text))
        {
            SetCommandState("就绪", Gray, false, "输入命令后自动校验");
            return;
        }

        var (cmd, err) = CommandParser.Parse(TxtIn.Text);
        if (cmd is not null)
        {
            SetCommandState("可运行 · Enter 执行", Purple, true, "命令格式有效");
            return;
        }

        string error = err ?? "解析失败";
        SetCommandState($"错误：{error}", Red, false, error, invalidInput: true);
    }

    private void SetCommandState(string text, Brush brush, bool canRun, string tooltip, bool invalidInput = false)
    {
        TxtConsoleState.Text = text;
        TxtConsoleState.Foreground = brush;
        TxtConsoleState.ToolTip = tooltip;
        TxtIn.ToolTip = tooltip;
        if (invalidInput) TxtIn.BorderBrush = Red;
        else TxtIn.ClearValue(Control.BorderBrushProperty);
        BtnRun.IsEnabled = canRun;
#if DEBUG
        // 文本输入会逐字触发校验；相同状态只记录一次，避免调试日志淹没真实命令路径。
        string debugText = $"{canRun}|{invalidInput}|{text}";
        if (!string.Equals(_lastCommandStateDebugText, debugText, StringComparison.Ordinal))
        {
            _lastCommandStateDebugText = debugText;
            Dbg.Log($"ConsolePage.SetCommandState: canRun={canRun} invalidInput={invalidInput} text={text}");
        }
#endif
    }

    private static string CommandExample()
    {
        string address = App.Settings.AddrFmt == 1 ? "92" : "49";
        return $"read {address} 00 8";
    }

    private static void AddConsoleTransaction(string direction, string operation, byte address, OpResult result, byte[]? writeData = null) =>
        App.Log.AddCapped(new LogEntry(DateTime.Now, direction, operation, I2cPage.DisplayAddress(address), result.Ret, result.Ms,
            direction == "TX" ? writeData : result.Data));
}
