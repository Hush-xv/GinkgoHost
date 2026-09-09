using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GinkgoHost.Views;

/// <summary>引脚示意圆点。电源/地显示灰色，信号线显示品牌紫。</summary>
public partial class PinDot : UserControl
{
    public static readonly DependencyProperty SignalProperty =
        DependencyProperty.Register(nameof(Signal), typeof(string), typeof(PinDot),
            new PropertyMetadata("SDA", (_, e) => ((PinDot)_).Refresh()));

    public static readonly DependencyProperty GrayProperty =
        DependencyProperty.Register(nameof(Gray), typeof(bool), typeof(PinDot),
            new PropertyMetadata(false, (_, e) => ((PinDot)_).Refresh()));

    public string Signal { get => (string)GetValue(SignalProperty); set => SetValue(SignalProperty, value); }
    public bool Gray { get => (bool)GetValue(GrayProperty); set => SetValue(GrayProperty, value); }

    private static readonly Brush SignalBrush = new SolidColorBrush(Color.FromRgb(0x9a, 0x86, 0xfd));
    private static readonly Brush PowerBrush = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));

    public PinDot()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        TxtSignal.Text = Signal;
        Dot.Fill = Gray ? PowerBrush : SignalBrush;
        TxtSignal.Opacity = Gray ? 0.55 : 1.0;
    }
}
