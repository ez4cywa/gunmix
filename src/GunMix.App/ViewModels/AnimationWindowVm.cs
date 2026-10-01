using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GunMix.Core.Cast;
using GunMix.Core.Model;
using Microsoft.Win32;

namespace GunMix.App.ViewModels;

/// <summary>
/// 动画音效工作台：把 .cast 的音频通知（实测帧号与别名）装配成一条完整动画音效并导出。
/// 动画名、帧率、帧号、别名来自文件实测；别名对应哪个音频文件由名称唯一命中或用户指定，
/// 界面必须分开显示两者，不宣称原版还原。
/// </summary>
public sealed class AnimationWindowVm : ViewModelBase
{
    private readonly MainViewModel _owner;
    private readonly Services.UiSettings _settings;
    private string _animationDir = "";
    private string _soundDir = "";
    private string _bankDir = "";
    private string _outputDir = "";

    public AnimationWindowVm(MainViewModel owner)
    {
        _owner = owner;
        // 目录来自每用户的设置文件；仓库里不含任何内置路径
        _settings = Services.UiSettings.Load();
        _animationDir = _settings.AnimationDir;
        _soundDir = _settings.SoundDir;
        _bankDir = _settings.BankDir;
        _outputDir = _settings.OutputDir.Length > 0
            ? _settings.OutputDir
            : (owner.Project.ActiveWeapon?.Export.OutputDirectory ?? "");
        ScanCommand = new RelayCommand(Scan, () => !_scanning);
        PreviewClipCommand = new RelayCommand(PreviewClip, () => SelectedClip != null);
        PreviewEventCommand = new RelayCommand(PreviewEvent, () => SelectedEvent != null);
        AssignCommand = new RelayCommand(AssignSelected, () => SelectedEvent != null && SelectedCandidate != null);
        AssignAutoCommand = new RelayCommand(AssignAllAuto, () => SelectedClip != null);
        RerollCommand = new RelayCommand(RerollContainers,
            () => SelectedClip?.Model.Events.Any(e => e.ContainerIds.Count > 1 && e.AutoAssigned) == true);
        ExportCommand = new RelayCommand(Export, () => SelectedClip != null);
        LoadClips();
    }

    public RelayCommand ScanCommand { get; }
    public RelayCommand PreviewClipCommand { get; }
    public RelayCommand PreviewEventCommand { get; }
    public RelayCommand AssignCommand { get; }
    public RelayCommand AssignAutoCommand { get; }
    public RelayCommand RerollCommand { get; }
    public RelayCommand ExportCommand { get; }

    public string AnimationDir
    {
        get => _animationDir;
        set => Set(ref _animationDir, value);
    }

    public string SoundDir
    {
        get => _soundDir;
        set => Set(ref _soundDir, value);
    }

    /// <summary>soundbank 目录（含 weapon_rex_*.json）；留空则只做名称推断。</summary>
    public string BankDir
    {
        get => _bankDir;
        set => Set(ref _bankDir, value);
    }

    public string OutputDir
    {
        get => _outputDir;
        set => Set(ref _outputDir, value);
    }

    /// <summary>Source 引擎格式导出（44.1 kHz / 16 bit + game_sounds 脚本）。</summary>
    public bool ExportSourceFormat
    {
        get => _owner.Project.ActiveWeapon?.Export.SourceEngineTarget == true;
        set
        {
            if (_owner.Project.ActiveWeapon is { } w)
            {
                w.Export.SourceEngineTarget = value;
                Raise();
            }
        }
    }

    /// <summary>自定义文件长度开关。</summary>
    public bool UseCustomLength
    {
        get => _owner.Project.ActiveWeapon?.Export.CustomLengthSeconds != null;
        set
        {
            if (_owner.Project.ActiveWeapon is { } w)
            {
                w.Export.CustomLengthSeconds = value ? w.Export.CustomLengthSeconds ?? 3.0 : null;
                Raise();
                Raise(nameof(CustomLengthSeconds));
            }
        }
    }

    /// <summary>自定义文件长度（秒）。</summary>
    public double CustomLengthSeconds
    {
        get => _owner.Project.ActiveWeapon?.Export.CustomLengthSeconds ?? 3.0;
        set
        {
            if (_owner.Project.ActiveWeapon is { } w)
            {
                w.Export.CustomLengthSeconds = Math.Clamp(Math.Round(value, 1), 0.1, 60);
                Raise();
            }
        }
    }

    // ───────── 扫描状态 ─────────

