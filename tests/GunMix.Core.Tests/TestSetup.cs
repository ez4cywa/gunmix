using System.IO;
using GunMix.Core.Persistence;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>
/// 测试装配：把自动恢复目录重定向到临时位置，
/// 避免测试写入用户真实的 %LOCALAPPDATA%\GunMix\recovery。
/// </summary>
internal static class TestSetup
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gunmix_test_recovery_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        ProjectStore.RecoveryDirectoryOverride = dir;
    }
}
