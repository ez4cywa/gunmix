using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using GunMix.App.Controls;
using GunMix.App.Services;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Cast;
using GunMix.Core.ExportTargets;
using GunMix.Core.Model;
using GunMix.Core.Mixing;
using GunMix.Core.Persistence;
using GunMix.Core.SoundBanks;
using GunMix.Core.Synthesis;
using GunMix.Core.Timeline;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace GunMix.App.ViewModels;

/// <summary>主工作台视图模型：素材导入、武器与配方管理、分层编辑、试听、导出与工程保存。</summary>
public sealed class MainViewModel : ViewModelBase
{
    private GunProject _project = new();
    private string? _projectPath;
    private bool _dirty;
    private readonly UndoStack _undo;
    private readonly Dictionary<Guid, string> _assetPaths = [];
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly TaskScheduler _uiScheduler;

    public AudioCache Cache { get; } = new();
    public AudioPlaybackService Playback { get; } = new();

    public MainViewModel()
    {
        _uiScheduler = SynchronizationContext.Current != null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;
        _undo = new UndoStack(ProjectStore.JsonOptions);
        _undo.Changed += () => { Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); };
        Playback.PlaybackStopped += () => _ui.Post(_ =>
        {
            IsPlaying = false;
            PlayheadSeconds = -1;
            PlayTimeText = "00:00.000";
            LevelL = 0;
            LevelR = 0;
            Raise(nameof(PlayCommand));
        }, null);
        Playback.PositionChanged += s => _ui.Post(_ =>
        {
            PlayTimeText = FormatUtil.FmtTime(s);
            PlayheadSeconds = s;
        }, null);
        Playback.LevelsChanged += (l, r) => _ui.Post(_ => { LevelL = l; LevelR = r; }, null);