    private bool _scanning;
    public bool Scanning
    {
        get => _scanning;
        private set
        {
            if (Set(ref _scanning, value))
            {
                Raise(nameof(ScanButtonText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string ScanButtonText => Scanning ? "扫描中…" : "扫描动画目录";

    private string _scanSummary = "";
    public string ScanSummary { get => _scanSummary; private set => Set(ref _scanSummary, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _error = "";
    public string Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    // ───────── 剪辑列表 ─────────

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) LoadClips(); }
    }

    public ObservableCollection<ClipVm> Clips { get; } = [];

    private ClipVm? _selectedClip;
    public ClipVm? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (Set(ref _selectedClip, value))
            {
                LoadEvents();
                Raise(nameof(PreviewClipCommand));
                Raise(nameof(AssignAutoCommand));
                Raise(nameof(ExportCommand));
            }
        }
    }

    public sealed class ClipVm : ViewModelBase
    {
        private readonly AnimationWindowVm _parent;
        public AnimationClip Model { get; }

        public ClipVm(AnimationWindowVm parent, AnimationClip model)
        {
            _parent = parent;
            Model = model;
        }

        public string Name => Model.Name;
        public string WeaponKey => Model.WeaponKey;
        public string FpsLabel => $"{Model.Framerate:0.#} fps";
        public int EventCount => Model.Events.Count;
        public int ResolvedCount => Model.Events.Count(e => e.AssetId != null);
        public int BankCount => Model.Events.Count(e => e.IsBankAuthoritative);
        public int MissingCount => Model.Events.Count(e => e.AssetId == null);

        /// <summary>就绪度：bank 权威数 / 总数；有未解析时提示还需指定几个。</summary>
        public string Readiness => MissingCount == 0
            ? $"{EventCount}/{EventCount} 已解析（soundbank {BankCount}）"
            : $"{ResolvedCount}/{EventCount} 已解析（soundbank {BankCount}）· 待指定 {MissingCount}";

        public bool FullyResolved => ResolvedCount == EventCount && EventCount > 0;
        public bool AllFromBank => EventCount > 0 && BankCount == EventCount;
        public string LoopLabel => Model.Looping ? "循环" : "单次";
        public double DurationEstimate => Model.Events.Count == 0 ? 0 : Model.Events.Max(e => e.Frame) / Model.Framerate;
        public int Seed => Model.Seed;

        public double MasterGainDb
        {
            get => Model.MasterGainDb;
            set { Model.MasterGainDb = Math.Clamp(Math.Round(value, 1), -60, 6); _parent.NotifyEdited(); Raise(); }
        }

        /// <summary>外部改动模型后刷新就绪度显示。</summary>
        public void RefreshReadiness()
        {
            Raise(nameof(Readiness));
            Raise(nameof(ResolvedCount));
            Raise(nameof(BankCount));
            Raise(nameof(MissingCount));
            Raise(nameof(FullyResolved));
            Raise(nameof(AllFromBank));
            Raise(nameof(Seed));
        }

        public string Display => $"{Model.Name}　{Model.WeaponKey}";
    }

    private void LoadClips()
    {
        Clips.Clear();
        var query = _owner.Project.Animations.AsEnumerable();
        var s = Search.Trim();
        if (s.Length > 0)
            query = query.Where(c => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                                     || c.WeaponKey.Contains(s, StringComparison.OrdinalIgnoreCase));
        foreach (var c in query.OrderBy(c => c.WeaponKey).ThenBy(c => c.Name))
            Clips.Add(new ClipVm(this, c));
        SelectedClip = Clips.FirstOrDefault();
        Raise(nameof(Clips));
    }

    // ───────── 事件表 ─────────

    public ObservableCollection<EventVm> Events { get; } = [];

    private EventVm? _selectedEvent;
    public EventVm? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (Set(ref _selectedEvent, value))
            {
                LoadCandidates();
                Raise(nameof(PreviewEventCommand));
                Raise(nameof(AssignCommand));
            }
        }
    }

    public sealed class EventVm : ViewModelBase
    {
        private readonly AnimationWindowVm _parent;
        public AnimEvent Model { get; }

        public EventVm(AnimationWindowVm parent, AnimEvent model)
        {
            _parent = parent;
            Model = model;
        }

        public int Frame => Model.Frame;
        public double TimeS => Math.Round(_parent.SelectedClip?.Model.FrameToSeconds(Model.Frame) ?? 0, 4);
        public string Alias => Model.Alias;
        public Guid? AssetId => Model.AssetId;

