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

        string line = TxtIn.Text;
        TxtIn.Clear();
        if (string.IsNullOrWhiteSpace(line)) return;
        _history.Add(line);
        _historyIdx = _history.Count;
        Print($"> {line}", Cyan);

        var (cmd, err) = CommandParser.Parse(line);
        if (cmd is null)
        {
            Print(err ?? "解析失败", Red);
            return;
        }
        _executing = true;
        TxtIn.IsEnabled = false;
        TxtConsoleState.Text = "执行中…";
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
            TxtConsoleState.Text = "就绪";
            TxtIn.Focus();
            Dbg.Log($"ConsolePage.TxtIn_KeyDown: completed {cmd.GetType().Name}");
        }
    }

    private async Task Exec(Cmd cmd)
    {
        switch (cmd)
        {
            case HelpCmd:
                Print("connect                       连接适配器", Gray);
                Print("disconnect                    断开适配器", Gray);
                Print("adapters                      列出适配器数量", Gray);
                Print("speed <kHz>                   修改速率，如 speed 400", Gray);
                Print("scan                          扫描从机地址 0x08–0x77", Gray);
                Print("read <addr7> [reg] <len>      读，如 read 50 8 或 read 50 00 8", Gray);
                Print("write <addr7> [reg] <b...>    写，如 write 50 de ad 或 write 50 00 de ad", Gray);
                Print("clear                         清屏", Gray);
                break;

            case ClearCmd:
                _out.Clear();
                break;

            case ConnectCmd:
            {
                var (count, ret) = await App.Bus.ConnectAsync(
                    App.Settings.Channel, App.Settings.ClockHz, (byte)App.Settings.ControlMode);
                Print(count <= 0
                    ? "未检测到 Ginkgo 适配器"
                    : ret == 0
                        ? $"已连接 · 通道 {App.Settings.Channel} · {App.Settings.ClockHz / 1000} kHz"
                        : $"打开失败：{GinkgoDriver.ErrorName(ret)}", ret == 0 ? Purple : Red);
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
                    : "命中：" + string.Join("  ", found.Select(a => $"0x{a:X2}")), Purple);
                break;
            }

            case ReadCmd r:
            {
                var res = r.Reg is byte reg
                    ? await App.Bus.ReadRegisterAsync(r.Addr, reg, r.Len)
                    : await App.Bus.RawReadAsync(r.Addr, r.Len);
                Print(res.Ok
                    ? $"RX 0x{r.Addr:X2} len={r.Len}  {I2cPage.Grouped(res.Data!)}  [{res.Ms:F1} ms]"
                    : $"读取失败：{GinkgoDriver.ErrorName(res.Ret)}", res.Ok ? Purple : Red);
                break;
            }

            case WriteCmd w:
            {
                var res = w.Reg is byte reg
                    ? await App.Bus.WriteRegisterAsync(w.Addr, reg, w.Data)
                    : await App.Bus.RawWriteAsync(w.Addr, w.Data);
                Print(res.Ok
                    ? $"TX 0x{w.Addr:X2} len={w.Data.Length}  {I2cPage.Grouped(w.Data)}  [{res.Ms:F1} ms]"
                    : $"写入失败：{GinkgoDriver.ErrorName(res.Ret)}", res.Ok ? Orange : Red);
                break;
            }
        }
    }
}
