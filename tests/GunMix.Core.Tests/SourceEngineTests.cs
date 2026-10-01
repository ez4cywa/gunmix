using System.IO;
using GunMix.Core.ExportTargets;
using GunMix.Core.Mixing;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>
/// Source 引擎（Left 4 Dead 2 / Garry's Mod）导出目标：可选项。
/// 校验格式转换（44.1 kHz / 16 bit PCM）与 game_sounds 脚本语法
/// （对齐 Source SDK 的 game_sounds_weapons.txt）。
/// </summary>
public class SourceEngineTests
{
    private static float[] Sine(int sampleRate, int seconds, double amplitude = 0.5)
    {
        var data = new float[sampleRate * seconds * 2];
        for (int i = 0; i < data.Length / 2; i++)
        {
            float v = (float)(amplitude * System.Math.Sin(2 * System.Math.PI * 440 * i / sampleRate));
            data[i * 2] = v;
            data[i * 2 + 1] = v;
        }
        return data;
    }

    [Fact]
    public void ToSourcePcm_ResamplesTo44100AndInt16()
    {
        var src = Sine(48000, 1);                        // 48 kHz 立体声 1 秒
        var pcm = SourceEngine.ToSourcePcm(src, 48000, 2, monoDownmix: false, dither: false, ditherSeed: 1);

        // 1 秒 44.1 kHz 立体声 16 bit = 44100 * 2 * 2 字节
        Assert.Equal(44100 * 2 * 2, pcm.Length);
        // 非静音：能量必须保留
        double sum = 0;
        for (int i = 0; i < pcm.Length; i += 2) sum += System.Math.Abs((double)SourceEngineBytes(pcm, i));
        Assert.True(sum > 1000, "重采样后能量丢失");
    }

    private static short SourceEngineBytes(byte[] bytes, int offset) =>
        System.BitConverter.ToInt16(bytes, offset);

    [Fact]
    public void ToSourcePcm_MonoDownmixHalvesChannels()
    {
        var src = Sine(48000, 1);
        var mono = SourceEngine.ToSourcePcm(src, 48000, 2, monoDownmix: true, dither: false, ditherSeed: 1);
        var stereo = SourceEngine.ToSourcePcm(src, 48000, 2, monoDownmix: false, dither: false, ditherSeed: 1);

        Assert.Equal(mono.Length * 2, stereo.Length);     // 单声道是双声道字节数的一半
        Assert.Equal(44100 * 2, mono.Length);
    }

    [Fact]
    public void ToSourcePcm_EqualChannelContentSurvivesDownmix()
    {
        // 我们的混音对单声道素材是等幅复制到左右：降混后能量不变
        var src = Sine(48000, 1);
        var stereo = SourceEngine.ToSourcePcm(src, 48000, 2, monoDownmix: false, dither: false, ditherSeed: 1);
        var mono = SourceEngine.ToSourcePcm(src, 48000, 2, monoDownmix: true, dither: false, ditherSeed: 1);

        double stereoRms = 0, monoRms = 0;
        for (int i = 0; i < stereo.Length / 4; i++) stereoRms += SourceEngineBytes(stereo, i * 4) * (double)SourceEngineBytes(stereo, i * 4);
        for (int i = 0; i < mono.Length / 2; i++) monoRms += SourceEngineBytes(mono, i * 2) * (double)SourceEngineBytes(mono, i * 2);
        stereoRms = System.Math.Sqrt(stereoRms / (stereo.Length / 4));
        monoRms = System.Math.Sqrt(monoRms / (mono.Length / 2));
        Assert.True(System.Math.Abs(stereoRms - monoRms) < 1.0, $"降混后能量变化过大：stereo={stereoRms:0.##} mono={monoRms:0.##}");
    }

    [Fact]
    public void BuildGameSounds_SingleFileUsesWave()
    {
        var entry = new SourceEngine.SoundEntry("Weapon_Mike4.Single",
            SourceEngine.ChannelWeapon, "1.0", SourceEngine.SoundLevelGunfire, "PITCH_NORM",
            ["weapons/mike4/single_001.wav"]);
        var text = SourceEngine.BuildGameSounds("mike4", [entry]);

        Assert.Contains("\"Weapon_Mike4.Single\"", text);
        Assert.Contains("\"channel\"\t\t\"CHAN_WEAPON\"", text);
        Assert.Contains("\"soundlevel\"\t\t\"SNDLVL_GUNFIRE\"", text);
        Assert.Contains("\"wave\"\t\t\"weapons/mike4/single_001.wav\"", text);
        Assert.DoesNotContain("rndwave", text);            // 单文件不写 rndwave
    }

