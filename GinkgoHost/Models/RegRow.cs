using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GinkgoHost.Models;

/// <summary>扩展页寄存器表/初始化序列共用行模型。Reg/Value 均为 hex 字符串。</summary>
public sealed class RegRow : INotifyPropertyChanged
{
    private string _reg = "00";
    private int _len = 1;
    private int _periodMs;
    private string _dir = "R";
    private string _value = "";
    private int _delayMs;
    private string _note = "";
    private string _status = "";
    private bool _polling;
    private int _pollCount;
    private bool _canExecute;
    private string _executeText = "执行";
    private string _executeToolTip = "执行此行";
    private bool _snapshotChanged;

    public string Reg { get => _reg; set { _reg = value; OnPropertyChanged(); } }
    public int Len { get => _len; set { _len = value; OnPropertyChanged(); } }
    /// <summary>周期轮询间隔 ms；0 = 不轮询。周期触发按此调度。</summary>
    public int PeriodMs { get => _periodMs; set { _periodMs = value; OnPropertyChanged(); } }
    /// <summary>R=读 W=写。初始化序列固定按写处理。</summary>
    public string Dir { get => _dir; set { _dir = value; OnPropertyChanged(); } }
    /// <summary>hex 字节串，空格分隔（如 "DE AD"）。</summary>
    public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
    /// <summary>初始化序列中该行执行前的延时。</summary>
    public int DelayMs { get => _delayMs; set { _delayMs = value; OnPropertyChanged(); } }
    public string Note { get => _note; set { _note = value; OnPropertyChanged(); } }

    /// <summary>最近一次执行结果：OK / ERR xx / 空。驱动行底色。</summary>
    [JsonIgnore]
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    /// <summary>周期轮询运行中标记。驱动行高亮；不落盘。</summary>
    [JsonIgnore]
    public bool Polling { get => _polling; set { _polling = value; OnPropertyChanged(); } }

    /// <summary>本轮周期轮询已执行次数，显示在执行按钮上。</summary>
    [JsonIgnore]
    public int PollCount { get => _pollCount; set { _pollCount = value; OnPropertyChanged(); } }

    /// <summary>行内操作可用性；轮询行在全局操作锁定时仍可点击停止。</summary>
    [JsonIgnore]
    public bool CanExecute { get => _canExecute; set { _canExecute = value; OnPropertyChanged(); } }

    /// <summary>行内操作文本与提示，不依赖虚拟化 DataGrid 单元格实例。</summary>
    [JsonIgnore]
    public string ExecuteText { get => _executeText; set { _executeText = value; OnPropertyChanged(); } }

    [JsonIgnore]
    public string ExecuteToolTip { get => _executeToolTip; set { _executeToolTip = value; OnPropertyChanged(); } }

    /// <summary>最近一次“读取全部”相对当前快照发生变化；只驱动界面，不落盘。</summary>
    [JsonIgnore]
    public bool SnapshotChanged { get => _snapshotChanged; set { _snapshotChanged = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
