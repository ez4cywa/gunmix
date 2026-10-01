namespace GunMix.Core.Model;

/// <summary>音频文件的真实格式信息（读取文件头得到）。</summary>
public sealed record AudioFormatInfo(int SampleRate, int BitsPerSample, int Channels, bool IsFloat, long FrameCount)
{
    public double DurationSeconds => SampleRate > 0 ? FrameCount / (double)SampleRate : 0;

    public string CodecName => IsFloat ? "IEEE float" : "PCM";

    public override string ToString() => $"{SampleRate} Hz / {BitsPerSample} bit / {(Channels == 1 ? "单声道" : Channels == 2 ? "双声道" : $"{Channels} 声道")}";
}