    [Fact]
    public void BuildGameSounds_MultipleFilesUseRndwave()
    {
        var entry = new SourceEngine.SoundEntry("Weapon_Mike4.Burst",
            SourceEngine.ChannelWeapon, "1.0", SourceEngine.SoundLevelGunfire, "PITCH_NORM",
            ["weapons/mike4/burst_001.wav", "weapons/mike4/burst_002.wav", "weapons/mike4/burst_003.wav"]);
        var text = SourceEngine.BuildGameSounds("mike4", [entry]);

        Assert.Contains("\"rndwave\"", text);              // 多变体用随机容器
        Assert.Equal(3, CountOccurrences(text, "\"wave\""));
        // rndwave 必须包住 wave 条目（语法对齐 Source SDK）
        int rnd = text.IndexOf("\"rndwave\"", StringComparison.Ordinal);
        int first = text.IndexOf("\"wave\"", StringComparison.Ordinal);
        Assert.True(rnd > 0 && first > rnd, "rndwave 应在 wave 之前");
    }

    [Fact]
    public void BuildGameSounds_SkipsEmptyEntries()
    {
        var empty = new SourceEngine.SoundEntry("Weapon_X.Empty",
            SourceEngine.ChannelItem, "0.7", SourceEngine.SoundLevelNorm, "PITCH_NORM", []);
        var text = SourceEngine.BuildGameSounds("x", [empty]);
        Assert.DoesNotContain("Weapon_X.Empty", text);
    }

    [Fact]
    public void SanitizeIdentifier_ProducesAsciiSafeName()
    {
        Assert.Equal("mike4", SourceEngine.SanitizeIdentifier("mike4"));
        Assert.Equal("ar_mike4", SourceEngine.SanitizeIdentifier("ar-mike4"));
        Assert.Equal("weapon", SourceEngine.SanitizeIdentifier("武器"));
        Assert.Equal("weap_rex_mike4", SourceEngine.SanitizeIdentifier("weap_rex_mike4"));
    }

    [Fact]
    public void GamePath_PointsUnderWeaponsFolder()
    {
        Assert.Equal("weapons/mike4/single_001.wav", SourceEngine.GamePath("mike4", "single_001.wav"));
        Assert.Equal("weapons/ar_mike4/fire.wav", SourceEngine.GamePath("ar-mike4", "fire.wav"));
    }

    [Fact]
    public void Export_SourceTarget_Writes44100MonoAndGameSounds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gunmix_src_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "mike4_single_001.wav");
            var result = Exporter.WriteWav(Sine(48000, 1), 48000, 2, 16, dither: false, ditherSeed: 1,
                attenuateToDbfs: null, wav, overwrite: false, sourceFormat: true, monoDownmix: true);
            Assert.True(result.Success, result.Error);

            // 校验写出的 WAV 是 44.1 kHz / 16 bit / 单声道（Source 格式）
            var (fmt, fmtError) = GunMix.Core.Audio.WavReader.ReadFormat(wav);
            Assert.Null(fmtError);
            Assert.NotNull(fmt);
            Assert.Equal(SourceEngine.SampleRate, fmt!.SampleRate);
            Assert.Equal(SourceEngine.BitsPerSample, fmt.BitsPerSample);
            Assert.Equal(1, fmt.Channels);

            // 生成 game_sounds 脚本并校验归类
            var entries = new[]
            {
                new SourceEngine.SoundEntry("Weapon_Mike4.Single", SourceEngine.ChannelWeapon, "1.0",
                    SourceEngine.SoundLevelGunfire, "PITCH_NORM", ["weapons/mike4/single_001.wav"]),
            };
            File.WriteAllText(Path.Combine(dir, "game_sounds_mike4.txt"),
                SourceEngine.BuildGameSounds("mike4", entries));
            var script = File.ReadAllText(Path.Combine(dir, "game_sounds_mike4.txt"));
            Assert.Contains("\"Weapon_Mike4.Single\"", script);
            Assert.Contains("weapons/mike4/single_001.wav", script);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { n++; i += token.Length; }
        return n;
    }
}
