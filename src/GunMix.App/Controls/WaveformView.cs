using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GunMix.Core.Audio;

namespace GunMix.App.Controls;

/// <summary>波形缩略图：显示源文件波形（min/max 桶峰值），双声道合并显示。</summary>
public class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(WaveformPeaks), typeof(WaveformView),
        new PropertyMetadata(null, static (d, _) => ((WaveformView)d).InvalidateVisual()));

    public static readonly DependencyProperty WaveColorProperty = DependencyProperty.Register(
        nameof(WaveColor), typeof(Color), typeof(WaveformView),
        new PropertyMetadata(Color.FromRgb(0x5F, 0xA8, 0xD8), static (d, _) => ((WaveformView)d).InvalidateVisual()));

    public WaveformPeaks? Peaks
    {
        get => (WaveformPeaks?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public Color WaveColor
    {
        get => (Color)GetValue(WaveColorProperty);
        set => SetValue(WaveColorProperty, value);
    }

    public WaveformView()
    {
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = TryFindResource("PanelBg") as Brush ?? Brushes.Transparent;
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var peaks = Peaks;
        if (peaks == null || peaks.Buckets == 0 || ActualWidth < 2) return;

        double mid = ActualHeight / 2;
        var brush = new SolidColorBrush(WaveColor);
        brush.Freeze();
        double step = ActualWidth / peaks.Buckets;
        for (int i = 0; i < peaks.Buckets; i++)
        {
            double hi = -peaks.Max[i] * mid * 0.95;
            double lo = -peaks.Min[i] * mid * 0.95;
            double y0 = Math.Max(0, mid + hi);
            double y1 = Math.Min(ActualHeight, mid + lo);
            if (y1 - y0 < 1) { y0 = mid - 0.5; y1 = mid + 0.5; }
            dc.DrawRoundedRectangle(brush, null, new Rect(i * step, y0, Math.Max(1, step - 0.4), y1 - y0), 0.5, 0.5);
        }
    }
}

/// <summary>静音/独听状态小按钮。独听用文字 S 标注。</summary>
public static class FormatUtil
{
    public static string FmtDb(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public static string FmtMs(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public static string FmtTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}
