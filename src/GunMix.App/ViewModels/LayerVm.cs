using System.Collections.ObjectModel;
using System.Windows.Media;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Model;

namespace GunMix.App.ViewModels;

/// <summary>混音层行视图模型：包装模型层，写入前推入撤销快照。</summary>
public sealed class LayerVm : ViewModelBase
{
    private readonly MainViewModel _owner;
    private readonly Layer _layer;

    public LayerVm(MainViewModel owner, Layer layer)
    {
        _owner = owner;
        _layer = layer;
    }

    public Layer Model => _layer;
    public Guid Id => _layer.Id;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string Name
    {
        get => _layer.Name;
        set { if (_layer.Name == value) return; _owner.EditLayer(_layer, "改名", invalidateManifest: false, l => l.Name = value); Raise(); }
    }

    public string Role => _layer.Role;

    public bool Enabled
    {
        get => _layer.Enabled;
        set { if (_layer.Enabled == value) return; _owner.EditLayer(_layer, "开关", invalidateManifest: true, l => l.Enabled = value); Raise(); }
    }

    public bool Solo
    {
        get => _layer.Solo;
        set { if (_layer.Solo == value) return; _owner.EditLayer(_layer, "独听", invalidateManifest: false, l => l.Solo = value); Raise(); }
    }

    public double GainDb
    {
        get => _layer.GainDb;
        set { if (_layer.GainDb == Math.Clamp(Math.Round(value, 1), -60, 6)) return; _owner.EditLayer(_layer, "增益", invalidateManifest: false, l => l.GainDb = Math.Clamp(Math.Round(value, 1), -60, 6)); Raise(); Raise(nameof(LoudnessDisplay)); }
    }

    public double DelayMs
    {
        get => _layer.DelayMs;
        set { if (_layer.DelayMs == Math.Clamp(Math.Round(value, 1), 0, 1000)) return; _owner.EditLayer(_layer, "延时", invalidateManifest: false, l => l.DelayMs = Math.Clamp(Math.Round(value, 1), 0, 1000)); Raise(); }
    }

    public string PoolDisplay
    {
        get
        {
            if (_layer.PoolAssetIds.Count > 0) return $"自选池 ×{_layer.PoolAssetIds.Count}";
            return _layer.PoolGroupKey.Length > 0 ? _layer.PoolGroupKey : "待指定";
        }
    }

    public VariantMode VariantMode
    {
        get => _layer.VariantMode;
        set { if (_layer.VariantMode == value) return; _owner.EditLayer(_layer, "样本选择", invalidateManifest: true, l => l.VariantMode = value); Raise(); }
    }

    public int? Seed
    {
        get => _layer.Seed;
        set { if (_layer.Seed == value) return; _owner.EditLayer(_layer, "种子", invalidateManifest: true, l => l.Seed = value); Raise(); }
    }

    public bool IsExperimental
    {
        get => _layer.IsExperimental;
        set { if (_layer.IsExperimental == value) return; _owner.EditLayer(_layer, "实验触发", invalidateManifest: true, l => l.IsExperimental = value); Raise(); }
    }

    public ExperimentalTrigger Trigger
    {
        get => _layer.Trigger;
        set { if (_layer.Trigger == value) return; _owner.EditLayer(_layer, "实验触发", invalidateManifest: true, l => l.Trigger = value); Raise(); }
    }

    public int TriggerShotNumber
    {
        get => _layer.TriggerShotNumber;
        set { if (_layer.TriggerShotNumber == Math.Max(1, value)) return; _owner.EditLayer(_layer, "实验触发", invalidateManifest: true, l => l.TriggerShotNumber = Math.Max(1, value)); Raise(); }
    }

    /// <summary>连发同时发声上限（0 = 不限）；不改变样本选择，只影响实例抢占淡出。</summary>
    public int BurstVoiceLimit
    {
        get => _layer.BurstVoiceLimit;
        set { if (_layer.BurstVoiceLimit == Math.Clamp(value, 0, 100)) return; _owner.EditLayer(_layer, "同时发声上限", invalidateManifest: true, l => l.BurstVoiceLimit = Math.Clamp(value, 0, 100)); Raise(); }
    }

    /// <summary>素材池响度（前 250 ms RMS 中位数），配平依据。</summary>
    public string LoudnessDisplay
    {
        get
        {
            var pool = AssetService.ResolvePool(_layer, _owner.Project.Assets, _owner.CurrentWeaponId);
            var rms = GunMix.Core.Synthesis.LoudnessMeter.GroupEarlyRms(pool);
            if (rms == null) return pool.Count == 0 ? "—" : "未测量（点“响度配平”时补测）";
            var e95 = pool.Select(a => a.Loudness?.Energy95Ms).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty().Max();
            return $"前 250 ms RMS {rms:0.0} dBFS · 输出约 {rms + _layer.GainDb:0.0} dBFS · 95% 能量 ≤ {e95:0} ms";
        }
    }