        public string ConfidenceLabel => Model.Source switch
        {
            AnimSource.BankSingle => "soundbank：唯一文件",
            AnimSource.BankContainer => $"soundbank：容器 {Model.ContainerIds.Count} 选 1",
            AnimSource.BankMissing => $"bank 有记录，{Model.BankMissingCount} 个音频未导出",
            AnimSource.NameExact => "名称精确匹配",
            AnimSource.NameSingle => "单一候选",
            AnimSource.NameMultiple => $"{Model.CandidateIds.Count} 个候选，需指定",
            AnimSource.NameFuzzy => $"{Model.CandidateIds.Count} 个近似候选，需确认",
            AnimSource.UserAssigned => "你指定的",
            _ => "待指定",
        };

        /// <summary>bank 权威＝绿，用户指定＝蓝，需确认＝黄，素材未导出＝红。</summary>
        public string ConfidenceColor => Model.Source switch
        {
            AnimSource.BankSingle or AnimSource.BankContainer => "#7FBF7F",
            AnimSource.UserAssigned => "#4C9FD8",
            AnimSource.BankMissing => "#E06C5F",
            AnimSource.NameExact or AnimSource.NameSingle when Model.AssetId != null => "#7FBF7F",
            _ => Model.AssetId != null ? "#4C9FD8" : "#D9B44A",
        };

        /// <summary>bank 命中的事件，其容器成员就是原版全集；界面据此说明候选来源。</summary>
        public bool IsBankAuthoritative => Model.IsBankAuthoritative;

        public bool HasContainer => Model.ContainerIds.Count > 1;

        public string FileLabel
        {
            get
            {
                if (Model.AssetId is not { } id) return IsBankAuthoritative ? "（成员未导出）" : "（待指定）";
                var label = _parent._owner.AnimationAssetLabel(id);
                return Model.ContainerIds.Count > 1
                    ? $"{label}（容器 {Model.ContainerIds.IndexOf(id) + 1}/{Model.ContainerIds.Count}）"
                    : label;
            }
        }

        public bool Enabled
        {
            get => Model.Enabled;
            set { Model.Enabled = value; _parent.NotifyEdited(); Raise(); }
        }

        public double GainDb
        {
            get => Model.GainDb;
            set { Model.GainDb = Math.Clamp(Math.Round(value, 1), -60, 6); _parent.NotifyEdited(); Raise(); }
        }

        public void Refresh()
        {
            Raise(nameof(TimeS));
            Raise(nameof(ConfidenceLabel));
            Raise(nameof(ConfidenceColor));
            Raise(nameof(FileLabel));
            Raise(nameof(HasContainer));
            Raise(nameof(IsBankAuthoritative));
        }
    }

    private void LoadEvents()
    {
        Events.Clear();
        if (SelectedClip is { } clip)
            foreach (var ev in clip.Model.Events)
                Events.Add(new EventVm(this, ev));
        SelectedEvent = Events.FirstOrDefault();
        Raise(nameof(Events));
        Raise(nameof(OtherNotes));
    }

    public IEnumerable<string> OtherNotes => SelectedClip?.Model.OtherNotes ?? [];

    // ───────── 候选 ─────────

    public ObservableCollection<CandidateVm> Candidates { get; } = [];

    private CandidateVm? _selectedCandidate;
    public CandidateVm? SelectedCandidate
    {
        get => _selectedCandidate;
        set { if (Set(ref _selectedCandidate, value)) Raise(nameof(AssignCommand)); }
    }

    public sealed class CandidateVm
    {
        public Guid AssetId { get; init; }
        public string Label { get; init; } = "";
        public string Sublabel { get; init; } = "";
        public bool IsCurrent { get; init; }
        public bool IsApproximate { get; init; }

        /// <summary>该候选来自 soundbank 容器（原版成员），而非名称推断。</summary>
        public bool IsBankMember { get; init; }
    }

    private void LoadCandidates()
    {
        Candidates.Clear();
        if (SelectedEvent is not { } ev) { Raise(nameof(Candidates)); return; }
        var model = ev.Model;
        bool bank = model.IsBankAuthoritative;
        // bank 命中时候选即原版容器成员；否则用名称推断候选
        var ids = bank && model.ContainerIds.Count > 0 ? model.ContainerIds : model.CandidateIds;
        bool fuzzy = model.Source == AnimSource.NameFuzzy;
        foreach (var id in ids)
        {
            var asset = _owner.FindAnimationAsset(id);
            Candidates.Add(new CandidateVm
            {
                AssetId = id,
                Label = _owner.AnimationAssetLabel(id),
                Sublabel = asset == null
                    ? "素材缺失"
                    : $"{asset.Format.SampleRate / 1000.0:0.#} kHz {asset.Format.BitsPerSample} bit · {asset.Format.DurationSeconds:0.000} s · {Path.GetFileName(asset.SourceDirectory)}",
                IsCurrent = model.AssetId == id,
                IsApproximate = fuzzy,
                IsBankMember = bank,
            });
        }
        SelectedCandidate = Candidates.FirstOrDefault(c => c.IsCurrent) ?? Candidates.FirstOrDefault();
        Raise(nameof(Candidates));
    }