        LoadDevices();
        ImportCommand = new RelayCommand(ImportFolder, () => !IsImporting);
        OpenCommand = new RelayCommand(OpenProject);
        SaveCommand = new RelayCommand(SaveProject, () => ProjectLoaded);
        SaveAsCommand = new RelayCommand(SaveProjectAs, () => ProjectLoaded);
        UndoCommand = new RelayCommand(DoUndo, () => CanUndo);
        RedoCommand = new RelayCommand(DoRedo, () => CanRedo);
        PlayCommand = new RelayCommand(PlayPreview, () => ProjectLoaded && !IsPlaying);
        StopCommand = new RelayCommand(() => { StopPlayback(); StatusText = "试听已停止"; Raise(nameof(PlayCommand)); });
        CopyRecipeCommand = new RelayCommand(CopyRecipe, () => CurrentRecipe != null);
        RenameRecipeCommand = new RelayCommand(RenameRecipe, () => CurrentRecipe != null);
        AddLayerCommand = new RelayCommand(AddLayer, () => CurrentRecipe != null);
        RemoveLayerCommand = new RelayCommand(RemoveLayer, () => SelectedLayer != null);
        RenameLayerCommand = new RelayCommand(RenameLayer, () => SelectedLayer != null);
        RegenerateCommand = new RelayCommand(RegenerateManifests, () => ProjectLoaded);
        SetSnapshotACommand = new RelayCommand(() => StoreSnapshot(true), () => ProjectLoaded);
        SetSnapshotBCommand = new RelayCommand(() => StoreSnapshot(false), () => ProjectLoaded);
        ApplySnapshotACommand = new RelayCommand(() => ApplySnapshot(true), () => _project.SnapshotA != null);
        ApplySnapshotBCommand = new RelayCommand(() => ApplySnapshot(false), () => _project.SnapshotB != null);
        ExportCommand = new RelayCommand(ShowExportDialog, () => ProjectLoaded);
        AddWeaponCommand = new RelayCommand(AddWeapon, () => ProjectLoaded);
        RenameWeaponCommand = new RelayCommand(RenameWeapon, () => ProjectLoaded);
        RenameProjectCommand = new RelayCommand(RenameProject, () => ProjectLoaded);
        RegenerateAdaptiveCommand = new RelayCommand(RegenerateAdaptive, () => ProjectLoaded);
        RebalanceCommand = new RelayCommand(RebalanceCurrentRecipe, () => CurrentRecipe != null);
    }

    public RelayCommand RegenerateAdaptiveCommand { get; }
    public RelayCommand RebalanceCommand { get; }

    public RelayCommand ImportCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CopyRecipeCommand { get; }
    public RelayCommand RenameRecipeCommand { get; }
    public RelayCommand AddLayerCommand { get; }
    public RelayCommand RemoveLayerCommand { get; }
    public RelayCommand RenameLayerCommand { get; }
    public RelayCommand RegenerateCommand { get; }
    public RelayCommand SetSnapshotACommand { get; }
    public RelayCommand SetSnapshotBCommand { get; }
    public RelayCommand ApplySnapshotACommand { get; }
    public RelayCommand ApplySnapshotBCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand AddWeaponCommand { get; }
    public RelayCommand RenameWeaponCommand { get; }
    public RelayCommand RenameProjectCommand { get; }

    public GunProject Project => _project;
    public void ApplyFireConfiguration(Recipe draft,IEnumerable<AssetInfo> pending)
    {
        if(CurrentRecipe is not { } recipe)return;
        PushUndo();
        var assets=pending.ToList();
        RegisterAnimationAssets(assets,assets.ToDictionary(a=>a.Id,a=>Path.Combine(a.SourceDirectory,a.FileName)));
        recipe.FireProfile=draft.FireProfile?.Clone();recipe.Layers=draft.Layers.Select(l=>l.Clone()).ToList();
        recipe.BurstRpm=draft.BurstRpm;recipe.BurstShotCount=draft.BurstShotCount;recipe.RandomSeed=draft.RandomSeed;
        recipe.SingleManifest=null;recipe.BurstManifest=null;
        MarkDirty();RebuildLayers();RegenerateManifests();RebuildAssetTree();
        StatusText="开火场景已应用；单发与连发试听、导出使用相同事件规则。";
    }

    public AudioCache AudioCacheForDialogs => Cache;

    public Guid CurrentWeaponId => _project.ActiveWeapon?.Id ?? Guid.Empty;

    public bool ProjectLoaded => _project.Weapons.Count > 0;

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    // ───────────────────────── 状态文本 ─────────────────────────

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _errorText = "";
    public string ErrorText { get => _errorText; set => Set(ref _errorText, value); }

    private string _windowTitle = "枪声分层工作台";
    public string WindowTitle { get => _windowTitle; set => Set(ref _windowTitle, value); }

    private string _projectInfo = "空工程 — 请导入音频文件夹";
    public string ProjectInfo { get => _projectInfo; set => Set(ref _projectInfo, value); }

    private void UpdateProjectInfo()
    {
        Raise(nameof(ProjectLoaded));
        var weapon = _project.ActiveWeapon;
        var recipe = weapon?.ActiveRecipe;
        ProjectInfo = weapon == null
            ? "空工程 — 请导入音频文件夹"
            : $"{weapon.Name} / {recipe?.Name ?? "无配方"}{(_dirty ? " · 未保存" : "")}";
        WindowTitle = $"{_project.ProjectName} — 枪声分层工作台{(_dirty ? " *" : "")}";
        Raise(nameof(CurrentWeaponType));
        Raise(nameof(CurrentProfile));
        Raise(nameof(RecipeNotes));
        Raise(nameof(HasRecipeNotes));
    }

    // ───────────────────────── 枪型与自适应合成 ─────────────────────────

    public static IReadOnlyList<string> WeaponTypeOptions { get; } = WeaponTypes.All;

    /// <summary>当前武器类型；修改后可点“按枪型生成配方”套用对应模板（已有配方不自动改动）。</summary>
    public string? CurrentWeaponType
    {
        get => _project.ActiveWeapon?.TypeName;
        set
        {
            if (value == null || _project.ActiveWeapon is not { } weapon || weapon.TypeName == value) return;
            PushUndo();
            weapon.TypeName = value;
            MarkDirty();
            RebuildWeapons();
            StatusText = $"武器类型改为「{value}」；点“按枪型生成配方”以套用该模板（不会改动你自建的配方）";
        }
    }

    /// <summary>连发是否加入松扳机尾音（序列释放时刻触发 *_plr_interrupt）；单发不受影响。</summary>
    public bool ReleaseTailEnabled
    {
        get => CurrentRecipe is { } r && ReleaseTail.IsEnabled(r);
        set => SetReleaseTail(value);
    }

    /// <summary>当前配方是否有可用的尾音素材（NPC 配方等没有 interrupt 素材）。</summary>
    public bool ReleaseTailAvailable
    {
        get
        {
            if (CurrentRecipe is not { } r) return false;
            if (ReleaseTail.Find(r) != null) return true;
            var groups = _project.Assets.Where(a => a.WeaponId == CurrentWeaponId).Select(a => a.GroupKey).ToHashSet();
            return ReleaseTail.CandidateGroup(r, groups) != null;
        }
    }

    public void SetReleaseTail(bool enabled)
    {
        if (CurrentRecipe is not { } recipe || _project.ActiveWeapon is not { } weapon) return;
        if (ReleaseTail.IsEnabled(recipe) == enabled) return;
        if (!ReleaseTailAvailable)
        {
            StatusText = "当前配方没有对应的 *_plr_interrupt 素材（NPC 配方无松扳机素材），无法加入尾音。";
            Raise(nameof(ReleaseTailEnabled));
            return;
        }
        PushUndo();
        var assets = _project.Assets.Where(a => a.WeaponId == weapon.Id).ToList();
        var layer = ReleaseTail.Ensure(recipe, assets)!;
        layer.Enabled = enabled;
        MarkDirty();
        RebuildLayers();
        RefreshManifestState();
        if (enabled)
        {
            var peak = AdaptiveRecipeBuilder.RenderPeakDb(recipe, assets, weapon.Id, LoadAsset, _project.SampleRate, ManifestKind.Burst);
            StatusText = $"连发已加入松扳机尾音（{layer.PoolGroupKey}，末发起点 + 一个射击间隔触发）；连发峰值 {peak:0.0} dBFS" +
                         (peak > 0 ? " —— 超出满刻度，请降低 TAIL 增益或导出时选择衰减" : "");
        }
        else
        {
            StatusText = "已关闭松扳机尾音";
        }
    }

    /// <summary>当前武器的枪型模板（界面分字段显示）。</summary>
    public WeaponProfile? CurrentProfile =>
        _project.ActiveWeapon is { } weapon ? WeaponProfiles.For(weapon.TypeName) : null;

    public string RecipeNotes => CurrentRecipe?.Notes ?? "";

    public bool HasRecipeNotes => RecipeNotes.Length > 0;

    /// <summary>自适应配方用的解码函数（峰值保护与补测响度）；无法读取时返回 null。</summary>
    private AssetBuffer? LoadAsset(AssetInfo asset)
    {
        var path = _assetPaths.TryGetValue(asset.Id, out var p) ? p : Path.Combine(asset.SourceDirectory, asset.FileName);
        if (!File.Exists(path)) return null;
        try { return Cache.Get(asset.Id, path, _project.SampleRate); }
        catch (WavDecodeException) { return null; }
    }

    /// <summary>旧工程素材没有响度测量时补测（只测开火类素材）。返回补测数量。</summary>
    private int EnsureLoudness(IEnumerable<AssetInfo> assets)
    {
        int measured = 0;
        foreach (var a in assets.Where(a => a.Loudness == null && a.Parsed?.Category != null))
        {
            var buffer = LoadAsset(a);
            if (buffer == null) continue;
            a.Loudness = LoudnessMeter.Measure(buffer.Data, buffer.Channels, buffer.SampleRate);
            measured++;
        }
        return measured;
    }

    /// <summary>按当前枪型重新生成自适应配方（只替换自适应配方，自建/复制的配方保留）。</summary>
    public void RegenerateAdaptive()
    {
        if (_project.ActiveWeapon is not { } weapon) return;
        PushUndo();
        var assets = _project.Assets.Where(a => a.WeaponId == weapon.Id).ToList();
        int measured = EnsureLoudness(assets);
        var created = ProjectFactory.RegenerateAdaptive(weapon, assets, LoadAsset, _project.SampleRate);
        EnsureManifestsFor(weapon);
        MarkDirty();
        ReloadRecipesFromWeapon();
        UpdateProjectInfo();
        StatusText = created.Count > 0
            ? $"已按「{weapon.TypeName}」生成 {created.Count} 个自适应配方：{string.Join("、", created.Select(r => r.Name))}" +
              (measured > 0 ? $"（补测响度 {measured} 个素材）" : "")
            : "当前武器没有可识别的主体（*_shot）素材组，未生成自适应配方。";
    }

    /// <summary>按枪型模板对当前配方重新配平增益（保留开关、延时、素材池）。</summary>
    public void RebalanceCurrentRecipe()
    {
        if (CurrentRecipe is not { } recipe || _project.ActiveWeapon is not { } weapon) return;
        PushUndo();
        var assets = _project.Assets.Where(a => a.WeaponId == weapon.Id).ToList();
        EnsureLoudness(assets);
        var message = AdaptiveRecipeBuilder.Rebalance(recipe, weapon, assets, LoadAsset, _project.SampleRate);
        MarkDirty();
        RebuildLayers();
        RefreshManifestState();
        UpdateProjectInfo();
        StatusText = message;
    }

    // ───────────────────────── 武器 ─────────────────────────

    public ObservableCollection<WeaponItemVm> Weapons { get; } = [];

    private WeaponItemVm? _selectedWeapon;
    public WeaponItemVm? SelectedWeapon
    {
        get => _selectedWeapon;
        set
        {
            if (Set(ref _selectedWeapon, value) && value != null)
            {
                _project.ActiveWeaponId = value.Id;
                // 外部生成或旧工程可能没有事件清单：补建缺失的清单（不改动已有清单，不进入撤销历史）
                EnsureManifestsFor(value.Weapon);
                Recipes.Clear();
                foreach (var r in value.Weapon.Recipes) Recipes.Add(new RecipeItemVm(r));
                SelectedRecipe = Recipes.FirstOrDefault(r => r.Recipe.Id == value.Weapon.ActiveRecipeId) ?? Recipes.FirstOrDefault();
                RebuildAssetTree();
                RebuildLayers();
                UpdateProjectInfo();
            }
        }
    }

    public sealed class WeaponItemVm
    {
        public Weapon Weapon { get; init; } = new();
        public Guid Id => Weapon.Id;
        public string Name => Weapon.Name;
        public string TypeName => Weapon.TypeName;
        public string Display => $"{Weapon.Name}　{Weapon.TypeName}";
        public override string ToString() => Display;
    }

    // ───────────────────────── 配方 ─────────────────────────

    public ObservableCollection<RecipeItemVm> Recipes { get; } = [];

    private RecipeItemVm? _selectedRecipe;
    public RecipeItemVm? SelectedRecipe
    {
        get => _selectedRecipe;
        set
        {
            // 切换武器时清空列表会让下拉框回写 null：不能据此覆盖该武器记住的当前配方
            if (value == null && Recipes.Count == 0) { _selectedRecipe = null; return; }
            if (Set(ref _selectedRecipe, value) && _project.ActiveWeapon is { } w)
            {
                w.ActiveRecipeId = value?.Recipe.Id ?? Guid.Empty;
                RebuildLayers();
                RefreshManifestState();
                UpdateProjectInfo();
                Raise(nameof(IsTeachingPreset));
            }
        }
    }

    public sealed class RecipeItemVm
    {
        public Recipe Recipe { get; }
        public RecipeItemVm(Recipe recipe) => Recipe = recipe;
        public string Name => Recipe.Name;
        public string Display => Recipe.Name;
        public override string ToString() => Display;
    }

    public string RecipeName => CurrentRecipe?.Name ?? "";

    public bool IsTeachingPreset => CurrentRecipe?.IsTeachingPreset == true;

    public Recipe? CurrentRecipe => _project.ActiveWeapon?.ActiveRecipe;

    // ───────────────────────── 素材树 ─────────────────────────

    private string _assetSearch = "";
    public string AssetSearch
    {
        get => _assetSearch;
        set { if (Set(ref _assetSearch, value)) RebuildAssetTree(); }
    }

    public ObservableCollection<AssetNodeVm> AssetTree { get; } = [];

    public sealed class AssetNodeVm
    {
        public string Title { get; init; } = "";
        public string? Detail { get; init; }
        public AssetInfo? Asset { get; init; }
        public ObservableCollection<AssetNodeVm>? Children { get; init; }
        public bool IsFile => Asset != null;
    }

    private void RebuildAssetTree()
    {
        AssetTree.Clear();
        var weaponId = CurrentWeaponId;
        var assets = _project.Assets.Where(a => a.WeaponId == weaponId).ToList();
        var search = AssetSearch.Trim();
        if (search.Length > 0)
            assets = assets.Where(a => a.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                       || a.GroupKey.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var bucketGroup in assets.GroupBy(a => NameParser.BucketOf(a.GroupKey)).OrderBy(b => BucketOrder(b.Key)))
        {
            var bucketNode = new AssetNodeVm { Title = bucketGroup.Key, Children = [] };
            foreach (var g in bucketGroup.GroupBy(a => a.GroupDisplay).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var groupNode = new AssetNodeVm { Title = g.Key, Detail = $"{g.Count()}", Children = [] };
                foreach (var a in g.OrderBy(a => a.Parsed?.Variant ?? 999).ThenBy(a => a.FileName))
                {
                    var v = a.Parsed?.Variant;
                    var label = v != null && a.GroupKey.Length > 0
                        ? $"{a.GroupKey} / {v:00}　{(a.Format.Channels == 1 ? "单" : "双")}声道"
                        : a.FileName;
                    groupNode.Children!.Add(new AssetNodeVm
                    {
                        Title = label,
                        Detail = $"{a.Format.SampleRate / 1000.0:0.#} kHz {a.Format.BitsPerSample}bit {a.Format.DurationSeconds:0.00}s",
                        Asset = a,
                    });
                }
                bucketNode.Children!.Add(groupNode);
            }
            AssetTree.Add(bucketNode);
        }
        Raise(nameof(AssetTree));
        Raise(nameof(AvailableGroupKeys));
    }

    /// <summary>当前武器的可用分组键（用于“指定到层”）。</summary>
    public List<string> AvailableGroupKeys => _project.Assets
        .Where(a => a.WeaponId == CurrentWeaponId && a.GroupKey.Length > 0)
        .Select(a => a.GroupKey)
        .Distinct()
        .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static IReadOnlyList<VariantMode> VariantModes { get; } =
        [VariantMode.Fixed, VariantMode.Rotation, VariantMode.Random];

    public static IReadOnlyList<ExperimentalTrigger> TriggerModes { get; } =
        [ExperimentalTrigger.PerShot, ExperimentalTrigger.ShotN, ExperimentalTrigger.ReleaseMoment];

    /// <summary>带中文标签的选项（选中值写回枚举）。</summary>
    public static IReadOnlyList<EnumItemVm<VariantMode>> VariantModeOptions { get; } =
    [
        new(VariantMode.Fixed, "固定样本"),
        new(VariantMode.Rotation, "顺序轮换"),
        new(VariantMode.Random, "随机（种子）"),
    ];

    public static IReadOnlyList<EnumItemVm<ExperimentalTrigger>> TriggerOptions { get; } =
    [
        new(ExperimentalTrigger.PerShot, "每发"),
        new(ExperimentalTrigger.ShotN, "指定第 N 发"),
        new(ExperimentalTrigger.ReleaseMoment, "序列释放时刻"),
    ];

    public sealed record EnumItemVm<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static int BucketOrder(string bucket) => bucket switch
    {
        "普通玩家" => 0,
        "ADS 候选" => 1,
        "消音" => 2,
        "NPC 距离候选" => 3,
        "实验分支（未启用）" => 4,
        "换弹 / Foley" => 5,
        "其他" => 6,
        _ => 7,
    };

    // ───────────────────────── 分层 ─────────────────────────

    public ObservableCollection<LayerVm> Layers { get; } = [];

    private LayerVm? _selectedLayer;
    public LayerVm? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (Set(ref _selectedLayer, value))
            {
                foreach (var l in Layers)
                    l.IsSelected = ReferenceEquals(l, value);
            }
        }
    }

    private void RebuildLayers()
    {
        Layers.Clear();
        if (CurrentRecipe is { } recipe)
        {
            foreach (var l in recipe.Layers)
            {
                var vm = new LayerVm(this, l);
                vm.RefreshAll();
                Layers.Add(vm);
            }
        }
        SelectedLayer = Layers.FirstOrDefault();
        Raise(nameof(Layers));
    }

    /// <summary>一次层编辑：推快照 → 修改 → 标脏 → 刷新状态。</summary>
    internal void EditLayer(Layer layer, string what, bool invalidateManifest, Action<Layer> change, bool rebuildManifests = false)
    {
        // 刷新界面期间下拉框等控件会同步回写绑定值：嵌套的层编辑只可能是这种回显，不是用户操作，直接忽略，
        // 否则“回写 → 编辑 → 刷新 → 回写”会无限递归
        if (_editingLayer) return;
        _editingLayer = true;
        try
        {
            PushUndo();
            var recipe = CurrentRecipe;
            var previousSingle = rebuildManifests && recipe?.SingleManifest?.Matches(recipe, ManifestKind.Single, _project.Assets, CurrentWeaponId) == true
                ? recipe.SingleManifest : null;
            var previousBurst = rebuildManifests && recipe?.BurstManifest?.Matches(recipe, ManifestKind.Burst, _project.Assets, CurrentWeaponId) == true
                ? recipe.BurstManifest : null;
            change(layer);
            MarkDirty();
            if (rebuildManifests) RebuildManifests(layer.Id, previousSingle, previousBurst);
            else if (invalidateManifest) RefreshManifestState();
            var vm = Layers.FirstOrDefault(x => x.Id == layer.Id);
            vm?.RefreshAll();
            if (SelectedLayer?.Id == layer.Id) SelectedLayer = vm; // 触发右侧属性刷新
        }
        finally
        {
            _editingLayer = false;
        }
    }

    private bool _editingLayer;

    // ───────────────────────── 序列与清单 ─────────────────────────

    private bool _burstMode = true;
    public bool BurstMode
    {
        get => _burstMode;
        set { if (Set(ref _burstMode, value)) { Raise(nameof(SingleMode)); RefreshTimeline(); } }
    }

    public bool SingleMode
    {
        get => !_burstMode;
        set => BurstMode = !value;
    }

    public int BurstRpm
    {
        get => CurrentRecipe?.BurstRpm ?? 600;
        set
        {
            if (CurrentRecipe == null) return;
            PushUndo();
            CurrentRecipe.BurstRpm = Math.Clamp(value, TimelineCompiler.MinRpm, TimelineCompiler.MaxRpm);
            MarkDirty();
            RefreshManifestState();
        }
    }

    public int BurstShotCount
    {
        get => CurrentRecipe?.BurstShotCount ?? 6;
        set
        {
            if (CurrentRecipe == null) return;
            PushUndo();
            CurrentRecipe.BurstShotCount = Math.Clamp(value, TimelineCompiler.MinShots, TimelineCompiler.MaxShots);
            MarkDirty();
            RefreshManifestState();
        }
    }

    public int RandomSeed
    {
        get => CurrentRecipe?.RandomSeed ?? 42;
        set
        {
            if (CurrentRecipe == null) return;
            PushUndo();
            CurrentRecipe.RandomSeed = value;
            MarkDirty();
            RefreshManifestState();
        }
    }

    private string _manifestStatus = "当前清单：—";
    public string ManifestStatus { get => _manifestStatus; set => Set(ref _manifestStatus, value); }

    private bool _manifestStale;
    public bool ManifestStale
    {
        get => _manifestStale;
        set
        {
            if (Set(ref _manifestStale, value))
                Raise(nameof(ManifestStatusColor));
        }
    }

    public string ManifestStatusColor => ManifestStale ? "#D9B44A" : "#7FBF7F";

    /// <summary>清单是否与当前配方匹配；不匹配时显示“预览待更新”。</summary>
    public void RefreshManifestState()
    {
        var recipe = CurrentRecipe;
        // 切换配方后射速/发数/种子显示须跟随当前配方
        Raise(nameof(BurstRpm));
        Raise(nameof(BurstShotCount));
        Raise(nameof(RandomSeed));
        Raise(nameof(ReleaseTailEnabled));
        Raise(nameof(ReleaseTailAvailable));
        if (recipe == null)
        {
            ManifestStatus = "当前清单：—";
            ManifestStale = false;
            return;
        }
        var kind = BurstMode ? ManifestKind.Burst : ManifestKind.Single;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        bool stale = manifest == null || !manifest.Matches(recipe, kind, _project.Assets, CurrentWeaponId);
        ManifestStale = stale;
        int shots = kind == ManifestKind.Burst ? recipe.BurstShotCount : 1;
        ManifestStatus = stale
            ? $"设置已修改，正在更新 —— 请先“重新生成变体”（当前清单 {manifest?.Entries.Count ?? 0} 事件）"
            : $"当前清单：{shots} 发 · 预览已更新";
        RefreshTimeline();
    }

    /// <summary>重新生成变体：产生新的事件清单（播放、停止再播、导出都复用当前清单）。</summary>
    public void RegenerateManifests()
    {
        if (_project.ActiveWeapon == null || CurrentRecipe == null) return;
        PushUndo();
        RebuildManifests();
        foreach (var l in Layers) l.RefreshAll();
        StatusText = "预览已更新";
    }

    private void RebuildManifests(Guid? changedLayerId = null, EventManifest? previousSingle = null, EventManifest? previousBurst = null)
    {
        var weapon = _project.ActiveWeapon;
        var recipe = CurrentRecipe;
        if (weapon == null || recipe == null) return;
        recipe.SingleManifest = TimelineCompiler.BuildManifest(recipe, _project.Assets, weapon.Id, ManifestKind.Single);
        recipe.BurstManifest = TimelineCompiler.BuildManifest(recipe, _project.Assets, weapon.Id, ManifestKind.Burst);
        // 局部素材选择不抹掉其他层在事件表里指定的样本。
        PreserveOtherLayers(recipe.SingleManifest, previousSingle);
        PreserveOtherLayers(recipe.BurstManifest, previousBurst);
        MarkDirty();
        RefreshManifestState();

        void PreserveOtherLayers(EventManifest fresh, EventManifest? previous)
        {
            if (changedLayerId == null || previous == null) return;
            var old = previous.Entries.ToLookup(e => (e.LayerId, e.ShotIndex, e.InstanceId));
            foreach (var entry in fresh.Entries.Where(e => e.LayerId != changedLayerId))
            {
                var original = old[(entry.LayerId, entry.ShotIndex, entry.InstanceId)].FirstOrDefault();
                if (original != null) entry.AssetId = original.AssetId;
            }
        }
    }

    private void EnsureManifests()
    {
        var weapon = _project.ActiveWeapon;
        var recipe = CurrentRecipe;
        if (weapon == null || recipe == null) return;
        var kind = BurstMode ? ManifestKind.Burst : ManifestKind.Single;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        if (manifest == null || !manifest.Matches(recipe, kind, _project.Assets, CurrentWeaponId))
            RegenerateManifests();
    }

    // ───────────────────────── 时间线显示 ─────────────────────────

    private IReadOnlyList<TimelineMarker>? _timelineMarkers;
    public IReadOnlyList<TimelineMarker>? TimelineMarkers
    {
        get => _timelineMarkers;
        private set => Set(ref _timelineMarkers, value);
    }

    private double _totalSeconds = 5;
    public double TotalSeconds { get => _totalSeconds; private set => Set(ref _totalSeconds, value); }

    private double _playheadSeconds = -1;
    public double PlayheadSeconds { get => _playheadSeconds; private set => Set(ref _playheadSeconds, value); }

    /// <summary>事件表：各层每发的样本与时间；双击/右键可替换指定发的样本。</summary>
    public ObservableCollection<EventRowVm> EventRows { get; } = [];

    public sealed class EventRowVm
    {
        public Guid LayerId { get; init; }
        public string LayerName { get; init; } = "";
        public int ShotIndex { get; init; }
        public string ShotDisplay { get; init; } = "";
        public Guid AssetId { get; set; }
        public string File { get; set; } = "";
        public double TimeS { get; init; }
        public double GainDb { get; init; }
    }

    /// <summary>替换指定发的样本：若想用 last_shot 替代某发普通 shot，把该发主体替换为指定文件（不自动叠加普通主体）。</summary>
    public void ReplaceEventAsset(EventRowVm row, Guid newAssetId)
    {
        var recipe = CurrentRecipe;
        var weapon = _project.ActiveWeapon;
        if (recipe == null || weapon == null) return;
        var kind = BurstMode ? ManifestKind.Burst : ManifestKind.Single;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        if (manifest == null) return;
        PushUndo();
        var entry = manifest.Entries.FirstOrDefault(e => e.LayerId == row.LayerId && e.ShotIndex == row.ShotIndex);
        if (entry == null) return;
        entry.AssetId = newAssetId;
        MarkDirty();
        RefreshTimeline();
        BuildEventRows(timelineForRows: true);
        StatusText = $"已替换第 {row.ShotIndex + 1} 发样本：{FindAsset(newAssetId)?.FileName ?? newAssetId.ToString()}";
    }

    private void BuildEventRows(bool timelineForRows = false)
    {
        EventRows.Clear();
        var weapon = _project.ActiveWeapon;
        var recipe = CurrentRecipe;
        if (weapon == null || recipe == null) return;
        var kind = BurstMode ? ManifestKind.Burst : ManifestKind.Single;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        if (manifest == null) return;
        var timeline = TimelineCompiler.Compile(recipe, manifest, _project.Assets, weapon.Id, _project.SampleRate, kind);
        foreach (var e in timeline.Events)
        {
            var layer = recipe.Layers.FirstOrDefault(l => l.Id == e.LayerId);
            var asset = timeline.AssetById.TryGetValue(e.AssetId, out var a) ? a : null;
            EventRows.Add(new EventRowVm
            {
                LayerId = e.LayerId,
                LayerName = layer?.Name ?? "?",
                ShotIndex = e.ShotIndex,
                ShotDisplay = kind == ManifestKind.Burst ? $"第 {e.ShotIndex + 1} 发" : "单发",
                AssetId = e.AssetId,
                File = asset?.FileName ?? "(缺失)",
                TimeS = Math.Round(e.StartSample / (double)_project.SampleRate, 3),
                GainDb = e.GainDb,
            });
        }
        Raise(nameof(EventRows));
    }

    private void RefreshTimeline()
    {
        var weapon = _project.ActiveWeapon;
        var recipe = CurrentRecipe;
        if (weapon == null || recipe == null)
        {
            TimelineMarkers = null;
            return;
        }
        var kind = BurstMode ? ManifestKind.Burst : ManifestKind.Single;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        if (manifest == null)
        {
            TimelineMarkers = null;
            return;
        }
        var timeline = TimelineCompiler.Compile(recipe, manifest, _project.Assets, weapon.Id, _project.SampleRate, kind);
        var byAsset = timeline.AssetById;
        TimelineMarkers = timeline.Events
            .Select(e => new TimelineMarker(e.StartSample, AssetLength(byAsset, e.AssetId), ""))
            .ToList();
        TotalSeconds = Math.Max(0.5, timeline.TotalSamples / (double)_project.SampleRate);
        BuildEventRows();
    }

    private static long AssetLength(IReadOnlyDictionary<Guid, AssetInfo> byAsset, Guid id)
    {
        return byAsset.TryGetValue(id, out var a) ? a.Format.FrameCount * a.Format.SampleRate / 48000 : 0;
    }

    // ───────────────────────── 试听 ─────────────────────────

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    private string _playTimeText = "00:00.000";
    public string PlayTimeText { get => _playTimeText; private set => Set(ref _playTimeText, value); }

    private double _levelL;
    public double LevelL { get => _levelL; private set => Set(ref _levelL, value); }

    private double _levelR;
    public double LevelR { get => _levelR; private set => Set(ref _levelR, value); }

    private string _deviceName = "系统默认输出";
    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }

    public ObservableCollection<DeviceItemVm> Devices { get; } = [];

    private DeviceItemVm? _selectedDevice;
    public DeviceItemVm? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value))
                DeviceName = value?.Name ?? "系统默认输出";
        }
    }

    public sealed class DeviceItemVm
    {
        public MMDevice? Device { get; init; }
        public string Name { get; init; } = "";
    }

    private void LoadDevices()
    {
        Devices.Clear();
        Devices.Add(new DeviceItemVm { Device = null, Name = "系统默认输出" });
        foreach (var (device, name) in Playback.EnumerateDevices())
            Devices.Add(new DeviceItemVm { Device = device, Name = name });
        SelectedDevice = Devices.FirstOrDefault();
    }

    public void RefreshDevices()
    {
        LoadDevices();
    }

    /// <summary>单层试听（忽略其他层与 Solo 状态）。</summary>
    public void PlayLayer(LayerVm layer)
    {
        if (!ProjectLoaded) return;
        EnsureManifests();
        var weapon = _project.ActiveWeapon!;
        var recipe = CurrentRecipe!;
        var kind = BurstMode ? ManifestKind.Burst : ManifestKind.Single;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        if (manifest == null) return;
        var all = TimelineCompiler.Compile(recipe, manifest, _project.Assets, weapon.Id, _project.SampleRate, kind);
        var filtered = new CompiledTimeline
        {
            Events = all.Events.Where(e => e.LayerId == layer.Id).ToList(),
            TotalSamples = all.TotalSamples,
            AssetById = all.AssetById,
        };
        if (filtered.Events.Count == 0)
        {
            ErrorText = $"层 {layer.Name} 当前没有事件（未启用或素材池为空）。";
            return;
        }
        ErrorText = "";
        PlayRendered(filtered, MonitorCompensationDb);
    }

    /// <summary>播放当前配方（单发或连发；直接文件预听与配方播放互斥）。</summary>
    public void PlayPreview()
    {
        if (!ProjectLoaded) return;
        EnsureManifests();
        var weapon = _project.ActiveWeapon!;
        var recipe = CurrentRecipe!;
        var kind = BurstMode ? ManifestKind.Burst : ManifestKind.Single;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        if (manifest == null) return;
        var timeline = TimelineCompiler.Compile(recipe, manifest, _project.Assets, weapon.Id, _project.SampleRate, kind);

        // Solo 只影响监听：仅播放独听层；Mute（关闭层）不在清单中，天然优先。
        var soloLayers = recipe.Layers.Where(l => l.Solo).Select(l => l.Id).ToHashSet();
        CompiledTimeline effective = soloLayers.Count > 0
            ? new CompiledTimeline
            {
                Events = timeline.Events.Where(e => soloLayers.Contains(e.LayerId)).ToList(),
                TotalSamples = timeline.TotalSamples,
                AssetById = timeline.AssetById,
                Issues = timeline.Issues,
            }
            : timeline;
        if (timeline.Issues.Count > 0)
        {
            ErrorText = string.Join("；", timeline.Issues.Select(i => $"{i.LayerName}：{i.Message}"));
            if (effective.Events.Count == 0) return;
        }
        else
        {
            ErrorText = "";
        }
        if (effective.Events.Count == 0)
        {
            ErrorText = "当前配方没有可播放的事件（所有层已关闭或素材池为空）。";
            return;
        }
        StatusText = "正在渲染预览…";
        PlayRendered(effective, MonitorCompensationDb);
    }

    private void PlayRendered(CompiledTimeline timeline, double monitorDb)
    {
        int generation = StopPlayback(immediate: true);
        int sampleRate = _project.SampleRate;
        var paths = timeline.AssetById.Values.ToDictionary(a => a.Id, AssetPathOf);
        StatusText = "正在渲染预览…";
        Task.Run(() =>
        {
            var mix = MixKernel.Render(timeline, ev => paths.TryGetValue(ev.AssetId, out var path) && File.Exists(path)
                ? Cache.Get(ev.AssetId, path, sampleRate) : null, 2);
            return new { mix.Data, mix.MissingAssets };
        }).ContinueWith(t =>
        {
            if (generation != Volatile.Read(ref _previewGeneration)) { _ = t.Exception; return; }
            if (t.IsFaulted)
            {
                ErrorText = $"预览渲染失败：{t.Exception?.GetBaseException().Message}";
                StatusText = "";
                return;
            }
            if (t.Result.MissingAssets.Count > 0)
            {
                ErrorText = $"素材缺失：{string.Join("、", t.Result.MissingAssets.Distinct().Take(5))}（可重新定位素材）";
            }
            StatusText = "正在启动输出设备…";
            // 设备初始化在后台 MTA 线程；完成后回调到界面线程。
            Playback.Play(t.Result.Data, sampleRate, 2, SelectedDevice?.Device, monitorDb, err =>
            {
                if (generation != Volatile.Read(ref _previewGeneration)) return;
                if (err != null)
                {
                    ErrorText = $"无法启动输出设备：{err}";
                    StatusText = "";
                }
                else
                {
                    IsPlaying = true;
                    StatusText = "试听中";
                    if (t.Result.MissingAssets.Count == 0) ErrorText = "";
                }
                Raise(nameof(PlayCommand));
                Raise(nameof(StopCommand));
            });
        }, _uiScheduler);
    }

    private int _previewGeneration;

    public int StopPlayback(bool immediate = false)
    {
        int generation = Interlocked.Increment(ref _previewGeneration);
        Playback.Stop(immediate);
        return generation;
    }

    public async Task PreviewAssetAsync(AssetInfo asset)
    {
        int generation = StopPlayback(immediate: true);
        string path = AssetPathOf(asset);
        StatusText = $"正在读取预听素材：{asset.FileName}";
        try
        {
            var prepared = await Task.Run(() =>
            {
                var decoded = WavReader.Read(path);
                if (decoded.Channels == 2) return (decoded.Data, decoded.SampleRate);
                var stereo = new float[checked(decoded.Data.Length * 2)];
                for (int i = 0; i < decoded.Data.Length; i++) stereo[i * 2] = stereo[i * 2 + 1] = decoded.Data[i];
                return (Data: stereo, decoded.SampleRate);
            });
            if (generation != Volatile.Read(ref _previewGeneration)) return;
            Playback.Play(prepared.Data, prepared.SampleRate, 2, SelectedDevice?.Device, 0, err =>
            {
                if (generation != Volatile.Read(ref _previewGeneration)) return;
                IsPlaying = err == null;
                ErrorText = err == null ? "" : $"无法预听：{err}";
                StatusText = err == null ? $"文件预听：{asset.FileName}（原始格式 {prepared.SampleRate} Hz）" : "";
            });
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _previewGeneration)) ErrorText = $"无法预听：{ex.Message}";
        }
    }

    private AssetBuffer? ResolveBuffer(ShotEvent ev)
    {
        var asset = _project.Assets.FirstOrDefault(a => a.Id == ev.AssetId);
        if (asset == null) return null;
        if (!_assetPaths.TryGetValue(asset.Id, out var path) || !File.Exists(path)) return null;
        return Cache.Get(asset.Id, path, _project.SampleRate);
    }

    // ───────────────────────── A/B ─────────────────────────

    /// <summary>仅影响监听的手动音量补偿 dB；显示数值，不进入导出。</summary>
    private double _monitorCompensationDb;
    public double MonitorCompensationDb
    {
        get => _monitorCompensationDb;
        set => Set(ref _monitorCompensationDb, Math.Clamp(value, -24, 24));
    }

    public string? SnapshotALabel => _project.SnapshotA?.Label;
    public string? SnapshotBLabel => _project.SnapshotB?.Label;

    private void StoreSnapshot(bool isA)
    {
        var recipe = CurrentRecipe;
        if (recipe == null) return;
        PushUndo();
        var snap = new AbSnapshot
        {
            Label = $"{recipe.Name}（{DateTime.Now:HH:mm:ss}）",
            RecipeId = recipe.Id,
            Recipe = recipe.Clone(),
            MonitorCompensationDb = _monitorCompensationDb,
        };
        if (isA) _project.SnapshotA = snap; else _project.SnapshotB = snap;
        MarkDirty();
        Raise(nameof(SnapshotALabel));
        Raise(nameof(SnapshotBLabel));
        StatusText = isA ? "已存为 A（含明确样本序列）" : "已存为 B（含明确样本序列）";
    }

    private void ApplySnapshot(bool isA)
    {
        var snap = isA ? _project.SnapshotA : _project.SnapshotB;
        var weapon = _project.ActiveWeapon;
        if (snap == null || weapon == null) return;
        PushUndo();
        // 快照存的是完整配方（含事件清单），从同一位置重新试听。
        var existing = weapon.Recipes.FirstOrDefault(r => r.Id == snap.RecipeId);
        if (existing != null)
        {
            var idx = weapon.Recipes.IndexOf(existing);
            weapon.Recipes[idx] = snap.Recipe.Clone();
            weapon.ActiveRecipeId = snap.RecipeId;
        }
        else
        {
            weapon.Recipes.Add(snap.Recipe.Clone());
            weapon.ActiveRecipeId = snap.Recipe.Id;
        }
        MonitorCompensationDb = snap.MonitorCompensationDb; // 显示其数值；仅影响监听
        MarkDirty();
        ReloadRecipesFromWeapon();
        StatusText = $"已切换到快照 {snap.Label}；监听补偿 {snap.MonitorCompensationDb:+0.0;-0.0} dB（仅监听，不进入导出）";
    }

    private void ReloadRecipesFromWeapon()
    {
        if (_project.ActiveWeapon is not { } w) return;
        Recipes.Clear();
        foreach (var r in w.Recipes) Recipes.Add(new RecipeItemVm(r));
        SelectedRecipe = Recipes.FirstOrDefault(r => r.Recipe.Id == w.ActiveRecipeId) ?? Recipes.FirstOrDefault();
        RebuildLayers();
        RefreshManifestState();
    }

    // ───────────────────────── 导入 / 保存 / 打开 ─────────────────────────

    public void ImportFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择音频文件夹（单把武器目录或包含多把武器的目录）",
        };
        if (dlg.ShowDialog() != true) return;
        ImportFolderInternal(dlg.FolderName);
    }

    /// <summary>由对话框或恢复流程调用。</summary>
    public void ImportFolderInternal(string folder) => _ = ImportFolderAsync(folder);

    private bool _isImporting;
    public bool IsImporting { get => _isImporting; private set => Set(ref _isImporting, value); }

    public async Task ImportFolderAsync(string folder)
    {
        if (IsImporting) return;
        IsImporting = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        ErrorText = "";
        StatusText = "正在扫描…";
        var destinationProject = _project;
        var overrides = new Dictionary<string, string>(_project.ManualGroupOverrides);
        int sampleRate = _project.SampleRate;
        using var cancel = new CancellationTokenSource();
        bool finished = false;
        var progressWindow = new Views.ImportDialog($"正在扫描 {Path.GetFileName(folder.TrimEnd('\\', '/'))}");
        progressWindow.Cancelled += () => cancel.Cancel();
        progressWindow.Closed += (_, _) => { if (!finished) cancel.Cancel(); };
        try
        {
            progressWindow.Show();
            var scanned = await Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                long lastUpdate = -200;
                var imported = new AssetService().ImportDirectory(folder,
                    progress: (done, total) =>
                    {
                        if (done != total && watch.ElapsedMilliseconds - lastUpdate < 100) return;
                        lastUpdate = watch.ElapsedMilliseconds;
                        _ui.Post(_ => { if (!finished && !cancel.IsCancellationRequested) progressWindow.Update(done, total); }, null);
                    },
                    isCancelled: () => cancel.IsCancellationRequested, includeSubdirectories: true,
                    manualGroupOverrides: overrides.Count > 0 ? overrides : null);
                cancel.Token.ThrowIfCancellationRequested();
                _ui.Post(_ => { if (!finished) progressWindow.SetStage("正在生成配方并计算峰值…"); }, null);
                var prepared = ProjectFactory.CreateFromImport(imported, folder, loader: asset =>
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    return Cache.Get(asset.Id, Path.Combine(asset.SourceDirectory, asset.FileName), sampleRate);
                });
                cancel.Token.ThrowIfCancellationRequested();
                return (Imported: imported, Project: prepared);
            }, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(destinationProject, _project))
            {
                StatusText = "工程已切换，旧导入结果已取消。";
                return;
            }
            var imported = scanned.Imported;
            if (imported.Assets.Count == 0 && imported.Failures.Count == 0)
            {
                StatusText = "";
                ErrorText = "没有找到 WAV 文件。支持单/双声道 PCM 16/24/32 bit 与 IEEE float 32 bit WAV。";
                return;
            }
            ApplyImport(imported, folder, scanned.Project);
        }
        catch (OperationCanceledException) { StatusText = "导入已取消，未应用导入结果。"; }
        catch (Exception ex) { ErrorText = $"导入失败：{ex.Message}"; StatusText = ""; }
        finally
        {
            finished = true;
            progressWindow.Close();
            IsImporting = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>应用导入结果（内部：供导入流程与测试直接调用）。</summary>
    internal void ApplyImport(ImportReport imported, string folder, GunProject? prepared = null)
    {
        PushUndo();
        if (_project.Assets.Count == 0 && _project.Weapons.Count == 0)
        {
            // 空工程：直接以导入内容建工程
            var newProject = prepared ?? ProjectFactory.CreateFromImport(imported, folder, loader: LoadAsset);
            _project = newProject;
        }
        else
        {
            // 追加导入：并入现有工程，按解析的武器归属分配（待确认归属一次显示）
            var byName = _project.Weapons.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);
            var created = new List<Weapon>();
            foreach (var asset in imported.Assets)
            {
                var weaponName = AssetService.WeaponNameOf(asset) ?? Path.GetFileName(folder.TrimEnd('\\', '/'));
                if (!byName.TryGetValue(weaponName, out var weapon))
                {
                    weapon = prepared?.Weapons.FirstOrDefault(w => w.Name.Equals(weaponName, StringComparison.OrdinalIgnoreCase))
                        ?? new Weapon { Name = weaponName, TypeName = WeaponTypes.GuessFromName(weaponName) };
                    _project.Weapons.Add(weapon);
                    byName[weaponName] = weapon;
                    created.Add(weapon);
                }
                asset.WeaponId = weapon.Id;
                _project.Assets.Add(asset);
            }
            // 新出现的武器：按目录类别定类型，并生成教学预设与自适应配方（此前追加导入的新武器没有配方）
            foreach (var weapon in created)
            {
                var assets = _project.Assets.Where(a => a.WeaponId == weapon.Id).ToList();
                weapon.TypeName = AssetService.TypeFromDirectories(assets) ?? weapon.TypeName;
                if (prepared == null) ProjectFactory.SetupNewWeapon(weapon, assets, LoadAsset, _project.SampleRate);
            }
        }

        // 解析素材路径与武器列表
        foreach (var a in imported.Assets)
        {
            var p = Path.Combine(a.SourceDirectory, a.FileName);
            _assetPaths[a.Id] = p;
        }
        RebuildWeapons();
        RebuildAssetTree();
        var active = _project.ActiveWeapon;
        if (active != null)
        {
            EnsureManifestsFor(active);
            RefreshManifestState();
        }
        MarkDirty();
        ProjectStore.SaveRecovery(_project, _projectPath);

        var failText = imported.Failures.Count > 0
            ? $"；失败 {imported.Failures.Count} 个（含具体原因，可复制错误摘要）"
            : "";
        ErrorText = imported.Failures.Count > 0
            ? string.Join("\n", imported.Failures.Select(f => $"{f.FilePath}：{f.Reason}").Take(20))
            : "";
        StatusText = $"导入完成：可用 {imported.Assets.Count} / {imported.TotalFiles}{failText}";
    }

    private void EnsureManifestsFor(Weapon weapon)
    {
        foreach (var recipe in weapon.Recipes)
        {
            recipe.SingleManifest ??= TimelineCompiler.BuildManifest(recipe, _project.Assets, weapon.Id, ManifestKind.Single);
            recipe.BurstManifest ??= TimelineCompiler.BuildManifest(recipe, _project.Assets, weapon.Id, ManifestKind.Burst);
        }
    }

    private void RebuildWeapons()
    {
        Weapons.Clear();
        foreach (var w in _project.Weapons) Weapons.Add(new WeaponItemVm { Weapon = w });
        SelectedWeapon = Weapons.FirstOrDefault(x => x.Id == _project.ActiveWeaponId) ?? Weapons.FirstOrDefault();
    }

    public void OpenProject()
    {
        if (!ConfirmDiscardIfDirty()) return;
        var dlg = new OpenFileDialog { Filter = "枪声工程 (*.gunmix.json)|*.gunmix.json|所有文件 (*.*)|*.*", Title = "打开工程" };
        if (dlg.ShowDialog() != true) return;
        OpenProjectPath(dlg.FileName);
    }

    /// <summary>按路径打开工程（对话框、命令行参数与恢复流程共用）。</summary>
    public void OpenProjectPath(string path)
    {
        try
        {
            var project = ProjectStore.Load(path);
            LoadProject(project, path);
            StatusText = $"已打开工程：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ErrorText = $"打开工程失败：{ex.Message}";
        }
    }

    private void LoadProject(GunProject project, string? path)
    {
        StopPlayback(immediate: true);
        _project = project;
        _projectPath = path;
        _dirty = false;
        _assetPaths.Clear();
        foreach (var a in _project.Assets)
        {
            var candidate = Path.Combine(a.SourceDirectory, a.FileName);
            var resolved = AssetService.Relocate(a, path != null ? Path.GetDirectoryName(path)! : "", _project.SourceRoot, out var conflict);
            if (resolved != null && File.Exists(resolved))
            {
                _assetPaths[a.Id] = resolved;
                if (conflict)
                    ErrorText = $"素材 {a.FileName} 同名但内容不同，未自动替代；请重新定位或显式接受新素材。";
            }
        }
        _undo.Clear();
        RebuildWeapons();
        UpdateProjectInfo();
        Raise(nameof(SnapshotALabel));
        Raise(nameof(SnapshotBLabel));

        var missing = _project.Assets.Where(a => !_assetPaths.ContainsKey(a.Id)).ToList();
        if (missing.Count > 0)
        {
            ErrorText = $"以下素材未找到，相关层暂停预览与导出：{string.Join("、", missing.Select(m => m.FileName).Take(8))}" +
                        (missing.Count > 8 ? " 等" : "");
            RelocateMissingAssets(missing);
        }
        StatusText = $"工程已加载（{_project.Assets.Count} 个素材，{_project.Weapons.Count} 把武器）";
    }

    /// <summary>重定位素材：按哈希匹配接受；同名但内容不同需显式确认。</summary>
    private void RelocateMissingAssets(List<AssetInfo> missing)
    {
        foreach (var asset in missing)
        {
            var r = MessageBox.Show(
                $"素材缺失：{asset.FileName}\n\n“是”= 手动选择文件（按哈希校验）\n“否”= 跳过（该层暂停预览与导出）",
                "重定位素材", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) continue;
            var dlg = new OpenFileDialog { Title = $"重定位 {asset.FileName}", Filter = "WAV (*.wav)|*.wav|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() != true) continue;
            try
            {
                var hash = AssetService.ComputeHash(dlg.FileName);
                if (hash == asset.Sha256)
                {
                    _assetPaths[asset.Id] = dlg.FileName;
                    asset.SourceDirectory = Path.GetDirectoryName(dlg.FileName) ?? asset.SourceDirectory;
                    StatusText = $"已重定位：{asset.FileName}";
                }
                else
                {
                    var accept = MessageBox.Show(
                        "所选文件内容（哈希）与原素材不同。同名但内容不同不会自动替代。\n\n“是”= 显式接受新素材\n“否”= 放弃",
                        "内容不同", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (accept == MessageBoxResult.Yes)
                    {
                        _assetPaths[asset.Id] = dlg.FileName;
                        asset.SourceDirectory = Path.GetDirectoryName(dlg.FileName) ?? asset.SourceDirectory;
                        StatusText = $"已显式接受新素材：{asset.FileName}";
                        Cache.Invalidate(asset.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorText = $"重定位失败：{ex.Message}";
            }
        }
        if (_project.Assets.All(a => _assetPaths.ContainsKey(a.Id) && File.Exists(_assetPaths[a.Id])))
        {
            ErrorText = "";
            RefreshManifestState();
        }
    }

    public void SaveProject()
    {
        if(_project.MigrationReport!=null&&!string.IsNullOrEmpty(_projectPath)&&File.Exists(_projectPath))
        {
            using var original=System.Text.Json.JsonDocument.Parse(File.ReadAllText(_projectPath));
            if(!original.RootElement.TryGetProperty("schemaVersion",out var v)||v.GetInt32()<3)
            {SaveProjectAs();return;}
        }
        if (_projectPath == null) { SaveProjectAs(); return; }
        try
        {
            ProjectStore.Save(_project, _projectPath);
            _dirty = false;
            _undo.MarkSaved();
            UpdateProjectInfo();
            StatusText = $"已保存：{_projectPath}";
            ErrorText = "";
        }
        catch (Exception ex)
        {
            ErrorText = $"保存失败：{ex.Message}（工程内容未丢失）";
        }
    }

    public void SaveProjectAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "枪声工程 (*.gunmix.json)|*.gunmix.json",
            FileName = _project.ProjectName + GunProject.FileExtension,
            Title = "工程另存为",
        };
        if (dlg.ShowDialog() != true) return;
        _projectPath = dlg.FileName;
        _project.ProjectName = Path.GetFileNameWithoutExtension(dlg.FileName).Replace(GunProject.FileExtension, "");
        SaveProject();
        Raise(nameof(SnapshotALabel));
    }

    public bool ConfirmDiscardIfDirty()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show("当前工程有未保存的修改。保存、放弃还是取消？\n\n选择“是”保存后继续，选“否”放弃修改，选“取消”返回。",
            "关闭未保存工程", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return r switch
        {
            MessageBoxResult.Yes => SaveAndReturn(),
            MessageBoxResult.No => true,
            _ => false,
        };

        bool SaveAndReturn()
        {
            SaveProject();
            return !_dirty;
        }
    }

    private void MarkDirty()
    {
        if (!_dirty)
        {
            _dirty = true;
            UpdateProjectInfo();
        }
        ProjectStore.SaveRecovery(_project, _projectPath);
    }

    // ───────────────────────── 撤销 / 重做 ─────────────────────────

    private void PushUndo() => _undo.Push(_project);

    public void DoUndo()
    {
        var restored = _undo.Undo(_project);
        if (restored == null) return;
        _project = restored;
        AfterRestore();
        StatusText = "已撤销";
    }

    public void DoRedo()
    {
        var restored = _undo.Redo(_project);
        if (restored == null) return;
        _project = restored;
        AfterRestore();
        StatusText = "已重做";
    }

    private void AfterRestore()
    {
        _dirty = true;
        foreach (var a in _project.Assets)
        {
            if (!_assetPaths.ContainsKey(a.Id))
            {
                var p = Path.Combine(a.SourceDirectory, a.FileName);
                if (File.Exists(p)) _assetPaths[a.Id] = p;
            }
        }
        RebuildWeapons();
        UpdateProjectInfo();
        Raise(nameof(SnapshotALabel));
        Raise(nameof(SnapshotBLabel));
    }

    internal void PushUndoExternal() => PushUndo();

    /// <summary>动画窗口的单项编辑：推撤销快照并标脏（与分层编辑同一条路径）。</summary>
    internal void NotifyEdited(string status)
    {
        PushUndo();
        MarkDirty();
        SyncAnimationAssetPaths();
        if (!string.IsNullOrEmpty(status)) StatusText = status;
    }

    /// <summary>仅标脏并同步路径（改动已由调用方推过快照时使用）。</summary>
    internal void TouchDirtyFromAnimation()
    {
        MarkDirty();
        SyncAnimationAssetPaths();
    }

    // ───────────────────────── 配方 / 层操作 ─────────────────────────

    private void CopyRecipe()
    {
        if (CurrentRecipe is not { } source || _project.ActiveWeapon is not { } weapon) return;
        var name = ShowInput("复制为新配方", "新配方名称：", source.Name + " 副本");
        if (name == null) return;
        PushUndo();
        var copy = source.Clone();
        copy.Name = string.IsNullOrWhiteSpace(name) ? source.Name + " 副本" : name.Trim();
        weapon.Recipes.Add(copy);
        weapon.ActiveRecipeId = copy.Id;
        MarkDirty();
        ReloadRecipesFromWeapon();
        StatusText = "已复制配方（ADS、消音与 NPC 建议通过“复制为新配方”建立）";
    }

    private void RenameRecipe()
    {
        if (CurrentRecipe is not { } recipe) return;
        var name = ShowInput("重命名配方", "配方名称：", recipe.Name);
        if (name == null || string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        recipe.Name = name.Trim();
        MarkDirty();
        ReloadRecipesFromWeapon();
    }

    private void AddLayer()
    {
        if (CurrentRecipe is not { } recipe) return;
        PushUndo();
        var layer = new Layer { Name = $"层 {recipe.Layers.Count + 1}", Role = LayerRoles.Custom, GainDb = 0 };
        recipe.Layers.Add(layer);
        MarkDirty();
        RebuildLayers();
        StatusText = "已添加层：选择素材池或从左侧拖入";
    }

    private void RemoveLayer()
    {
        if (SelectedLayer is not { } vm || CurrentRecipe is not { } recipe) return;
        PushUndo();
        recipe.Layers.Remove(vm.Model);
        MarkDirty();
        RebuildLayers();
        RefreshManifestState();
    }

    private void RenameLayer()
    {
        if (SelectedLayer is not { } vm) return;
        var name = ShowInput("重命名层", "层名称：", vm.Name);
        if (name == null || string.IsNullOrWhiteSpace(name)) return;
        vm.Name = name.Trim();
    }

    private void AddWeapon()
    {
        var name = ShowInput("添加武器", "武器名称：", "new_weapon");
        if (name == null || string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        var weapon = new Weapon { Name = name.Trim(), TypeName = WeaponTypes.Custom };
        ProjectFactory.AddDefaultRecipes(weapon, []);
        _project.Weapons.Add(weapon);
        MarkDirty();
        RebuildWeapons();
        SelectedWeapon = Weapons.LastOrDefault();
        StatusText = "已添加武器：可从素材库把分组指定到层（素材全部要求重新映射）";
    }

    private void RenameWeapon()
    {
        if (_project.ActiveWeapon is not { } weapon) return;
        var name = ShowInput("重命名武器", "武器名称：", weapon.Name);
        if (name == null || string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        weapon.Name = name.Trim();
        if (weapon.Export.NamePrefix == "weapon" || weapon.Export.NamePrefix.Length == 0)
            weapon.Export.NamePrefix = weapon.Name;
        MarkDirty();
        RebuildWeapons();
        UpdateProjectInfo();
    }

    private void RenameProject()
    {
        var name = ShowInput("重命名工程", "工程名称：", _project.ProjectName);
        if (name == null || string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        _project.ProjectName = name.Trim();
        MarkDirty();
        UpdateProjectInfo();
    }

    // ───────────────────────── 素材辅助 ─────────────────────────

    public AssetInfo? FindAsset(Guid id) => _project.Assets.FirstOrDefault(a => a.Id == id);

    public AssetInfo? FirstPoolAsset(Layer layer) =>
        AssetService.ResolvePool(layer, _project.Assets, CurrentWeaponId).FirstOrDefault();

    /// <summary>当前实际选中的文件，用于名称、格式、路径和波形的统一显示。</summary>
    public AssetInfo? DisplayedLayerAsset(Layer layer)
    {
        var pool = AssetService.ResolvePool(layer, _project.Assets, CurrentWeaponId);
        if (layer.VariantMode == VariantMode.Fixed && layer.FixedAssetId is { } fixedId)
            return pool.FirstOrDefault(a => a.Id == fixedId) ?? pool.FirstOrDefault();
        var recipe = CurrentRecipe;
        var manifest = recipe?.SingleManifest;
        if (recipe != null && manifest != null && manifest.Matches(recipe, ManifestKind.Single, _project.Assets, CurrentWeaponId))
        {
            var assetId = manifest.Entries.FirstOrDefault(e => e.LayerId == layer.Id)?.AssetId;
            if (assetId != null) return pool.FirstOrDefault(a => a.Id == assetId) ?? FindAsset(assetId.Value);
        }
        return pool.FirstOrDefault();
    }

    public string AssetPathOf(AssetInfo asset) =>
        _assetPaths.TryGetValue(asset.Id, out var p) ? p : Path.Combine(asset.SourceDirectory, asset.FileName);

    public void SetAssetPath(Guid assetId, string path) => _assetPaths[assetId] = path;

    public IReadOnlyList<AssetInfo> MissingAssets =>
        _project.Assets.Where(a => !_assetPaths.ContainsKey(a.Id) || !File.Exists(_assetPaths[a.Id])).ToList();

    /// <summary>手动分组：指定文件到某分组键（手动分组优先，重新扫描不覆盖）。</summary>
    public void AssignGroup(AssetInfo asset, string groupKey)
    {
        PushUndo();
        asset.GroupKey = groupKey;
        asset.GroupSource = GroupSource.Manual;
        _project.ManualGroupOverrides[asset.FileName] = groupKey;
        MarkDirty();
        RebuildAssetTree();
        foreach (var l in Layers) l.RefreshAll();
        StatusText = $"已手动分组：{asset.FileName} → {groupKey}";
    }

    /// <summary>把素材池指定到层（拖入中央指定层）。</summary>
    public void AssignPool(Layer layer, string groupKey, List<Guid>? assetIds = null)
    {
        EditLayer(layer, "素材池", invalidateManifest: true, l =>
        {
            l.PoolAssetIds = assetIds is { Count: > 0 } ? [.. assetIds] : [];
            l.PoolGroupKey = assetIds is { Count: > 0 } ? "" : groupKey;
            var pool = AssetService.ResolvePool(l, _project.Assets, CurrentWeaponId);
            if (!pool.Any(a => a.Id == l.FixedAssetId)) l.FixedAssetId = pool.FirstOrDefault()?.Id;
        }, rebuildManifests: true);
        StatusText = $"层 {layer.Name} 素材池 → {(assetIds is { Count: > 0 } ? $"{assetIds.Count} 个文件" : groupKey)}";
    }

    public void AssignFixedAsset(Layer layer, Guid assetId)
    {
        var asset = AssetService.ResolvePool(layer, _project.Assets, CurrentWeaponId).FirstOrDefault(a => a.Id == assetId);
        if (asset == null) return;
        EditLayer(layer, "固定样本", invalidateManifest: true, l =>
        {
            l.FixedAssetId = assetId;
            l.VariantMode = VariantMode.Fixed;
        }, rebuildManifests: true);
        StatusText = $"层 {layer.Name} 固定样本 → {asset.FileName}";
    }

    /// <summary>复制配方到另一武器（只复制结构参数，素材要求重新映射）。</summary>
    public void CloneRecipeToWeapon(Recipe recipe, Weapon target)
    {
        PushUndo();
        ProjectFactory.CloneRecipeToWeapon(recipe, target, recipe.Name + "（来自 " + _project.ActiveWeapon?.Name + "）");
        MarkDirty();
        StatusText = $"配方结构已复制到 {target.Name}：素材全部要求重新映射";
    }

    // ───────────────────────── 导出 ─────────────────────────

    /// <summary>导出预览信息：目标文件名与预计时长（按当前清单编译）。</summary>
    internal void PrepareExportManifests()
    {
        var recipe = CurrentRecipe;
        if (recipe == null) return;
        if (recipe.SingleManifest?.Matches(recipe, ManifestKind.Single, _project.Assets, CurrentWeaponId) != true
            || recipe.BurstManifest?.Matches(recipe, ManifestKind.Burst, _project.Assets, CurrentWeaponId) != true)
            RegenerateManifests();
    }

    public (string FileName, double? EstimatedSeconds, string KindLabel) ExportPreviewInfo(ManifestKind kind)
    {
        var weapon = _project.ActiveWeapon;
        var recipe = CurrentRecipe;
        if (weapon == null || recipe == null) return ("", null, "");
        string prefix = string.IsNullOrWhiteSpace(weapon.Export.NamePrefix) ? weapon.Name : weapon.Export.NamePrefix;
        string fileName = kind == ManifestKind.Single
            ? $"{prefix}_single_{weapon.Export.SingleCounter:000}.wav"
            : $"{prefix}_burst_{recipe.BurstRpm}rpm_{recipe.BurstShotCount}shot{(ReleaseTail.IsEnabled(recipe) ? ReleaseTail.FileTag : "")}_{weapon.Export.BurstCounter:000}.wav";
        double? est = null;
        var manifest = kind == ManifestKind.Single ? recipe.SingleManifest : recipe.BurstManifest;
        if (manifest != null)
        {
            var timeline = TimelineCompiler.Compile(recipe, manifest, _project.Assets, weapon.Id, _project.SampleRate, kind);
            est = timeline.TotalSamples / (double)_project.SampleRate;
        }
        return (fileName, est, kind == ManifestKind.Single ? "单发" : "连发");
    }

    private void ShowExportDialog()
    {
        EnsureManifests();
        var dlg = new Views.ExportDialog(this) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
    }

    /// <summary>渲染并导出（由导出对话框调用）。kind 决定使用哪个已保存清单；导出从不可变快照读取。</summary>
    public ExportResult? ExportKind(ManifestKind kind, ExportOptions options)
    {
        var weapon = _project.ActiveWeapon;
        var recipe = CurrentRecipe;
        if (weapon == null || recipe == null) return null;
        var manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
        if (manifest == null)
        {
            RegenerateManifests();
            manifest = kind == ManifestKind.Burst ? recipe.BurstManifest : recipe.SingleManifest;
            if (manifest == null) return new ExportResult { Success = false, Error = "无法生成事件清单。" };
        }
        // 导出使用启动时的配方快照（当前层设置）；独听与监听补偿不进入导出。
        var timeline = TimelineCompiler.Compile(recipe, manifest, _project.Assets, weapon.Id, _project.SampleRate, kind);
        if (timeline.Events.Count == 0)
            return new ExportResult { Success = false, Error = "没有可导出的事件（启用层为空或素材缺失）。" };
        if (timeline.Issues.Any(i=>i.IsError))
            return new ExportResult { Success = false, Error = "参数无效：" + string.Join("；", timeline.Issues.Where(i=>i.IsError).Select(i => $"{i.LayerName}：{i.Message}")) };

        var mix = MixKernel.Render(timeline, ResolveBuffer, 2);
        if (mix.MissingAssets.Count > 0)
            return new ExportResult { Success = false, Error = $"素材缺失：{string.Join("、", mix.MissingAssets.Distinct().Take(5))}" };

        // 自动截尾：只截尾部低于阈值的静音，保留设定毫秒的自然尾巴；记录进导出报告。
        var trimmed = options.TrimTail
            ? TailTrimmer.Trim(mix.Data, 2, _project.SampleRate, options.TrimThresholdDb, options.TrimTailMs)
            : new TailTrimResult(mix.Data, 0, mix.Data.Length / 2L);

        // 自定义文件长度：非 null 时精确到该秒数，覆盖自动截尾
        var exportData = options.CustomLengthSeconds is { } customLen
            ? Exporter.ApplyCustomLength(trimmed.Data, _project.SampleRate, 2, customLen)
            : trimmed.Data;

        string prefix = string.IsNullOrWhiteSpace(weapon.Export.NamePrefix) ? weapon.Name : weapon.Export.NamePrefix;
        string fileName = kind == ManifestKind.Single
            ? $"{prefix}_single_{weapon.Export.SingleCounter:000}.wav"
            : $"{prefix}_burst_{recipe.BurstRpm}rpm_{recipe.BurstShotCount}shot{(ReleaseTail.IsEnabled(recipe) ? ReleaseTail.FileTag : "")}_{weapon.Export.BurstCounter:000}.wav";

        string outDir = options.OutputDirectory.Length > 0 ? options.OutputDirectory : ProjectDirectory();
        Directory.CreateDirectory(outDir);
        string finalPath = Path.Combine(outDir, fileName);
        if (!options.Overwrite)
        {
            int suffix=1;
            while(File.Exists(finalPath))
                finalPath=Path.Combine(outDir,Path.GetFileNameWithoutExtension(fileName)+$"_new{suffix++:000}"+Path.GetExtension(fileName));
        }

        var result = Exporter.WriteWav(exportData, _project.SampleRate, 2, options.BitDepth,
            options.Dither, options.DitherSeed, options.AttenuateToDbfs, finalPath, options.Overwrite,
            sourceFormat: options.Target == ExportTarget.SourceEngine,
            monoDownmix: options.SourceMono) with
        {
            TrimmedFrames = trimmed.RemovedFrames,
            TrimThresholdDb = options.TrimTail ? options.TrimThresholdDb : null,
            TrimTailMs = options.TrimTail ? options.TrimTailMs : null,
        };

        if (result.Success && options.Target == ExportTarget.SourceEngine)
            WriteGameSoundsScript(weapon.Export.NamePrefix.Length > 0 ? weapon.Export.NamePrefix : weapon.Name, outDir);

        if (result.Success)
        {
            var json = RecipeJsonBuilder.Build(_project, weapon, recipe, manifest, timeline, timeline.AssetById, kind,
                options.BitDepth, options.Dither, options.DitherSeed, result.QuantizedPeakDbfs, result.DurationSeconds,
                result.AppliedGainDb, Path.GetFileName(finalPath),
                options.TrimTail ? (options.TrimThresholdDb, options.TrimTailMs, trimmed.RemovedFrames) : null);
            var jsonPath = Path.ChangeExtension(finalPath, ".recipe.json");
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
            if(recipe.FireProfile?.Enabled==true)
            {
                var evidence=System.Text.Json.JsonSerializer.Serialize(new { schema="gunmix-evidence/3", software=RecipeJsonBuilder.SoftwareVersion,
                    referenceBuildSha256=GunMix.Core.Fire.EvidenceLedger.BuildSha256, facts=GunMix.Core.Fire.EvidenceLedger.Facts,
                    sources=recipe.FireProfile.Banks.Select(b=>new{b.Name,b.BankKey,b.SourcePath,b.SourceSha256,b.ParseCapability}),
                    ruleOrigin=recipe.FireProfile.RuleOrigin, inputFingerprint=timeline.InputFingerprint, diagnostics=timeline.Issues,
                    events=timeline.DomainEvents, instances=timeline.Events, commands=timeline.Commands },ProjectStore.JsonOptions);
                File.WriteAllText(Path.ChangeExtension(finalPath,".evidence-report.json"),evidence,new UTF8Encoding(false));
            }

            if (kind == ManifestKind.Single) weapon.Export.SingleCounter++;
            else weapon.Export.BurstCounter++;
            weapon.Export.OutputDirectory = outDir;
            weapon.Export.BitDepth = options.BitDepth;
            weapon.Export.DitherEnabled = options.Dither;
            weapon.Export.AttenuateToDbfs = options.AttenuateToDbfs;
            weapon.Export.TrimTail = options.TrimTail;
            weapon.Export.TrimThresholdDb = options.TrimThresholdDb;
            weapon.Export.TrimTailMs = options.TrimTailMs;
            weapon.Export.SourceEngineTarget = options.Target == ExportTarget.SourceEngine;
            weapon.Export.SourceMono = options.SourceMono;
            weapon.Export.CustomLengthSeconds = options.CustomLengthSeconds;
            MarkDirty();
        }
        return result;
    }

    private string ProjectDirectory() =>
        _projectPath != null ? Path.GetDirectoryName(_projectPath)! : Directory.GetCurrentDirectory();

    public sealed class ExportOptions
    {
        public bool ExportSingle { get; set; } = true;
        public bool ExportBurst { get; set; } = true;
        public int BitDepth { get; set; } = 24;
        public bool Dither { get; set; } = true;
        public int DitherSeed { get; set; } = 20260927;
        public double? AttenuateToDbfs { get; set; }
        public string OutputDirectory { get; set; } = "";
        public bool Overwrite { get; set; }

        /// <summary>自动截尾：从混合结果末尾截掉低于阈值的尾部静音，保留设定毫秒的自然尾巴。</summary>
        public bool TrimTail { get; set; } = true;
        public double TrimThresholdDb { get; set; } = -60.0;
        public double TrimTailMs { get; set; } = 120.0;

        /// <summary>导出目标：通用 WAV，或 Source 引擎（Left 4 Dead 2 / Garry's Mod）格式。可选项。</summary>
        public ExportTarget Target { get; set; } = ExportTarget.Generic;

        /// <summary>Source 目标下是否降混为单声道（3D 空间声源的常规做法）。</summary>
        public bool SourceMono { get; set; }

        /// <summary>
        /// 自定义文件长度（秒）。非 null 时输出精确到该时长：
        /// 比混音长则尾部补静音，比混音短则截断。覆盖自动截尾。
        /// </summary>
        public double? CustomLengthSeconds { get; set; }
    }

    public enum ExportTarget
    {
        /// <summary>通用 WAV：48 kHz / 立体声，位深与抖动按上面设置。</summary>
        Generic,

        /// <summary>Source 引擎（Left 4 Dead 2 / Garry's Mod）：44.1 kHz / 16 bit PCM + game_sounds 脚本。</summary>
        SourceEngine,
    }

    // ───────────────────────── 动画音效 ─────────────────────────

    /// <summary>动画素材的路径登记（工程保存时写入 AnimationAssetPaths）。</summary>
    internal void RegisterAnimationAssets(IEnumerable<AssetInfo> assets, IReadOnlyDictionary<Guid, string> paths)
    {
        foreach (var a in assets)
        {
            if (!_project.Assets.Any(x => x.Id == a.Id)) _project.Assets.Add(a);
            if (paths.TryGetValue(a.Id, out var p)) _assetPaths[a.Id] = p;
        }
    }

    internal void SetAnimationAssetPath(Guid assetId, string path) => _assetPaths[assetId] = path;

    /// <summary>已加载的声音库索引（soundbank 实测映射）。</summary>
    internal SoundBankIndex? Banks { get; private set; }

    private string? BanksDirectory;

    /// <summary>扫描动画目录、声音目录与可选的 soundbank 目录。</summary>
    internal AnimScanReport ScanAnimations(string animationDir, IReadOnlyList<string> soundDirs, string? bankDir = null)
    {
        if (bankDir != null && (Banks == null || BanksDirectory != bankDir))
        {
            // bank 内的 snd 是相对 sounds 根的路径
            var soundsRoot = soundDirs.FirstOrDefault(Directory.Exists) ?? "";
            Banks = SoundBankIndex.Load(bankDir, soundsRoot);
            BanksDirectory = bankDir;
        }

        var report = AnimationAssembler.Scan(animationDir, soundDirs, _project.Assets, banks: Banks);
        RegisterAnimationAssets(report.NewAssets, report.AssetPaths);
        foreach (var clip in report.Clips)
        {
            var existing = _project.Animations.FirstOrDefault(c => c.CastPath == clip.CastPath);
            if (existing != null)
            {
                // 重新扫描：保留用户已确认的指定与增益，更新来源、容器成员与帧数据
                var byAlias = existing.Events.ToDictionary(e => (e.Frame, e.Alias));
                foreach (var ev in clip.Events)
                {
                    if (!byAlias.TryGetValue((ev.Frame, ev.Alias), out var prev)) continue;
                    ev.GainDb = prev.GainDb;
                    ev.Enabled = prev.Enabled;
                    if (!prev.AutoAssigned && prev.AssetId != null)
                    {
                        ev.AssetId = prev.AssetId;
                        ev.AutoAssigned = false;
                        ev.ContainerMode = prev.ContainerMode;
                    }
                }
                existing.CandidateRefresh(clip);
            }
            else
            {
                _project.Animations.Add(clip);
            }
        }
        SyncAnimationAssetPaths();
        MarkDirty();
        return report;
    }

    private void SyncAnimationAssetPaths()
    {
        _project.AnimationAssetPaths.Clear();
        foreach (var clip in _project.Animations)
        {
            foreach (var ev in clip.Events)
            {
                if (ev.AssetId is { } id && _assetPaths.TryGetValue(id, out var p))
                    _project.AnimationAssetPaths[id.ToString()] = p;
                foreach (var cand in ev.CandidateIds)
                {
                    if (_assetPaths.TryGetValue(cand, out var cp))
                        _project.AnimationAssetPaths[cand.ToString()] = cp;
                }
            }
        }
    }

    /// <summary>编译动画时间轴（帧号/帧率 → 采样，绝对时间取整）。</summary>
    internal CompiledTimeline CompileAnimation(AnimationClip clip)
    {
        var timeline = AnimationAssembler.BuildTimeline(clip, _project.Assets, _project.SampleRate);
        foreach (var ev in clip.Events)
        {
            if (ev.AssetId is { } id && !_assetPaths.ContainsKey(id))
            {
                var asset = _project.Assets.FirstOrDefault(a => a.Id == id);
                if (asset != null)
                {
                    var candidate = AnimationAssembler.LocateAsset(asset, _project.AnimationAssetPaths, id);
                    if (candidate != null) _assetPaths[id] = candidate;
                }
            }
        }
        return timeline;
    }

    internal void PreviewAnimation(AnimationClip clip, double monitorDb)
    {
        var timeline = CompileAnimation(clip);
        if (timeline.Events.Count == 0)
        {
            ErrorText = "该动画没有已指定文件且启用的音频事件；先在右侧为事件选择文件。";
            return;
        }
        if (timeline.Issues.Count > 0)
            ErrorText = string.Join("；", timeline.Issues.Take(4).Select(i => $"{i.LayerName}：{i.Message}"));
        StatusText = "正在渲染动画预览…";
        PlayRendered(timeline, monitorDb);
    }

    /// <summary>预览单个事件（只渲染该事件，便于逐帧核对）。</summary>
    internal void PreviewAnimationEvent(AnimationClip clip, AnimEvent ev)
    {
        if (ev.AssetId == null)
        {
            ErrorText = "该事件尚未指定音频文件。";
            return;
        }
        var timeline = CompileAnimation(clip);
        var single = new CompiledTimeline
        {
            Events = [.. timeline.Events.Where(e => e.LayerId == ev.Id)],
            TotalSamples = timeline.TotalSamples,
            AssetById = timeline.AssetById,
        };
        if (single.Events.Count == 0)
        {
            ErrorText = "该事件没有可播放的数据。";
            return;
        }
        PlayRendered(single, MonitorCompensationDb);
    }

    internal ExportResult? ExportAnimation(
        AnimationClip clip, string outputPath, int bitDepth, bool dither, int ditherSeed,
        double? attenuateToDbfs, bool trimTail, double trimThresholdDb, double trimTailMs,
        double? customLengthSeconds = null)
    {
        var export = _project.ActiveWeapon?.Export;
        var timeline = CompileAnimation(clip);
        if (timeline.Events.Count == 0)
            return new ExportResult { Success = false, Error = "没有已指定文件且启用的音频事件。" };
        if (timeline.Issues.Count > 0)
            return new ExportResult { Success = false, Error = "存在未解析事件：" + string.Join("；", timeline.Issues.Take(4).Select(i => i.Message)) };

        var mix = MixKernel.Render(timeline, ResolveBuffer, 2);
        if (mix.MissingAssets.Count > 0)
            return new ExportResult { Success = false, Error = $"素材缺失：{string.Join("、", mix.MissingAssets.Distinct().Take(5))}" };

        var trimmed = trimTail
            ? TailTrimmer.Trim(mix.Data, 2, _project.SampleRate, trimThresholdDb, trimTailMs)
            : new TailTrimResult(mix.Data, 0, mix.Data.Length / 2L);

        var exportData = customLengthSeconds is { } len
            ? Exporter.ApplyCustomLength(trimmed.Data, _project.SampleRate, 2, len)
            : trimmed.Data;

        var result = Exporter.WriteWav(exportData, _project.SampleRate, 2, bitDepth, dither, ditherSeed,
            attenuateToDbfs, outputPath, overwrite: false,
            sourceFormat: export?.SourceEngineTarget == true,
            monoDownmix: export?.SourceMono == true) with
        {
            TrimmedFrames = trimmed.RemovedFrames,
            TrimThresholdDb = trimTail ? trimThresholdDb : null,
            TrimTailMs = trimTail ? trimTailMs : null,
        };

        if (result.Success && export?.SourceEngineTarget == true)
            WriteGameSoundsScript(clip.WeaponKey.Length > 0 ? clip.WeaponKey : clip.Name, Path.GetDirectoryName(outputPath) ?? ".");

        if (result.Success)
        {
            var json = AnimationRecipeJson.Build(clip, timeline, _project.Assets, _project.SampleRate,
                bitDepth, dither, ditherSeed, result.PeakDbfs, result.DurationSeconds, result.AppliedGainDb,
                trimTail ? (trimThresholdDb, trimTailMs, trimmed.RemovedFrames) : null,
                Path.GetFileName(outputPath), Banks?.Banks.Count ?? 0);
            File.WriteAllText(Path.ChangeExtension(outputPath, ".anim.json"), json, new UTF8Encoding(false));
        }
        return result;
    }

    /// <summary>动画素材的显示名（基名，便于列表阅读）。</summary>
    internal string AnimationAssetLabel(Guid id)
    {
        var asset = _project.Assets.FirstOrDefault(a => a.Id == id);
        return asset == null ? "(缺失)" : AnimationSoundLibrary.BaseName(asset.FileName);
    }

    /// <summary>
    /// 生成 game_sounds_*.txt（Source 引擎 / L4D2 / GMod）。可选项，与 Source 目标配套。
    /// 扫描输出目录里本次武器的 WAV，按事件归类：多个变体自动写成 rndwave 列表，
    /// 语法对齐 Source SDK 的 game_sounds_weapons.txt（顶层条目 + channel/volume/soundlevel/pitch）。
    /// </summary>
    private static void WriteGameSoundsScript(string weaponName, string outputDirectory)
    {
        var safe = SourceEngine.SanitizeIdentifier(weaponName);
        var byEvent = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var wav in Directory.EnumerateFiles(outputDirectory, "*.wav").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(wav);
            var ev = EventNameOf(fileName, safe);
            if (!byEvent.TryGetValue(ev, out var list)) byEvent[ev] = list = [];
            list.Add(SourceEngine.GamePath(weaponName, fileName));
        }
        if (byEvent.Count == 0) return;

        var entries = byEvent.Select(kv => new SourceEngine.SoundEntry(
            kv.Key,
            SourceEngine.ChannelWeapon,
            "1.0",
            SourceEngine.SoundLevelGunfire,
            "PITCH_NORM",
            kv.Value));
        var text = SourceEngine.BuildGameSounds(weaponName, entries);
        File.WriteAllText(Path.Combine(outputDirectory, $"game_sounds_{safe}.txt"), text, new UTF8Encoding(false));
    }

    /// <summary>文件名 → game_sounds 事件名：<c>*_single_*</c> → Single，<c>*_burst_*</c> → Burst，其余用基名。</summary>
    private static string EventNameOf(string fileName, string weaponSafe)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Contains("_single_")) return $"Weapon_{weaponSafe}.Single";
        if (stem.Contains("_burst_")) return $"Weapon_{weaponSafe}.Burst";
        return $"Weapon_{weaponSafe}.{SourceEngine.SanitizeIdentifier(stem)}";
    }

    internal AssetInfo? FindAnimationAsset(Guid id) => _project.Assets.FirstOrDefault(a => a.Id == id);

    // ───────────────────────── 对话框辅助 ─────────────────────────

    public string? ShowInput(string title, string label, string initial) =>
        Views.InputDialog.Show(Application.Current.MainWindow, title, label, initial);

    // ───────────────────────── 关闭 / 恢复 ─────────────────────────

    public void Shutdown()
    {
        Interlocked.Increment(ref _previewGeneration);
        if (_dirty) ProjectStore.SaveRecovery(_project, _projectPath);
        Playback.Dispose();
        Cache.Dispose();
    }

    /// <summary>启动时若有自动恢复文件，提示是否恢复未保存内容。</summary>
    public void TryRestoreRecovery()
    {
        var path = ProjectStore.LatestRecoveryPath;
        if (path == null || !File.Exists(path)) return;
        if (new FileInfo(path).Length == 0) return;
        var r = MessageBox.Show(
            "发现未保存工程的自动恢复文件（与最后一次明确保存的工程分开存放）。要恢复未保存内容吗？",
            "恢复未保存内容", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            var project = ProjectStore.Load(path);
            LoadProject(project, null);
            StatusText = "已恢复自动保存内容（尚未保存为正式工程）";
        }
        catch (Exception ex)
        {
            ErrorText = $"恢复失败：{ex.Message}";
        }
    }
}
