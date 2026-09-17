using System.Collections.ObjectModel;
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

    public sealed record ConsoleLine(string Line, Brush Brush);

    private readonly ObservableCollection<ConsoleLine> _out = new();
    private readonly List<string> _history = new();
    private int _historyIdx;
    private string _historyDraft = string.Empty;
    private bool _executing;
    private bool _followOutput = true;
    private CancellationTokenSource? _readLoopCts;
#if DEBUG
    private string? _lastCommandStateDebugText;
#endif

    // 控制台输出色：选亮/暗两种底色都可读的中间色值（画刷随行记录绑定，不随主题动态切换）
    private static readonly Brush Cyan = new SolidColorBrush(Color.FromRgb(0x4B, 0xA3, 0xE3));
    private static readonly Brush Purple = new SolidColorBrush(Color.FromRgb(0x7C, 0x68, 0xD8));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xE8, 0x96, 0x0C));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE2, 0x3A, 0x37));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public ConsolePage()
    {
        InitializeComponent();
        LstOut.ItemsSource = _out;
        Print("GinkgoHost 控制台。输入 help 查看命令。", Gray);
        TxtIn.GotKeyboardFocus += (_, _) => UpdateCommandHint();
        Loaded += (_, _) =>
        {
            App.Bus.StateChanged += OnBusStateChanged;
            UpdateCommandHint();
            UpdateCommandState();
            UpdateReadLoopActionState();
            RefreshConsoleSession();
            FocusCommandInput(); // 切入页面后可直接输入命令
        };
        Unloaded += (_, _) =>
        {
            App.Bus.StateChanged -= OnBusStateChanged;
            StopReadLoop(false);
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
        _out.Add(new ConsoleLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}", brush));
        if (_out.Count > 1000) _out.RemoveAt(0); // 控制台也走环形，防长跑膨胀
        if (_followOutput && LstOut.Items.Count > 0)
            LstOut.ScrollIntoView(LstOut.Items[^1]);
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

    private async void TxtIn_KeyDown(object sender, KeyEventArgs e)
    {
        if (_executing) return;
        if (e.Key == Key.Escape && _readLoopCts is not null)
        {
            StopReadLoop(true);
            e.Handled = true;
            return;
        }
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
        if (_followOutput && LstOut is not null && LstOut.Items.Count > 0)
            LstOut.ScrollIntoView(LstOut.Items[^1]);
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
            Print($"{err ?? "解析失败"} · {CommandExample()}", Red);
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
                var (count, ret) = await App.Bus.ConnectAsync(
                    App.Settings.Channel, App.Settings.ClockHz, (byte)App.Settings.ControlMode);
                int logRet = count <= 0 ? (count == 0 ? -15 : count) : ret;
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "控制台连接", "—", logRet, 0,
                    logRet == 0
                        ? System.Text.Encoding.UTF8.GetBytes($"Ginkgo · CH{App.Settings.Channel} · {App.Settings.ClockHz / 1000} kHz")
                        : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(logRet))));
                Print(count <= 0
                    ? "未检测到 Ginkgo 适配器"
                    : ret == 0
                        ? $"已连接 · 通道 {App.Settings.Channel} · {App.Settings.ClockHz / 1000} kHz"
                        : $"打开失败：{GinkgoDriver.ErrorName(ret)}",
                    count <= 0 ? Orange : ret == 0 ? Purple : Red);
#if DEBUG
                Dbg.Log($"ConsolePage.Exec.ConnectCmd: adapters={count} ret={ret} logRet={logRet}");
#endif
                break;
            }

            case DisconnectCmd:
            {
                int ret = await App.Bus.CloseAsync();
                App.Log.AddCapped(new LogEntry(DateTime.Now, "SYS", "控制台断开", "—", ret, 0,
                    ret == 0 ? null : System.Text.Encoding.UTF8.GetBytes(GinkgoDriver.ErrorName(ret))));
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

    private void UpdateReadLoopActionState()
    {
        if (BtnStopRead is null) return;
        bool active = _readLoopCts is not null;
        BtnStopRead.IsEnabled = active;
        BtnStopRead.ToolTip = active ? "停止正在运行的周期读" : "没有正在运行的周期读";
        RefreshConsoleSession();
    }

    /// <summary>控制台头部仅呈现会话摘要，命令校验仍由底部状态栏负责，避免信息重复。</summary>
    private void RefreshConsoleSession()
    {
        if (TxtConsoleSession is null) return;
        string connection = App.Bus.IsOpen
            ? $"CH{App.Bus.Channel} · {App.Bus.ClockHz / 1000} kHz"
            : "未连接";
        string loop = _readLoopCts switch
        {
            { IsCancellationRequested: true } => "停止中",
            not null => "轮询中",
            _ => "空闲"
        };
        TxtConsoleSession.Text = $"{connection} · {loop} · 历史 {_history.Count}";
        TxtConsoleSession.Foreground = App.Bus.IsOpen ? Purple : Gray;
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
