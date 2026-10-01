using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GunMix.App.ViewModels;
using GunMix.App.Views;
using GunMix.Core.Audio;
using GunMix.Core.Model;

namespace GunMix.App;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 命令行传入工程路径时直接打开（双击 .gunmix.json / 关联程序 / 自动化测试）。
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            Vm.OpenProjectPath(args[1]);
            return;
        }
        Vm.TryRestoreRecovery();
    }

    // ───────── 键盘 ─────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入框聚焦时，Space 不触发播放
        if (e.OriginalSource is TextBox or ComboBox or Slider) return;
        if (e.Key == Key.Space)
        {
            if (Vm.IsPlaying) Vm.Playback.Stop();
            else if (Vm.PlayCommand.CanExecute(null)) Vm.PlayCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (Vm.ExportCommand.CanExecute(null)) Vm.ExportCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!Vm.ConfirmDiscardIfDirty())
        {
            e.Cancel = true;
            return;
        }
        Vm.Shutdown();
    }

    // ───────── 素材库交互 ─────────

    private void OnAssetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as TreeViewItem)?.DataContext is not MainViewModel.AssetNodeVm node || node.Asset == null) return;
        PreviewAsset(node.Asset);
    }

    /// <summary>直接文件预听（与配方播放互斥：先停止当前输出）。</summary>
    private void PreviewAsset(AssetInfo asset)
    {
        Vm.Playback.Stop(immediate: true);
        try
        {
            var decoded = WavReader.Read(Vm.AssetPathOf(asset));
            var stereo = decoded.Channels == 2 ? decoded.Data : MonoToStereo(decoded.Data);
            Vm.Playback.Play(stereo, decoded.SampleRate, 2, Vm.SelectedDevice?.Device, 0, err =>
            {
                if (err != null)
                    Vm.ErrorText = $"无法预听：{err}";
                else
                {
                    Vm.StatusText = $"文件预听：{asset.FileName}（原始格式 {decoded.SampleRate} Hz，不改当前配方）";
                    Vm.ErrorText = "";
                }
            });
        }
        catch (Exception ex)
        {
            Vm.ErrorText = $"无法预听：{ex.Message}";
        }
    }

    private static float[] MonoToStereo(float[] mono)
    {
        var stereo = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            stereo[i * 2] = mono[i];
            stereo[i * 2 + 1] = mono[i];
        }
        return stereo;
    }

    private void OnAssetContextMenu(object sender, ContextMenuEventArgs e)
    {
        if ((sender as TreeViewItem)?.DataContext is not MainViewModel.AssetNodeVm node || node.Asset == null) return;
        var asset = node.Asset;
        var menu = new ContextMenu();
        var poolItem = new MenuItem { Header = $"把分组 {asset.GroupKey} 指定到选中层" };
        poolItem.Click += (_, _) =>
        {
            if (Vm.SelectedLayer is { } layer)
                Vm.AssignPool(layer.Model, asset.GroupKey);
            else
                Vm.ErrorText = "请先在中央选中一层。";
        };
        menu.Items.Add(poolItem);
        var manual = new MenuItem { Header = "手动分组…" };
        manual.Click += (_, _) =>
        {
            var name = InputDialog.Show(this, "手动分组", $"为 {asset.FileName} 指定分组键（手动分组优先，重新扫描不覆盖）：", asset.GroupKey);
            if (!string.IsNullOrWhiteSpace(name))
                Vm.AssignGroup(asset, name.Trim());
        };
        menu.Items.Add(manual);
        var preview = new MenuItem { Header = "预听此文件" };
        preview.Click += (_, _) => PreviewAsset(asset);
        menu.Items.Add(preview);
        (sender as TreeViewItem)!.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ───────── 层行交互 ─────────

    private void OnLayerRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is LayerVm vm)
            Vm.SelectedLayer = vm;
    }

    private void OnPlayLayerClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LayerVm vm)
            Vm.PlayLayer(vm);
    }

    /// <summary>动画音效工作台：独立窗口，与分层工作台共用混音内核与工程。</summary>
    private void OnOpenAnimation(object sender, RoutedEventArgs e)
    {
        var win = new Views.AnimationWindow(Vm) { Owner = this };
        win.Show();
    }

    private void OnAssignPoolClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedLayer is not { } layer)
        {
            Vm.ErrorText = "请先在中央选中一层。";
            return;
        }
        if (CmbPoolAssign.SelectedItem is not string group || group.Length == 0)
        {
            Vm.ErrorText = "请先选择一个分组。";
            return;
        }
        Vm.AssignPool(layer.Model, group);
    }

    // ───────── 事件表 ─────────

    private void OnEventRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is MainViewModel.EventRowVm row)
        {
            var layerVm = Vm.Layers.FirstOrDefault(l => l.Id == row.LayerId);
            if (layerVm == null) return;
            var pool = layerVm.Pool;
            if (pool.Count == 0)
            {
                Vm.ErrorText = "该层没有可用素材池，无法替换。";
                return;
            }
            var menu = new ContextMenu();
            foreach (var asset in pool)
            {
                var mi = new MenuItem { Header = asset.VariantLabel, Tag = asset.Id };
                mi.Click += (s, _) =>
                {
                    if (s is MenuItem { Tag: Guid id })
                        Vm.ReplaceEventAsset(row, id);
                };
                menu.Items.Add(mi);
            }
            grid.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
