using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GunMix.App.Controls;

/// <summary>颜色字符串 → SolidColorBrush（用于状态文本着色）。</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = [];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0) return Brushes.Gray;
        if (!Cache.TryGetValue(s, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
            brush.Freeze();
            Cache[s] = brush;
        }
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool 反转 → 可见性：false 显示，true 隐藏（空状态覆盖层用）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>变体模式显示名。</summary>
public sealed class VariantModeNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Core.Model.VariantMode.Fixed => "固定样本",
        Core.Model.VariantMode.Rotation => "顺序轮换",
        Core.Model.VariantMode.Random => "随机（种子）",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "固定样本" => Core.Model.VariantMode.Fixed,
        "顺序轮换" => Core.Model.VariantMode.Rotation,
        "随机（种子）" => Core.Model.VariantMode.Random,
        _ => Core.Model.VariantMode.Rotation,
    };
}

/// <summary>实验触发方式显示名。</summary>
public sealed class TriggerNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Core.Model.ExperimentalTrigger.PerShot => "每发",
        Core.Model.ExperimentalTrigger.ShotN => "指定第 N 发",
        Core.Model.ExperimentalTrigger.ReleaseMoment => "序列释放时刻",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "每发" => Core.Model.ExperimentalTrigger.PerShot,
        "指定第 N 发" => Core.Model.ExperimentalTrigger.ShotN,
        "序列释放时刻" => Core.Model.ExperimentalTrigger.ReleaseMoment,
        _ => Core.Model.ExperimentalTrigger.PerShot,
    };
}
