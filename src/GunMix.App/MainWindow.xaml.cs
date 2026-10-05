using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GunMix.App.ViewModels;
using GunMix.App.Views;
using GunMix.Core.Audio;
using GunMix.Core.Model;

namespace GunMix.App;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;
    private void OnOpenFireStudio(object sender,RoutedEventArgs e)
    {
        if(Vm.CurrentRecipe==null){Vm.ErrorText="先导入武器素材或打开工程。";return;}
        Vm.StopPlayback(immediate:true);
        new FireStudioWindow(Vm){Owner=this}.ShowDialog();
    }

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
        // 键盘保存/导出与鼠标点击一样，先提交当前输入框的待生效数值。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.S or Key.E or Key.O
            && Keyboard.FocusedElement is TextBox activeInput)
            activeInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (e.Key == Key.Space)
        {
            // 保留按钮、复选框、下拉框及输入控件的原生空格键行为。
            if (ReservesSpaceKey(Keyboard.FocusedElement as DependencyObject)) return;
            if (Vm.IsPlaying) Vm.StopPlayback();
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

    internal static bool ReservesSpaceKey(DependencyObject? source)
    {
        for (var current = source; current != null;)
        {
            if (current is TextBoxBase or PasswordBox or Selector or Slider or ButtonBase or ListBoxItem or TreeView or TreeViewItem)
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnInspectorToggle(object sender, RoutedEventArgs e)
    {
        if (InspectorColumn == null || LayerInspector == null) return;
        bool visible = InspectorToggle.IsChecked == true;
        InspectorColumn.Width = new GridLength(visible ? 290 : 0);
        LayerInspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LayerList != null) LayerList.MaxHeight = Math.Clamp(ActualHeight - 620, 140, 600);
    }

    private void OnLayerSettingsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LayerVm layer) return;
        Vm.SelectedLayer = layer;
        InspectorToggle.IsChecked = true;
        CmbPoolAssign.Focus();
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
    private async void PreviewAsset(AssetInfo asset) => await Vm.PreviewAssetAsync(asset);

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

    /// <summary>
    /// 右键层行：列出同武器内与该层角色相关的分组，点击即切换素材池。
    /// 分同角色（优先）与全部分组两段，当前分组打勾；固定样本段可选具体文件。
    /// </summary>
    private void OnLayerContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not LayerVm layer) return;
        Vm.SelectedLayer = layer;

        var weaponId = Vm.CurrentWeaponId;
        var groups = Vm.Project.Assets
            .Where(a => a.WeaponId == weaponId && a.GroupKey.Length > 0)
            .Select(a => a.GroupKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0) { e.Handled = true; return; }

        var roleKeyword = layer.Role switch
        {
            "SHOT" => "shot", "MECH" => "mech", "LOW" => "lfe",
            "SWT" => "swt", "ATMO" => "atmo", _ => "",
        };

        var menu = new System.Windows.Controls.ContextMenu();
        var current = layer.Model.PoolGroupKey;

        void AddGroupItem(string groupKey, bool isCurrent)
        {
            var mi = new MenuItem { Header = groupKey, IsChecked = isCurrent };
            mi.Click += (_, _) => Vm.AssignPool(layer.Model, groupKey);
            menu.Items.Add(mi);
        }

        // ── 同角色分组（优先） ──
        var sameRole = groups.Where(g => g.Contains(roleKeyword, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameRole.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = $"同角色（{roleKeyword}）", IsEnabled = false });
            foreach (var g in sameRole) AddGroupItem(g, g == current);
            menu.Items.Add(new Separator());
        }

        // ── 其他分组 ──
        var others = groups.Except(sameRole).ToList();
        if (others.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = "其他分组", IsEnabled = false });
            foreach (var g in others) AddGroupItem(g, g == current);
            menu.Items.Add(new Separator());
        }

        // ── 固定样本 ──
        var pool = layer.Pool;
        if (pool.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = "固定样本", IsEnabled = false });
            foreach (var asset in pool)
            {
                var mi = new MenuItem
                {
                    Header = $"　{asset.VariantLabel}",
                    IsChecked = layer.Model.FixedAssetId == asset.Id && layer.Model.VariantMode == VariantMode.Fixed,
                };
                var assetId = asset.Id;
                mi.Click += (_, _) =>
                {
                    Vm.AssignFixedAsset(layer.Model, assetId);
                };
                menu.Items.Add(mi);
            }
        }

        border.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
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