    /// <summary>规则来源：分组来自命名识别或手动设置；触发规则标注“用户设定，原版条件未确认”。</summary>
    public string RuleSource
    {
        get
        {
            var src = _layer.PoolGroupKey.Length > 0 ? "命名推断" : "用户设置";
            if (_layer.IsExperimental) return $"{src} · 触发为用户设定，原版条件未确认";
            return src;
        }
    }

    /// <summary>当前变体显示（清单当前选择的文件）。</summary>
    public string CurrentVariant
    {
        get
        {
            var manifest = _owner.CurrentRecipe?.SingleManifest;
            var entry = manifest?.Entries.FirstOrDefault(e => e.LayerId == _layer.Id);
            if (entry == null) return "—";
            var asset = _owner.FindAsset(entry.AssetId);
            if (asset == null) return "素材缺失";
            var v = asset.Parsed?.Variant;
            return v != null ? $"{asset.GroupKey} / {v:00}" : asset.FileName;
        }
    }

    public string FormatDisplay
    {
        get
        {
            var asset = _owner.FirstPoolAsset(_layer);
            if (asset == null) return "—";
            return $"{asset.Format.SampleRate} kHz / {asset.Format.BitsPerSample} bit / {(asset.Format.Channels == 1 ? "单声道" : "双声道")}";
        }
    }

    public string LengthDisplay
    {
        get
        {
            var asset = _owner.FirstPoolAsset(_layer);
            if (asset == null) return "—";
            return $"{asset.Format.DurationSeconds:0.000} 秒（文件头实测）";
        }
    }

    public string PathDisplay => _owner.FirstPoolAsset(_layer) is { } a ? _owner.AssetPathOf(a) : "—";

    /// <summary>当前层素材池（用于固定样本选择与事件表替换）。</summary>
    /// <summary>成员不变时返回同一集合实例，避免绑定的下拉框因列表替换而重置选择。</summary>
    public ObservableCollection<AssetInfo> Pool
    {
        get
        {
            var pool = _owner.CurrentRecipe == null
                ? []
                : AssetService.ResolvePool(_layer, _owner.Project.Assets, _owner.CurrentWeaponId);
            if (_pool == null || !_pool.Select(a => a.Id).SequenceEqual(pool.Select(a => a.Id)))
                _pool = new ObservableCollection<AssetInfo>(pool);
            return _pool;
        }
    }

    private ObservableCollection<AssetInfo>? _pool;

    public Guid? FixedAssetId
    {
        get => _layer.FixedAssetId;
        // 下拉框在列表替换或切换选中层时会回写 null 或上一层的样本：只接受本层素材池内的新值，避免循环与串层
        set { if (value == null || _layer.FixedAssetId == value || Pool.All(a => a.Id != value)) return; _owner.EditLayer(_layer, "固定样本", invalidateManifest: true, l => l.FixedAssetId = value); Raise(); }
    }

    private WaveformPeaks? _peaks;
    public WaveformPeaks? Peaks
    {
        get => _peaks;
        private set => Set(ref _peaks, value);
    }

    public void RefreshWaveform()
    {
        var asset = _owner.FirstPoolAsset(_layer);
        if (asset == null)
        {
            Peaks = null;
            return;
        }
        var scheduler = System.Threading.SynchronizationContext.Current != null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;
        Task.Run(() =>
        {
            try
            {
                var path = _owner.AssetPathOf(asset);
                return _owner.Cache.GetPeaks(asset.Id, path, _owner.Project.SampleRate, 240);
            }
            catch
            {
                return null;
            }
        }).ContinueWith(t =>
        {
            if (t.Result != null) Peaks = t.Result;
        }, scheduler);
    }

    public void RefreshAll()
    {
        Raise(nameof(PoolDisplay));
        Raise(nameof(CurrentVariant));
        Raise(nameof(FormatDisplay));
        Raise(nameof(LengthDisplay));
        Raise(nameof(PathDisplay));
        Raise(nameof(Pool));
        Raise(nameof(RuleSource));
        Raise(nameof(LoudnessDisplay));
        Raise(nameof(BurstVoiceLimit));
        RefreshWaveform();
    }

    public ImageSource? WaveformImage => null;
}