    private void AssignSelected()
    {
        if (SelectedEvent is not { } ev || SelectedCandidate is not { } cand) return;
        ev.Model.AssetId = cand.AssetId;
        ev.Model.AutoAssigned = false;   // 用户显式指定，优先于 bank 容器选样
        ev.Model.Source = AnimSource.UserAssigned;
        _owner.NotifyEdited($"第 {ev.Frame} 帧事件已指定：{cand.Label}");
        RefreshEventRow(ev);
        SelectedClip?.RefreshReadiness();
    }

    /// <summary>
    /// 容器换种子：换一次种子即从原版容器里取另一个成员，仍属原版集合。
    /// </summary>
    private void RerollContainers()
    {
        if (SelectedClip is not { } clip) return;
        _owner.PushUndoExternal();
        clip.Model.Seed = unchecked(clip.Model.Seed * 31 + 7);
        _owner.TouchDirtyFromAnimation();
        ReloadEvents();
        _owner.StatusText = $"容器选样种子已换为 {clip.Model.Seed}（只在原版容器成员内更换）";
    }

    /// <summary>批量采用唯一命中：只处理自动已定的项，多候选与近似候选不动。</summary>
    private void AssignAllAuto()
    {
        if (SelectedClip is not { } clip) return;
        int n = 0;
        foreach (var ev in clip.Model.Events)
        {
            if (ev.AssetId == null && ev.CandidateIds.Count == 1
                && ev.Source is AnimSource.NameExact or AnimSource.NameSingle)
            {
                ev.AssetId = ev.CandidateIds[0];
                ev.AutoAssigned = true;
                n++;
            }
        }
        _owner.NotifyEdited(n > 0 ? $"已采用 {n} 个唯一命中；其余仍需手动指定" : "没有可自动采用的唯一命中");
        ReloadEvents();
    }

    private void RefreshEventRow(EventVm ev)
    {
        ev.Refresh();
        LoadCandidates();
    }

    private void ReloadEvents()
    {
        var keep = SelectedEvent?.Model;
        LoadEvents();
        if (keep != null) SelectedEvent = Events.FirstOrDefault(e => e.Model == keep);
        SelectedClip?.RefreshReadiness();
    }

    private void NotifyEdited(string status = "")
    {
        _owner.NotifyEdited(status);
        SelectedClip?.RefreshReadiness();
    }

    // ───────── 扫描 ─────────

