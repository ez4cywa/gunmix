using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GunMix.App.ViewModels;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Mixing;
using GunMix.Core.Timeline;

namespace GunMix.App.Views;

public partial class ExportDialog : Window
{
    private readonly MainViewModel _vm;
    private string? _lastSinglePath;
    private string? _lastBurstPath;

    public ExportDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        // 输出位置可选：默认记住上次目录；格式在代码中选中，避免 XAML 解析期间
        // SelectionChanged 提前触发而访问尚未创建的控件（曾导致点击导出即崩溃）。
        var export = _vm.Project.ActiveWeapon?.Export;
        TxtOutDir.Text = export?.OutputDirectory ?? "";
        TxtSeed.Text = (export?.DitherSeed ?? 20260927).ToString();
        CmbFormat.SelectedIndex = IndexOfBitDepth(export?.BitDepth ?? 24);
        ChkTrimTail.IsChecked = export?.TrimTail ?? true;
        TxtTrimThreshold.Text = (export?.TrimThresholdDb ?? -60).ToString("0.#");
        TxtTrimTailMs.Text = (export?.TrimTailMs ?? 120).ToString("0.#");
        UpdateInfo();
        Loaded += (_, _) => UpdateInfo();
    }

    private static int IndexOfBitDepth(int bits) => bits switch
    {
        16 => 1,
        32 => 2,
        _ => 0,
    };

    private void UpdateInfo()
    {
        var recipe = _vm.CurrentRecipe;
        var weapon = _vm.Project.ActiveWeapon;
        if (recipe == null || weapon == null) return;

        var (singleFile, singleSec, _) = _vm.ExportPreviewInfo(ManifestKind.Single);
        var (burstFile, burstSec, _) = _vm.ExportPreviewInfo(ManifestKind.Burst);
        SingleFileName.Text = singleFile;
        BurstFileName.Text = burstFile;
        SingleDuration.Text = singleSec is { } s1
            ? $"预计时长 {s1:0.000} 秒（完整尾部保留）"
            : "清单尚未生成：关闭后点“重新生成变体”";
        BurstDuration.Text = burstSec is { } s2
            ? $"预计时长 {s2:0.000} 秒（{recipe.BurstRpm} RPM × {recipe.BurstShotCount} 发{(_vm.ReleaseTailEnabled ? "，含松扳机尾音" : "")}）"
            : "清单尚未生成：关闭后点“重新生成变体”";
        BurstBadge.Text = $"{recipe.BurstRpm} RPM × {recipe.BurstShotCount} 发{(_vm.ReleaseTailEnabled ? " + 尾音" : "")}";
    }

    private int BitDepth => CmbFormat.SelectedItem is ComboBoxItem { Tag: string s } ? int.Parse(s) : 24;

    private MainViewModel.ExportOptions BuildOptions(bool single, bool burst)
    {
        _ = int.TryParse(TxtSeed.Text, out var seed);
        double ParseNum(TextBox box, double fallback, double min, double max) =>
            double.TryParse(box.Text, out var v) ? Math.Clamp(v, min, max) : fallback;
        return new MainViewModel.ExportOptions
        {
            ExportSingle = single,
            ExportBurst = burst,
            BitDepth = BitDepth,
            Dither = ChkDither.IsChecked == true,
            DitherSeed = seed,
            AttenuateToDbfs = ChkAttenuate.IsChecked == true ? -1.0 : null,
            OutputDirectory = TxtOutDir.Text.Trim(),
            Overwrite = false, // 同名时默认生成新名称
            TrimTail = ChkTrimTail.IsChecked == true,
            TrimThresholdDb = ParseNum(TxtTrimThreshold, -60, -90, -20),
            TrimTailMs = ParseNum(TxtTrimTailMs, 120, 0, 2000),
        };
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChkDither == null) return; // XAML 解析期间可能提前触发
        ChkDither.IsEnabled = BitDepth != 32; // 抖动只对整数导出有意义
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择导出目录" };
        if (dlg.ShowDialog() == true)
            TxtOutDir.Text = dlg.FolderName;
    }

    private void SetBusy(bool busy)
    {
        BtnSingle.IsEnabled = !busy;
        BtnBurst.IsEnabled = !busy;
        BtnExportChecked.IsEnabled = !busy;
        Bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnExportSingle(object sender, RoutedEventArgs e) => RunExport([ManifestKind.Single]);

    private void OnExportBurst(object sender, RoutedEventArgs e) => RunExport([ManifestKind.Burst]);

    private void OnExportChecked(object sender, RoutedEventArgs e)
    {
        bool single = ChkSingle.IsChecked == true;
        bool burst = ChkBurst.IsChecked == true;
        if (!single && !burst)
        {
            MessageBox.Show(this, "请至少勾选“单发音效”或“连发音效”之一。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var kinds = new List<ManifestKind>();
        if (single) kinds.Add(ManifestKind.Single);
        if (burst) kinds.Add(ManifestKind.Burst);
        RunExport(kinds);
    }

    private void RunExport(IReadOnlyList<ManifestKind> kinds)
    {
        var options = BuildOptions(
            kinds.Contains(ManifestKind.Single),
            kinds.Contains(ManifestKind.Burst));

        SetBusy(true);
        ResultText.Text = "";

        Task.Run(() => kinds.Select(kind => (Kind: kind, Result: _vm.ExportKind(kind, options))).ToList())
            .ContinueWith(t =>
            {
                SetBusy(false);
                if (t.IsFaulted)
                {
                    ResultText.Text = $"导出失败：{t.Exception?.GetBaseException().Message}";
                    return;
                }
                var sb = new System.Text.StringBuilder();
                foreach (var (kind, result) in t.Result)
                {
                    var label = kind == ManifestKind.Single ? "单发" : "连发";
                    if (result is { Success: true, FinalPath: not null })
                    {
                        if (kind == ManifestKind.Single) _lastSinglePath = result.FinalPath;
                        else _lastBurstPath = result.FinalPath;
                        sb.AppendLine($"[成功] {label}：{result.FinalPath}");
                        sb.AppendLine($"       时长 {result.DurationSeconds:0.000}s，量化前峰值 {result.PeakDbfs:0.00} dBFS，" +
                                      $"文件峰值 {result.QuantizedPeakDbfs:0.00} dBFS" +
                                      (result.AppliedGainDb != null ? $"，应用显式衰减 {result.AppliedGainDb:0.00} dB（已记录进报告）" : "") +
                                      (result.TrimmedFrames > 0 ? $"，自动截尾 {result.TrimmedFrames / 48000.0:0.000}s（阈值 {result.TrimThresholdDb:0.#} dB，保留尾 {result.TrimTailMs:0.#} ms）" : ""));
                        sb.AppendLine($"       配套 JSON：{Path.ChangeExtension(result.FinalPath, ".recipe.json")}");
                    }
                    else
                    {
                        sb.AppendLine($"[失败] {label}：{result?.Error ?? "未知错误"}");
                    }
                }
                sb.AppendLine(options.TrimTail
                    ? "峰值基于实际混合结果计算；尾部静音已按设定阈值截断（参数记入报告与 JSON）。"
                    : "峰值基于实际混合结果计算；未启用截尾，尾部完整自然结束。");
                ResultText.Text = sb.ToString();
                UpdateInfo(); // 计数器已递增，刷新目标文件名
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnPlaySingle(object sender, RoutedEventArgs e)
    {
        var path = _lastSinglePath;
        if (path == null || !File.Exists(path))
        {
            MessageBox.Show(this, "还没有成功导出的单发文件。", "播放", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法播放：{ex.Message}", "播放", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = _lastSinglePath ?? _lastBurstPath;
        var dir = path != null ? Path.GetDirectoryName(path) : (TxtOutDir.Text.Length > 0 ? TxtOutDir.Text : null);
        if (dir == null || !Directory.Exists(dir))
        {
            MessageBox.Show(this, "输出目录不存在。", "打开文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
