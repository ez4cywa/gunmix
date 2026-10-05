using GunMix.App.Services;
using NAudio.Wave;
using Xunit;

namespace GunMix.Core.Tests;

public class PlaybackResponsivenessTests
{
    [Fact]
    public async Task StopDoesNotWaitForSlowDeviceInitialization()
    {
        using var player = new BlockingPlayer();
        using var service = new AudioPlaybackService(() => player);
        service.Play(new float[96000], 48000, 2, null);
        Assert.True(player.Initializing.Wait(TimeSpan.FromSeconds(3)));
        var stop = Task.Run(() => service.Stop(immediate: true));
        bool responsive = await Task.WhenAny(stop, Task.Delay(400)) == stop;
        player.ReleaseInit.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(responsive, "设备初始化未完成时，停止操作不能等待初始化锁，否则界面会卡死。");
    }

    [Fact]
    public async Task FadeCompletionStopsDeviceOutsideItsReadingThread()
    {
        using var player = new ReadingPlayer();
        using var service = new AudioPlaybackService(() => player);
        var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Play(new float[192000], 48000, 2, null, onStarted: e => started.TrySetResult(e));
        Assert.Null(await started.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        service.Stop();
        player.BeginReading.Set();
        Assert.True(await Task.Run(() => player.Stopped.Wait(TimeSpan.FromSeconds(3))));
        Assert.False(player.StoppedOnReadThread, "淡出不能在音频 Read 线程上 Stop/Join 自己。");
        Assert.False(service.IsPlaying);
    }

    private sealed class ReadingPlayer : IWavePlayer
    {
        private IWaveProvider? _source;
        private Thread? _reader;
        private volatile bool _stop;
        public ManualResetEventSlim BeginReading { get; } = new();
        public ManualResetEventSlim Stopped { get; } = new();
        public bool StoppedOnReadThread { get; private set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; }
        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public void Init(IWaveProvider source) => _source = source;
        public void Play()
        {
            PlaybackState = PlaybackState.Playing;
            _reader = new Thread(() =>
            {
                BeginReading.Wait(TimeSpan.FromSeconds(3));
                var bytes = new byte[8192];
                while (!_stop && _source!.Read(bytes, 0, bytes.Length) > 0) Thread.Yield();
            }) { IsBackground = true };
            _reader.Start();
        }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop()
        {
            StoppedOnReadThread |= Thread.CurrentThread == _reader;
            _stop = true; BeginReading.Set();
            if (_reader != null && Thread.CurrentThread != _reader) _reader.Join(TimeSpan.FromSeconds(2));
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs()); Stopped.Set();
        }
        public void Dispose() { _stop = true; BeginReading.Set(); }
    }

    private sealed class BlockingPlayer : IWavePlayer
    {
        public ManualResetEventSlim Initializing { get; } = new();
        public ManualResetEventSlim ReleaseInit { get; } = new();
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; }
        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public void Init(IWaveProvider source) { Initializing.Set(); ReleaseInit.Wait(TimeSpan.FromSeconds(5)); }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Dispose() { ReleaseInit.Set(); }
    }
}
