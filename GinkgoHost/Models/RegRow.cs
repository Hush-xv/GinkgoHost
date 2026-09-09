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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
