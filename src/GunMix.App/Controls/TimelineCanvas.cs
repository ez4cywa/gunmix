using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GunMix.App.Controls;

public sealed record TimelineMarker(long StartSample, long LengthSamples, string Label);

/// <summary>序列时间线：各发起点与尾部范围 + 时间刻度。</summary>
public class TimelineCanvas : FrameworkElement
{
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<TimelineMarker>), typeof(TimelineCanvas),
        new PropertyMetadata(null, static (d, _) => ((TimelineCanvas)d).InvalidateVisual()));

    public static readonly DependencyProperty TotalSecondsProperty = DependencyProperty.Register(
        nameof(TotalSeconds), typeof(double), typeof(TimelineCanvas),
        new PropertyMetadata(5.0, static (d, _) => ((TimelineCanvas)d).InvalidateVisual()));

    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(TimelineCanvas),
        new PropertyMetadata(-1.0, static (d, _) => ((TimelineCanvas)d).InvalidateVisual()));

    public IReadOnlyList<TimelineMarker>? Markers
    {
        get => (IReadOnlyList<TimelineMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public double TotalSeconds
    {
        get => (double)GetValue(TotalSecondsProperty);
        set => SetValue(TotalSecondsProperty, value);
    }

    public double PlayheadSeconds
    {
        get => (double)GetValue(PlayheadSecondsProperty);
        set => SetValue(PlayheadSecondsProperty, value);
    }

    public TimelineCanvas()
    {
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = TryFindResource("PanelBg") as Brush ?? Brushes.Transparent;
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var gridPen = CreatePen("#333D48", 1);
        var markerBrush = CreateBrush("#7EC3EC");
        var textBrush = CreateBrush("#8A98A5");
        var playheadPen = CreatePen("#E06C5F", 1.5);
        var tailBrush = CreateBrush("#3A5C7A");

        double w = ActualWidth, h = ActualHeight;
        if (w < 10) return;
        double total = Math.Max(0.05, TotalSeconds);
        double footer = 14;

        // 时间刻度
        double step = ChooseStep(total, w);
        for (double t = 0; t <= total + 1e-9; t += step)
        {
            double x = t / total * w;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h - footer));
            var label = new FormattedText($"{t:0.0}s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 10, textBrush, 1.25);
            dc.DrawText(label, new Point(x + 2, h - footer + 1));
        }

        var markers = Markers;
        if (markers != null)
        {
            foreach (var m in markers)
            {
                double x = m.StartSample / 48000.0 / total * w;
                double mw = Math.Max(2.5, m.LengthSamples / 48000.0 / total * w);
                // 尾部范围
                dc.DrawRectangle(tailBrush, null, new Rect(x, 0, mw, h - footer));
                // 发起点
                dc.DrawRectangle(markerBrush, null, new Rect(x, 0, 2, h - footer));
            }
        }

        if (PlayheadSeconds >= 0)
        {
            double x = PlayheadSeconds / total * w;
            dc.DrawLine(playheadPen, new Point(x, 0), new Point(x, h - footer));
        }
    }

    private static double ChooseStep(double total, double width)
    {
        double rough = total / Math.Max(4, width / 80);
        double[] steps = [0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 5, 10, 30, 60];
        foreach (var s in steps)
            if (s >= rough) return s;
        return 60;
    }

    private static Pen CreatePen(string hex, double thickness)
    {
        var pen = new Pen(CreateBrush(hex), thickness);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
