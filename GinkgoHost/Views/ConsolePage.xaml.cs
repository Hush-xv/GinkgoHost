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
    public sealed record ConsoleLine(string Line, Brush Brush);

    private readonly ObservableCollection<ConsoleLine> _out = new();
    private readonly List<string> _history = new();
    private int _historyIdx;
    private bool _executing;
    private CancellationTokenSource? _readLoopCts;

    private static readonly Brush Cyan = new SolidColorBrush(Color.FromRgb(0x90, 0xca, 0xf9));
    private static readonly Brush Purple = new SolidColorBrush(Color.FromRgb(0x9a, 0x86, 0xfd));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xff, 0xa7, 0x26));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xef, 0x53, 0x50));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9e, 0x9e, 0x9e));

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
        };
        Unloaded += (_, _) =>
        {
            App.Bus.StateChanged -= OnBusStateChanged;
            StopReadLoop(false);
        };
    }

    private void Print(string text, Brush brush)
    {
        _out.Add(new ConsoleLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}", brush));
        if (_out.Count > 1000) _out.RemoveAt(0); // 控制台也走环形，防长跑膨胀
        LstOut.ScrollIntoView(LstOut.Items[^1]);
    }

    private void BtnClearOutput_Click(object sender, RoutedEventArgs e)
    {
        Dbg.Log($"ConsolePage.BtnClearOutput_Click: clear {_out.Count} lines");
        _out.Clear();
        Print("输出已清空。输入 help 查看命令。", Gray);
        TxtIn.Focus();
    }

    private void OnBusStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnBusStateChanged);
            return;
        }
        if (!App.Bus.IsOpen) StopReadLoop(false);
    }

    private async void TxtIn_KeyDown(object sender, KeyEventArgs e)
    {
        if (_executing) return;
        if (e.Key == Key.Up)
        {
            if (_history.Count == 0) return;
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
            TxtIn.Text = _historyIdx < _history.Count ? _history[_historyIdx] : string.Empty;
            TxtIn.CaretIndex = TxtIn.Text.Length;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunInputAsync();
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e) => await RunInputAsync();

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
        TxtIn.Clear();
        if (string.IsNullOrWhiteSpace(line)) return;
        _history.Add(line);
        _historyIdx = _history.Count;
        Print($"> {line}", Cyan);

        var (cmd, err) = CommandParser.Parse(line);
        if (cmd is null)
        {
            Print($"{err ?? "解析失败"} · {CommandExample()}", Red);
            return;
        }
        _executing = true;
        TxtIn.IsEnabled = false;
        BtnRun.IsEnabled = false;
        TxtConsoleState.Text = "执行中…";
        TxtConsoleState.Foreground = Orange;
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
            BtnRun.IsEnabled = true;
            TxtConsoleState.Text = "就绪";
            TxtConsoleState.Foreground = Gray;
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
                Print("adapters                      列出适配器数量", Gray);
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
                _out.Clear();
                break;

            case StatusCmd:
                Print(App.Bus.IsOpen
                    ? $"已连接 · 通道 {App.Bus.Channel} · {App.Bus.ClockHz / 1000} kHz"
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
                var (count, ret) = await App.Bus.ConnectAsync(
                    App.Settings.Channel, App.Settings.ClockHz, (byte)App.Settings.ControlMode);
                Print(count <= 0
                    ? "未检测到 Ginkgo 适配器"
                    : ret == 0
                        ? $"已连接 · 通道 {App.Settings.Channel} · {App.Settings.ClockHz / 1000} kHz"
                        : $"打开失败：{GinkgoDriver.ErrorName(ret)}",
                    count <= 0 ? Orange : ret == 0 ? Purple : Red);
                break;
            }

            case DisconnectCmd:
                await App.Bus.CloseAsync();
                Print("已断开", Gray);
                break;

            case ScanAdapterCmd:
                Print($"适配器数量：{App.Bus.AdapterCount}（connect 时自动扫描）", Gray);
                break;

            case SpeedCmd s:
            {
                int ret = await App.Bus.ApplyConfigAsync(
                    App.Settings.Channel, s.KHz * 1000, (byte)App.Settings.ControlMode);
                App.Settings.ClockHz = s.KHz * 1000;
                App.Settings.Save();
                Print(ret == 0 ? $"速率已切换 {s.KHz} kHz" : $"切换失败：{GinkgoDriver.ErrorName(ret)}", ret == 0 ? Purple : Red);
                break;
            }

            case ScanBusCmd:
            {
                var found = await App.Bus.ScanBusAsync();
                Print(found.Count == 0
                    ? "总线空闲，未发现从机"
                    : "命中：" + string.Join("  ", found.Select(I2cPage.DisplayAddress)),
                    found.Count == 0 ? Orange : Purple);
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
                if (_readLoopCts is not null)
                {
                    Print("已有周期读正在运行；输入 stop 后再启动新的周期读", Orange);
                    break;
                }
                _readLoopCts = new CancellationTokenSource();
                _ = RunReadLoopAsync(loop, _readLoopCts);
                Print($"周期读已启动 · {I2cPage.DisplayAddress(ConsoleAddr7(loop.Addr))} · {loop.PeriodMs} ms · 输入 stop 停止", Purple);
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
        Dbg.Log("ConsolePage.StopReadLoop: cancellation requested");
        if (announce) Print("正在停止周期读…", Gray);
    }

    /// <summary>控制台输入跟随全局地址格式，底层驱动始终接收 7-bit 地址。</summary>
    private static byte ConsoleAddr7(byte address)
    {
        if (App.Settings.AddrFmt != 1) return address;
        if ((address & 1) != 0) throw new ArgumentException("8-bit 地址须为偶数写地址，例如 92");
        return (byte)(address >> 1);
    }

    private void UpdateCommandHint() => TxtIn.PlaceholderText = $"输入命令，例如：{CommandExample()}";

    private static string CommandExample()
    {
        string address = App.Settings.AddrFmt == 1 ? "92" : "49";
        return $"read {address} 00 8";
    }

    private static void AddConsoleTransaction(string direction, string operation, byte address, OpResult result, byte[]? writeData = null) =>
        App.Log.AddCapped(new LogEntry(DateTime.Now, direction, operation, I2cPage.DisplayAddress(address), result.Ret, result.Ms,
            direction == "TX" ? writeData : result.Data));
}