    public void Scan()
    {
        Error = "";
        if (!Directory.Exists(AnimationDir))
        {
            Error = $"动画目录不存在：{AnimationDir}";
            return;
        }
        if (!Directory.Exists(SoundDir))
        {
            Error = $"声音目录不存在：{SoundDir}";
            return;
        }
        var bankDir = BankDir.Trim();
        if (bankDir.Length > 0 && !Directory.Exists(bankDir))
        {
            Error = $"soundbank 目录不存在：{bankDir}（留空则只做名称推断）";
            return;
        }
        Scanning = true;
        Status = bankDir.Length > 0 ? "正在读取 soundbank 与扫描动画…" : "正在扫描动画目录（无 soundbank）…";
        var bankArg = bankDir.Length > 0 ? bankDir : null;

        // 记住本次使用的目录，下次打开自动填好
        _settings.AnimationDir = AnimationDir;
        _settings.SoundDir = SoundDir;
        _settings.BankDir = BankDir;
        _settings.OutputDir = OutputDir;
        _settings.Save();

        Task.Run(() => _owner.ScanAnimations(AnimationDir, [SoundDir], bankArg))
            .ContinueWith(t =>
            {
                Scanning = false;
                if (t.IsFaulted)
                {
                    Error = $"扫描失败：{t.Exception?.GetBaseException().Message}";
                    return;
                }
                var r = t.Result;
                LoadClips();
                ScanSummary = bankArg != null
                    ? $"soundbank {r.BanksLoaded} 个 bank · cast {r.CastFilesSeen} 个 · 含音频 {r.ClipsWithAudio} 条 · " +
                      $"事件 {r.TotalEvents} 个 · bank 权威 {r.BankEvents} 个（其中随机容器 {r.BankContainers} 个）· " +
                      $"素材未导出 {r.BankMissing} 个 · 其余靠名称推断或需手动指定"
                    : $"cast {r.CastFilesSeen} 个 · 含音频 {r.ClipsWithAudio} 条 · 事件 {r.TotalEvents} 个 · " +
                      $"名称唯一命中 {r.AutoResolved} 个（未加载 soundbank，映射不权威）";
                if (r.Failures.Count > 0)
                    Error = string.Join("\n", r.Failures.Take(10));
                Status = "绿色＝soundbank 原版映射；蓝色＝你指定；黄色＝需确认；红色＝原版有但素材未导出";
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ───────── 试听与导出 ─────────

    private void PreviewClip()
    {
        if (SelectedClip is not { } clip) return;
        Error = "";
        _owner.PreviewAnimation(clip.Model, _owner.MonitorCompensationDb);
        if (_owner.ErrorText.Length > 0) Error = _owner.ErrorText;
    }

    private void PreviewEvent()
    {
        if (SelectedClip is not { } clip || SelectedEvent is not { } ev) return;
        Error = "";
        _owner.PreviewAnimationEvent(clip.Model, ev.Model);
        if (_owner.ErrorText.Length > 0) Error = _owner.ErrorText;
    }

    private void Export()
    {
        if (SelectedClip is not { } clip) return;
        var unresolved = clip.Model.Events.Count(e => e.AssetId == null && e.Enabled);
        if (unresolved > 0)
        {
            MessageBox.Show(
                $"还有 {unresolved} 个启用的事件没有指定音频文件。原版声音配置未随导出提供，软件不会替你猜或借用其他文件。\n\n" +
                "可以先在右侧为这些事件指定文件，或取消它们的勾选后再导出。",
                "存在未指定事件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            var dlg = new OpenFolderDialog { Title = "选择动画音效输出目录" };
            if (dlg.ShowDialog() != true) return;
            OutputDir = dlg.FolderName;
        }
        Directory.CreateDirectory(OutputDir);
        var safe = string.Join("_", clip.Model.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var path = Path.Combine(OutputDir, $"anim_{safe}.wav");
        if (File.Exists(path))
            path = Path.Combine(OutputDir, $"anim_{safe}_{DateTime.Now:HHmmss}.wav");

        Status = "正在导出…";
        Error = "";
        var export = _owner.Project.ActiveWeapon?.Export;
        Task.Run(() => _owner.ExportAnimation(clip.Model, path,
                export?.BitDepth ?? 24, export?.DitherEnabled ?? true, export?.DitherSeed ?? 20260927,
                export?.AttenuateToDbfs,
                export == null || export.TrimTail, export?.TrimThresholdDb ?? -60, export?.TrimTailMs ?? 120,
                export?.CustomLengthSeconds))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Error = $"导出失败：{t.Exception?.GetBaseException().Message}";
                    return;
                }
                var r = t.Result;
                if (r is { Success: true })
                {
                    Status = $"已导出：{r.FinalPath}　时长 {r.DurationSeconds:0.000} s，峰值 {r.PeakDbfs:0.00} dBFS" +
                             (r.TrimmedFrames > 0 ? $"，截尾 {r.TrimmedFrames / 48000.0:0.000} s" : "") +
                             $"；配套 anim_*.anim.json";
                    _owner.NotifyEdited("动画音效已导出，工程已标记为未保存");
                }
                else
                {
                    Error = $"导出失败：{r?.Error}";
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void BrowseAnimationDir()
    {
        var dlg = new OpenFolderDialog { Title = "选择动画目录（含 .cast）" };
        if (dlg.ShowDialog() == true) AnimationDir = dlg.FolderName;
    }

    public void BrowseSoundDir()
    {
        var dlg = new OpenFolderDialog { Title = "选择声音根目录（含各武器与 reloads 子目录）" };
        if (dlg.ShowDialog() == true) SoundDir = dlg.FolderName;
    }

    public void BrowseBankDir()
    {
        var dlg = new OpenFolderDialog { Title = "选择 soundbank 目录（含 weapon_rex_*.json）" };
        if (dlg.ShowDialog() == true) BankDir = dlg.FolderName;
    }

    public void BrowseOutputDir()
    {
        var dlg = new OpenFolderDialog { Title = "选择输出目录" };
        if (dlg.ShowDialog() == true) OutputDir = dlg.FolderName;
    }
}
