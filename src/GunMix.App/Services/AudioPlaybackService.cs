using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GunMix.App.Services;

/// <summary>
/// 试听输出：单发或短连射预渲染后经 WASAPI 共享模式播放；
/// 停止试听用短淡出停止当前输出（只是播放器操作）。
/// 设备初始化在后台 MTA 线程进行：格式不匹配时 NAudio 用 DMO 重采样，必须在 MTA 线程创建，
/// 否则在界面 STA 线程上会失败导致“无声且无提示”。
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private IWavePlayer? _output;
    private readonly Func<IWavePlayer>? _outputFactory;
    private FadeOutProvider? _current;
    private readonly object _lock = new();
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly MMDeviceNotificationNotifications _notificationStub;
    private int _playGen;
    private int _disposed;

    public bool IsPlaying { get; private set; }
    public string? DeviceFriendlyName { get; private set; }

    public event Action? PlaybackStopped;
    public event Action<double>? PositionChanged; // 秒
    public event Action<double, double>? LevelsChanged; // L/R 峰值 0..1
    public event Action? DeviceRemoved;

    public AudioPlaybackService() : this(null) { }

    internal AudioPlaybackService(Func<IWavePlayer>? outputFactory)
    {
        _outputFactory = outputFactory;
        _enumerator = outputFactory == null ? new MMDeviceEnumerator() : null;
        // 设备被拔出：停止播放并更新设备状态，不丢工程，不无限重试抢占设备。
        _notificationStub = new MMDeviceNotificationNotifications();
        _notificationStub.DeviceRemoved += _ =>
        {
            HandleDeviceRemoved();
            DeviceRemoved?.Invoke();
        };
        try
        {
            _enumerator?.RegisterEndpointNotificationCallback(_notificationStub);
        }
        catch
        {
            // 个别系统无设备枚举能力时忽略
        }
    }

    /// <summary>枚举输出设备。</summary>
    public IReadOnlyList<(MMDevice Device, string Name)> EnumerateDevices()
    {
        var list = new List<(MMDevice, string)>();
        if (_enumerator == null) return list;
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add((d, d.FriendlyName));
            }
        }
        catch
        {
            // 无输出设备时允许编辑、保存和离线导出
        }
        return list;
    }

    /// <summary>
    /// 播放预渲染的 float32 交错数据（48 kHz 立体声）。
    /// 设备创建、格式协商与重采样在 MTA 后台线程完成；完成后在调用线程上下文回调 onStarted(error)。
    /// 所选设备失败时自动回退系统默认设备；仍失败则回调具体错误。
    /// </summary>
    public void Play(float[] data, int sampleRate, int channels, MMDevice? device, double monitorGainDb = 0,
        Action<string?>? onStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var ctx = SynchronizationContext.Current;
        Stop(immediate: true);
        int gen;
        lock (_lock) gen = ++_playGen;

        Task.Run(() =>
        {
            string? error = null;
            IWavePlayer? pendingOutput = null;
            try
            {
                lock (_lock) { if (gen != _playGen) return; }
                // COM 格式协商和设备初始化可能很慢，绝不能占用界面停止操作需要的锁。
                pendingOutput = CreateOutput(device, out var effectiveDevice);
                var source = new BufferedFloatSource(data, sampleRate, channels);
                var fader = new FadeOutProvider(source, monitorGainDb);
                fader.FadeCompleted += () => StopGeneration(gen);
                pendingOutput.Init(new SampleProviderWaveAdapter(fader));
                lock (_lock) { if (gen != _playGen) return; }
                pendingOutput.Play();
                lock (_lock)
                {
                    if (gen != _playGen) return;
                    _current = fader;
                    _current.BlockPlayed += OnBlockPlayed;
                    _output = pendingOutput;
                    _output.PlaybackStopped += OnStopped;
                    IsPlaying = true;
                    DeviceFriendlyName = effectiveDevice?.FriendlyName ?? "系统默认输出";
                    pendingOutput = null; // 发布成功，生命周期交给停止路径。
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    if (gen != _playGen) return;
                    IsPlaying = false;
                }
                error = DescribeError(ex);
            }
            finally { QueueCleanup(pendingOutput); }
            if (onStarted != null)
            {
                void Notify() { if (gen == Volatile.Read(ref _playGen)) onStarted(error); }
                if (ctx != null) ctx.Post(_ => Notify(), null);
                else Notify();
            }
        });
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        COMException ce => $"音频设备初始化失败（0x{ce.ErrorCode:X8}）。可尝试选择其他输出设备。",
        _ => ex.Message,
    };

    private IWavePlayer CreateOutput(MMDevice? device, out MMDevice? effectiveDevice)
    {
        if (_outputFactory != null) { effectiveDevice = null; return _outputFactory(); }
        if (device != null)
        {
            try
            {
                effectiveDevice = device;
                return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, 120);
            }
            catch
            {
                // 所选设备失败：回退系统默认设备
            }
        }
        effectiveDevice = null;
        return new WasapiOut(AudioClientShareMode.Shared, useEventSync: false, 120);
    }

    private void OnBlockPlayed(double positionSeconds, (float L, float R) peak)
    {
        PositionChanged?.Invoke(positionSeconds);
        LevelsChanged?.Invoke(Math.Abs(peak.L), Math.Abs(peak.R));
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        bool was;
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _output)) return;
            was = IsPlaying;
            IsPlaying = false;
        }
        if (was) PlaybackStopped?.Invoke();
    }

    /// <summary>停止：短淡出后停止当前输出。</summary>
    public void Stop(bool immediate = false)
    {
        IWavePlayer? output;
        bool was;
        lock (_lock)
        {
            if (_current != null && !immediate && IsPlaying)
            {
                _current.BeginFade(0.06); // 短淡出
                return;
            }
            _playGen++;
            was = IsPlaying;
            output = DetachOutput();
            IsPlaying = false;
        }
        QueueCleanup(output);
        if (was) PlaybackStopped?.Invoke();
    }

    private void StopGeneration(int generation)
    {
        IWavePlayer? output;
        bool was;
        lock (_lock)
        {
            if (generation != _playGen) return;
            _playGen++;
            was = IsPlaying;
            output = DetachOutput();
            IsPlaying = false;
        }
        // Read 回调线程不能 Stop/Join 自己；在后台完成设备停止和释放。
        QueueCleanup(output);
        if (was) PlaybackStopped?.Invoke();
    }

    private IWavePlayer? DetachOutput()
    {
        if (_current != null) _current.BlockPlayed -= OnBlockPlayed;
        _current = null;
        var output = _output;
        if (output != null) output.PlaybackStopped -= OnStopped;
        _output = null;
        return output;
    }

    private static void QueueCleanup(IWavePlayer? output)
    {
        if (output == null) return;
        _ = Task.Run(() =>
        {
            try { output.Stop(); } catch { /* 设备可能已拔出 */ }
            try { output.Dispose(); } catch { /* 停止失败也必须尝试释放 */ }
        });
    }

    /// <summary>设备被拔出时停止播放并更新状态，不丢工程。</summary>
    public void HandleDeviceRemoved()
    {
        Stop(immediate: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop(immediate: true);
        if (_enumerator != null) _ = Task.Run(() =>
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notificationStub); } catch { }
            try { _enumerator.Dispose(); } catch { }
        });
    }

    /// <summary>输出设备移除通知（COM 回调适配）。</summary>
    private sealed class MMDeviceNotificationNotifications : IMMNotificationClient
    {
        public event Action<string>? DeviceRemoved;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnDeviceRemoved(string deviceId) => DeviceRemoved?.Invoke(deviceId);

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey propertyKey) { }
    }

    /// <summary>
    /// ISampleProvider → IWaveProvider 适配器。自己分配 float[] 暂存，用 Buffer.BlockCopy
    /// 按原始字节交接，避免 NAudio 扩展方法用 WaveBuffer 把 byte[] 当作 float[] 传回下游。
    /// </summary>
    private sealed class SampleProviderWaveAdapter(ISampleProvider source) : IWaveProvider
    {
        private readonly ISampleProvider _source = source;
        private float[] _scratch = Array.Empty<float>();

        public WaveFormat WaveFormat { get; } = source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            int bytesPerSample = WaveFormat.BitsPerSample / 8;
            if (bytesPerSample <= 0) return 0;
            int samplesRequired = count / bytesPerSample;
            if (samplesRequired == 0) return 0;
            if (_scratch.Length < samplesRequired)
                Array.Resize(ref _scratch, samplesRequired);

            int read = _source.Read(_scratch, 0, samplesRequired);
            if (read <= 0) return 0;
            Buffer.BlockCopy(_scratch, 0, buffer, offset, read * bytesPerSample);
            return read * bytesPerSample;
        }
    }

    /// <summary>float 数据源，逐块上报位置与峰值（供进度与电平显示）。</summary>
    private sealed class BufferedFloatSource(float[] data, int sampleRate, int channels) : ISampleProvider
    {
        private int _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _pos);
            if (n <= 0) return 0;
            // BlockCopy 按字节搬运，不依赖数组元素类型
            Buffer.BlockCopy(data, _pos * 4, buffer, offset * 4, n * 4);
            _pos += n;
            return n;
        }
    }

    /// <summary>监听音量补偿在播放出口处理，不能进入导出；淡出由本类实现。</summary>
    private sealed class FadeOutProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float _gain;
        private double _fadeLevel = 1;
        private double _fadeStep;
        private bool _fading;
        private bool _fadeEnded;
        private long _framesRead;

        public event Action? FadeCompleted;
        public event Action<double, (float, float)>? BlockPlayed;

        public FadeOutProvider(ISampleProvider source, double monitorGainDb)
        {
            _source = source;
            _gain = (float)Math.Pow(10, monitorGainDb / 20.0);
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public void BeginFade(double seconds)
        {
            _fadeStep = 1.0 / Math.Max(1, WaveFormat.SampleRate * seconds);
            _fading = true;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _source.Read(buffer, offset, count);
            if (n == 0) return 0;

            float peakL = 0, peakR = 0;
            int ch = WaveFormat.Channels;
            int frames = n / ch;
            for (int f = 0; f < frames; f++)
            {
                float g = _fadeEnded ? 0 : _gain;
                if (_fading)
                {
                    g *= (float)Math.Max(0.0, _fadeLevel);
                    _fadeLevel -= _fadeStep;
                    if (_fadeLevel <= 0)
                    {
                        _fadeLevel = 0;
                        _fading = false;
                        _fadeEnded = true;
                        FadeCompleted?.Invoke();
                    }
                }
                for (int c = 0; c < ch; c++)
                {
                    int idx = offset + f * ch + c;
                    buffer[idx] *= g;
                    float a = Math.Abs(buffer[idx]);
                    if (c == 0 && a > peakL) peakL = a;
                    if (c == 1 && a > peakR) peakR = a;
                }
            }
            _framesRead += frames;
            BlockPlayed?.Invoke(_framesRead / (double)WaveFormat.SampleRate, (peakL, peakR));
            return n;
        }
    }
}
