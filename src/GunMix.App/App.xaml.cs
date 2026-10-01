using System.Windows;
using System.Windows.Threading;

namespace GunMix.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 出错时显示具体信息而不闪退：工程内容不丢失，可先保存（Ctrl+S）再继续。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        MessageBox.Show(
            $"软件遇到错误：{args.Exception.Message}\n\n工程内容未丢失；建议先保存工程（Ctrl+S）。若反复出现请重启软件。",
            "枪声分层工作台 — 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }
}
