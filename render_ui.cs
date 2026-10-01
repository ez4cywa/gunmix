#:project src/GunMix.App/GunMix.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
// 离屏渲染 MainWindow 为 PNG（窗口放在屏幕外、不激活，不影响桌面）。
// 用法：dotnet run --no-cache render_ui.cs -- <工程.gunmix.json> <武器名> <输出.png> [tail]
// （--no-cache：确保使用最新构建的 GunMix.App，而不是文件型程序缓存的旧版本）
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GunMix.App;
using GunMix.App.ViewModels;

var a = Environment.GetCommandLineArgs().Skip(1).ToArray();
string projectPath = a[0], weaponName = a[1], outPng = a[2];
bool tail = a.Length > 3 && a[3] == "tail";

var t = new Thread(() =>
{
    var app = new App();
    app.InitializeComponent();
    var w = new MainWindow
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -20000, Top = -20000, Width = 1600, Height = 960,
        ShowActivated = false, ShowInTaskbar = false,
    };
    w.Show();
    var vm = (MainViewModel)w.DataContext;
    vm.OpenProjectPath(projectPath);
    vm.SelectedWeapon = vm.Weapons.First(x => x.Name == weaponName);
    if (tail) vm.ReleaseTailEnabled = true;
    vm.RegenerateManifests();

    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
    timer.Tick += (_, _) =>
    {
        timer.Stop();
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle((Brush)app.Resources["WinBg"], null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPng)) enc.Save(fs);
        Console.WriteLine($"saved {outPng}; status: {vm.StatusText}");
        vm.Shutdown();
        Environment.Exit(0);
    };
    timer.Start();
    Dispatcher.Run();
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
